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
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25.Modules;

namespace RetroIO.Protocols.X25.Runtime
{
    /// <summary>
    /// Where the telnet PAD server is in its life.
    /// </summary>
    public enum TelnetPadState
    {
        /// <summary>
        /// Not listening: switched off, or stopped.
        /// </summary>
        Off,

        /// <summary>
        /// Listening for connections.
        /// </summary>
        Listening,

        /// <summary>
        /// The server could not start (for example the port is in use). <see cref="TelnetPadStatus.Error"/> says why.
        /// </summary>
        Failed
    }

    /// <summary>
    /// A snapshot of the telnet PAD for a status display.
    /// </summary>
    public sealed class TelnetPadStatus
    {
        /// <summary>
        /// Off, listening or failed.
        /// </summary>
        public TelnetPadState State { get; }

        /// <summary>
        /// The address listened on (or tried), empty when off.
        /// </summary>
        public string Address { get; }

        /// <summary>
        /// The port listened on (or tried), 0 when off.
        /// </summary>
        public int Port { get; }

        /// <summary>
        /// The number of telnet sessions open now.
        /// </summary>
        public int Sessions { get; }

        /// <summary>
        /// Why the server failed to start; empty unless <see cref="State"/> is <see cref="TelnetPadState.Failed"/>.
        /// </summary>
        public string Error { get; }

        /// <summary>
        /// Creates a status snapshot.
        /// </summary>
        /// <param name="state">The state.</param>
        /// <param name="address">The address.</param>
        /// <param name="port">The port.</param>
        /// <param name="sessions">The open session count.</param>
        /// <param name="error">The failure reason.</param>
        public TelnetPadStatus(TelnetPadState state, string address, int port, int sessions, string error)
        {
            State = state;
            Address = address;
            Port = port;
            Sessions = sessions;
            Error = error;
        }

        /// <summary>
        /// The status as one line for the person: <c>Telnet PAD: listening on 127.0.0.1:2323, 2 sessions</c>,
        /// <c>Telnet PAD: off</c> or <c>Telnet PAD: failed: ...</c>.
        /// </summary>
        public string Text
        {
            get
            {
                switch (State)
                {
                    case TelnetPadState.Listening:
                        return "Telnet PAD: listening on " + Address + ":" + Port + ", " + Sessions + (Sessions == 1 ? " session" : " sessions");
                    case TelnetPadState.Failed:
                        return "Telnet PAD: failed: " + Error;
                    default:
                        return "Telnet PAD: off";
                }
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Text;
        }
    }

    /// <summary>
    /// A telnet server that gives every connection its own X.28 PAD. A person connects with an ordinary
    /// terminal program, sees the <c>PAD&gt; </c> prompt and types PAD commands (CALL, CLR, STAT, PAR, SET ...);
    /// after a call is connected the typed characters go to the host and the host's output comes back.
    /// </summary>
    /// <remarks>
    /// Each session owns one <see cref="PadClient"/> made by the factory given to the constructor. The
    /// PAD does the echo, as it does in the Desktop PAD window, so the server offers WILL ECHO and
    /// WILL SUPPRESS-GO-AHEAD and the telnet client sends one character at a time without echoing.
    /// When the client closes the connection (or the server stops) the call is cleared and the PAD is released.
    /// There is no login: the default bind address is the loopback interface.
    /// </remarks>
    public sealed class TelnetPadServer : IDisposable
    {
        private const string Banner = "\r\nX.28 PAD over telnet. Type HELP for the commands, CALL <address> to place a call.\r\n\r\n";

        private readonly ILogger _logger;
        private readonly Func<string, PadClient> _createPad;
        private readonly Action<PadClient> _releasePad;
        private readonly object _lock = new object();
        private readonly List<Session> _sessions = new List<Session>();

        private TelnetPadConfig _config = new TelnetPadConfig();
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private TelnetPadState _state = TelnetPadState.Off;
        private string _error = string.Empty;
        private string _address = string.Empty;
        private int _port;
        private int _sessionCounter;

