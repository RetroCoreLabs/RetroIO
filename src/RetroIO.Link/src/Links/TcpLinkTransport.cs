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
using Microsoft.Extensions.Logging;

namespace RetroIO.Link
{
    /// <summary>
    /// TCP implementation of <see cref="ILinkTransport"/> (single connection, client or server).
    /// </summary>
    /// <remarks>
    /// Moves raw bytes over a TCP connection so any codec-based link — SLIP or HDLC — can run over TCP.
    /// In server mode it listens and accepts one connection; in client mode it connects to a remote host.
    /// A reader thread raises <see cref="DataReceived"/>; framing, FCS, and LAPB live above this transport
    /// in <see cref="CodecLink"/>. Nagle is disabled for latency.
    /// </remarks>
    public sealed class TcpLinkTransport : ILinkTransport
    {
        private readonly ILogger _logger;
        private readonly string _name;
        private readonly bool _isServer;
        private readonly string _host;
        private readonly int _reconnectDelayMs;
        private readonly byte[] _readBuffer = new byte[4096];
        private readonly object _lock = new object();

        private int _port;
        private TcpListener? _listener;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private Thread? _acceptThread;
        private Thread? _connectThread;
        private Thread? _readerThread;
        private volatile bool _running;
        private bool _disposed;

        /// <summary>
        /// Raised on the reader thread when raw bytes arrive from the TCP connection.
        /// </summary>
        /// <remarks>
        /// The bytes are in a buffer this transport reuses; they are only valid for the duration of
        /// the callback.
        /// </remarks>
        public event LinkTransportDataReceived? DataReceived;

        /// <summary>
        /// Raised each time a TCP connection is set up (connected in client mode or accepted in server
        /// mode).
        /// </summary>
        /// <remarks>
        /// It is not raised when a server only starts listening.
        /// </remarks>
        public event LinkTransportOpened? Opened;

        /// <summary>
        /// Raised when the transport closes or the connection drops.
        /// </summary>
        /// <remarks>
        /// <see cref="Close"/> raises it with the reason "closed". The reader thread raises it with
        /// the cause when the connection drops while the transport is still running.
        /// </remarks>
        public event LinkTransportClosed? Closed;

        /// <summary>
        /// Creates a TCP transport (no connection is made until <see cref="Open"/> is called).
        /// </summary>
        /// <param name="logger">
        /// Logger for diagnostics.
        /// </param>
        /// <param name="name">
        /// Transport name (usually the owning link's name).
        /// </param>
        /// <param name="isServer">
        /// True to listen and accept a connection; false to connect to <paramref name="host"/>.
        /// </param>
        /// <param name="host">
        /// Remote host to connect to in client mode, or the bind address in server mode.
        /// </param>
        /// <param name="port">
        /// TCP port. In server mode, 0 lets the OS choose a free port (readable via <see cref="Port"/>).
        /// </param>
        /// <param name="reconnectDelayMs">
        /// Milliseconds to wait before reconnecting after the connection drops. 0 (the default) disables automatic reconnect.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="logger"/>, <paramref name="name"/>, or <paramref name="host"/> is null.
        /// </exception>
        public TcpLinkTransport(ILogger logger, string name, bool isServer, string host, int port, int reconnectDelayMs = 0)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _isServer = isServer;
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _port = port;
            _reconnectDelayMs = reconnectDelayMs;
        }

        /// <summary>
        /// Gets the name of this transport.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Gets the link type this transport represents, which is always TcpLapb.
        /// </summary>
        public LinkType LinkType => LinkType.TcpLapb;

        /// <summary>
        /// Gets a value indicating whether a TCP connection is set up and connected.
        /// </summary>
        /// <remarks>
        /// A server that is only listening is not open.
        /// </remarks>
        public bool IsOpen => _stream != null && _client?.Connected == true;

        /// <summary>
        /// The TCP port in use (the OS-assigned port after a server opens with port 0).
        /// </summary>
        public int Port => _port;

        /// <summary>
        /// Opens the transport: listens in server mode, or connects in client mode.
        /// </summary>
        /// <remarks>
        /// What happens:
        ///  - Server mode starts a listener on the host address and port and accepts one connection
        ///    at a time on a background thread.
        ///  - Client mode with a reconnect delay dials, and re-dials after a drop, on a background thread.
        ///  - Client mode without a reconnect delay connects once, on the calling thread.
        /// </remarks>
        /// <returns>
        /// <c>true</c> if the listener or dial thread was started or the connection was made;
        /// <c>false</c> if it failed at once (a warning is logged).
        /// </returns>
        public bool Open()
        {
            try
            {
                _running = true;
                if (_isServer)
                {
                    _listener = new TcpListener(IPAddress.Parse(_host), _port);
                    _listener.Start();
                    _port = ((IPEndPoint)_listener.LocalEndpoint).Port;

                    _acceptThread = new Thread(AcceptLoop)
                    {
                        Name = $"TcpTransport-Accept-{_name}",
                        IsBackground = true
                    };
                    _acceptThread.Start();
                    _logger.LogInformation("TcpLinkTransport[{Name}]: listening on {Host}:{Port}", _name, _host, _port);
                    return true;
                }

                // Client mode: with a reconnect delay, dial (and re-dial) on a background thread so a trunk
                // keeps retrying until it links and re-links after a drop. Otherwise dial once synchronously.
                if (_reconnectDelayMs > 0)
                {
                    _connectThread = new Thread(ConnectLoop)
                    {
                        Name = $"TcpTransport-Connect-{_name}",
                        IsBackground = true
                    };
                    _connectThread.Start();
                    return true;
                }

                _logger.LogInformation("TcpLinkTransport[{Name}]: connecting to {Host}:{Port}", _name, _host, _port);
                TcpClient client = new TcpClient();
                client.Connect(_host, _port);
                _logger.LogInformation("TcpLinkTransport[{Name}]: connected to {Host}:{Port}", _name, _host, _port);
                SetupConnection(client);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("TcpLinkTransport[{Name}]: open failed ({Host}:{Port}): {Error}", _name, _host, _port, ex.Message);
                _running = false;
                return false;
            }
        }

