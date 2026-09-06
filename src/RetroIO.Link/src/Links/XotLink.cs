using RetroIO.Link.Framing;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Lapb;
using RetroIO.Link.Models;

namespace RetroIO.Link
{
    /// <summary>
    /// XOT (X.25 over TCP) link supporting two modes:
    /// - RFC 1613 mode (default): XOT header + raw X.25 PLP packet
    /// - Cisco mode (UseLapbFraming=true): XOT header + full LAPB frame (0x7E flags, byte-stuffing, FCS)
    /// </summary>
    public class XotLink : ILink, ILapbCapable, IDisposable
    {
        private readonly ILogger _logger;
        private readonly XotLinkConfig _config;

        // LAPB components - only used when UseLapbFraming = true
        private readonly HdlcFramer? _hdlcFramer;

        /// <summary>
        /// The one XOT framing codec, shared with the X.25 client that talks to the C switch.
        /// </summary>
        private readonly XotFrameCodec _xotCodec;
        private readonly LapbStation? _lapbInstance;

        private TcpListener? _listener;
        private TcpClient? _tcpClient;
        private NetworkStream? _networkStream;
        private Thread? _receiveThread;
        private Thread? _acceptThread;
        private Timer? _keepAliveTimer;
        private volatile bool _stopping = false;

        private LinkStatus _status = LinkStatus.Stopped;
        private readonly LinkStatistics _statistics;
        private readonly object _lock = new object();
        private bool _disposed = false;

        // XOT header constants
        private const ushort XOT_VERSION = 0x0000; // Version 0
        private const int XOT_HEADER_SIZE = 4;
        private const int MAX_XOT_PACKET_SIZE = 4096;

        // Receive buffers for XOT framing
        private readonly byte[] _receiveBuffer = new byte[MAX_XOT_PACKET_SIZE];

        /// <summary>
        /// Create a new XOT link
        /// </summary>
        public XotLink(ILogger logger, XotLinkConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            if (string.IsNullOrWhiteSpace(config.Name))
                throw new ArgumentException("Link name is required", nameof(config));

            _xotCodec = new XotFrameCodec(config.Name, new XotStreamSink(this));
            _xotCodec.FrameReceived += OnXotFrame;
            _xotCodec.FramingFailed += OnXotFramingFailed;


            // Only create LAPB components if UseLapbFraming is enabled
            if (_config.UseLapbFraming)
            {
                _hdlcFramer = new HdlcFramer(logger, config.Name);
                _lapbInstance = new LapbStation(
                    logger,
                    config.Name,
                    config.WindowSize,
                    config.MaxRetries,
                    config.T1TimeoutMs,
                    config.T2TimeoutMs,
                    config.T3TimeoutMs,
                    config.IsPrimary);

                // Wire up LAPB events
                _hdlcFramer.FrameReceived += OnHdlcFrameReceived;
                _hdlcFramer.BytesToSend += OnHdlcBytesToSend;
                _lapbInstance.FrameToTransmit += OnLapbFrameToTransmit;
                _lapbInstance.FrameReceived += OnLapbFrameReceived;
                _lapbInstance.DataReceived += OnLapbDataReceived;
                _lapbInstance.StateChanged += OnLapbStateChanged;
            }

            _statistics = new LinkStatistics
            {
                LinkName = config.Name,
                LinkType = LinkType.Xot,
                CurrentStatus = LinkStatus.Stopped
            };

            string modeDesc = _config.UseLapbFraming ? "Cisco XOT with LAPB" : "RFC 1613 XOT";
            _logger.LogInformation("XotLink[{Name}]: Created ({Mode} {Address}:{Port}) - {ModeDesc}",
                config.Name, config.Mode, config.RemoteAddress, config.Port, modeDesc);
        }

        #region ILink Implementation

        public string Name => _config.Name;
        public LinkType Type => LinkType.Xot;
        public LinkStatus Status => _status;
        public string RemoteMachineId => _config.RemoteMachineId;
        public ProtocolModeConfig ProtocolMode => _config.ProtocolMode;