        /// <summary>
        /// Creates a stopped server.
        /// </summary>
        /// <param name="logger">
        /// Diagnostics logger.
        /// </param>
        /// <param name="createPad">
        /// Makes the PAD for a new session; it receives a unique session name.
        /// </param>
        /// <param name="releasePad">
        /// Gives a PAD back when its session ends (the caller unregisters and disposes it).
        /// </param>
        public TelnetPadServer(ILogger logger, Func<string, PadClient> createPad, Action<PadClient> releasePad)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _createPad = createPad ?? throw new ArgumentNullException(nameof(createPad));
            _releasePad = releasePad ?? throw new ArgumentNullException(nameof(releasePad));
        }

        /// <summary>
        /// Raised when the state or the number of sessions changes. It runs on a server thread.
        /// </summary>
        public event EventHandler? StatusChanged;

        /// <summary>
        /// The port actually listening (or tried); 0 when off.
        /// </summary>
        public int Port
        {
            get
            {
                lock (_lock)
                {
                    return _port;
                }
            }
        }

        /// <summary>
        /// Takes a snapshot of the state for a status display.
        /// </summary>
        /// <returns>
        /// The status.
        /// </returns>
        public TelnetPadStatus GetStatus()
        {
            lock (_lock)
            {
                return new TelnetPadStatus(_state, _address, _port, _sessions.Count, _error);
            }
        }

        /// <summary>
        /// Starts listening with the given settings. A server that is already running is stopped first.
        /// Nothing is thrown when the port cannot be opened: the state becomes
        /// <see cref="TelnetPadState.Failed"/> and the reason is in the status.
        /// </summary>
        /// <param name="config">
        /// The settings (copied).
        /// </param>
        /// <returns>
        /// True when the server is listening.
        /// </returns>
        public bool Start(TelnetPadConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            Stop();

            TelnetPadConfig copy = new TelnetPadConfig
            {
                Enabled = config.Enabled,
                Port = config.Port,
                BindAddress = config.BindAddress,
                MaxSessions = config.MaxSessions
            };

            string? problem = copy.Validate();
            if (problem != null)
            {
                SetFailed(copy, problem);
                return false;
            }

            TelnetPadConfig.TryParseBindAddress(copy.BindAddress, out IPAddress address);
            TcpListener listener = new TcpListener(address, copy.Port);
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                string reason = ex is SocketException se && se.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? "port " + copy.Port + " is already in use"
                    : ex.Message;
                SetFailed(copy, reason);
                _logger.LogError(ex, "Telnet PAD could not listen on {Address}:{Port}", address, copy.Port);
                return false;
            }

            lock (_lock)
            {
                _config = copy;
                _listener = listener;
                _running = true;
                _state = TelnetPadState.Listening;
                _error = string.Empty;
                _address = address.ToString();
                _port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "TelnetPad-Accept" };
                _acceptThread.Start();
            }

