//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RetroIO.Identity;
using RetroIO.Network.DialUp;
using RetroIO.Network.Nat;
using RetroIO.Network.Router;
using RetroIO.Server.Hosting.Configuration;

namespace RetroIO.Server.DialUp;

/// <summary>
/// RetroServer's emulated dial-up ISP: a TCP port a caller's modem dials, a text login against
/// RetroServer's users, then the caller's IP packets over SLIP onto one shared subnet with a NAT
/// behind it.
/// </summary>
/// <remarks>
/// <para>
/// ONE THREAD owns everything that moves: every call's session, the router and the NAT. The accept
/// task only queues new sockets; the pump thread takes them. Nothing else is shared between threads
/// except that queue and the counters, which are read with <see cref="Interlocked"/>.
/// </para>
/// <para>
/// The NAT counts time in polls, <see cref="NatRouter.PollsPerSecond"/> of them a second, so it is
/// polled on that schedule from a <see cref="Stopwatch"/>. The callers' bytes are moved on every pass
/// of the loop, which runs about every millisecond.
/// </para>
/// <para>
/// Who may dial in: only users with the dial-up right (<see cref="RetroUser.AllowDialUp"/>; Ronny
/// 2026-11-22). A user's fixed address (<see cref="RetroUser.DialUpAddress"/>) is used when set,
/// otherwise the router's pool gives one. A fixed address already in use, or a full pool, ends the
/// call with a reason.
/// </para>
/// <para>
/// The password check takes real time (an Argon2id hash), so it runs on a worker
/// (<see cref="DialUpSettings.LoginStart"/>): the session waits in its checking stage and the pump
/// thread goes on moving every other call. Until 2026-11-22 it ran on the pump thread and held up
/// every call while one password was checked.
/// </para>
/// </remarks>
public sealed class DialUpServer : IDisposable
{
    private const int LineBufferSize = 4096;
    private const int MaxPacket = 65535;

    private readonly DialUpServerConfiguration _config;
    private readonly IRetroIDP _idp;
    private readonly Action<string> _log;
    private readonly ConcurrentQueue<Socket> _incoming = new ConcurrentQueue<Socket>();
    private readonly List<Call> _calls = new List<Call>();
    private readonly byte[] _receive = new byte[LineBufferSize];
    private readonly byte[] _send = new byte[LineBufferSize];
    private readonly byte[] _upstream = new byte[MaxPacket];
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();

    private IpRouter? _router;
    private NatEthernetBackend? _nat;
    private TcpListener? _listener;
    private Thread? _pump;
    private Task? _accept;
    private uint _nameServer;
    private bool _disposed;

    private long _callsAnswered;
    private long _callsRefusedFull;
    private long _loginsRefused;
    private long _bytesFromCallers;
    private long _bytesToCallers;
    private int _callsActive;

    /// <summary>
    /// Initialises the server. Nothing listens until <see cref="Start"/>.
    /// </summary>
    /// <param name="config">
    /// The settings.
    /// </param>
    /// <param name="idp">
    /// RetroServer's users.
    /// </param>
    /// <param name="log">
    /// Receives one line per event.
    /// </param>
    public DialUpServer(DialUpServerConfiguration config, IRetroIDP idp, Action<string> log)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _idp = idp ?? throw new ArgumentNullException(nameof(idp));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Gets the port the server listens on once started; useful when the configured port is 0.
    /// </summary>
    public int ListeningPort { get; private set; }

    /// <summary>
    /// Gets how many calls were answered.
    /// </summary>
    public long CallsAnswered => Interlocked.Read(ref _callsAnswered);

    /// <summary>
    /// Gets how many calls are up now.
    /// </summary>
    public int CallsActive => Volatile.Read(ref _callsActive);

    /// <summary>
    /// Gets how many calls were turned away because <see cref="DialUpServerConfiguration.MaxCalls"/> were up.
    /// </summary>
    public long CallsRefusedFull => Interlocked.Read(ref _callsRefusedFull);

    /// <summary>
    /// Gets how many login attempts were refused: unknown user, wrong password or no dial-up right.
    /// </summary>
    public long LoginsRefused => Interlocked.Read(ref _loginsRefused);

