using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Transport
{
    /// <summary>
    /// XOT Server transport interface for inbound X.25 over TCP connections
    /// Implements RFC 1613 (X.25 over TCP) listener for incoming XOT connections
    /// </summary>
    public class XotServerTransport : ITransportInterface
    {
        private readonly ILogger _logger;
        private readonly XotServerConfig _config;
        private readonly object _clientLock = new object();
        private TransportStatus _status = TransportStatus.Stopped;
        private readonly TransportStatistics _statistics;
        private TcpListener? _tcpListener;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly Dictionary<string, XotServerConnection> _clientConnections = new Dictionary<string, XotServerConnection>();

        public XotServerTransport(ILogger logger, XotServerConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            
            _statistics = new TransportStatistics
            {
                TransportName = _config.Name,
                TransportType = TransportType.XOT_Server,
                CurrentStatus = TransportStatus.Stopped,
                StartTime = DateTime.UtcNow
            };
        }

        #region ITransportInterface Implementation

        public string Name => _config.Name;
        public TransportType Type => TransportType.XOT_Server;
        public TransportStatus Status => _status;

        public bool PlaceCall(X25CallRequest callRequest)
        {
            // XOT Server transport typically doesn't place outbound calls
            // Outbound calls are handled by XOT Client transport
            _logger.LogWarning("XOT server transport {Name} cannot place outbound calls", Name);
            return false;
        }

        public bool AcceptCall(X25CallRequest callRequest)
        {
            if (_status != TransportStatus.Active)
            {
                _logger.LogWarning("XOT server transport {Name} not active, cannot accept call", Name);
                return false;
            }

            try
            {
                _logger.LogInformation("Accepting X.25 call via XOT server {Name} from {CallingAddress} to {CalledAddress}", 
                                     Name, callRequest.CallingAddress, callRequest.CalledAddress);

                // Find the client connection that sent this call request
                // Full implementation would track which connection the call came from
                _statistics.InboundCallsAccepted++;
                
                // Placeholder for call acceptance
                // Full implementation would:
                // 1. Send X.25 call accepted packet back to originating XOT client
                // 2. Establish virtual circuit for data transfer
                _logger.LogInformation("XOT call accepted successfully via {Name}", Name);
                return true;
            }
            catch (Exception ex)
            {
                _statistics.ConnectionErrors++;
                _logger.LogError(ex, "Error accepting XOT call via {Name}", Name);
                return false;
            }
        }

        public bool RejectCall(X25CallRequest callRequest, byte cause)
        {
            if (_status != TransportStatus.Active)
            {
                _logger.LogWarning("XOT server transport {Name} not active, cannot reject call", Name);
                return false;
            }

            try
            {
                _logger.LogInformation("Rejecting X.25 call via XOT server {Name} from {CallingAddress} to {CalledAddress} with cause 0x{Cause:X2}", 
                                     Name, callRequest.CallingAddress, callRequest.CalledAddress, cause);

                // Find the client connection and send call clear
                _statistics.InboundCallsRejected++;
                
                // Placeholder for call rejection
                // Full implementation would send X.25 clear request packet with cause code
                _logger.LogInformation("XOT call rejected successfully via {Name}", Name);
                return true;
            }
            catch (Exception ex)
            {
                _statistics.ConnectionErrors++;
                _logger.LogError(ex, "Error rejecting XOT call via {Name}", Name);
                return false;
            }
        }

        public bool Start()
        {
            if (_status != TransportStatus.Stopped)
            {
                _logger.LogWarning("XOT server transport {Name} already started", Name);
                return _status == TransportStatus.Active;
            }

            try
            {
                SetStatus(TransportStatus.Starting, "Starting XOT server transport");

                _logger.LogInformation("Starting XOT server transport {Name} on {Address}:{Port}", 
                                     Name, _config.ListenAddress, _config.ListenPort);

                // Parse listen address
                var listenAddress = IPAddress.Parse(_config.ListenAddress);
                _tcpListener = new TcpListener(listenAddress, _config.ListenPort);
                _cancellationTokenSource = new CancellationTokenSource();

                // Start listening
                _tcpListener.Start();

                // Start accepting connections in background
                new Thread(() => AcceptConnectionsLoop(_cancellationTokenSource.Token)) { IsBackground = true }.Start();

                SetStatus(TransportStatus.Active, "XOT server listening for connections");
                _statistics.StartTime = DateTime.UtcNow;
                
                _logger.LogInformation("XOT server transport {Name} started successfully on {Address}:{Port}", 
                                     Name, _config.ListenAddress, _config.ListenPort);
                return true;
            }
            catch (Exception ex)
            {
                SetStatus(TransportStatus.Error, $"Startup error: {ex.Message}");
                _logger.LogError(ex, "Error starting XOT server transport {Name}", Name);
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
                SetStatus(TransportStatus.Stopping, "Stopping XOT server transport");

                _logger.LogInformation("Stopping XOT server transport {Name}", Name);

                // Stop accepting new connections
                _cancellationTokenSource?.Cancel();
                _tcpListener?.Stop();

                // Close all client connections
                lock (_clientLock)
                {
                    foreach (var connection in _clientConnections.Values)
                    {
                        connection.Close();
                    }
                    _clientConnections.Clear();
                }

                SetStatus(TransportStatus.Stopped, "XOT server transport stopped");
                _logger.LogInformation("XOT server transport {Name} stopped", Name);
            }
            catch (Exception ex)
            {
                SetStatus(TransportStatus.Error, $"Shutdown error: {ex.Message}");
                _logger.LogError(ex, "Error stopping XOT server transport {Name}", Name);
            }
        }

        public TransportStatistics GetStatistics()
        {
            _statistics.CurrentStatus = _status;
            _statistics.ActiveConnections = _clientConnections.Count;
            return _statistics;
        }

        public event EventHandler<X25CallRequest>? IncomingCall;
        public event EventHandler<TransportStatusEventArgs>? StatusChanged;
        public event EventHandler<TransportDataEventArgs>? DataReceived;

        #endregion

        #region XOT Server Methods

        /// <summary>
        /// Accept incoming XOT connections in background
        /// </summary>
        private void AcceptConnectionsLoop(CancellationToken cancellationToken)
        {
            _logger.LogDebug("XOT server {Name} started accepting connections", Name);

            while (!cancellationToken.IsCancellationRequested && _tcpListener != null)
            {
                try
                {
                    var tcpClient = _tcpListener.AcceptTcpClient();
                    var clientEndpoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";

                    _logger.LogInformation("XOT server {Name} accepted connection from {ClientEndpoint}", Name, clientEndpoint);

                    // Check connection limit
                    lock (_clientLock)
                    {
                        if (_clientConnections.Count >= _config.MaxConnections)
                        {
                            _logger.LogWarning("XOT server {Name} connection limit reached ({Max}), rejecting {ClientEndpoint}", 
                                             Name, _config.MaxConnections, clientEndpoint);
                            tcpClient.Close();
                            continue;
                        }
                    }

                    // Create connection handler
                    var connection = new XotServerConnection(_logger, tcpClient, clientEndpoint);
                    connection.IncomingCall += OnClientIncomingCall;
                    connection.ConnectionClosed += OnClientConnectionClosed;
                    connection.DataReceived += OnClientDataReceived;

                    lock (_clientLock)
                    {
                        _clientConnections[clientEndpoint] = connection;
                    }

                    // Start handling this connection
                    new Thread(() => connection.HandleConnectionLoop(cancellationToken)) { IsBackground = true }.Start();
                }
                catch (ObjectDisposedException)
                {
                    // Listener was stopped
                    break;
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogError(ex, "Error accepting XOT connection on server {Name}", Name);
                        _statistics.ConnectionErrors++;
                    }
                }
            }

            _logger.LogDebug("XOT server {Name} stopped accepting connections", Name);
        }

        /// <summary>
        /// Handle incoming call from client connection
        /// </summary>
        /// <summary>
        /// True when the connection belongs to this server.
        /// </summary>
        public bool Owns(XotServerConnection connection)
        {
            lock (_clientLock)
            {
                foreach (var kv in _clientConnections)
                {
                    if (ReferenceEquals(kv.Value, connection))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void OnClientIncomingCall(object? sender, X25CallRequest callRequest)
        {
            _statistics.InboundCallsReceived++;
            
            _logger.LogDebug("XOT server {Name} received incoming call from {CallingAddress} to {CalledAddress}", 
                           Name, callRequest.CallingAddress, callRequest.CalledAddress);

            // Check if we accept calls for this X.121 address
            if (AcceptsX121Address(callRequest.CalledAddress))
            {
                // Forward to routing system
                IncomingCall?.Invoke(this, callRequest);
            }
            else
            {
                _logger.LogWarning("XOT server {Name} rejecting call to unaccepted X.121 address: {CalledAddress}", 
                                 Name, callRequest.CalledAddress);
                
                // Reject call with "access barred" cause
                RejectCall(callRequest, 0x0B);
            }
        }

        /// <summary>
        /// Handle client connection closed
        /// </summary>
        private void OnClientConnectionClosed(object? sender, string clientEndpoint)
        {
            lock (_clientLock)
            {
                if (_clientConnections.Remove(clientEndpoint))
                {
                    _logger.LogInformation("XOT server {Name} client connection closed: {ClientEndpoint}", Name, clientEndpoint);
                }
            }
        }

        /// <summary>
        /// Handle data received from client connection
        /// Re-fire event to external subscribers (for bridge pass-through mode)
        /// </summary>
        private void OnClientDataReceived(object? sender, TransportDataEventArgs e)
        {
            // Update statistics
            _statistics.BytesReceived += e.Length;

            // Set transport reference and forward to external subscribers
            e.Transport = this;
            DataReceived?.Invoke(this, e);

            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("XOT server {Name} received {Length} bytes from client", Name, e.Length);
        }

        /// <summary>
        /// Check if we accept calls for specified X.121 address.
        /// If no patterns are configured, accepts all addresses.
        /// </summary>
        private bool AcceptsX121Address(string x121Address)
        {
            // No patterns configured = accept all
            if (_config.AcceptedX121Ranges.Count == 0)
                return true;

            foreach (var pattern in _config.AcceptedX121Ranges)
            {
                try
                {
                    var regex = new System.Text.RegularExpressions.Regex(pattern);
                    if (regex.IsMatch(x121Address))
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Invalid X.121 address pattern: {Pattern}", pattern);
                }
            }

            return false;
        }

        #endregion

        #region Private Methods

        private void SetStatus(TransportStatus newStatus, string reason)
        {
            var oldStatus = _status;
            _status = newStatus;
            _statistics.CurrentStatus = newStatus;

            _logger.LogDebug("XOT server transport {Name} status changed: {OldStatus} -> {NewStatus} ({Reason})", 
                           Name, oldStatus, newStatus, reason);

            StatusChanged?.Invoke(this, new TransportStatusEventArgs
            {
                OldStatus = oldStatus,
                NewStatus = newStatus,
                Reason = reason
            });
        }

        #endregion
    }

    /// <summary>
    /// Configuration for XOT Server transport
    /// </summary>
    public class XotServerConfig
    {
        /// <summary>
        /// Unique name for this transport interface
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// IP address to listen on (0.0.0.0 for all interfaces)
        /// </summary>
        public string ListenAddress { get; set; } = "0.0.0.0";

        /// <summary>
        /// TCP port to listen on (standard XOT port is 1998)
        /// </summary>
        public int ListenPort { get; set; } = 1998;

        /// <summary>
        /// Maximum number of concurrent client connections
        /// </summary>
        public int MaxConnections { get; set; } = 100;

        /// <summary>
        /// X.121 address patterns we accept calls for
        /// </summary>
        public List<string> AcceptedX121Ranges { get; set; } = new List<string>();

        /// <summary>
        /// Connection timeout in milliseconds
        /// </summary>
        public int ConnectionTimeout { get; set; } = 300000; // 5 minutes

        /// <summary>
        /// Optional description
        /// </summary>
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents a single XOT client connection to the server
    /// </summary>
    public class XotServerConnection
    {
        private readonly ILogger _logger;
        private readonly TcpClient _tcpClient;
        private readonly string _clientEndpoint;
        private readonly NetworkStream _stream;

        public XotServerConnection(ILogger logger, TcpClient tcpClient, string clientEndpoint)
        {
            _logger = logger;
            _tcpClient = tcpClient;
            _clientEndpoint = clientEndpoint;
            _stream = tcpClient.GetStream();
        }

        public event EventHandler<X25CallRequest>? IncomingCall;
        public event EventHandler<string>? ConnectionClosed;
        public event EventHandler<TransportDataEventArgs>? DataReceived;

        /// <summary>
        /// Handle XOT packets from this client connection
        /// </summary>
        public void HandleConnectionLoop(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Started handling XOT connection from {ClientEndpoint}", _clientEndpoint);

            try
            {
                var buffer = new byte[4096];

                while (!cancellationToken.IsCancellationRequested && _tcpClient.Connected)
                {
                    // Read XOT header (4 bytes: version + length)
                    var headerBytes = ReadExact(_stream, 4);
                    if (headerBytes == null)
                        break; // Connection closed

                    var version = (headerBytes[0] << 8) | headerBytes[1];
                    var length = (headerBytes[2] << 8) | headerBytes[3];

                    _logger.LogDebug("Received XOT packet from {ClientEndpoint}: version={Version}, length={Length}", 
                                   _clientEndpoint, version, length);

                    if (version != 0)
                    {
                        _logger.LogWarning("Unsupported XOT version {Version} from {ClientEndpoint}", version, _clientEndpoint);
                        continue;
                    }

                    if (length > 4096)
                    {
                        _logger.LogWarning("XOT packet too large ({Length} bytes) from {ClientEndpoint}", length, _clientEndpoint);
                        continue;
                    }

                    // Read X.25 packet data
                    var packetData = ReadExact(_stream, length);
                    if (packetData == null)
                        break;

                    // Fire DataReceived event for bridge pass-through mode
                    // Every packet - Call Request, data, clear, restart - goes to the runtime, which
                    // feeds it to this connection's X.25 engine. Since 2026-09-06 nothing is decoded
                    // here: the old ProcessX25Packet answered restarts and raised IncomingCall itself,
                    // which put a second protocol handler on the same connection.
                    DataReceived?.Invoke(this, new TransportDataEventArgs
                    {
                        Data = packetData,
                        Length = length,
                        Transport = null, // set by XotServerTransport when it re-raises
                        Connection = this,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling XOT connection from {ClientEndpoint}", _clientEndpoint);
            }
            finally
            {
                Close();
                ConnectionClosed?.Invoke(this, _clientEndpoint);
            }
        }

        /// <summary>
        /// Process received X.25 packet - parse type and dispatch
        /// </summary>
        private void ProcessX25Packet(byte[] packetData)
        {
            try
            {
                if (packetData.Length < 3)
                {
                    _logger.LogWarning("X.25 packet too short ({Length} bytes) from {ClientEndpoint}", packetData.Length, _clientEndpoint);
                    return;
                }

                byte pti = packetData[2];

                if (pti == 0xFB) // Restart Request
                {
                    _logger.LogInformation("X.25 RESTART REQUEST from {ClientEndpoint} - sending RESTART CONFIRM", _clientEndpoint);

                    // Build Restart Confirm packet: GFI=0x10|LCG=0, LCN=0x00, PTI=0xFF
                    var restartConfirm = new byte[] { 0x10, 0x00, 0xFF };
                    SendPacket(restartConfirm);
                }
                else if (pti == 0x0B) // Call Request
                {
                    var callRequest = new X25CallRequest(packetData);
                    // Store the LCN from the packet for response routing
                    byte gfi = packetData[0];
                    byte lcnLow = packetData[1];
                    ushort lcn = (ushort)(((gfi & 0x0F) << 8) | lcnLow);
                    callRequest.SourceLcn = lcn;

                    _logger.LogInformation("X.25 CALL REQUEST from {ClientEndpoint}: calling={CallingAddress} -> called={CalledAddress} LCN={LCN}",
                                         _clientEndpoint, callRequest.CallingAddress, callRequest.CalledAddress, lcn);

                    // Store reference to this connection for sending responses back
                    callRequest.SourceConnection = this;

                    IncomingCall?.Invoke(this, callRequest);
                }
                else
                {
                    _logger.LogDebug("X.25 packet type 0x{PTI:X2} from {ClientEndpoint}", pti, _clientEndpoint);
                    // Data, Clear, RR etc. are handled via the DataReceived event
                    // fired in HandleConnectionLoop (before ProcessX25Packet is called)
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing X.25 packet from {ClientEndpoint}", _clientEndpoint);
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

        /// <summary>
        /// Send an X.25 packet back to this client via XOT encapsulation (RFC 1613)
        /// </summary>
        public bool SendPacket(byte[] x25Packet)
        {
            try
            {
                if (!_tcpClient.Connected) return false;

                // XOT header: 2 bytes version (0x0000) + 2 bytes length (big-endian)
                var header = new byte[4];
                header[0] = 0x00;
                header[1] = 0x00;
                header[2] = (byte)((x25Packet.Length >> 8) & 0xFF);
                header[3] = (byte)(x25Packet.Length & 0xFF);

                lock (_stream)
                {
                    _stream.Write(header, 0, 4);
                    _stream.Write(x25Packet, 0, x25Packet.Length);
                    _stream.Flush();
                }

                _logger.LogDebug("Sent XOT packet ({Length} bytes) to {ClientEndpoint}", x25Packet.Length, _clientEndpoint);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending XOT packet to {ClientEndpoint}", _clientEndpoint);
                return false;
            }
        }

        public void Close()
        {
            try
            {
                _stream?.Close();
                _tcpClient?.Close();
                _logger.LogDebug("Closed XOT connection from {ClientEndpoint}", _clientEndpoint);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing XOT connection from {ClientEndpoint}", _clientEndpoint);
            }
        }
    }
}