        public bool Start()
        {
            lock (_lock)
            {
                if (_status != LinkStatus.Stopped)
                {
                    _logger.LogWarning("XotLink[{Name}]: Cannot start - status is {Status}",
                        _config.Name, _status);
                    return false;
                }

                _stopping = false;
                _xotCodec.Reset();

                try
                {
                    SetStatus(LinkStatus.Starting, "Initializing XOT connection");

                    if (_config.Mode == TcpMode.Server)
                    {
                        StartServer();
                    }
                    else
                    {
                        StartClient();
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    SetStatus(LinkStatus.Error, $"Failed to start: {ex.Message}");
                    _logger.LogError(ex, "XotLink[{Name}]: Failed to start", _config.Name);
                    return false;
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (_status == LinkStatus.Stopped || _status == LinkStatus.Stopping)
                    return;

                _stopping = true;

                try
                {
                    SetStatus(LinkStatus.Stopping, "Closing XOT connection");

                    // Stop keep-alive timer
                    _keepAliveTimer?.Dispose();
                    _keepAliveTimer = null;

                    // Disconnect LAPB if in use and connected
                    if (_config.UseLapbFraming && _lapbInstance != null && _lapbInstance.State == LapbState.DataTransfer)
                    {
                        _lapbInstance.Disconnect();
                    }

                    // Close TCP
                    CloseConnection();

                    // Stop listener
                    _listener?.Stop();
                    _listener = null;

                    // Wait for threads
                    if (_receiveThread != null && _receiveThread.IsAlive)
                    {
                        _receiveThread.Join(1000);
                    }
                    _receiveThread = null;

                    if (_acceptThread != null && _acceptThread.IsAlive)
                    {
                        _acceptThread.Join(1000);
                    }
                    _acceptThread = null;

                    // Reset LAPB if in use
                    if (_config.UseLapbFraming)
                    {
                        _lapbInstance?.Reset();
                        _hdlcFramer?.Reset();
                    }

                    SetStatus(LinkStatus.Stopped, "XOT connection closed");

                    _logger.LogInformation("XotLink[{Name}]: Stopped", _config.Name);
                }
                catch (Exception ex)
                {
                    SetStatus(LinkStatus.Error, $"Error stopping: {ex.Message}");
                    _logger.LogError(ex, "XotLink[{Name}]: Error stopping", _config.Name);
                }
            }
        }

        public bool SendData(ReadOnlySpan<byte> payload)
        {
            if (_status != LinkStatus.Active)
            {
                _logger.LogWarning("XotLink[{Name}]: Cannot send - not active", _config.Name);
                return false;
            }

            if (_config.UseLapbFraming)
            {
                // Cisco mode: Send through LAPB
                if (_lapbInstance == null || _lapbInstance.State != LapbState.DataTransfer)
                {
                    _logger.LogWarning("XotLink[{Name}]: Cannot send - LAPB not connected (state: {State})",
                        _config.Name, _lapbInstance?.State ?? LapbState.Disconnected);
                    return false;
                }

                return _lapbInstance.SendData(payload);
            }
            else
            {
                // RFC 1613 mode: Send raw payload in XOT frame (copy — the caller's buffer may be reused)
                byte[] payloadCopy = payload.ToArray();
                SendXotFrame(payloadCopy, payloadCopy.Length);
                _statistics.FramesTransmitted++;
                return true;
            }
        }

        public LinkStatistics GetStatistics()
        {
            _statistics.CurrentStatus = _status;
            return _statistics;
        }

        public event LinkPayloadReceived? PayloadReceived;
        public event LinkStatusChanged? StatusChanged;

        #endregion

        #region ILapbCapable Implementation

        public LapbState LapbState => _lapbInstance?.State ?? LapbState.Disconnected;

        public (byte VS, byte VR, byte VA) GetSequenceNumbers()
        {
            if (_lapbInstance != null)
            {
                return _lapbInstance.GetSequenceNumbers();
            }
            return (0, 0, 0);
        }

        public void Connect()
        {
            if (!_config.UseLapbFraming)
            {
                _logger.LogWarning("XotLink[{Name}]: LAPB Connect ignored - UseLapbFraming is false", _config.Name);
                return;
            }

            if (_status != LinkStatus.Active)
            {
                _logger.LogWarning("XotLink[{Name}]: Cannot connect LAPB - link not active", _config.Name);
                return;
            }

            _lapbInstance?.Connect();
        }

        public void Disconnect()
        {
            if (!_config.UseLapbFraming || _lapbInstance == null)
            {
                return;
            }
            _lapbInstance.Disconnect();
        }

        public LapbStatistics GetLapbStatistics()
        {
            return _lapbInstance?.GetStatistics() ?? new LapbStatistics();
        }

        public event EventHandler<LapbStateChangedEventArgs>? LapbStateChanged;

        public void InjectFrame(byte[] frameData, int length)
        {
            if (_lapbInstance != null)
            {
                _lapbInstance.ReceiveFrame(frameData, length);
            }
        }

        public void SendFrame(byte[] frame, int length)
        {
            if (_lapbInstance != null)
            {
                _lapbInstance.SendFrame(frame, length);
            }
        }

        public event EventHandler<LapbFrameEventArgs>? FrameReceived;
        public event EventHandler<LapbFrameEventArgs>? FrameTransmitted;

        #endregion

        #region Server Mode

        private void StartServer()
        {
            var endpoint = new IPEndPoint(IPAddress.Any, _config.Port);
            _listener = new TcpListener(endpoint);
            _listener.Start();

            _logger.LogInformation("XotLink[{Name}]: Listening on port {Port}",
                _config.Name, _config.Port);

            // Accept connections in background
            _acceptThread = new Thread(ServerAcceptLoop)
            {
                Name = $"XotLink-{_config.Name}-Accept",
                IsBackground = true
            };
            _acceptThread.Start();

            SetStatus(LinkStatus.Active, "Listening for connection");
        }

        private void ServerAcceptLoop()
        {
            while (!_stopping && _listener != null)
            {
                try
                {
                    if (_listener.Pending())
                    {
                        var client = _listener.AcceptTcpClient();
                        EnableTcpKeepAlive(client);
                        HandleNewConnection(client);
                    }
                    else
                    {
                        Thread.Sleep(100);
                    }
                }
                catch (SocketException) when (_stopping)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!_stopping)
                    {
                        _logger.LogError(ex, "XotLink[{Name}]: Accept error", _config.Name);
                    }
                }
            }
        }