    /// <summary>
    /// Gets how many bytes callers sent.
    /// </summary>
    public long BytesFromCallers => Interlocked.Read(ref _bytesFromCallers);

    /// <summary>
    /// Gets how many bytes were sent to callers.
    /// </summary>
    public long BytesToCallers => Interlocked.Read(ref _bytesToCallers);

    /// <summary>
    /// Builds the subnet, the router and the NAT, and starts listening.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the prefix length is outside 8 to 29.
    /// </exception>
    /// <exception cref="FormatException">
    /// Thrown when the configured network is not a dotted IPv4 address.
    /// </exception>
    public void Start()
    {
        int prefix = _config.PrefixLength;
        if (prefix < 8 || prefix > 29)
        {
            throw new ArgumentOutOfRangeException(nameof(DialUpServerConfiguration.PrefixLength), prefix, "The subnet prefix must be 8 to 29.");
        }

        uint mask = 0xFFFFFFFFu << (32 - prefix);
        uint network = Ipv4.Parse(_config.Network) & mask;
        uint broadcast = network | ~mask;
        uint routerAddress = network + 1;
        uint gateway = network + 2;
        _nameServer = network + 3;
        uint firstCaller = network + 15;

        _router = new IpRouter(new RouterSettings
        {
            Network = network,
            PrefixLength = prefix,
            RouterAddress = routerAddress,
            PoolFirst = firstCaller,
            PoolLast = broadcast - 1,
            UpstreamAddresses = new[] { gateway, _nameServer },
        });

        // The NAT's gateway MAC: a locally administered address (02-...), as the NAT's own default is.
        byte[] gatewayMac = new byte[] { 0x02, 0x52, 0x53, 0x00, 0x02, 0x02 };
        _nat = new NatEthernetBackend(
            new NatAddressPlan(network, mask, gateway, _nameServer, firstCaller, gatewayMac),
            hostLoopback: true,
            nameServer: null,
            findHostNameServer: true);
        _router.OnUpstream += ToNat;
        _nat.OnPacketReceived += FromNat;
        _nat.Start();

        _listener = new TcpListener(IPAddress.Parse(_config.BindAddress), _config.Port);
        _listener.Start();
        ListeningPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _accept = Task.Run(AcceptLoop);
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "dial-up" };
        _pump.Start();

