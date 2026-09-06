using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Transport
{
    /// <summary>
    /// XOT Client transport interface for outbound X.25 over TCP connections
    /// Implements RFC 1613 (X.25 over TCP) for outbound call placement
    /// </summary>
    public class XotClientTransport : ITransportInterface
    {
        private readonly ILogger _logger;
        private readonly XotClientConfig _config;
        private XotInterfaceConfig? _interfaceConfig;
        private readonly object _connectionLock = new object();
        private readonly ManualResetEvent _restartConfirmReceived = new ManualResetEvent(false);
        private readonly ManualResetEvent _switchRestartSettled = new ManualResetEvent(false);
        private TransportStatus _status = TransportStatus.Stopped;
        private readonly TransportStatistics _statistics;
        private readonly Dictionary<string, XotConnection> _connections = new Dictionary<string, XotConnection>();

        public XotClientTransport(ILogger logger, XotClientConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            
            _statistics = new TransportStatistics
            {
                TransportName = _config.Name,
                TransportType = TransportType.XOT_Client,
                CurrentStatus = TransportStatus.Stopped,
                StartTime = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Set the interface config (provides RemoteIPAddress and RemotePort)
        /// </summary>
        /// <summary>The interface configuration this transport was created from, if any.</summary>
        public XotInterfaceConfig? InterfaceConfig => _interfaceConfig;

        public void SetInterfaceConfig(XotInterfaceConfig interfaceConfig)
        {
            _interfaceConfig = interfaceConfig;
        }

        /// <summary>
        /// Get the configured remote IP address for this client transport
        /// </summary>
        public string? GetRemoteIPAddress() => _interfaceConfig?.RemoteIPAddress;

        #region ITransportInterface Implementation

        public string Name => _config.Name;
        public TransportType Type => TransportType.XOT_Client;
        public TransportStatus Status => _status;

        /// <summary>
        /// Installed by the runtime: places the call through this connection's X.25 engine once the
        /// TCP connection is up. The transport no longer builds packets or waits for a Restart
        /// Confirmation itself - the engine does both (X.25 4.1.1: no call set-up in r2).
        /// </summary>
        public Func<XotClientTransport, X25CallRequest, bool>? PacketLayerPlaceCall { get; set; }

        /// <summary>
        /// Writes one complete X.25 packet to this transport's connection.
        /// </summary>
        public bool SendPacket(byte[] x25Packet)
        {
            string? ip = _interfaceConfig?.RemoteIPAddress;
            return ip != null && SendX25Packet(ip, x25Packet);
        }

        public bool PlaceCall(X25CallRequest callRequest)
        {
            if (_status != TransportStatus.Active)
            {
                _logger.LogWarning("XOT client transport {Name} not active, cannot place call", Name);
                return false;
            }

            var remoteIP = _interfaceConfig?.RemoteIPAddress;
            var remotePort = _interfaceConfig?.RemotePort ?? 1998;

            if (string.IsNullOrEmpty(remoteIP))
            {
                _logger.LogError("XOT client transport {Name} has no remote IP configured", Name);
                return false;
            }

            try
            {
                _logger.LogInformation("Placing XOT call via {Name} to {CalledAddress} -> {RemoteIP}:{RemotePort}",
                                     Name, callRequest.CalledAddress, remoteIP, remotePort);

                _statistics.OutboundCallsAttempted++;

                // Step 1: Establish TCP connection (reuses existing if already connected)
                var connected = ConnectToXotGateway(remoteIP, remotePort);
                if (!connected)
                {
                    _logger.LogError("Failed to connect to XOT gateway {RemoteIP}:{RemotePort}", remoteIP, remotePort);
                    return false;
                }

                // Step 2: Send X.25 Restart Request and wait for Confirm
                if (PacketLayerPlaceCall == null)
                {
                    _logger.LogError("XOT client transport {Name}: no packet layer installed to place the call", Name);
                    return false;
                }

                var sent = PacketLayerPlaceCall(this, callRequest);
                if (sent)
                {
                    _statistics.OutboundCallsSucceeded++;
                    _logger.LogInformation("XOT call placed via {Name} to {CalledAddress}", Name, callRequest.CalledAddress);
                }

                return sent;
            }
            catch (Exception ex)
            {
                _statistics.ConnectionErrors++;
                _logger.LogError(ex, "Error placing XOT call via {Name}", Name);
                return false;
            }
        }

        public bool AcceptCall(X25CallRequest callRequest)
        {
            // XOT Client transport typically doesn't accept incoming calls
            // Incoming calls are handled by XOT Server transport
            _logger.LogWarning("XOT client transport {Name} cannot accept incoming calls", Name);
            return false;
        }

        public bool RejectCall(X25CallRequest callRequest, byte cause)
        {
            // XOT Client transport typically doesn't reject incoming calls
            _logger.LogWarning("XOT client transport {Name} cannot reject incoming calls", Name);
            return false;
        }

        public bool Start()
        {
            if (_status != TransportStatus.Stopped)
            {
                _logger.LogWarning("XOT client transport {Name} already started", Name);
                return _status == TransportStatus.Active;
            }

            try
            {
                SetStatus(TransportStatus.Starting, "Starting XOT client transport");

                _logger.LogInformation("Starting XOT client transport {Name}", Name);

                // XOT client starts in ready state - connections are established on-demand
                SetStatus(TransportStatus.Active, "XOT client transport ready");
                _statistics.StartTime = DateTime.UtcNow;
                
                _logger.LogInformation("XOT client transport {Name} started successfully", Name);
                return true;
            }
            catch (Exception ex)
            {
                SetStatus(TransportStatus.Error, $"Startup error: {ex.Message}");
                _logger.LogError(ex, "Error starting XOT client transport {Name}", Name);
                return false;
            }
        }

        public void Stop()
        {
            if (_status == TransportStatus.Stopped || _status == TransportStatus.Stopping)
            {
                return;
            }

            try
            {
                SetStatus(TransportStatus.Stopping, "Stopping XOT client transport");

                _logger.LogInformation("Stopping XOT client transport {Name}", Name);

                // Close all active connections
                lock (_connectionLock)
                {
                    foreach (var connection in _connections.Values)
                    {
                        connection.Close();
                    }
                    _connections.Clear();
                }

                SetStatus(TransportStatus.Stopped, "XOT client transport stopped");
                _logger.LogInformation("XOT client transport {Name} stopped", Name);
            }
            catch (Exception ex)
            {
                SetStatus(TransportStatus.Error, $"Shutdown error: {ex.Message}");
                _logger.LogError(ex, "Error stopping XOT client transport {Name}", Name);
            }
        }

        public TransportStatistics GetStatistics()
        {
            _statistics.CurrentStatus = _status;
            _statistics.ActiveConnections = _connections.Count;
            return _statistics;
        }

#pragma warning disable CS0067 // Event required by interface, not used in this implementation
        public event EventHandler<X25CallRequest>? IncomingCall;
#pragma warning restore CS0067
        public event EventHandler<TransportStatusEventArgs>? StatusChanged;
        public event EventHandler<TransportDataEventArgs>? DataReceived;

        #endregion

        #region XOT-Specific Methods

        /// <summary>
        /// Establish XOT connection to specified IP address
        /// This is called by the routing system with destination from route
        /// </summary>
        public bool ConnectToXotGateway(string ipAddress, int port = 1998)
        {
            lock (_connectionLock)
            {
                if (_connections.ContainsKey(ipAddress))
                {
                    _logger.LogDebug("XOT connection to {IpAddress} already exists", ipAddress);
                    return true;
                }
            }

            try
            {
                _logger.LogInformation("Establishing XOT connection to {IpAddress}:{Port}", ipAddress, port);

                var connection = new XotConnection(_logger, ipAddress, port);

                // Subscribe to events
                connection.DataReceived += OnConnectionDataReceived;
                connection.ConnectionClosed += OnConnectionClosed;

                var success = connection.Connect(_config.ConnectTimeout);

                if (success)
                {
                    lock (_connectionLock)
                    {
                        _connections[ipAddress] = connection;
                    }
                    
                    _logger.LogInformation("XOT connection to {IpAddress} established", ipAddress);
                    return true;
                }
                else
                {
                    _logger.LogWarning("Failed to establish XOT connection to {IpAddress}", ipAddress);
                    _statistics.ConnectionErrors++;
                    return false;
                }
            }
            catch (Exception ex)
            {
                _statistics.ConnectionErrors++;
                _logger.LogError(ex, "Error connecting to XOT gateway {IpAddress}", ipAddress);
                return false;
            }
        }

        /// <summary>
        /// Send X.25 packet over XOT connection
        /// </summary>
        public bool SendX25Packet(string destinationIp, byte[] x25Packet)
        {
            XotConnection? connection;
            lock (_connectionLock)
            {
                if (!_connections.TryGetValue(destinationIp, out connection))
                {
                    _logger.LogError("No XOT connection to {DestinationIp}", destinationIp);
                    return false;
                }
            }

            try
            {
                var success = connection.SendPacket(x25Packet);
                if (success)
                {
                    _statistics.BytesTransmitted += x25Packet.Length;
                }
                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending X.25 packet over XOT to {DestinationIp}", destinationIp);
                return false;
            }
        }

        #endregion

        #region Private Methods

        private void SetStatus(TransportStatus newStatus, string reason)
        {
            var oldStatus = _status;
            _status = newStatus;
            _statistics.CurrentStatus = newStatus;

            _logger.LogDebug("XOT client transport {Name} status changed: {OldStatus} -> {NewStatus} ({Reason})",
                           Name, oldStatus, newStatus, reason);

            StatusChanged?.Invoke(this, new TransportStatusEventArgs
            {
                OldStatus = oldStatus,
                NewStatus = newStatus,
                Reason = reason
            });
        }

        /// <summary>
        /// Handle data received from XOT connection
        /// Re-fire event to external subscribers (for bridge pass-through mode)
        /// </summary>
        private void OnConnectionDataReceived(object? sender, TransportDataEventArgs e)
        {
            // Update statistics
            _statistics.BytesReceived += e.Length;

            if (e.Data != null && e.Data.Length >= 3)
            {
                byte pti = e.Data[2];

                // Handle Restart Confirm (0xFF)
                if (pti == 0xFF)
                {
                    _restartConfirmReceived.Set();
                }
                // Handle incoming Restart Request (0xFB) — send back Restart Confirm
                else if (pti == 0xFB)
                {
                    var remoteIP = _interfaceConfig?.RemoteIPAddress;
                    if (!string.IsNullOrEmpty(remoteIP))
                    {
                        var restartConfirm = new byte[] { 0x10, 0x00, 0xFF };
                        SendX25Packet(remoteIP, restartConfirm);
                        _switchRestartSettled.Set();
                        _logger.LogDebug("Sent Restart Confirm in response to switch Restart Request");
                    }
                }
            }

            // Set transport reference and forward to external subscribers
            e.Transport = this;
            DataReceived?.Invoke(this, e);

            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("XOT client {Name} received {Length} bytes", Name, e.Length);
        }

        /// <summary>
        /// Handle connection closed event
        /// </summary>
        private void OnConnectionClosed(object? sender, string remoteAddress)
        {
            lock (_connectionLock)
            {
                if (_connections.Remove(remoteAddress))
                {
                    _logger.LogInformation("XOT client {Name} connection to {RemoteAddress} closed", Name, remoteAddress);
                }
            }
        }

        #endregion
    }

    /// <summary>
    /// Configuration for XOT Client transport
    /// </summary>
    public class XotClientConfig
    {
        /// <summary>
        /// Unique name for this transport interface
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Local source IP address for outbound connections
        /// </summary>
        public string SourceAddress { get; set; } = "0.0.0.0";

        /// <summary>
        /// Connection timeout in milliseconds
        /// </summary>
        public int ConnectTimeout { get; set; } = 30000;

        /// <summary>
        /// Keep-alive interval in seconds
        /// </summary>
        public int KeepAliveInterval { get; set; } = 60;

        /// <summary>
        /// Maximum number of concurrent connections
        /// </summary>
        public int MaxConnections { get; set; } = 100;

        /// <summary>
        /// Optional description
        /// </summary>
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents a single XOT TCP connection to a remote gateway
    /// </summary>
    internal class XotConnection
    {
        private readonly ILogger _logger;
        private readonly string _remoteAddress;
        private readonly int _remotePort;
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private CancellationTokenSource? _cancellationTokenSource;

        /// <summary>
        /// Serialises whole XOT frames onto the stream.
        /// </summary>
        /// <remarks>
        /// Its own object rather than locking <c>_stream</c>, because <c>_stream</c> is replaced on
        /// reconnect and a lock taken on the old instance would guard nothing. See
        /// <see cref="SendPacket"/> for what interleaved writes did to the wire.
        /// </remarks>
        private readonly object _sendLock = new object();

        public event EventHandler<TransportDataEventArgs>? DataReceived;
        public event EventHandler<string>? ConnectionClosed;

        public XotConnection(ILogger logger, string remoteAddress, int remotePort)
        {
            _logger = logger;
            _remoteAddress = remoteAddress;
            _remotePort = remotePort;
        }

        public bool Connect(int timeoutMs)
        {
            try
            {
                _tcpClient = new TcpClient();
                
                // Set timeout for connection
                // Use synchronous connect with timeout
                var connectResult = _tcpClient.BeginConnect(_remoteAddress, _remotePort, null, null);
                var success = connectResult.AsyncWaitHandle.WaitOne(timeoutMs);
                
                if (!success)
                {
                    _logger.LogWarning("XOT connection timeout to {Address}:{Port}", _remoteAddress, _remotePort);
                    _tcpClient?.Close();
                    return false;
                }
                
                _tcpClient.EndConnect(connectResult);

                _stream = _tcpClient.GetStream();

                // Start receive loop
                _cancellationTokenSource = new CancellationTokenSource();
                new Thread(() => ReceiveLoop(_cancellationTokenSource.Token)) { IsBackground = true }.Start();

                _logger.LogDebug("XOT TCP connection established to {Address}:{Port}", _remoteAddress, _remotePort);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to establish XOT connection to {Address}:{Port}", _remoteAddress, _remotePort);
                _tcpClient?.Close();
                return false;
            }
        }

        public bool SendPacket(byte[] x25Packet)
        {
            if (_stream == null || !_tcpClient?.Connected == true)
            {
                _logger.LogError("XOT connection not established to {Address}:{Port}", _remoteAddress, _remotePort);
                return false;
            }

            try
            {
                // XOT encapsulation (RFC 1613): 2-byte version 0x0000, 2-byte big-endian length,
                // then the X.25 packet.
                //
                // ONE buffer and ONE lock, and both matter. MEASURED 2026-09-04: this used to be two
                // separate _stream.Write calls with no lock, and TWO threads reach here - the caller
                // sending DATA, and the receive thread sending RR from
                // ProtocolRuntime.SendRRToXotConnection, which runs outside the routing lock.
                // NetworkStream.Write is not atomic across calls, so the two frames interleaved: the
                // peer read one frame's length header followed by another frame's bytes, the frame
                // boundary was destroyed, and it decoded the remains as an X.25 packet with a
                // sequence number that made no sense. The C reference answered exactly as ITU-T
                // X.25 4.4.1.3 says it must - RESET, cause 0x05 local procedure error, diagnostic
                // 0x01 invalid P(S) - which is what a bulk transfer kept dying of.
                //
                // XotServerTransport.SendPacket already took this lock; only the client half was
                // missed, and the tests dial OUT, so they took the unprotected path every time.
                var frame = new byte[4 + x25Packet.Length];
                frame[0] = 0x00;
                frame[1] = 0x00;
                frame[2] = (byte)((x25Packet.Length >> 8) & 0xFF);
                frame[3] = (byte)(x25Packet.Length & 0xFF);
                Buffer.BlockCopy(x25Packet, 0, frame, 4, x25Packet.Length);

                lock (_sendLock)
                {
                    _stream.Write(frame, 0, frame.Length);
                    _stream.Flush();
                }

                _logger.LogDebug("Sent XOT packet ({Length} bytes) to {Address}:{Port}", x25Packet.Length, _remoteAddress, _remotePort);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send XOT packet to {Address}:{Port}", _remoteAddress, _remotePort);
                return false;
            }
        }

        /// <summary>
        /// Receive loop for incoming XOT packets
        /// </summary>
        private void ReceiveLoop(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Started receiving XOT packets from {Address}:{Port}", _remoteAddress, _remotePort);

            try
            {
                while (!cancellationToken.IsCancellationRequested && _stream != null && _tcpClient?.Connected == true)
                {
                    // Read XOT header (4 bytes: version + length)
                    var headerBytes = ReadExact(_stream, 4);
                    if (headerBytes == null)
                        break; // Connection closed

                    var version = (headerBytes[0] << 8) | headerBytes[1];
                    var length = (headerBytes[2] << 8) | headerBytes[3];

                    _logger.LogDebug("Received XOT packet from {Address}:{Port}: version={Version}, length={Length}",
                                   _remoteAddress, _remotePort, version, length);

                    if (version != 0)
                    {
                        _logger.LogWarning("Unsupported XOT version {Version} from {Address}:{Port}", version, _remoteAddress, _remotePort);
                        continue;
                    }

                    if (length > 4096)
                    {
                        _logger.LogWarning("XOT packet too large ({Length} bytes) from {Address}:{Port}", length, _remoteAddress, _remotePort);
                        continue;
                    }

                    // Read X.25 packet data
                    var packetData = ReadExact(_stream, length);
                    if (packetData == null)
                        break;

                    // Fire DataReceived event for bridge pass-through mode
                    DataReceived?.Invoke(this, new TransportDataEventArgs
                    {
                        Data = packetData,
                        Length = length,
                        Transport = null // Will be set by XotClientTransport
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error receiving XOT packets from {Address}:{Port}", _remoteAddress, _remotePort);
            }
            finally
            {
                ConnectionClosed?.Invoke(this, _remoteAddress);
            }
        }

        /// <summary>
        /// Read exact number of bytes from stream
        /// </summary>
        private byte[]? ReadExact(NetworkStream stream, int count)
        {
            var buffer = new byte[count];
            var totalRead = 0;

            while (totalRead < count)
            {
                var bytesRead = stream.Read(buffer, totalRead, count - totalRead);
                if (bytesRead == 0)
                    return null; // Connection closed

                totalRead += bytesRead;
            }

            return buffer;
        }

        public void Close()
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                _stream?.Close();
                _tcpClient?.Close();
                _logger.LogDebug("XOT connection to {Address}:{Port} closed", _remoteAddress, _remotePort);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing XOT connection to {Address}:{Port}", _remoteAddress, _remotePort);
            }
        }
    }
}