        private void HandleNewConnection(TcpClient client)
        {
            lock (_lock)
            {
                // Close existing connection if any
                CloseConnection();

                _tcpClient = client;
                _tcpClient.NoDelay = true;
                _networkStream = _tcpClient.GetStream();

                var remoteEndPoint = (IPEndPoint?)_tcpClient.Client.RemoteEndPoint;
                _statistics.StartTime = DateTime.UtcNow;

                _logger.LogInformation("XotLink[{Name}]: Client connected from {Address}:{Port}",
                    _config.Name, remoteEndPoint?.Address, remoteEndPoint?.Port);

                // Reset XOT payload buffer
                _xotCodec.Reset();

                // Reset LAPB for new connection if in use
                if (_config.UseLapbFraming)
                {
                    _lapbInstance?.Reset();
                    _hdlcFramer?.Reset();
                }

                // Start keep-alive if configured
                StartKeepAlive();

                // Start receive thread
                _receiveThread = new Thread(ReceiveLoop)
                {
                    Name = $"XotLink-{_config.Name}-Receive",
                    IsBackground = true
                };
                _receiveThread.Start();
            }
        }

        #endregion

        #region Client Mode

        private void StartClient()
        {
            _statistics.StartTime = DateTime.UtcNow;

            // Connect in background
            _receiveThread = new Thread(ClientConnectAndReceive)
            {
                Name = $"XotLink-{_config.Name}-Client",
                IsBackground = true
            };
            _receiveThread.Start();
        }

