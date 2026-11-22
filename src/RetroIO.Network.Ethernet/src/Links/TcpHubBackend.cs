//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace RetroIO.Network.Ethernet
{
    /// <summary>
    /// A TCP hub joining many emulators into one Ethernet segment (<c>hub:PORT</c>). It listens,
    /// any number of members connect to it with <c>tcp:HOST:PORT</c> (this package's
    /// <see cref="TcpLinkBackend"/>, the old ND-100 code or nd100x), and every frame from one
    /// member goes to all the others. The machine running the hub is a member too.
    /// </summary>
    /// <remarks>
    /// It speaks the same bytes as a TCP link (<see cref="EthernetLinkWire"/>), as the old code's
    /// relay (Emulated.HW/Common/Network/TcpEthernetRelay.cs) does, so a member cannot tell a hub
    /// from a link. Like that relay it is a plain hub: no address learning, a frame never goes
    /// back to the member it came from. Each member has its own receive thread; a send to a dead
    /// member is dropped and its own thread removes it when it sees the link close.
    /// </remarks>
    public sealed class TcpHubBackend : IEthernetBackend
    {
        // ── Constants ──

        /// <summary>
        /// The spec prefix.
        /// </summary>
        public const string Prefix = "hub";

        // ── Fields ──

        private readonly EthernetLinkReceiveQueue _queue = new EthernetLinkReceiveQueue(EthernetLinkWire.TcpMaxFrameBytes);
        private readonly List<Member> _members = new List<Member>();
        private readonly object _membersLock = new object();
        private int _port;
        private Socket? _listener;
        private Thread? _acceptThread;
        private volatile bool _stop;
        private long _framesForwarded;

        // ── Events ──

        /// <inheritdoc/>
        public event EthernetFrameHandler? OnFrameReceived;

        // ── Constructors ──

        /// <summary>
        /// Initializes a new <see cref="TcpHubBackend"/>.
        /// </summary>
        /// <param name="port">
        /// The port to listen on; 0 lets the system pick (see <see cref="Port"/>).
        /// </param>
        public TcpHubBackend(int port)
        {
            _port = port;
        }

        // ── Properties ──

        /// <inheritdoc/>
        public bool IsRunning => _acceptThread != null;

        /// <summary>
        /// Gets the port listened on (the system's pick once started, when 0 was asked).
        /// </summary>
        public int Port => _port;

        /// <summary>
        /// Gets how many members are connected, not counting this machine.
        /// </summary>
        public int MemberCount
        {
            get
            {
                lock (_membersLock)
                {
                    return _members.Count;
                }
            }
        }

        /// <summary>
        /// Gets how many frames came in from members (each counted once).
        /// </summary>
        public long FramesForwarded => Interlocked.Read(ref _framesForwarded);

        /// <summary>
        /// Gets how many frames for this machine were dropped because it fell behind.
        /// </summary>
        public long FramesDroppedFull => _queue.FramesDroppedFull;

        // ── Public methods ──

        /// <summary>
        /// Adds this backend's spec reader to <see cref="EthernetBackends"/>.
        /// </summary>
        public static void RegisterFactory()
        {
            EthernetBackends.Register(CreateFromSpec);
        }

        /// <summary>
        /// Makes a hub from <c>hub</c> or <c>hub:PORT</c> (3094 when left out), or gives <c>null</c>.
        /// </summary>
        /// <param name="spec">
        /// The spec.
        /// </param>
        /// <returns>
        /// The backend, or <c>null</c>.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown for a hub spec with a bad port.
        /// </exception>
        public static IEthernetBackend? CreateFromSpec(string spec)
        {
            if (spec == null)
            {
                return null;
            }
            string s = spec.Trim();
            if (s.Equals(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return new TcpHubBackend(EthernetLinkWire.DefaultPort);
            }
            if (!s.StartsWith(Prefix + ":", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            string t = s.Substring(Prefix.Length + 1).Trim();
            if (t.Length == 0)
            {
                return new TcpHubBackend(EthernetLinkWire.DefaultPort);
            }
            if (!int.TryParse(t, out int port) || port < 0 || port > 65535)
            {
                throw new ArgumentException("The port in '" + spec + "' is not a number from 1 to 65535.", nameof(spec));
            }
            return new TcpHubBackend(port);
        }

        /// <inheritdoc/>
        public void Start()
        {
            if (_acceptThread != null)
            {
                return;
            }
            _stop = false;
            Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Any, _port));
            listener.Listen(16);
            _port = ((IPEndPoint)listener.LocalEndPoint!).Port;
            _listener = listener;
            _acceptThread = new Thread(AcceptLoop) { Name = "TcpHub", IsBackground = true };
            _acceptThread.Start();
        }

        /// <inheritdoc/>
        public void Stop()
        {
            _stop = true;
            Socket? listener = _listener;
            _listener = null;
            CloseQuietly(listener);
            Member[] members;
            lock (_membersLock)
            {
                members = _members.ToArray();
                _members.Clear();
            }
            for (int i = 0; i < members.Length; i++)
            {
                CloseQuietly(members[i].Socket);
            }
            _acceptThread?.Join(2000);
            _acceptThread = null;
        }

        /// <inheritdoc/>
        public void Send(byte[] frame, int offset, int length)
        {
            if (length <= 0 || length > EthernetLinkWire.TcpMaxFrameBytes)
            {
                return;
            }
            SendToMembers(new ReadOnlySpan<byte>(frame, offset, length), null);
        }

        /// <inheritdoc/>
        public void Poll()
        {
            _queue.Deliver(OnFrameReceived);
        }

        // ── Private methods ──

        private static void CloseQuietly(Socket? socket)
        {
            if (socket == null)
            {
                return;
            }
            try
            {
                socket.Close();
            }
            catch (SocketException)
            {
                // Already gone.
            }
        }

        private void AcceptLoop()
        {
            while (!_stop)
            {
                Socket? listener = _listener;
                if (listener == null)
                {
                    return;
                }
                Socket socket;
                try
                {
                    socket = listener.Accept();
                }
                catch (SocketException)
                {
                    if (_stop)
                    {
                        return;
                    }
                    Thread.Sleep(100);
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                Thread thread = new Thread(MemberLoop) { Name = "TcpHubMember", IsBackground = true };
                thread.Start(socket);
            }
        }

        private void MemberLoop(object? state)
        {
            Socket socket = (Socket)state!;
            Member member = new Member(socket);
            byte[] buffer = new byte[EthernetLinkWire.TcpMaxFrameBytes];
            bool added = false;
            try
            {
                socket.NoDelay = true;
                if (!EthernetLinkWire.Handshake(socket))
                {
                    return;
                }
                lock (_membersLock)
                {
                    _members.Add(member);
                }
                added = true;
                while (!_stop && EthernetLinkWire.ReceiveFrame(socket, buffer, out int length))
                {
                    Interlocked.Increment(ref _framesForwarded);
                    ReadOnlySpan<byte> frame = new ReadOnlySpan<byte>(buffer, 0, length);
                    SendToMembers(frame, member);
                    _queue.Put(frame);
                }
            }
            catch (SocketException)
            {
                // The member left.
            }
            catch (ObjectDisposedException)
            {
                // Stop() closed it.
            }
            finally
            {
                if (added)
                {
                    lock (_membersLock)
                    {
                        _members.Remove(member);
                    }
                }
                CloseQuietly(socket);
            }
        }

        // Every member but the one the frame came from (null: it came from this machine).
        private void SendToMembers(ReadOnlySpan<byte> frame, Member? from)
        {
            Member[] members;
            lock (_membersLock)
            {
                members = _members.ToArray();
            }
            for (int i = 0; i < members.Length; i++)
            {
                Member m = members[i];
                if (ReferenceEquals(m, from))
                {
                    continue;
                }
                try
                {
                    lock (m.SendLock)
                    {
                        EthernetLinkWire.SendFrame(m.Socket, frame);
                    }
                }
                catch (SocketException)
                {
                    // A dead member: dropped for it; its own thread removes it.
                }
                catch (ObjectDisposedException)
                {
                    // Closed by Stop().
                }
            }
        }

        // ── Nested types ──

        private sealed class Member
        {
            public Member(Socket socket)
            {
                Socket = socket;
            }

            public Socket Socket { get; }

            public object SendLock { get; } = new object();
        }
    }
}
