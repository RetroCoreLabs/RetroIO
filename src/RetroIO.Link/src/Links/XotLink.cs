using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Lapb;
using RetroIO.Link.Models;
using RetroIO.Link.Lapb.DeleteMe;

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
        private readonly LapbInstance? _lapbInstance;

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
        private readonly byte[] _xotPayloadBuffer = new byte[MAX_XOT_PACKET_SIZE];
        private int _xotPayloadPos = 0;
        private int _expectedXotLength = -1;

        /// <summary>
        /// Create a new XOT link
        /// </summary>
        public XotLink(ILogger logger, XotLinkConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            if (string.IsNullOrWhiteSpace(config.Name))
                throw new ArgumentException("Link name is required", nameof(config));


            // Only create LAPB components if UseLapbFraming is enabled
            if (_config.UseLapbFraming)
            {
                _hdlcFramer = new HdlcFramer(logger, config.Name);
                _lapbInstance = new LapbInstance(
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
                _xotPayloadPos = 0;
                _expectedXotLength = -1;

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
                _xotPayloadPos = 0;
                _expectedXotLength = -1;

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
                    _xotPayloadPos = 0;
                    _expectedXotLength = -1;

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
        /// Process received TCP data, extracting XOT frames
        /// </summary>
        private void ProcessReceivedXotData(byte[] data, int length)
        {
            int offset = 0;

            while (offset < length)
            {
                // Add data to XOT payload buffer
                int bytesToCopy = Math.Min(length - offset, _xotPayloadBuffer.Length - _xotPayloadPos);
                Array.Copy(data, offset, _xotPayloadBuffer, _xotPayloadPos, bytesToCopy);
                _xotPayloadPos += bytesToCopy;
                offset += bytesToCopy;

                // Try to extract complete XOT frames
                while (TryExtractXotFrame())
                {
                    // TryExtractXotFrame processes the frame
                }
            }
        }

        /// <summary>
        /// Try to extract a complete XOT frame from the buffer
        /// </summary>
        private bool TryExtractXotFrame()
        {
            // Need at least XOT header to determine length
            if (_xotPayloadPos < XOT_HEADER_SIZE)
            {
                return false;
            }

            // Parse header if not yet done
            if (_expectedXotLength < 0)
            {
                // Version (2 bytes, network byte order)
                ushort version = (ushort)((_xotPayloadBuffer[0] << 8) | _xotPayloadBuffer[1]);

                // Length (2 bytes, network byte order)
                _expectedXotLength = (_xotPayloadBuffer[2] << 8) | _xotPayloadBuffer[3];

                if (version != XOT_VERSION)
                {
                    _logger.LogWarning("XotLink[{Name}]: Unsupported XOT version {Version}",
                        _config.Name, version);
                }

                if (_expectedXotLength > MAX_XOT_PACKET_SIZE - XOT_HEADER_SIZE)
                {
                    _logger.LogWarning("XotLink[{Name}]: XOT payload too large ({Length} bytes)",
                        _config.Name, _expectedXotLength);
                    _statistics.Errors++;

                    // Reset and discard
                    _xotPayloadPos = 0;
                    _expectedXotLength = -1;
                    return false;
                }
            }

            // Check if we have complete XOT frame
            int totalLength = XOT_HEADER_SIZE + _expectedXotLength;
            if (_xotPayloadPos < totalLength)
            {
                return false;
            }

            // Handle keep-alive (length = 0)
            if (_expectedXotLength == 0)
            {
                _logger.LogDebug("XotLink[{Name}]: Keep-alive received", _config.Name);
            }
            else
            {
                // Extract payload
                byte[] payload = new byte[_expectedXotLength];
                Array.Copy(_xotPayloadBuffer, XOT_HEADER_SIZE, payload, 0, _expectedXotLength);

                if (_config.UseLapbFraming)
                {
                    // Cisco mode: Payload is LAPB frame with flags, stuffing, FCS
                    // Pass to HdlcFramer to process
                    _hdlcFramer?.ProcessBytes(payload, _expectedXotLength);
                }
                else
                {
                    // RFC 1613 mode: Payload is raw X.25 packet
                    ProcessRawX25Packet(payload, _expectedXotLength);
                }
            }

            // Shift remaining data
            int remaining = _xotPayloadPos - totalLength;
            if (remaining > 0)
            {
                Array.Copy(_xotPayloadBuffer, totalLength, _xotPayloadBuffer, 0, remaining);
            }
            _xotPayloadPos = remaining;
            _expectedXotLength = -1;

            return remaining >= XOT_HEADER_SIZE;
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
                // Build XOT frame: Version (2 bytes) + Length (2 bytes) + payload
                byte[] xotFrame = new byte[XOT_HEADER_SIZE + length];

                // Version (network byte order)
                xotFrame[0] = (byte)((XOT_VERSION >> 8) & 0xFF);
                xotFrame[1] = (byte)(XOT_VERSION & 0xFF);

                // Length (network byte order)
                xotFrame[2] = (byte)((length >> 8) & 0xFF);
                xotFrame[3] = (byte)(length & 0xFF);

                // Payload
                Array.Copy(data, 0, xotFrame, XOT_HEADER_SIZE, length);

                _networkStream.Write(xotFrame, 0, xotFrame.Length);
                _statistics.BytesTransmitted += xotFrame.Length;

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

            try
            {
                // XOT keep-alive: empty packet (length = 0)
                byte[] keepAlive = new byte[XOT_HEADER_SIZE];
                keepAlive[0] = (byte)((XOT_VERSION >> 8) & 0xFF);
                keepAlive[1] = (byte)(XOT_VERSION & 0xFF);
                keepAlive[2] = 0;
                keepAlive[3] = 0;

                _networkStream.Write(keepAlive, 0, keepAlive.Length);

                _logger.LogDebug("XotLink[{Name}]: Keep-alive sent", _config.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("XotLink[{Name}]: Keep-alive failed: {Error}",
                    _config.Name, ex.Message);
            }
        }

        #endregion

        #region Private Methods

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