        private void ClientConnectAndReceive()
        {
            while (!_stopping)
            {
                try
                {
                    SetStatus(LinkStatus.Starting, "Connecting...");

                    _tcpClient = new TcpClient();
                    _tcpClient.NoDelay = true;
                    EnableTcpKeepAlive(_tcpClient);

                    // Connect with timeout
                    var connectTask = _tcpClient.ConnectAsync(_config.RemoteAddress, _config.Port);
                    bool connected = connectTask.Wait(_config.ConnectTimeoutMs);

                    if (!connected || !_tcpClient.Connected)
                    {
                        throw new SocketException((int)SocketError.TimedOut);
                    }

                    _networkStream = _tcpClient.GetStream();

                    _logger.LogInformation("XotLink[{Name}]: Connected to {Address}:{Port}",
                        _config.Name, _config.RemoteAddress, _config.Port);

                    // Reset XOT payload buffer
                    _xotCodec.Reset();

                    SetStatus(LinkStatus.Active, "Connected");

                    // Start keep-alive if configured
                    StartKeepAlive();

                    // Enter receive loop
                    ReceiveLoop();

                    // If we get here, connection was closed
                    if (!_stopping)
                    {
                        HandleDisconnection();
                    }
                }
                catch (Exception ex) when (!_stopping)
                {
                    _logger.LogWarning("XotLink[{Name}]: Connection failed: {Error}",
                        _config.Name, ex.Message);

                    SetStatus(LinkStatus.Error, $"Connection failed: {ex.Message}");
                    _statistics.Errors++;

                    // Wait before reconnect
                    if (_config.ReconnectDelayMs > 0 && !_stopping)
                    {
                        _logger.LogInformation("XotLink[{Name}]: Reconnecting in {Delay}ms...",
                            _config.Name, _config.ReconnectDelayMs);
                        Thread.Sleep(_config.ReconnectDelayMs);
                    }
                    else if (_config.ReconnectDelayMs <= 0)
                    {
                        break;
                    }
                }
            }
        }

        private void HandleDisconnection()
        {
            lock (_lock)
            {
                CloseConnection();

                if (_config.UseLapbFraming)
                {
                    _lapbInstance?.Reset();
                    _hdlcFramer?.Reset();
                }

                if (_config.ReconnectDelayMs > 0)
                {
                    SetStatus(LinkStatus.Error, "Disconnected - will reconnect");
                }
                else
                {
                    SetStatus(LinkStatus.Error, "Disconnected");
                }
            }
        }

        #endregion

        #region Receive Loop