            _logger.LogInformation("Telnet PAD listening on {Address}:{Port}", _address, _port);
            RaiseStatusChanged();
            return true;
        }

        /// <summary>
        /// Stops listening, clears every call and closes every session.
        /// </summary>
        public void Stop()
        {
            TcpListener? listener;
            Thread? acceptThread;
            Session[] sessions;
            lock (_lock)
            {
                _running = false;
                listener = _listener;
                _listener = null;
                acceptThread = _acceptThread;
                _acceptThread = null;
                sessions = _sessions.ToArray();
            }

            try
            {
                listener?.Stop();
            }
            catch (Exception)
            {
                // best effort: the listener is going away
            }

            if (acceptThread != null && acceptThread != Thread.CurrentThread)
            {
                acceptThread.Join(2000);
            }

            for (int i = 0; i < sessions.Length; i++)
            {
                sessions[i].Close();
            }

            for (int i = 0; i < sessions.Length; i++)
            {
                sessions[i].WaitForEnd(3000);
            }

            bool changed;
            lock (_lock)
            {
                changed = _state != TelnetPadState.Off || _error.Length > 0;
                _state = TelnetPadState.Off;
                _error = string.Empty;
                _address = string.Empty;
                _port = 0;
            }

            if (changed)
            {
                RaiseStatusChanged();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Stop();
        }

        private void SetFailed(TelnetPadConfig attempted, string reason)
        {
            lock (_lock)
            {
                _state = TelnetPadState.Failed;
                _error = reason;
                _address = attempted.BindAddress;
                _port = attempted.Port;
            }

            _logger.LogWarning("Telnet PAD failed to start: {Reason}", reason);
            RaiseStatusChanged();
        }

        private void RaiseStatusChanged()
        {
            try
            {
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telnet PAD status handler failed");
            }
        }

        private void AcceptLoop()
        {
            TcpListener? listener;
            lock (_lock)
            {
                listener = _listener;
            }

            while (_running && listener != null)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    break; // the listener was stopped
                }

                try
                {
                    client.NoDelay = true;
                    client.SendTimeout = 5000;
                }
                catch (Exception)
                {
                    // socket already gone; the session below ends at once
                }

                Session? session = null;
                bool tooMany;
                lock (_lock)
                {
                    tooMany = _sessions.Count >= _config.MaxSessions;
                    if (!tooMany)
                    {
                        _sessionCounter++;
                        session = new Session(this, client, _sessionCounter);
                        _sessions.Add(session);
                    }
                }

                if (session == null)
                {
                    RefuseTooMany(client);
                    continue;
                }

                RaiseStatusChanged();
                session.Start();
            }
        }

        private void RefuseTooMany(TcpClient client)
        {
            _logger.LogInformation("Telnet PAD refused a connection: {Max} sessions are already open", _config.MaxSessions);
            try
            {
                byte[] text = Encoding.ASCII.GetBytes("\r\nPAD busy: all " + _config.MaxSessions + " telnet sessions are in use. Try again later.\r\n");
                client.GetStream().Write(text, 0, text.Length);
            }
            catch (Exception)
            {
                // the client may already have gone
            }
            finally
            {
                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                    // best effort
                }
            }
        }

        private void SessionEnded(Session session)
        {
            lock (_lock)
            {
                _sessions.Remove(session);
            }

            RaiseStatusChanged();
        }

        /// <summary>
        /// One telnet connection with its own PAD.
        /// </summary>
        private sealed class Session
        {
            private readonly TelnetPadServer _owner;
            private readonly TcpClient _client;
            private readonly int _number;
            private readonly object _writeLock = new object();
            private readonly ManualResetEventSlim _ended = new ManualResetEventSlim(false);
            private readonly TelnetInputFilter _filter = new TelnetInputFilter();
            private NetworkStream? _stream;
            private PadClient? _pad;
            private volatile bool _closed;

            public Session(TelnetPadServer owner, TcpClient client, int number)
            {
                _owner = owner;
                _client = client;
                _number = number;
            }

            public void Start()
            {
                Thread t = new Thread(Run) { IsBackground = true, Name = "TelnetPad-Session-" + _number };
                t.Start();
            }

            public bool WaitForEnd(int milliseconds)
            {
                return _ended.Wait(milliseconds);
            }

            public void Close()
            {
                _closed = true;
                try
                {
                    _client.Close();
                }
                catch (Exception)
                {
                    // best effort
                }
            }

            private void Run()
            {
                PadClient? pad = null;
                try
                {
                    _stream = _client.GetStream();

                    // We echo (through the PAD) and we suppress go-ahead; we ask the client to do the same.
                    // The client then sends each key at once and does not echo it itself.
                    byte[] offer =
                    {
                        TelnetInputFilter.Iac, TelnetInputFilter.Will, TelnetInputFilter.OptionEcho,
                        TelnetInputFilter.Iac, TelnetInputFilter.Will, TelnetInputFilter.OptionSuppressGoAhead,
                        TelnetInputFilter.Iac, TelnetInputFilter.Do, TelnetInputFilter.OptionSuppressGoAhead
                    };
                    WriteRaw(offer, offer.Length);

                    pad = _owner._createPad("TelnetPad_" + _number);
                    _pad = pad;
                    pad.DataReceived += OnPadData;
                    pad.CharacterEcho += OnPadEcho;
                    // EXIT at the * prompt: the PAD has cleared any call; say goodbye and close this telnet
                    // connection. The read loop then ends and End gives the PAD back.
                    pad.ExitRequested += OnPadExit;

                    WriteText(Banner);
                    pad.Start();

                    while (!_closed)
                    {
                        int read = _stream.ReadByte();
                        if (read < 0)
                        {
                            break; // the client closed the connection
                        }

                        TelnetFilterResult r = _filter.Feed((byte)read);
                        if (r.HasReply)
                        {
                            byte[] reply = { TelnetInputFilter.Iac, r.ReplyVerb, r.ReplyOption };
                            WriteRaw(reply, reply.Length);
                        }

                        if (r.HasData)
                        {
                            pad.OnKey((char)r.Data);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!_closed)
                    {
                        _owner._logger.LogDebug(ex, "Telnet PAD session {Number} ended with an error", _number);
                    }
                }
                finally
                {
                    End(pad);
                }
            }

            private void End(PadClient? pad)
            {
                _closed = true;
                if (pad != null)
                {
                    // Stop writing to the dead socket first, then clear the call and give the PAD back.
                    pad.DataReceived -= OnPadData;
                    pad.CharacterEcho -= OnPadEcho;
                    pad.ExitRequested -= OnPadExit;
                    try
                    {
                        pad.Disconnect();
                    }
                    catch (Exception ex)
                    {
                        _owner._logger.LogWarning(ex, "Telnet PAD session {Number}: clearing the call failed", _number);
                    }

                    try
                    {
                        _owner._releasePad(pad);
                    }
                    catch (Exception ex)
                    {
                        _owner._logger.LogWarning(ex, "Telnet PAD session {Number}: releasing the PAD failed", _number);
                    }
                }

                try
                {
                    _client.Close();
                }
                catch (Exception)
                {
                    // best effort
                }

                _owner.SessionEnded(this);
                _ended.Set();
            }

            private void OnPadData(object? sender, PadDataEventArgs e)
            {
                WriteText(e.Data);
            }

            private void OnPadExit(object? sender, EventArgs e)
            {
                WriteText("\r\nDisconnected.\r\n");
                Close();
            }

            private void OnPadEcho(object? sender, PadCharacterEventArgs e)
            {
                byte[] one = { e.Character };
                WriteEscaped(one, 1);
            }

            private void WriteText(string text)
            {
                if (text.Length == 0)
                {
                    return;
                }

                // One byte per character (Latin-1): nothing is lost for the 8-bit data a host may send.
                byte[] bytes = new byte[text.Length];
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    bytes[i] = c < 256 ? (byte)c : (byte)'?';
                }

                WriteEscaped(bytes, bytes.Length);
            }

            /// <summary>
            /// Writes terminal output, doubling every 0xFF so the client does not take it for a telnet command.
            /// </summary>
            private void WriteEscaped(byte[] data, int length)
            {
                int ffCount = 0;
                for (int i = 0; i < length; i++)
                {
                    if (data[i] == 0xFF)
                    {
                        ffCount++;
                    }
                }

                if (ffCount == 0)
                {
                    WriteRaw(data, length);
                    return;
                }

                byte[] doubled = new byte[length + ffCount];
                int o = 0;
                for (int i = 0; i < length; i++)
                {
                    doubled[o++] = data[i];
                    if (data[i] == 0xFF)
                    {
                        doubled[o++] = 0xFF;
                    }
                }

                WriteRaw(doubled, doubled.Length);
            }

            private void WriteRaw(byte[] data, int length)
            {
                NetworkStream? stream = _stream;
                if (stream == null || _closed)
                {
                    return;
                }

                // The PAD can write from the session thread (echo) and from the runtime's threads (host
                // output) at the same time: one writer at a time keeps the bytes in order.
                lock (_writeLock)
                {
                    try
                    {
                        stream.Write(data, 0, length);
                    }
                    catch (Exception)
                    {
                        // The client is gone or too slow. Closing makes the read loop end, which clears the call.
                        Close();
                    }
                }
            }
        }
    }
}
