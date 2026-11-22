//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace RetroIO.Network.Ethernet
{
    /// <summary>
    /// A TCP link between two emulators: one side listens (<c>listen:PORT</c>), the other
    /// connects (<c>tcp:HOST:PORT</c>), and every Ethernet frame either sends reaches the other.
    /// A connecting side can also dial a hub (<see cref="TcpHubBackend"/>, or the old code's
    /// relay), which joins many emulators into one segment.
    /// </summary>
    /// <remarks>
    /// The bytes on the wire are in <see cref="EthernetLinkWire"/> and in
    /// RetroCore's host networking design (host-networking-design.md in the RetroCore repository) section 12, the same as the old
    /// ND-100 code and nd100x. A background thread owns the connection: it connects (again after
    /// a drop, waiting 250 ms doubling to 4 s), or accepts the next peer, and puts received
    /// frames in a bounded queue that <see cref="Poll"/> empties on the emulation thread. A
    /// listening side takes one peer at a time, as the old code does; a second peer waits until
    /// the first leaves.
    /// </remarks>
    public sealed class TcpLinkBackend : IEthernetBackend
    {
        // ── Constants ──

        /// <summary>
        /// The spec prefix of a connecting side.
        /// </summary>
        public const string ConnectPrefix = "tcp";

        /// <summary>
        /// The spec prefix of a listening side.
        /// </summary>
        public const string ListenPrefix = "listen";

        private const int FirstRetryMs = 250;
        private const int LastRetryMs = 4000;

        // ── Fields ──

        private readonly bool _listen;
        private readonly string _host;
        private readonly EthernetLinkReceiveQueue _queue = new EthernetLinkReceiveQueue(EthernetLinkWire.TcpMaxFrameBytes);
        private readonly object _sendLock = new object();
        private int _port;
        private Socket? _listener;
        private Socket? _connection;
        private Thread? _thread;
        private volatile bool _stop;
        private long _framesSent;
        private long _framesReceived;
        private long _linksUp;

        // ── Events ──

        /// <inheritdoc/>
        public event EthernetFrameHandler? OnFrameReceived;

        // ── Constructors ──

        /// <summary>
        /// Initializes a new <see cref="TcpLinkBackend"/>.
        /// </summary>
        /// <param name="listen">
        /// <c>true</c> to listen, <c>false</c> to connect.
        /// </param>
        /// <param name="host">
        /// The host to connect to (ignored when listening).
        /// </param>
        /// <param name="port">
        /// The port; 0 when listening lets the system pick (see <see cref="Port"/>).
        /// </param>
        public TcpLinkBackend(bool listen, string host, int port)
        {
            _listen = listen;
            _host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
            _port = port;
        }

        // ── Properties ──

        /// <inheritdoc/>
        public bool IsRunning => _thread != null;

        /// <summary>
        /// Gets whether a peer is connected and greeted right now.
        /// </summary>
        public bool IsLinkUp => Volatile.Read(ref _connection) != null;

        /// <summary>
        /// Gets the port: the one listened on once started, or the one dialled.
        /// </summary>
        public int Port => _port;

        /// <summary>
        /// Gets how many frames were sent.
        /// </summary>
        public long FramesSent => Interlocked.Read(ref _framesSent);

        /// <summary>
        /// Gets how many frames were received.
        /// </summary>
        public long FramesReceived => Interlocked.Read(ref _framesReceived);

        /// <summary>
        /// Gets how many times a link came up.
        /// </summary>
        public long LinksUp => Interlocked.Read(ref _linksUp);

        /// <summary>
        /// Gets how many received frames were dropped because the guest fell behind.
        /// </summary>
        public long FramesDroppedFull => _queue.FramesDroppedFull;

        /// <summary>
        /// Gets a one-line description, as the setting reads.
        /// </summary>
        public string Description => _listen ? ListenPrefix + ":" + _port : ConnectPrefix + ":" + _host + ":" + _port;

        // ── Public methods ──

        /// <summary>
        /// Adds this backend's spec reader to <see cref="EthernetBackends"/>.
        /// </summary>
        public static void RegisterFactory()
        {
            EthernetBackends.Register(CreateFromSpec);
        }

        /// <summary>
        /// Makes a TCP link from a spec, or gives <c>null</c> when the spec is not a TCP link:
        /// <c>listen</c>, <c>listen:PORT</c>, <c>tcp:HOST</c>, <c>tcp:HOST:PORT</c> (the port is
        /// 3094 when left out). The old code's <c>tcp-listen</c> spelling is accepted too.
        /// </summary>
        /// <param name="spec">
        /// The spec.
        /// </param>
        /// <returns>
        /// The backend, or <c>null</c>.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown for a TCP spec with a bad host or port.
        /// </exception>
        public static IEthernetBackend? CreateFromSpec(string spec)
        {
            if (spec == null)
            {
                return null;
            }
            string s = spec.Trim();
            if (s.Equals(ListenPrefix, StringComparison.OrdinalIgnoreCase) || s.Equals("tcp-listen", StringComparison.OrdinalIgnoreCase))
            {
                return new TcpLinkBackend(true, string.Empty, EthernetLinkWire.DefaultPort);
            }
            string? listenPort = null;
            if (s.StartsWith(ListenPrefix + ":", StringComparison.OrdinalIgnoreCase))
            {
                listenPort = s.Substring(ListenPrefix.Length + 1);
            }
            else if (s.StartsWith("tcp-listen:", StringComparison.OrdinalIgnoreCase))
            {
                listenPort = s.Substring("tcp-listen:".Length);
            }
            if (listenPort != null)
            {
                return new TcpLinkBackend(true, string.Empty, ParsePort(listenPort, spec, true));
            }
            if (!s.StartsWith(ConnectPrefix + ":", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            string body = s.Substring(ConnectPrefix.Length + 1).Trim();
            int colon = body.LastIndexOf(':');
            string host = colon < 0 ? body : body.Substring(0, colon).Trim();
            if (host.Length == 0)
            {
                throw new ArgumentException("The TCP link '" + spec + "' names no host. Use tcp:HOST:PORT.", nameof(spec));
            }
            int port = colon < 0 ? EthernetLinkWire.DefaultPort : ParsePort(body.Substring(colon + 1), spec, false);
            return new TcpLinkBackend(false, host, port);
        }

        /// <inheritdoc/>
        public void Start()
        {
            if (_thread != null)
            {
                return;
            }
            _stop = false;
            if (_listen)
            {
                Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Any, _port));
                listener.Listen(1);
                _port = ((IPEndPoint)listener.LocalEndPoint!).Port;
                _listener = listener;
            }
            _thread = new Thread(_listen ? ListenLoop : ConnectLoop) { Name = "TcpLink", IsBackground = true };
            _thread.Start();
        }

        /// <inheritdoc/>
        public void Stop()
        {
            _stop = true;
            Socket? listener = _listener;
            _listener = null;
            Close(listener);
            Close(Interlocked.Exchange(ref _connection, null));
            _thread?.Join(2000);
            _thread = null;
        }

        /// <inheritdoc/>
        public void Send(byte[] frame, int offset, int length)
        {
            Socket? connection = Volatile.Read(ref _connection);
            if (connection == null || length <= 0 || length > EthernetLinkWire.TcpMaxFrameBytes)
            {
                return;
            }
            try
            {
                lock (_sendLock)
                {
                    EthernetLinkWire.SendFrame(connection, new ReadOnlySpan<byte>(frame, offset, length));
                }
                Interlocked.Increment(ref _framesSent);
            }
            catch (SocketException)
            {
                // The peer went away in the middle: drop the link, the thread makes a new one.
                Close(connection);
            }
            catch (ObjectDisposedException)
            {
                // Stop() closed it under us.
            }
        }

        /// <inheritdoc/>
        public void Poll()
        {
            _queue.Deliver(OnFrameReceived);
        }

        // ── Private methods ──

        private static int ParsePort(string text, string spec, bool zeroAllowed)
        {
            string t = text.Trim();
            if (t.Length == 0)
            {
                return EthernetLinkWire.DefaultPort;
            }
            if (!int.TryParse(t, out int port) || port < (zeroAllowed ? 0 : 1) || port > 65535)
            {
                throw new ArgumentException("The port in '" + spec + "' is not a number from 1 to 65535.", nameof(spec));
            }
            return port;
        }

        private static void Close(Socket? socket)
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

        private void ListenLoop()
        {
            while (!_stop)
            {
                Socket? listener = _listener;
                if (listener == null)
                {
                    return;
                }
                Socket peer;
                try
                {
                    peer = listener.Accept();
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
                Run(peer);
            }
        }

        private void ConnectLoop()
        {
            int wait = FirstRetryMs;
            while (!_stop)
            {
                Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Connect(_host, _port);
                }
                catch (SocketException)
                {
                    Close(socket);
                    if (_stop)
                    {
                        return;
                    }
                    Thread.Sleep(wait);
                    wait = Math.Min(wait * 2, LastRetryMs);
                    continue;
                }
                wait = FirstRetryMs;
                Run(socket);
                if (!_stop)
                {
                    Thread.Sleep(wait);
                }
            }
        }

        // Greets the peer, then reads frames into the queue until the link drops.
        private void Run(Socket socket)
        {
            byte[] buffer = new byte[EthernetLinkWire.TcpMaxFrameBytes];
            try
            {
                socket.NoDelay = true;
                if (!EthernetLinkWire.Handshake(socket))
                {
                    return;
                }
                Volatile.Write(ref _connection, socket);
                Interlocked.Increment(ref _linksUp);
                while (!_stop && EthernetLinkWire.ReceiveFrame(socket, buffer, out int length))
                {
                    Interlocked.Increment(ref _framesReceived);
                    _queue.Put(new ReadOnlySpan<byte>(buffer, 0, length));
                }
            }
            catch (SocketException)
            {
                // The link dropped.
            }
            catch (ObjectDisposedException)
            {
                // Stop() closed it.
            }
            finally
            {
                Interlocked.CompareExchange(ref _connection, null, socket);
                Close(socket);
            }
        }
    }
}
