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
    /// TCP-LAPB link with HDLC byte-stuffing over TCP.
    /// Implements both ILink and ILapbCapable interfaces.
    /// Each TcpLapbLink has its own independent LAPB state machine.
    /// </summary>
    public class TcpLapbLink : ILink, ILapbCapable, IDisposable
    {
        private readonly ILogger _logger;
        private readonly TcpLapbLinkConfig _config;
        private readonly HdlcFramer _hdlcFramer;
        private readonly LapbInstance _lapbInstance;

        private TcpListener? _listener;
        private TcpClient? _tcpClient;
        private NetworkStream? _networkStream;
        private Thread? _receiveThread;
        private Timer? _reconnectTimer;
        private volatile bool _stopping = false;

        private LinkStatus _status = LinkStatus.Stopped;
        private readonly LinkStatistics _statistics;
        private readonly object _lock = new object();
        private bool _disposed = false;

        // Receive buffer
        private readonly byte[] _receiveBuffer = new byte[4096];

        /// <summary>
        /// Create a new TCP-LAPB link
        /// </summary>
        public TcpLapbLink(ILogger logger, TcpLapbLinkConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            if (string.IsNullOrWhiteSpace(config.Name))
                throw new ArgumentException("Link name is required", nameof(config));

            // Create components
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

            // Wire up events
            _hdlcFramer.FrameReceived += OnHdlcFrameReceived;
            _hdlcFramer.BytesToSend += OnHdlcBytesToSend;
            _lapbInstance.FrameToTransmit += OnLapbFrameToTransmit;
            _lapbInstance.FrameReceived += OnLapbFrameReceived;
            _lapbInstance.DataReceived += OnLapbDataReceived;
            _lapbInstance.StateChanged += OnLapbStateChanged;

            _statistics = new LinkStatistics
            {
                LinkName = config.Name,
                LinkType = LinkType.TcpLapb,
                CurrentStatus = LinkStatus.Stopped
            };

            _logger.LogInformation("TcpLapbLink[{Name}]: Created ({Mode} {Address}:{Port})",
                config.Name, config.Mode, config.RemoteAddress, config.Port);
        }

        #region ILink Implementation

        public string Name => _config.Name;
        public LinkType Type => LinkType.TcpLapb;
        public LinkStatus Status => _status;
        public string RemoteMachineId => _config.RemoteMachineId;
        public ProtocolModeConfig ProtocolMode => _config.ProtocolMode;

        public bool Start()
        {
            lock (_lock)
            {
                if (_status != LinkStatus.Stopped)
                {
                    _logger.LogWarning("TcpLapbLink[{Name}]: Cannot start - status is {Status}",
                        _config.Name, _status);
                    return false;
                }

                _stopping = false;

                try
                {
                    SetStatus(LinkStatus.Starting, "Initializing TCP connection");

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
                    _logger.LogError(ex, "TcpLapbLink[{Name}]: Failed to start", _config.Name);
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
                    SetStatus(LinkStatus.Stopping, "Closing TCP connection");

                    // Stop reconnect timer
                    _reconnectTimer?.Dispose();
                    _reconnectTimer = null;

                    // Disconnect LAPB if connected
                    if (_lapbInstance.State == LapbState.DataTransfer)
                    {
                        _lapbInstance.Disconnect();
                    }

                    // Close TCP
                    CloseConnection();

                    // Stop listener
                    _listener?.Stop();
                    _listener = null;

                    // Wait for receive thread
                    if (_receiveThread != null && _receiveThread.IsAlive)
                    {
                        _receiveThread.Join(1000);
                    }
                    _receiveThread = null;

                    // Reset LAPB
                    _lapbInstance.Reset();
                    _hdlcFramer.Reset();

                    SetStatus(LinkStatus.Stopped, "TCP connection closed");

                    _logger.LogInformation("TcpLapbLink[{Name}]: Stopped", _config.Name);
                }
                catch (Exception ex)
                {
                    SetStatus(LinkStatus.Error, $"Error stopping: {ex.Message}");
                    _logger.LogError(ex, "TcpLapbLink[{Name}]: Error stopping", _config.Name);
                }
            }
        }

        public bool SendData(ReadOnlySpan<byte> payload)
        {
            if (_status != LinkStatus.Active)
            {
                _logger.LogWarning("TcpLapbLink[{Name}]: Cannot send - not active", _config.Name);
                return false;
            }

            if (_lapbInstance.State != LapbState.DataTransfer)
            {
                _logger.LogWarning("TcpLapbLink[{Name}]: Cannot send - LAPB not connected", _config.Name);
                return false;
            }

            // Send via LAPB I-frame; the payload is opaque (X.25 or SINTRAN decided above the link)
            return _lapbInstance.SendData(payload);
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

        public LapbState LapbState => _lapbInstance.State;

        public (byte VS, byte VR, byte VA) GetSequenceNumbers()
        {
            return _lapbInstance.GetSequenceNumbers();
        }

        public void Connect()
        {
            if (_status != LinkStatus.Active)
            {
                _logger.LogWarning("TcpLapbLink[{Name}]: Cannot connect LAPB - link not active", _config.Name);
                return;
            }

            _lapbInstance.Connect();
        }

        public void Disconnect()
        {
            _lapbInstance.Disconnect();
        }

        public LapbStatistics GetLapbStatistics()
        {
            return _lapbInstance.GetStatistics();
        }

        public event EventHandler<LapbStateChangedEventArgs>? LapbStateChanged;

        public void InjectFrame(byte[] frameData, int length)
        {
            _lapbInstance.ReceiveFrame(frameData, length);
        }

        public void SendFrame(byte[] frame, int length)
        {
            _lapbInstance.SendFrame(frame, length);
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

            _logger.LogInformation("TcpLapbLink[{Name}]: Listening on port {Port}",
                _config.Name, _config.Port);

            // Wait for connection in background
            _receiveThread = new Thread(ServerAcceptLoop)
            {
                Name = $"TcpLapbLink-{_config.Name}-Accept",
                IsBackground = true
            };
            _receiveThread.Start();

            SetStatus(LinkStatus.Active, "Listening for connection");
        }

        private void ServerAcceptLoop()
        {
            while (!_stopping && _listener != null)
            {
                try
                {
                    // Check if listener has pending connection
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
                    // Expected during shutdown
                    break;
                }
                catch (Exception ex)
                {
                    if (!_stopping)
                    {
                        _logger.LogError(ex, "TcpLapbLink[{Name}]: Accept error", _config.Name);
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

                _logger.LogInformation("TcpLapbLink[{Name}]: Client connected from {Address}:{Port}",
                    _config.Name, remoteEndPoint?.Address, remoteEndPoint?.Port);

                // Start receive thread for this connection
                var receiveThread = new Thread(ReceiveLoop)
                {
                    Name = $"TcpLapbLink-{_config.Name}-Receive",
                    IsBackground = true
                };
                receiveThread.Start();
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
                Name = $"TcpLapbLink-{_config.Name}-Client",
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

                    _logger.LogInformation("TcpLapbLink[{Name}]: Connected to {Address}:{Port}",
                        _config.Name, _config.RemoteAddress, _config.Port);

                    SetStatus(LinkStatus.Active, "Connected");

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
                    _logger.LogWarning("TcpLapbLink[{Name}]: Connection failed: {Error}",
                        _config.Name, ex.Message);

                    SetStatus(LinkStatus.Error, $"Connection failed: {ex.Message}");
                    _statistics.Errors++;

                    // Wait before reconnect
                    if (_config.ReconnectDelayMs > 0 && !_stopping)
                    {
                        _logger.LogInformation("TcpLapbLink[{Name}]: Reconnecting in {Delay}ms...",
                            _config.Name, _config.ReconnectDelayMs);
                        Thread.Sleep(_config.ReconnectDelayMs);
                    }
                    else if (_config.ReconnectDelayMs <= 0)
                    {
                        // No auto-reconnect
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
                _lapbInstance.Reset();
                _hdlcFramer.Reset();

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
                            _hdlcFramer.ProcessBytes(_receiveBuffer, bytesRead);
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
                        _logger.LogError(ex, "TcpLapbLink[{Name}]: Receive error", _config.Name);
                        _statistics.Errors++;
                    }
                    break;
                }
            }
        }

        private void CloseConnection()
        {
            try
            {
                _networkStream?.Close();
                _tcpClient?.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TcpLapbLink[{Name}]: Error closing connection", _config.Name);
            }
            finally
            {
                _networkStream = null;
                _tcpClient = null;
            }
        }

        #endregion

        #region HDLC Framer Events

        private void OnHdlcFrameReceived(object? sender, FrameEventArgs e)
        {
            if (!e.FcsValid)
            {
                _logger.LogWarning("TcpLapbLink[{Name}]: Frame FCS error discarded", _config.Name);
                _statistics.Errors++;
                return;
            }

            _statistics.FramesReceived++;

            // Pass to LAPB for processing
            _lapbInstance.ReceiveFrame(e.Data, e.Length);
        }

        private void OnHdlcBytesToSend(object? sender, byte[] stuffedData)
        {
            if (_networkStream == null || _tcpClient == null || !_tcpClient.Connected)
                return;

            try
            {
                _networkStream.Write(stuffedData, 0, stuffedData.Length);
                _statistics.BytesTransmitted += stuffedData.Length;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TcpLapbLink[{Name}]: Error writing to TCP", _config.Name);
                _statistics.Errors++;
            }
        }

        #endregion

        #region LAPB Events

        private void OnLapbFrameToTransmit(object? sender, LapbFrameEventArgs e)
        {
            _statistics.FramesTransmitted++;

            // Fire FrameTransmitted event for monitoring/testing
            FrameTransmitted?.Invoke(this, e);

            // Send through HDLC framer (adds FCS and byte-stuffing)
            _hdlcFramer.SendFrame(e.FrameData, e.Length);
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

            _logger.LogInformation("TcpLapbLink[{Name}]: LAPB state {OldState} -> {NewState}",
                _config.Name, e.OldState, e.NewState);
        }

        #endregion

        #region Private Methods

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

            _lapbInstance.Dispose();

            _logger.LogInformation("TcpLapbLink[{Name}]: Disposed", _config.Name);
        }

        #endregion
    }
}