        private void ReceiveLoop()
        {
            while (!_stopping && _networkStream != null && _tcpClient != null && _tcpClient.Connected)
            {
                try
                {
                    if (_networkStream.DataAvailable)
                    {
                        int bytesRead = _networkStream.Read(_receiveBuffer, 0, _receiveBuffer.Length);

                        if (bytesRead > 0)
                        {
                            _statistics.BytesReceived += bytesRead;
                            ProcessReceivedXotData(_receiveBuffer, bytesRead);
                        }
                        else
                        {
                            // Connection closed
                            break;
                        }
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                    if (!_stopping)
                    {
                        _logger.LogError(ex, "XotLink[{Name}]: Receive error", _config.Name);
                        _statistics.Errors++;
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// Hands received TCP bytes to the XOT codec.
        /// </summary>
        /// <remarks>
        /// The framing used to be decoded here, in the third copy of the same parser. That copy
        /// only logged a wrong version and then carried on parsing, taking the next two octets as a
        /// length even though it had just been told the header was not a header.
        /// </remarks>
        /// <param name="data">
        /// The bytes read from the socket.
        /// </param>
        /// <param name="length">
        /// How many of them are valid.
        /// </param>
        private void ProcessReceivedXotData(byte[] data, int length)
        {
            _xotCodec.ProcessBytes(new ReadOnlySpan<byte>(data, 0, length));
        }

        /// <summary>
        /// One complete XOT payload from the codec.
        /// </summary>
        /// <param name="linkId">
        /// The link the payload arrived on.
        /// </param>
        /// <param name="frame">
        /// The payload: an LAPB frame in Cisco mode, a raw X.25 packet otherwise.
        /// </param>
        private void OnXotFrame(string linkId, ReadOnlySpan<byte> frame)
        {
            byte[] payload = frame.ToArray();

            if (_config.UseLapbFraming)
            {
                // Cisco mode: the payload is an LAPB frame with flags, byte-stuffing and an FCS.
                _hdlcFramer?.ProcessBytes(payload, payload.Length);
                return;
            }

            // RFC 1613 mode: the payload is a raw X.25 packet.
            ProcessRawX25Packet(payload, payload.Length);
        }

        /// <summary>
        /// The byte stream cannot be framed any further, so the connection goes.
        /// </summary>
        /// <remarks>
        /// RFC 1613 has no flag or marker to find the next frame boundary with. Once a header is
        /// wrong, every byte after it is at an unknown offset and would be read as another header.
        /// </remarks>
        /// <param name="linkId">
        /// The link that failed.
        /// </param>
        /// <param name="reason">
        /// What was wrong.
        /// </param>
        private void OnXotFramingFailed(string linkId, string reason)
        {
            _logger.LogError("XotLink[{Name}]: XOT framing failed ({Reason}) - closing the connection", _config.Name, reason);
            _statistics.Errors++;
            HandleDisconnection();
        }

        /// <summary>
        /// Writes XOT-framed bytes to the socket. The codec calls this with the header already on.
        /// </summary>
        private sealed class XotStreamSink : IFrameTransport
        {
            private readonly XotLink _owner;

            /// <summary>
            /// Creates the sink.
            /// </summary>
            /// <param name="owner">
            /// The link that owns the socket.
            /// </param>
            public XotStreamSink(XotLink owner)
            {
                _owner = owner;
            }

            /// <inheritdoc/>
            public void Send(ReadOnlySpan<byte> bytes)
            {
                _owner.WriteToSocket(bytes);
            }
        }

        /// <summary>
        /// Process a raw X.25 packet (RFC 1613 mode)
        /// </summary>
        private void ProcessRawX25Packet(byte[] data, int length)
        {
            // Deliver the payload opaque; X.25 vs SINTRAN classification happens above the link.
            _statistics.FramesReceived++;
            PayloadReceived?.Invoke(this, data, length);
        }

        #endregion

        #region HDLC Framer Events (Cisco mode only)

        private void OnHdlcFrameReceived(object? sender, FrameEventArgs e)
        {
            if (!e.FcsValid)
            {
                _logger.LogWarning("XotLink[{Name}]: Frame FCS error discarded", _config.Name);
                _statistics.Errors++;
                return;
            }

            _statistics.FramesReceived++;

            // Pass to LAPB for processing
            _lapbInstance?.ReceiveFrame(e.Data, e.Length);
        }

        private void OnHdlcBytesToSend(object? sender, byte[] stuffedData)
        {
            // Wrap the stuffed LAPB frame in XOT header and send
            SendXotFrame(stuffedData, stuffedData.Length);
        }

        #endregion

        #region LAPB Events (Cisco mode only)

        private void OnLapbFrameToTransmit(object? sender, LapbFrameEventArgs e)
        {
            _statistics.FramesTransmitted++;

            // Fire FrameTransmitted event for monitoring/testing
            FrameTransmitted?.Invoke(this, e);

            // Send through HDLC framer (adds FCS and byte-stuffing)
            _hdlcFramer?.SendFrame(e.FrameData, e.Length);
        }

        private void OnLapbFrameReceived(object? sender, LapbFrameEventArgs e)
        {
            // Forward to external subscribers for monitoring/testing
            FrameReceived?.Invoke(this, e);
        }

        private void OnLapbDataReceived(object? sender, byte[] data)
        {
            // Deliver the payload opaque; X.25 vs SINTRAN classification happens above the link.
            PayloadReceived?.Invoke(this, data, data.Length);
        }

        private void OnLapbStateChanged(object? sender, LapbStateChangedEventArgs e)
        {
            LapbStateChanged?.Invoke(this, e);

            _logger.LogInformation("XotLink[{Name}]: LAPB state {OldState} -> {NewState}",
                _config.Name, e.OldState, e.NewState);
        }

        #endregion

        #region XOT Framing

        /// <summary>
        /// Send data wrapped in XOT header
        /// </summary>
        private void SendXotFrame(byte[] data, int length)
        {
            if (_networkStream == null || _tcpClient == null || !_tcpClient.Connected)
            {
                _logger.LogWarning("XotLink[{Name}]: Cannot send - not connected", _config.Name);
                return;
            }

            try
            {
                // The codec puts the RFC 1613 header on and hands the whole thing to XotStreamSink,
                // which is what actually writes to the socket. One encoder, shared with the client
                // that talks to the C switch.
                _xotCodec.SendFrame(new ReadOnlySpan<byte>(data, 0, length));

                _logger.LogDebug("XotLink[{Name}]: Sent XOT frame ({Length} bytes payload)",
                    _config.Name, length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "XotLink[{Name}]: Error sending XOT frame", _config.Name);
                _statistics.Errors++;
            }
        }

        #endregion

        #region Keep-Alive

        private void StartKeepAlive()
        {
            if (_config.KeepAliveIntervalSeconds > 0)
            {
                int intervalMs = _config.KeepAliveIntervalSeconds * 1000;
                _keepAliveTimer = new Timer(SendKeepAlive, null, intervalMs, intervalMs);
            }
        }

        private void SendKeepAlive(object? state)
        {
            if (_stopping || _networkStream == null || _tcpClient == null || !_tcpClient.Connected)
            {
                return;
            }

            // Nothing is sent. There is no XOT keep-alive: RFC 1613 gives the length field only
            // legal X.25 packet lengths, and says an illegal one closes the connection. A
            // zero-length frame every minute, which is what this used to send, is an illegal length,
            // so a conforming peer drops the link exactly when it has been quiet - which is when a
            // keep-alive was supposed to help. TCP's own keep-alive is turned on for the socket
            // instead; it works below XOT and needs no protocol of its own.
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Writes already-framed bytes to the socket. Only <see cref="XotStreamSink"/> calls this.
        /// </summary>
        /// <param name="bytes">
        /// The framed bytes.
        /// </param>
        private void WriteToSocket(ReadOnlySpan<byte> bytes)
        {
            NetworkStream? stream = _networkStream;
            if (stream == null)
            {
                return;
            }

            stream.Write(bytes);
            _statistics.BytesTransmitted += bytes.Length;
        }

        /// <summary>
        /// Turns on TCP's own keep-alive for a connection.
        /// </summary>
        /// <remarks>
        /// This replaces the zero-length XOT frame this link used to send every minute. There is no
        /// XOT keep-alive: RFC 1613 gives the length field only legal X.25 packet lengths and says
        /// an illegal one closes the connection, so that frame made a conforming peer drop the link
        /// exactly when it had been quiet. TCP's keep-alive sits below XOT, needs no protocol of its
        /// own, and is what the operating system is for.
        /// </remarks>
        /// <param name="client">
        /// The connection to set it on.
        /// </param>
        private void EnableTcpKeepAlive(TcpClient client)
        {
            try
            {
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            }
            catch (Exception ex)
            {
                // Not fatal: the link works without it, it just takes longer to notice a peer that
                // vanished without closing.
                _logger.LogDebug("XotLink[{Name}]: could not enable TCP keep-alive: {Error}", _config.Name, ex.Message);
            }
        }

        private void CloseConnection()
        {
            try
            {
                _keepAliveTimer?.Dispose();
                _keepAliveTimer = null;

                _networkStream?.Close();
                _tcpClient?.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "XotLink[{Name}]: Error closing connection", _config.Name);
            }
            finally
            {
                _networkStream = null;
                _tcpClient = null;
            }
        }

        private void SetStatus(LinkStatus newStatus, string reason)
        {
            var oldStatus = _status;
            _status = newStatus;
            _statistics.CurrentStatus = newStatus;

            StatusChanged?.Invoke(this, oldStatus, newStatus, reason);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            Stop();

            _lapbInstance?.Dispose();

            _logger.LogInformation("XotLink[{Name}]: Disposed", _config.Name);
        }

        #endregion
    }
}