        _log($"[dialup] listening on {_config.BindAddress}:{ListeningPort}; callers on {Ipv4.Format(network)}/{prefix}, "
            + $"router {Ipv4.Format(routerAddress)}, gateway {Ipv4.Format(gateway)}, name server {Ipv4.Format(_nameServer)}, "
            + $"pool {Ipv4.Format(firstCaller)}..{Ipv4.Format(broadcast - 1)}, at most {_config.MaxCalls} calls");
    }

    /// <summary>
    /// Hangs up every call and stops.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
            // Already closed.
        }

        _pump?.Join(TimeSpan.FromSeconds(5));
        try
        {
            _accept?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The accept loop ends by cancellation; its exceptions are handled inside it.
        }

        _nat?.Stop();

        while (_incoming.TryDequeue(out Socket? waiting))
        {
            waiting.Dispose();
        }

        _stop.Dispose();
    }

    private async Task AcceptLoop()
    {
        TcpListener listener = _listener!;
        CancellationToken token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                _log($"[dialup] accept failed: {ex.SocketErrorCode}");
                continue;
            }

            socket.NoDelay = true;
            _incoming.Enqueue(socket);
        }
    }

    private void PumpLoop()
    {
        Stopwatch clock = Stopwatch.StartNew();
        long ticksPerPoll = Stopwatch.Frequency / NatRouter.PollsPerSecond;
        long nextPoll = clock.ElapsedTicks;
        CancellationToken token = _stop.Token;

        while (!token.IsCancellationRequested)
        {
            TakeNewCalls();
            bool busy = MoveBytes();

            long now = clock.ElapsedTicks;
            if (now >= nextPoll)
            {
                for (int i = 0; i < _calls.Count; i++)
                {
                    _calls[i].Session.Poll();
                }

                _nat!.Poll();
                nextPoll += ticksPerPoll;
                if (nextPoll < now)
                {
                    // Fell behind, for example during a login check: do not catch up in a burst.
                    nextPoll = now + ticksPerPoll;
                }
            }

            RemoveEndedCalls();
            if (!busy)
            {
                Thread.Sleep(1);
            }
        }

        for (int i = 0; i < _calls.Count; i++)
        {
            _calls[i].Session.CallerHungUp();
            _calls[i].Socket.Dispose();
        }

        _calls.Clear();
        Volatile.Write(ref _callsActive, 0);
    }

    private void TakeNewCalls()
    {
        while (_incoming.TryDequeue(out Socket? socket))
        {
            if (_calls.Count >= _config.MaxCalls)
            {
                Interlocked.Increment(ref _callsRefusedFull);
                _log($"[dialup] call from {socket.RemoteEndPoint} refused: {_config.MaxCalls} calls are up");
                TrySend(socket, "\r\nNo line free\r\n"u8);
                socket.Dispose();
                continue;
            }

            Call call = new Call(this, socket);
            RouterDialUpSide side = new RouterDialUpSide(_router!, _nameServer, addressLookup: call.LookUpAddress);
            DialUpSettings settings = new DialUpSettings
            {
                Greeting = _config.Greeting,
                LoginStart = call.StartLogin,
            };
            call.Session = new SlipDialUpSession(settings, side);
            call.Session.OnEnded += call.Ended;
            _calls.Add(call);
            Interlocked.Increment(ref _callsAnswered);
            Volatile.Write(ref _callsActive, _calls.Count);
            _log($"[dialup] call from {call.RemoteText} answered ({_calls.Count} up)");
            call.Session.Answer();
        }
    }

    private bool MoveBytes()
    {
        bool busy = false;
        for (int i = 0; i < _calls.Count; i++)
        {
            Call call = _calls[i];
            if (call.EndReason != null)
            {
                continue;
            }

            if (!ReadFromCaller(call, ref busy))
            {
                continue;
            }

            WriteToCaller(call, ref busy);
        }

        return busy;
    }

    private bool ReadFromCaller(Call call, ref bool busy)
    {
        try
        {
            if (!call.Socket.Poll(0, SelectMode.SelectRead))
            {
                return true;
            }

            int read = call.Socket.Receive(_receive, 0, _receive.Length, SocketFlags.None);
            if (read == 0)
            {
                call.Session.CallerHungUp();
                call.EndReason ??= "the caller hung up";
                return false;
            }

            busy = true;
            Interlocked.Add(ref _bytesFromCallers, read);
            for (int k = 0; k < read; k++)
            {
                if (!call.Session.TryTakeFromCaller(_receive[k]))
                {
                    // The session's input is full; the rest of this read is lost, as on a real line.
                    break;
                }
            }

            return true;
        }
        catch (SocketException ex)
        {
            call.Session.CallerHungUp();
            call.EndReason ??= "the line failed: " + ex.SocketErrorCode;
            return false;
        }
    }

    private void WriteToCaller(Call call, ref bool busy)
    {
        int count = DrainSession(call);
        if (count == 0)
        {
            return;
        }

        busy = true;
        if (!TrySend(call.Socket, new ReadOnlySpan<byte>(_send, 0, count)))
        {
            call.Session.CallerHungUp();
            call.EndReason ??= "the line failed while sending";
            return;
        }

        Interlocked.Add(ref _bytesToCallers, count);
    }

    private int DrainSession(Call call)
    {
        int count = 0;
        while (count < _send.Length && call.Session.TryGiveToCaller(out byte value))
        {
            _send[count++] = value;
        }

        return count;
    }

    private void RemoveEndedCalls()
    {
        for (int i = _calls.Count - 1; i >= 0; i--)
        {
            Call call = _calls[i];
            if (call.EndReason == null && !call.Session.Ended)
            {
                continue;
            }

            // What the session still has to say (a goodbye line) goes out before the line drops.
            int count = DrainSession(call);
            if (count > 0)
            {
                TrySend(call.Socket, new ReadOnlySpan<byte>(_send, 0, count));
            }

            _log($"[dialup] call from {call.RemoteText} ended: {call.EndReason ?? "ended"}");
            call.Socket.Dispose();
            _calls.RemoveAt(i);
            Volatile.Write(ref _callsActive, _calls.Count);
        }
    }

    private static bool TrySend(Socket socket, ReadOnlySpan<byte> bytes)
    {
        try
        {
            while (bytes.Length > 0)
            {
                int sent = socket.Send(bytes, SocketFlags.None);
                bytes = bytes.Slice(sent);
            }

            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void ToNat(ReadOnlySpan<byte> packet)
    {
        // One reused buffer: everything runs on the pump thread, and SendPacket takes the bytes at once.
        packet.CopyTo(_upstream);
        _nat!.SendPacket(_upstream, 0, packet.Length);
    }

    private void FromNat(byte[] packet, int offset, int length)
    {
        _router!.SendFromUpstream(new ReadOnlySpan<byte>(packet, offset, length));
    }

    /// <summary>
    /// Checks a caller's login against RetroServer's users, with the dial-up right, on a worker, and
    /// answers the session when done.
    /// </summary>
    /// <remarks>
    /// Runs off the pump thread. It writes only the call's fixed address (before answering, so the
    /// pump thread sees it when it acts on the answer) and the refused-login counter (Interlocked).
    /// </remarks>
    private async Task CheckUserAsync(Call call, string name, string password, DialUpLoginAnswer answer)
    {
        try
        {
            ProtocolAuthResult result = await RetroUserProtocolAuth
                .AuthenticateAsync(_idp, name, password, RetroFsProtocol.DialUp).ConfigureAwait(false);
            if (result != ProtocolAuthResult.Success)
            {
                Interlocked.Increment(ref _loginsRefused);
                _log($"[dialup] login '{name}' from {call.RemoteText} refused: {result}");
                answer.Refuse();
                return;
            }

            RetroUser? user = await _idp.GetUserByUsernameAsync(name).ConfigureAwait(false);
            uint fixedAddress = 0;
            string fixedText = string.Empty;
            if (user != null && !string.IsNullOrEmpty(user.DialUpAddress))
            {
                fixedAddress = Ipv4.Parse(user.DialUpAddress);
                fixedText = user.DialUpAddress;
            }

            call.FixedAddress = fixedAddress;
            _log($"[dialup] login '{name}' from {call.RemoteText} accepted, "
                + (fixedText.Length == 0 ? "address from the pool" : "fixed address " + fixedText));
            answer.Accept();
        }
        catch (Exception ex) when (ex is IOException || ex is FormatException || ex is InvalidOperationException)
        {
            // The user store could not be read, or the stored fixed address is not an address: the
            // caller is refused and the reason is logged; nothing is let in on a failed check.
            Interlocked.Increment(ref _loginsRefused);
            _log($"[dialup] login '{name}' from {call.RemoteText} refused: the check failed ({ex.GetType().Name}: {ex.Message})");
            answer.Refuse();
        }
    }

    /// <summary>
    /// One call: its socket, its session and what its login found.
    /// </summary>
    private sealed class Call
    {
        private readonly DialUpServer _server;

        public Call(DialUpServer server, Socket socket)
        {
            _server = server;
            Socket = socket;
            RemoteText = socket.RemoteEndPoint?.ToString() ?? "?";
        }

        public Socket Socket { get; }

        public string RemoteText { get; }

        public SlipDialUpSession Session { get; set; } = null!;

        public string? EndReason { get; set; }

        private uint _fixedAddress;

        /// <summary>
        /// Gets or sets the fixed address the login found; written by the login worker, read by the pump thread.
        /// </summary>
        public uint FixedAddress
        {
            get => Volatile.Read(ref _fixedAddress);
            set => Volatile.Write(ref _fixedAddress, value);
        }

        public void StartLogin(string name, string password, DialUpLoginAnswer answer)
        {
            _ = Task.Run(() => _server.CheckUserAsync(this, name, password, answer));
        }

        public uint LookUpAddress(string userName) => FixedAddress;

        public void Ended(string reason) => EndReason ??= reason;
    }
}
