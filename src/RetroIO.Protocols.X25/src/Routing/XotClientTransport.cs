//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Transport
{
    /// <summary>
    /// XOT Client transport interface for outbound X.25 over TCP connections
    /// Implements RFC 1613 (X.25 over TCP) for outbound call placement
    /// </summary>
    /// <remarks>
    /// RFC 1613 section 5: "A separate TCP connection MUST be used for each X.25 virtual circuit." Every call this
    /// transport carries gets a TCP connection of its own (an <see cref="XotClientCircuit"/>, returned by
    /// <see cref="ConnectToXotGateway"/>), packets are sent on that connection only, and the connection is closed when
    /// its call ends (a Clear Confirmation crosses it, either way). A closed connection is never used again. Until
    /// 2026-11-18 this transport kept ONE connection per peer address, put every call on it and kept it after the calls
    /// ended (the same fault the switch port and the client had, fixed in RetroIO 9f755dd); the real Cisco router clears
    /// the first call when a second Call Request arrives on a connection that carries one (cause 19, diagnostic 23).
    /// </remarks>
    public class XotClientTransport : ITransportInterface
    {
        private readonly ILogger _logger;
        private readonly XotClientConfig _config;
        private XotInterfaceConfig? _interfaceConfig;
        private readonly object _connectionLock = new object();
        private TransportStatus _status = TransportStatus.Stopped;
        private readonly TransportStatistics _statistics;

        // Every open connection, one per circuit, guarded by _connectionLock. A list, not a table keyed by address: two
        // circuits to one peer are two entries.
        private readonly List<XotClientCircuit> _connections = new List<XotClientCircuit>();

        /// <summary>
        /// Creates the XOT client transport in the stopped state.
        /// </summary>
        /// <param name="logger">
        /// Diagnostics logger.
        /// </param>
        /// <param name="config">
        /// The client transport configuration.
        /// </param>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="logger"/> or <paramref name="config"/> is null.
        /// </exception>
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
        /// The interface configuration this transport was created from, if any.
        /// </summary>
        public XotInterfaceConfig? InterfaceConfig => _interfaceConfig;

        /// <summary>
        /// Sets the interface configuration that supplies the remote IP address and port.
        /// </summary>
        /// <param name="interfaceConfig">
        /// The interface configuration.
        /// </param>
        public void SetInterfaceConfig(XotInterfaceConfig interfaceConfig)
        {
            _interfaceConfig = interfaceConfig;
        }

        /// <summary>
        /// Get the configured remote IP address for this client transport
        /// </summary>
        public string? GetRemoteIPAddress() => _interfaceConfig?.RemoteIPAddress;

        #region ITransportInterface Implementation

        /// <summary>
        /// Gets the unique name of this transport interface, taken from the configuration.
        /// </summary>
        public string Name => _config.Name;
        /// <summary>
        /// Gets the type of transport, always <see cref="TransportType.XOT_Client"/>.
        /// </summary>
        public TransportType Type => TransportType.XOT_Client;
        /// <summary>
        /// Gets the current status of the transport.
        /// </summary>
        public TransportStatus Status => _status;

        /// <summary>
        /// Installed by the user of the transport: places the call through an X.25 engine on the new TCP connection
        /// <see cref="PlaceCall"/> opened for it (one connection per circuit, RFC 1613 section 5). The transport does not
        /// build packets or wait for a Restart Confirmation itself - the engine does both (X.25 4.1.1: no call set-up in
        /// r2). The engine sends with <see cref="SendX25Packet(XotClientCircuit, byte[])"/> on that connection.
        /// </summary>
        public Func<XotClientTransport, XotClientCircuit, X25CallRequest, bool>? PacketLayerPlaceCall { get; set; }

        /// <summary>
        /// Not usable on this transport, and it says so: a packet belongs to one circuit, and every circuit has its own
        /// TCP connection (RFC 1613 section 5), so a packet with no connection named has nowhere to go. Use
        /// <see cref="SendX25Packet(XotClientCircuit, byte[])"/>.
        /// </summary>
        /// <param name="x25Packet">
        /// The packet, which is not sent.
        /// </param>
        /// <returns>
        /// Always false.
        /// </returns>
        public bool SendPacket(byte[] x25Packet)
        {
            _logger.LogError("XOT client transport {Name}: a packet was sent with no connection named; each circuit has its own connection (RFC 1613 section 5), so it is not sent", Name);
            return false;
        }

        /// <summary>
        /// Connects to the configured remote XOT gateway and places the call through the installed packet layer.
        /// </summary>
        /// <param name="callRequest">
        /// The call request to place.
        /// </param>
        /// <returns>
        /// True when the packet layer accepted the call. False when the transport is not active, no remote IP is configured, the connection failed, no packet layer is installed, or an error occurred.
        /// </returns>
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

                if (PacketLayerPlaceCall == null)
                {
                    _logger.LogError("XOT client transport {Name}: no packet layer installed to place the call", Name);
                    return false;
                }

                // Step 1: a new TCP connection for this call (RFC 1613 section 5), never one another call has used.
                XotClientCircuit? circuit = ConnectToXotGateway(remoteIP, remotePort);
                if (circuit == null)
                {
                    _logger.LogError("Failed to connect to XOT gateway {RemoteIP}:{RemotePort}", remoteIP, remotePort);
                    return false;
                }

                // Step 2: the engine restarts the packet layer on that connection and sends the Call Request.
                var sent = PacketLayerPlaceCall(this, circuit, callRequest);
                if (!sent)
                {
                    // The call never started, so its connection has no call to carry.
                    CloseCircuit(circuit);
                }
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

        /// <summary>
        /// Does nothing except log a warning, because a client transport does not accept incoming calls.
        /// </summary>
        /// <param name="callRequest">
        /// The call request, which is ignored.
        /// </param>
        /// <returns>
        /// Always false.
        /// </returns>
        public bool AcceptCall(X25CallRequest callRequest)
        {
            // XOT Client transport typically doesn't accept incoming calls
            // Incoming calls are handled by XOT Server transport
            _logger.LogWarning("XOT client transport {Name} cannot accept incoming calls", Name);
            return false;
        }

        /// <summary>
        /// Does nothing except log a warning, because a client transport does not reject incoming calls.
        /// </summary>
        /// <param name="callRequest">
        /// The call request, which is ignored.
        /// </param>
        /// <param name="cause">
        /// The clear cause code, which is ignored.
        /// </param>
        /// <returns>
        /// Always false.
        /// </returns>
        public bool RejectCall(X25CallRequest callRequest, byte cause)
        {
            // XOT Client transport typically doesn't reject incoming calls
            _logger.LogWarning("XOT client transport {Name} cannot reject incoming calls", Name);
            return false;
        }

        /// <summary>
        /// Moves the transport to the active state; TCP connections are made later when a call is placed.
        /// </summary>
        /// <returns>
        /// True when the transport is active afterwards, false when starting failed.
        /// </returns>
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

        /// <summary>
        /// Closes all connections and moves the transport to the stopped state.
        /// </summary>
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

                // Close every connection, one per circuit. Taken out of the list first, so the close events they raise
                // find nothing to remove and are not counted as the peer dropping them.
                XotClientCircuit[] open;
                lock (_connectionLock)
                {
                    open = _connections.ToArray();
                    _connections.Clear();
                }

                for (int i = 0; i < open.Length; i++)
                {
                    open[i].Close();
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

        /// <summary>
        /// Gets the transport's statistics, with the current status and the number of open
        /// connections brought up to date first.
        /// </summary>
        /// <returns>
        /// The transport's own statistics object, not a copy.
        /// </returns>
        public TransportStatistics GetStatistics()
        {
            _statistics.CurrentStatus = _status;
            lock (_connectionLock)
            {
                _statistics.ActiveConnections = _connections.Count;
            }

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
        /// Required by the interface for incoming calls. A client transport never raises it, on purpose.
        /// </summary>
        /// <remarks>
        /// Every packet from a dialled connection, a Call Request included, is raised through
        /// <see cref="DataReceived"/> and decoded by the X.25 engine the runtime keeps for that
        /// connection; the engine raises the incoming call and answers it. Raising this event as well
        /// would put a second protocol handler on the same connection, which is what the 2026-09-06
        /// migration removed from the XOT server: the router would route the call a second time and
        /// the runtime would log that the call cannot be answered, while the engine answers it.
        /// </remarks>
        public event EventHandler<X25CallRequest>? IncomingCall;
#pragma warning restore CS0067
        /// <summary>
        /// Raised when the transport status changes, with the old status, the new status and the
        /// reason.
        /// </summary>
        public event EventHandler<TransportStatusEventArgs>? StatusChanged;
        /// <summary>
        /// Raised for every X.25 packet received on one of the transport's connections, with the
        /// transport set on the event data. Used for bridge pass-through mode.
        /// </summary>
        public event EventHandler<TransportDataEventArgs>? DataReceived;

        #endregion

        #region XOT-Specific Methods

        /// <summary>
        /// Opens a NEW TCP connection to an XOT gateway for one circuit (RFC 1613 section 5: a separate TCP connection
        /// for each X.25 virtual circuit). Every call gives a connection of its own, also to a peer that already has one;
        /// nothing is shared or reused.
        /// </summary>
        /// <param name="ipAddress">
        /// The gateway's address.
        /// </param>
        /// <param name="port">
        /// The gateway's XOT port (1998 is the port RFC 1613 names).
        /// </param>
        /// <returns>
        /// The connection for the circuit, or null when it could not be opened (counted, with the reason, in the
        /// statistics). There is no automatic retry: the next try is made by the next call.
        /// </returns>
        public XotClientCircuit? ConnectToXotGateway(string ipAddress, int port = 1998)
        {
            try
            {
                _logger.LogInformation("XOT client {Name}: opening a TCP connection to {IpAddress}:{Port} for one circuit", Name, ipAddress, port);

                var connection = new XotClientCircuit(_logger, ipAddress, port);
                connection.DataReceived += OnConnectionDataReceived;
                connection.ConnectionClosed += OnConnectionClosed;

                // Added to the list before it is connected, so a peer that closes it at once is still found and
                // counted, and Stop() can close a connection whose connect is still under way.
                lock (_connectionLock)
                {
                    _connections.Add(connection);
                }

                if (connection.Connect(_config.ConnectTimeout))
                {
                    _statistics.LastConnectedUtc = DateTime.UtcNow;
                    _logger.LogInformation("XOT client {Name}: TCP connection to {IpAddress}:{Port} is up", Name, ipAddress, port);
                    return connection;
                }

                lock (_connectionLock)
                {
                    _connections.Remove(connection);
                }

                _statistics.ConnectionErrors++;
                _statistics.LastError = connection.LastError.Length > 0 ? connection.LastError : "could not connect";
                _statistics.LastFailureUtc = DateTime.UtcNow;
                _logger.LogWarning("XOT client {Name}: could not connect to {IpAddress}:{Port}: {Reason}. No automatic retry: the next try is made when the next call needs this link", Name, ipAddress, port, _statistics.LastError);
                return null;
            }
            catch (Exception ex)
            {
                _statistics.ConnectionErrors++;
                _statistics.LastError = ex.Message;
                _statistics.LastFailureUtc = DateTime.UtcNow;
                _logger.LogError(ex, "Error connecting to XOT gateway {IpAddress}", ipAddress);
                return null;
            }
        }

        /// <summary>
        /// Sends one X.25 packet on the connection of its circuit. When the packet is a Clear Confirmation the call has
        /// ended, and the connection is closed after the packet has gone (RFC 1613 section 5: the connection belongs to
        /// the one circuit).
        /// </summary>
        /// <param name="circuit">
        /// The circuit's connection, from <see cref="ConnectToXotGateway"/>.
        /// </param>
        /// <param name="x25Packet">
        /// The complete X.25 packet.
        /// </param>
        /// <returns>
        /// True when the packet was written; false when the connection is closed or the write failed.
        /// </returns>
        public bool SendX25Packet(XotClientCircuit circuit, byte[] x25Packet)
        {
            ArgumentNullException.ThrowIfNull(circuit);
            ArgumentNullException.ThrowIfNull(x25Packet);

            bool known;
            lock (_connectionLock)
            {
                known = _connections.Contains(circuit);
            }

            if (!known)
            {
                _logger.LogError("XOT client {Name}: the connection to {Address} is closed; its call has ended or it was dropped, and it is never used again", Name, circuit.RemoteAddress);
                return false;
            }

            bool success;
            try
            {
                success = circuit.SendPacket(x25Packet);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending X.25 packet over XOT to {DestinationIp}", circuit.RemoteAddress);
                return false;
            }

            if (success)
            {
                _statistics.BytesTransmitted += x25Packet.Length;
                if (IsClearConfirmation(x25Packet, x25Packet.Length))
                {
                    _logger.LogInformation("XOT client {Name}: the call on the connection to {Address} has ended (Clear Confirmation sent); closing it", Name, circuit.RemoteAddress);
                    CloseCircuit(circuit);
                }
            }

            return success;
        }

        /// <summary>
        /// Closes one circuit's connection, for example when the call was never placed. It is not counted as the peer
        /// dropping it.
        /// </summary>
        /// <param name="circuit">
        /// The circuit's connection.
        /// </param>
        public void CloseCircuit(XotClientCircuit circuit)
        {
            ArgumentNullException.ThrowIfNull(circuit);
            lock (_connectionLock)
            {
                _connections.Remove(circuit);
            }

            circuit.Close();
        }

        /// <summary>
        /// True when the packet is a Clear Confirmation (X.25 Table 5-1: packet type identifier 00010111), the last packet
        /// of a call in either direction.
        /// </summary>
        private static bool IsClearConfirmation(byte[] packet, int length)
        {
            return length >= 3 && packet[2] == 0x17;
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
            XotClientCircuit? circuit = sender as XotClientCircuit;

            if (circuit != null && e.Data != null && e.Data.Length >= 3)
            {
                byte pti = e.Data[2];

                // Handle incoming Restart Request (0xFB) - send back Restart Confirm, on the connection it came in on
                // (the restart is of that circuit's packet layer only: every circuit has its own connection).
                if (pti == 0xFB)
                {
                    var restartConfirm = new byte[] { 0x10, 0x00, 0xFF };
                    SendX25Packet(circuit, restartConfirm);
                    _logger.LogDebug("Sent Restart Confirm in response to switch Restart Request");
                }
            }

            // Set transport reference and the connection (the circuit) and forward to external subscribers
            e.Transport = this;
            e.Connection = circuit;
            DataReceived?.Invoke(this, e);

            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("XOT client {Name} received {Length} bytes", Name, e.Length);

            // A Clear Confirmation from the peer ends the call this connection was opened for: close it, after the
            // subscribers have seen the packet. It is never used for another call (RFC 1613 section 5).
            if (circuit != null && e.Data != null && IsClearConfirmation(e.Data, e.Length))
            {
                _logger.LogInformation("XOT client {Name}: the call on the connection to {Address} has ended (Clear Confirmation received); closing it", Name, circuit.RemoteAddress);
                CloseCircuit(circuit);
            }
        }

        /// <summary>
        /// Handle connection closed event
        /// </summary>
        private void OnConnectionClosed(object? sender, string remoteAddress)
        {
            XotClientCircuit? circuit = sender as XotClientCircuit;
            lock (_connectionLock)
            {
                // Only a connection still in the list was closed from outside; one this transport closed itself (its
                // call ended, or Stop) was taken out first.
                if (circuit != null && _connections.Remove(circuit))
                {
                    // Closed while the transport is running: the peer or the network ended it.
                    if (_status == TransportStatus.Active || _status == TransportStatus.Connected)
                    {
                        _statistics.Disconnects++;
                        _logger.LogWarning("XOT client {Name}: connection to {RemoteAddress} closed by the peer or the network. It is opened again when the next call needs it", Name, remoteAddress);
                    }
                    else
                    {
                        _logger.LogInformation("XOT client {Name} connection to {RemoteAddress} closed", Name, remoteAddress);
                    }
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
    /// One XOT TCP connection to a remote gateway, made by <see cref="XotClientTransport.ConnectToXotGateway"/> for ONE
    /// circuit (RFC 1613 section 5). Packets go through <see cref="XotClientTransport.SendX25Packet(XotClientCircuit, byte[])"/>;
    /// it is closed when its call ends and never used again.
    /// </summary>
    public sealed class XotClientCircuit
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

        internal event EventHandler<TransportDataEventArgs>? DataReceived;
        internal event EventHandler<string>? ConnectionClosed;

        internal XotClientCircuit(ILogger logger, string remoteAddress, int remotePort)
        {
            _logger = logger;
            _remoteAddress = remoteAddress;
            _remotePort = remotePort;
        }

        /// <summary>
        /// Gets the gateway's address.
        /// </summary>
        public string RemoteAddress => _remoteAddress;

        /// <summary>
        /// Gets the gateway's XOT port.
        /// </summary>
        public int RemotePort => _remotePort;

        /// <summary>
        /// Gets the reason the last <see cref="Connect"/> failed, in words; empty if it has not failed.
        /// </summary>
        public string LastError { get; private set; } = string.Empty;

        internal bool Connect(int timeoutMs)
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
                    LastError = "no answer in " + timeoutMs + " ms";
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
                LastError = ex.Message;
                _logger.LogDebug(ex, "Failed to establish XOT connection to {Address}:{Port}", _remoteAddress, _remotePort);
                _tcpClient?.Close();
                return false;
            }
        }

        internal bool SendPacket(byte[] x25Packet)
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

                    // A wrong header ends the connection. RFC 1613 has no flag or marker to find
                    // the next frame boundary with, so once these four octets are not a header,
                    // nothing after them is at a known offset. Both of these used to carry on round
                    // the loop and read the next four bytes as another header - and in the
                    // over-length case those four bytes were the start of a payload that had never
                    // been consumed, so every packet after it was rubbish.
                    if (version != 0)
                    {
                        _logger.LogError("XOT version 0x{Version:X4} from {Address}:{Port}, expected 0x0000 - closing the connection",
                            version, _remoteAddress, _remotePort);
                        break;
                    }

                    if (length > 4100 || length < 3)
                    {
                        _logger.LogError("XOT length {Length} from {Address}:{Port} is not a legal X.25 packet length (3..4100) - closing the connection",
                            length, _remoteAddress, _remotePort);
                        break;
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
                // The socket is closed here, not only announced. Leaving the loop without closing
                // left the connection open with nobody reading it, so a peer that had sent a bad
                // header saw a live connection that would never answer again.
                Close();
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

        internal void Close()
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