        /// <summary>
        /// Closes the connection and the listener, and raises <see cref="Closed"/> with the reason
        /// "closed".
        /// </summary>
        /// <remarks>
        /// The event is raised on every call, also when nothing was open. An error while closing is
        /// logged at debug level only.
        /// </remarks>
        public void Close()
        {
            _running = false;
            lock (_lock)
            {
                try
                {
                    _stream?.Close();
                    _client?.Close();
                    _listener?.Stop();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "TcpLinkTransport[{Name}]: error while closing", _name);
                }
                finally
                {
                    _stream = null;
                    _client = null;
                    _listener = null;
                }
            }

            Closed?.Invoke(_name, "closed");
        }

        /// <summary>
        /// Writes raw bytes to the TCP connection.
        /// </summary>
        /// <remarks>
        /// When there is no connection the bytes are dropped without a message. A write error is
        /// logged and not thrown.
        /// </remarks>
        /// <param name="data">
        /// The bytes to write. Only referenced for the duration of the call.
        /// </param>
        public void Write(ReadOnlySpan<byte> data)
        {
            NetworkStream? stream = _stream;
            if (stream == null)
            {
                return;
            }

            lock (_lock)
            {
                try
                {
                    stream.Write(data);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "TcpLinkTransport[{Name}]: write failed", _name);
                }
            }
        }

        /// <summary>
        /// Closes the transport and waits up to 2 seconds each for the accept, dial and reader threads.
        /// </summary>
        /// <remarks>
        /// Calling it again does nothing.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Close();
            _acceptThread?.Join(2000);
            _connectThread?.Join(2000);
            _readerThread?.Join(2000);
        }

        private void AcceptLoop()
        {
            // Accept one connection at a time; when it closes, accept the next (so a peer can reconnect).
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener!.AcceptTcpClient();
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        _logger.LogWarning("TcpLinkTransport[{Name}]: accept failed: {Error}", _name, ex.Message);
                    }
                    break;
                }

                _logger.LogInformation("TcpLinkTransport[{Name}]: accepted connection from {Remote}",
                    _name, client.Client.RemoteEndPoint);
                SetupConnection(client);
                _readerThread?.Join();
            }
        }

        private void ConnectLoop()
        {
            int attempt = 0;
            while (_running)
            {
                attempt++;
                try
                {
                    _logger.LogInformation("TcpLinkTransport[{Name}]: dial attempt {Attempt} to {Host}:{Port}",
                        _name, attempt, _host, _port);
                    TcpClient client = new TcpClient();
                    client.Connect(_host, _port);
                    _logger.LogInformation("TcpLinkTransport[{Name}]: connected to {Host}:{Port} (attempt {Attempt})",
                        _name, _host, _port, attempt);
                    attempt = 0;
                    SetupConnection(client);
                    _readerThread?.Join(); // block until this connection drops, then re-dial below
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        _logger.LogWarning("TcpLinkTransport[{Name}]: dial attempt {Attempt} to {Host}:{Port} failed: {Error}; retrying in {Delay}ms",
                            _name, attempt, _host, _port, ex.Message, _reconnectDelayMs);
                    }
                }

                if (_running)
                {
                    Thread.Sleep(_reconnectDelayMs);
                }
            }
        }

        private void SetupConnection(TcpClient client)
        {
            lock (_lock)
            {
                _client = client;
                _client.NoDelay = true;
                _stream = _client.GetStream();
            }

            _logger.LogInformation("TcpLinkTransport[{Name}]: connection up {Local} <-> {Remote}",
                _name, client.Client.LocalEndPoint, client.Client.RemoteEndPoint);

            _readerThread = new Thread(ReaderLoop)
            {
                Name = $"TcpTransport-Reader-{_name}",
                IsBackground = true
            };
            _readerThread.Start();

            Opened?.Invoke(_name);
        }

        private void ReaderLoop()
        {
            string reason = "closed";
            while (_running)
            {
                NetworkStream? stream = _stream;
                if (stream == null)
                {
                    reason = "stream gone";
                    break;
                }

                try
                {
                    int read = stream.Read(_readBuffer, 0, _readBuffer.Length);
                    if (read <= 0)
                    {
                        reason = "remote closed the connection (read 0)";
                        break;
                    }

                    DataReceived?.Invoke(_name, _readBuffer.AsSpan(0, read));
                }
                catch (Exception ex)
                {
                    reason = "read error: " + ex.Message;
                    if (_running)
                    {
                        _logger.LogWarning("TcpLinkTransport[{Name}]: read error: {Error}", _name, ex.Message);
                    }
                    break;
                }
            }

            if (_running)
            {
                _logger.LogInformation("TcpLinkTransport[{Name}]: connection down ({Reason})", _name, reason);
                Closed?.Invoke(_name, reason);
            }
        }
    }
}
