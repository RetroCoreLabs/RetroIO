//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Transport
{
    /// <summary>
    /// XOT Server transport interface for inbound X.25 over TCP connections
    /// Implements RFC 1613 (X.25 over TCP) listener for incoming XOT connections
    /// </summary>
    public class XotServerTransport : ITransportInterface, IXotListenerClient
    {
        private readonly ILogger _logger;
        private readonly XotServerConfig _config;
        private readonly object _clientLock = new object();
        private TransportStatus _status = TransportStatus.Stopped;
        private readonly TransportStatistics _statistics;
        private TcpListener? _tcpListener;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly Dictionary<string, XotServerConnection> _clientConnections = new Dictionary<string, XotServerConnection>();

        /// <summary>
        /// Creates the XOT server transport in the stopped state.
        /// </summary>
        /// <param name="logger">
        /// Diagnostics logger.
        /// </param>
        /// <param name="config">
        /// The server transport configuration.
        /// </param>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="logger"/> or <paramref name="config"/> is null.
        /// </exception>
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

        /// <summary>
        /// Gets the unique name of this transport interface, taken from the configuration.
        /// </summary>
        public string Name => _config.Name;
        /// <summary>
        /// Gets the type of transport, always <see cref="TransportType.XOT_Server"/>.
        /// </summary>
        public TransportType Type => TransportType.XOT_Server;
        /// <summary>
        /// Gets the current status of the transport.
        /// </summary>
        public TransportStatus Status => _status;

        /// <summary>
        /// Does nothing except log a warning, because a server transport does not place outbound calls.
        /// </summary>
        /// <param name="callRequest">
        /// The call request, which is ignored.
        /// </param>
        /// <returns>
        /// Always false.
        /// </returns>
        public bool PlaceCall(X25CallRequest callRequest)
        {
            // XOT Server transport typically doesn't place outbound calls
            // Outbound calls are handled by XOT Client transport
            _logger.LogWarning("XOT server transport {Name} cannot place outbound calls", Name);
            return false;
        }

        /// <summary>
        /// Always throws, because answering a call with a Call Accepted packet is not implemented.
        /// </summary>
        /// <remarks>
        /// The transport does not track which TCP connection a call arrived on, so it cannot send a
        /// Call Accepted packet back to the caller. Earlier code counted the call as accepted and
        /// returned <c>true</c> without sending anything; that hid the missing mechanism.
        /// </remarks>
        /// <param name="callRequest">
        /// The call request that would be accepted.
        /// </param>
        /// <returns>
        /// Never returns.
        /// </returns>
        /// <exception cref="NotSupportedException">
        /// Always thrown: accepting an incoming call on the XOT server transport is not implemented.
        /// </exception>
        public bool AcceptCall(X25CallRequest callRequest)
        {
            throw new NotSupportedException(
                "XotServerTransport.AcceptCall is not implemented: the server transport cannot send a Call Accepted packet to the calling XOT connection.");
        }

        /// <summary>
        /// Always throws, because clearing a call with a Clear Request packet is not implemented.
        /// </summary>
        /// <remarks>
        /// The transport does not track which TCP connection a call arrived on, so it cannot send a
        /// Clear Request packet with the cause code back to the caller. Earlier code counted the call
        /// as rejected and returned <c>true</c> without sending anything; that hid the missing mechanism.
        /// </remarks>
        /// <param name="callRequest">
        /// The call request that would be rejected.
        /// </param>
        /// <param name="cause">
        /// The clear cause code that would be sent.
        /// </param>
        /// <returns>
        /// Never returns.
        /// </returns>
        /// <exception cref="NotSupportedException">
        /// Always thrown: rejecting an incoming call on the XOT server transport is not implemented.
        /// </exception>
        public bool RejectCall(X25CallRequest callRequest, byte cause)
        {
            throw new NotSupportedException(
                "XotServerTransport.RejectCall is not implemented: the server transport cannot send a Clear Request packet to the calling XOT connection.");
        }

        /// <summary>
        /// Starts a TCP listener on the configured address and port and accepts XOT connections on a background thread.
        /// </summary>
        /// <returns>
        /// True when the transport is active afterwards, false when starting failed.
        /// </returns>
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

                _cancellationTokenSource = new CancellationTokenSource();

                if (_config.Listeners != null)
                {
                    // Shared port: the registry keeps the one listening socket and hands this server
                    // every connection no peer entry claims (see XotListenerRegistry).
                    _config.Listeners.Join(_config.ListenAddress, _config.ListenPort, this);
                }
                else
                {
                    // Parse listen address
                    var listenAddress = IPAddress.Parse(_config.ListenAddress);
                    _tcpListener = new TcpListener(listenAddress, _config.ListenPort);

                    // Start listening
                    _tcpListener.Start();

                    // Start accepting connections in background
                    new Thread(() => AcceptConnectionsLoop(_cancellationTokenSource.Token)) { IsBackground = true }.Start();
                }

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

        /// <summary>
        /// Stops the listener, closes all client connections and moves the transport to the stopped state.
        /// </summary>
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
                _config.Listeners?.Leave(this);

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

        /// <summary>
        /// Gets the transport's statistics, with the current status and the number of client
        /// connections brought up to date first.
        /// </summary>
        /// <returns>
        /// The transport's own statistics object, not a copy.
        /// </returns>
        public TransportStatistics GetStatistics()
        {
            _statistics.CurrentStatus = _status;
            _statistics.ActiveConnections = _clientConnections.Count;
            return _statistics;
        }

        /// <summary>
        /// Sets the call, byte and error counters of this transport back to zero.
        /// </summary>
        public void ResetStatistics()
        {
            _statistics.ResetCounters();
        }

#pragma warning disable CS0067 // Event required by interface, not used in this implementation
        /// <summary>
        /// Required by the interface for incoming calls. The server transport never raises it.
        /// </summary>
        /// <remarks>
        /// Every packet from a client connection, a Call Request included, is raised through
        /// <see cref="DataReceived"/> and decoded by the X.25 engine the runtime keeps for that
        /// connection (since 2026-09-06); the runtime raises and answers the call there, and applies
        /// <see cref="XotServerConfig.AcceptedX121Ranges"/> through <see cref="AcceptsX121Address"/>.
        /// The old handler that raised this event was never reached and was removed 2026-11-17.
        /// </remarks>
        public event EventHandler<X25CallRequest>? IncomingCall;
#pragma warning restore CS0067
        /// <summary>
        /// Raised when the transport status changes, with the old status, the new status and the
        /// reason.
        /// </summary>
        public event EventHandler<TransportStatusEventArgs>? StatusChanged;
        /// <summary>
        /// Raised for every X.25 packet received from a client connection, with the transport set
        /// on the event data. Used for bridge pass-through mode.
        /// </summary>
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

                    if (!AdoptClient(tcpClient, clientEndpoint, cancellationToken))
                    {
                        continue;
                    }
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


        // IXotListenerClient: an old-style server takes every address, so it is never "unlisted".
        IPAddress? IXotListenerClient.PeerAddress => null;

        bool IXotListenerClient.RefuseUnlisted => false;

        void IXotListenerClient.OnConnection(TcpClient client, IPAddress remote, bool listed)
        {
            string endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            _logger.LogInformation("XOT server {Name} accepted connection from {ClientEndpoint}", Name, endpoint);
            AdoptClient(client, endpoint, _cancellationTokenSource?.Token ?? CancellationToken.None);
        }

        /// <summary>
        /// Takes over one accepted TCP connection: checks the limit, wires the events and starts its
        /// reader thread. Used by the own accept loop and by the shared listener.
        /// </summary>
        /// <returns>False when the connection was refused (limit reached).</returns>
        private bool AdoptClient(TcpClient tcpClient, string clientEndpoint, CancellationToken cancellationToken)
        {
            // Check connection limit
            lock (_clientLock)
            {
                if (_clientConnections.Count >= _config.MaxConnections)
                {
                    _logger.LogWarning("XOT server {Name} connection limit reached ({Max}), rejecting {ClientEndpoint}",
                                     Name, _config.MaxConnections, clientEndpoint);
                    tcpClient.Close();
                    return false;
                }
            }

            // Create connection handler
            var connection = new XotServerConnection(_logger, tcpClient, clientEndpoint);
            connection.ConnectionClosed += OnClientConnectionClosed;
            connection.DataReceived += OnClientDataReceived;
            connection.PacketSent += OnClientPacketSent;

            lock (_clientLock)
            {
                _clientConnections[clientEndpoint] = connection;
            }

            _statistics.LastConnectedUtc = DateTime.UtcNow;
            _logger.LogInformation("XOT server {Name}: connection from {ClientEndpoint} is up", Name, clientEndpoint);

            // Start handling this connection
            new Thread(() => connection.HandleConnectionLoop(cancellationToken)) { IsBackground = true }.Start();
            return true;
        }

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

        /// <summary>
        /// Handle client connection closed
        /// </summary>
        private void OnClientConnectionClosed(object? sender, string clientEndpoint)
        {
            lock (_clientLock)
            {
                if (_clientConnections.Remove(clientEndpoint))
                {
                    if (_status == TransportStatus.Active || _status == TransportStatus.Connected)
                    {
                        _statistics.Disconnects++;
                    }

                    _logger.LogInformation("XOT server {Name} client connection closed: {ClientEndpoint}", Name, clientEndpoint);
                }
            }
        }

        /// <summary>
        /// Counts the bytes of a packet written to a client connection. Until 2026-11-12 only the bytes
        /// received were counted, so the Status window showed 0 bytes sent for an XOT listener that had
        /// answered thousands of packets (found by a burst test). The lock is there because several
        /// client connections write at the same time and a plain += would lose counts.
        /// </summary>
        private void OnClientPacketSent(object? sender, int length)
        {
            lock (_bytesSentLock)
            {
                _statistics.BytesTransmitted += length;
            }
        }

        private readonly object _bytesSentLock = new object();

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
        /// <remarks>
        /// Called by the runtime (ProtocolRuntime.OnEndpointIncomingCall), where a call on one of this
        /// server's connections arrives through the connection's own X.25 engine; a refused call is
        /// cleared there with cause 0x0B (access barred).
        /// </remarks>
        /// <param name="x121Address">The called address of the incoming call.</param>
        /// <returns>True when the address matches a pattern, or no pattern is set.</returns>
        public bool AcceptsX121Address(string x121Address)
        {
            // No patterns configured = accept all
            if (_config.AcceptedX121Ranges.Count == 0)
                return true;

            for (int i = 0; i < _config.AcceptedX121Ranges.Count; i++)
            {
                string pattern = _config.AcceptedX121Ranges[i];
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

        /// <summary>
        /// When set, the server does not open its own listening socket: it joins the shared listener
        /// for its port, so several XOT entries (server and peer) can use the same port. It then
        /// takes every connection no other entry claims by source address, as it always did. When null
        /// (the default) the server opens its own socket, as before.
        /// </summary>
        public XotListenerRegistry? Listeners { get; set; }
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

        /// <summary>
        /// Creates a handler for one accepted client connection.
        /// </summary>
        /// <param name="logger">
        /// Diagnostics logger.
        /// </param>
        /// <param name="tcpClient">
        /// The accepted TCP client.
        /// </param>
        /// <param name="clientEndpoint">
        /// The remote endpoint text, used in log messages.
        /// </param>
        public XotServerConnection(ILogger logger, TcpClient tcpClient, string clientEndpoint)
        {
            _logger = logger;
            _tcpClient = tcpClient;
            _clientEndpoint = clientEndpoint;
            _stream = tcpClient.GetStream();
        }

        /// <summary>
        /// Raised when the connection closes; the argument is the client endpoint text.
        /// </summary>
        public event EventHandler<string>? ConnectionClosed;
        /// <summary>
        /// Raised when data arrives on the connection.
        /// </summary>
        public event EventHandler<TransportDataEventArgs>? DataReceived;
        /// <summary>
        /// Raised after a packet was written to the connection; the argument is the packet length in bytes.
        /// </summary>
        public event EventHandler<int>? PacketSent;

        /// <summary>
        /// Handle XOT packets from this client connection
        /// </summary>
        public void HandleConnectionLoop(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Started handling XOT connection from {ClientEndpoint}", _clientEndpoint);

            try
            {
                var buffer = new byte[4100];

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

                    // A wrong header ends the connection. RFC 1613 has no flag or marker to find
                    // the next frame boundary with, so once these four octets are not a header,
                    // nothing after them is at a known offset. Both of these used to carry on round
                    // the loop and read the next four bytes as another header - and in the
                    // over-length case those four bytes were the start of a payload that had never
                    // been consumed, so every packet after it was rubbish.
                    if (version != 0)
                    {
                        _logger.LogError("XOT version 0x{Version:X4} from {ClientEndpoint}, expected 0x0000 - closing the connection",
                            version, _clientEndpoint);
                        break;
                    }

                    if (length > 4100 || length < 3)
                    {
                        _logger.LogError("XOT length {Length} from {ClientEndpoint} is not a legal X.25 packet length (3..4100) - closing the connection",
                            length, _clientEndpoint);
                        break;
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
            catch (Exception ex) when (IsPeerGone(ex))
            {
                // The far end went away without a clean TCP close (a program that exits while packets are
                // still on the way makes the network stack reset the connection). That is what a peer
                // leaving looks like, not a fault of this program: it was logged as an Error with a stack
                // trace each time a command-line tool finished (found by a burst test on 2026-11-12).
                _logger.LogWarning("XOT connection from {ClientEndpoint} was reset by the far end: {Reason}", _clientEndpoint, ex.GetBaseException().Message);
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
                PacketSent?.Invoke(this, x25Packet.Length);
                return true;
            }
            catch (Exception ex) when (IsPeerGone(ex))
            {
                _logger.LogWarning("XOT packet not sent to {ClientEndpoint}: the far end has reset the connection ({Reason})", _clientEndpoint, ex.GetBaseException().Message);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending XOT packet to {ClientEndpoint}", _clientEndpoint);
                return false;
            }
        }

        /// <summary>
        /// True when the exception is the far end resetting or dropping the TCP connection.
        /// </summary>
        private static bool IsPeerGone(Exception ex)
        {
            Exception inner = ex.GetBaseException();
            if (inner is System.Net.Sockets.SocketException socket)
            {
                return socket.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset
                    || socket.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionAborted
                    || socket.SocketErrorCode == System.Net.Sockets.SocketError.Shutdown;
            }

            return false;
        }

        /// <summary>
        /// Closes the network stream and the TCP client.
        /// </summary>
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
