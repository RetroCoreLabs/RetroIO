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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Transport
{
    /// <summary>
    /// Configuration of one XOT peer: dials out when a call is routed to it and accepts connections
    /// from the same peer.
    /// </summary>
    public class XotPeerConfig
    {
        /// <summary>Gets or sets the unique name of the entry.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the peer's IP address (or host name): where we dial, and which source address is accepted as this peer.</summary>
        public string? PeerAddress { get; set; }

        /// <summary>Gets or sets the peer's TCP port for dialling out (RFC 1613 section 5: 1998).</summary>
        public int PeerPort { get; set; } = 1998;

        /// <summary>Gets or sets the local address to listen on.</summary>
        public string ListenAddress { get; set; } = "0.0.0.0";

        /// <summary>Gets or sets the local TCP port to listen on.</summary>
        public int ListenPort { get; set; } = 1998;

        /// <summary>Gets or sets the time to wait for a dialled connection, in milliseconds.</summary>
        public int ConnectTimeoutMs { get; set; } = 5000;

        /// <summary>Gets or sets whether the TCP keep-alive option is switched on for the connections.</summary>
        public bool TcpKeepalive { get; set; } = true;

        /// <summary>
        /// Gets or sets the most connections open at the same time, dialled and accepted together: one
        /// per X.25 channel of the line, as on x25emu's XOT port (tools/x25switch.c, conn_max). The
        /// runtime sets it from <c>XotInterfaceConfig.LciCount</c>. A connection that arrives while all
        /// are taken is not accepted (or, on a shared port where another entry had room, is held open and
        /// unread) until a connection closes; it is never accepted and then closed.
        /// </summary>
        public int MaxConnections { get; set; } = 16;

        /// <summary>Gets or sets whether connections from addresses no entry lists are refused.</summary>
        public bool AllowOnlyListedPeers { get; set; }

        /// <summary>Gets or sets the first logical channel number for calls we place (dialled connections).</summary>
        public ushort LciBase { get; set; } = 1;

        /// <summary>Gets or sets how many logical channel numbers we may use on a dialled connection.</summary>
        public ushort LciCount { get; set; } = 16;

        /// <summary>Gets or sets the listener registry to share a port through; null gives the entry a private one.</summary>
        public XotListenerRegistry? Listeners { get; set; }

        /// <summary>
        /// Gets or sets whether the entry only dials: no listening socket is opened and nothing is accepted. This is how the
        /// old "Client" entry runs: one new TCP connection for each call (RFC 1613 section 5).
        /// </summary>
        public bool DialOnly { get; set; }
    }

    /// <summary>
    /// What one unlisted source address did on a peer's port.
    /// </summary>
    public sealed class XotUnlistedPeerReport
    {
        /// <summary>Gets the source address.</summary>
        public string Address { get; internal set; } = string.Empty;

        /// <summary>Gets how many connections it made since the entry started.</summary>
        public int Accepted { get; internal set; }

        /// <summary>Gets how many of its connections are open now.</summary>
        public int OpenNow { get; internal set; }

        /// <summary>Gets the bytes received from it.</summary>
        public long BytesIn { get; internal set; }

        /// <summary>Gets the bytes sent to it.</summary>
        public long BytesOut { get; internal set; }

        /// <summary>Gets when it last connected, or null.</summary>
        public DateTime? LastAcceptedUtc { get; internal set; }

        /// <summary>Gets how many of its connections ended while a call was still up.</summary>
        public int Drops { get; internal set; }
    }

    /// <summary>
    /// A snapshot of one XOT peer, with the dialling direction and the accepting direction counted
    /// apart.
    /// </summary>
    public sealed class XotPeerReport
    {
        /// <summary>Gets the entry name.</summary>
        public string Name { get; internal set; } = string.Empty;

        /// <summary>Gets the peer address we dial and match, as text; empty if none.</summary>
        public string PeerAddress { get; internal set; } = string.Empty;

        /// <summary>Gets the peer port we dial.</summary>
        public int PeerPort { get; internal set; }

        /// <summary>Gets the local port we listen on.</summary>
        public int ListenPort { get; internal set; }

        /// <summary>Gets how many times a call tried to dial out.</summary>
        public int DialAttempts { get; internal set; }

        /// <summary>Gets how many dials failed.</summary>
        public int DialFailures { get; internal set; }

        /// <summary>Gets how many connections we opened since the entry started.</summary>
        public int DialedOpened { get; internal set; }

        /// <summary>Gets how many dialled connections are open now.</summary>
        public int DialedOpenNow { get; internal set; }

        /// <summary>Gets how many dialled connections ended while a call was still up.</summary>
        public int DialDrops { get; internal set; }

        /// <summary>Gets why the last dial failed; empty if none has.</summary>
        public string LastDialError { get; internal set; } = string.Empty;

        /// <summary>Gets when a dial last worked, or null.</summary>
        public DateTime? LastDialConnectedUtc { get; internal set; }

        /// <summary>Gets when a dial last failed, or null.</summary>
        public DateTime? LastDialFailureUtc { get; internal set; }

        /// <summary>Gets the bytes received on dialled connections.</summary>
        public long DialBytesIn { get; internal set; }

        /// <summary>Gets the bytes sent on dialled connections.</summary>
        public long DialBytesOut { get; internal set; }

        /// <summary>Gets how many connections the peer made to us.</summary>
        public int Accepted { get; internal set; }

        /// <summary>Gets how many accepted connections are open now.</summary>
        public int AcceptedOpenNow { get; internal set; }

        /// <summary>Gets how many accepted connections ended while a call was still up.</summary>
        public int AcceptDrops { get; internal set; }

        /// <summary>Gets when a connection was last accepted, or null.</summary>
        public DateTime? LastAcceptedUtc { get; internal set; }

        /// <summary>Gets the bytes received on accepted connections.</summary>
        public long AcceptBytesIn { get; internal set; }

        /// <summary>Gets the bytes sent on accepted connections.</summary>
        public long AcceptBytesOut { get; internal set; }

        /// <summary>Gets how many Call Request packets came in on accepted connections.</summary>
        public int CallsReceived { get; internal set; }

        /// <summary>Gets how many calls we placed (a Call Request went out on a dialled connection).</summary>
        public int CallsPlaced { get; internal set; }

        /// <summary>Gets how many connections from unlisted addresses were refused.</summary>
        public int RefusedUnlisted { get; internal set; }

        /// <summary>Gets one entry per unlisted source address that connected.</summary>
        public List<XotUnlistedPeerReport> Unlisted { get; } = new List<XotUnlistedPeerReport>();
    }

    /// <summary>
    /// One XOT TCP connection of a peer entry, dialled by us or accepted from the peer. It carries one
    /// virtual circuit, as RFC 1613 section 5 wants.
    /// </summary>
    public sealed class XotPeerLink
    {
        private readonly ILogger _logger;
        private readonly XotPeerTransport _owner;
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly object _sendLock = new object();
        private volatile bool _closedByUs;
        private int _closedRaised;

        internal XotPeerLink(ILogger logger, XotPeerTransport owner, TcpClient tcp, bool dialed, IPAddress? remote, bool listed)
        {
            _logger = logger;
            _owner = owner;
            _tcp = tcp;
            _stream = tcp.GetStream();
            Dialed = dialed;
            RemoteAddress = remote;
            Listed = listed;
            RemoteText = tcp.Client.RemoteEndPoint?.ToString() ?? "unknown";
            OpenedUtc = DateTime.UtcNow;
        }

        /// <summary>Gets the entry this connection belongs to.</summary>
        public XotPeerTransport Owner => _owner;

        /// <summary>Gets whether we opened this connection (true) or the peer did (false).</summary>
        public bool Dialed { get; }

        /// <summary>Gets whether the source address is listed as this entry's peer. Always true for a dialled connection.</summary>
        public bool Listed { get; }

        /// <summary>Gets the peer's address.</summary>
        public IPAddress? RemoteAddress { get; }

        /// <summary>Gets the peer's address and port as text.</summary>
        public string RemoteText { get; }

        /// <summary>Gets when the connection was made.</summary>
        public DateTime OpenedUtc { get; }

        /// <summary>
        /// Gets how many circuits are up on the connection, worked out from the Call Request and
        /// Clear Confirmation packets that pass. It is how a close is told apart: a connection that ends
        /// with a circuit still up was dropped; one that ends with none was closed after the call.
        /// </summary>
        public int OpenCircuits { get; internal set; }

        /// <summary>Gets the bytes received (X.25 packet bytes, without the 4-byte XOT header).</summary>
        public long BytesIn { get; internal set; }

        /// <summary>Gets the bytes sent (X.25 packet bytes, without the 4-byte XOT header).</summary>
        public long BytesOut { get; internal set; }

        /// <summary>Gets whether this side closed the connection.</summary>
        internal bool ClosedByUs => _closedByUs;

        internal event Action<XotPeerLink, byte[]>? PacketReceived;
        internal event Action<XotPeerLink>? Closed;

        /// <summary>
        /// Writes one X.25 packet to the connection inside an XOT frame (RFC 1613 section 4: 2 octets
        /// version 0, 2 octets length, then the packet), as one write under a lock so two threads
        /// cannot interleave their frames.
        /// </summary>
        /// <param name="x25Packet">The packet, without the XOT header.</param>
        /// <returns>True when the frame was written.</returns>
        public bool SendPacket(byte[] x25Packet)
        {
            byte[] frame = new byte[4 + x25Packet.Length];
            frame[2] = (byte)((x25Packet.Length >> 8) & 0xFF);
            frame[3] = (byte)(x25Packet.Length & 0xFF);
            Buffer.BlockCopy(x25Packet, 0, frame, 4, x25Packet.Length);

            try
            {
                lock (_sendLock)
                {
                    if (_closedByUs || !_tcp.Connected)
                    {
                        return false;
                    }

                    _stream.Write(frame, 0, frame.Length);
                    _stream.Flush();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("XOT peer {Name}: packet not sent to {Remote}: {Reason}", _owner.Name, RemoteText, ex.GetBaseException().Message);
                return false;
            }

            _owner.NoteSent(this, x25Packet);
            return true;
        }

        internal void StartReceiving()
        {
            new Thread(ReceiveLoop) { IsBackground = true, Name = "XotPeer:" + _owner.Name }.Start();
        }

        private void ReceiveLoop()
        {
            try
            {
                while (!_closedByUs)
                {
                    byte[]? header = ReadExact(4);
                    if (header == null)
                    {
                        break;
                    }

                    int version = (header[0] << 8) | header[1];
                    int length = (header[2] << 8) | header[3];

                    // A wrong header ends the connection: RFC 1613 has no marker to find the next
                    // frame with, so after a bad header nothing is at a known place.
                    if (version != 0)
                    {
                        _logger.LogError("XOT peer {Name}: version 0x{Version:X4} from {Remote}, expected 0x0000 - closing the connection", _owner.Name, version, RemoteText);
                        break;
                    }

                    if (length > 4100 || length < 3)
                    {
                        _logger.LogError("XOT peer {Name}: length {Length} from {Remote} is not a legal X.25 packet length (3..4100) - closing the connection", _owner.Name, length, RemoteText);
                        break;
                    }

                    byte[]? packet = ReadExact(length);
                    if (packet == null)
                    {
                        break;
                    }

                    PacketReceived?.Invoke(this, packet);
                }
            }
            catch (Exception ex)
            {
                if (!_closedByUs)
                {
                    _logger.LogWarning("XOT peer {Name}: connection to {Remote} ended: {Reason}", _owner.Name, RemoteText, ex.GetBaseException().Message);
                }
            }
            finally
            {
                CloseNow();
                if (Interlocked.Exchange(ref _closedRaised, 1) == 0)
                {
                    Closed?.Invoke(this);
                }
            }
        }

        private byte[]? ReadExact(int count)
        {
            byte[] buffer = new byte[count];
            int total = 0;
            while (total < count)
            {
                int read = _stream.Read(buffer, total, count - total);
                if (read == 0)
                {
                    return null;
                }

                total += read;
            }

            return buffer;
        }

        /// <summary>
        /// Closes the socket at once.
        /// </summary>
        public void Close()
        {
            _closedByUs = true;
            CloseNow();
        }

        /// <summary>
        /// Closes the connection after a short wait that lets the packets already written (the Clear
        /// Confirmation, say) leave. A plain close can send a reset instead, and a reset can make the
        /// peer throw away data it has not read yet.
        /// </summary>
        public void CloseGracefully()
        {
            _closedByUs = true;
            try
            {
                _tcp.Client.LingerState = new LingerOption(true, 2);
            }
            catch (Exception)
            {
                // The socket is already gone; the close below is then a no-op.
            }

            CloseNow();
        }

        private void CloseNow()
        {
            try
            {
                _stream.Close();
                _tcp.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "XOT peer {Name}: error closing the connection to {Remote}", _owner.Name, RemoteText);
            }
        }
    }

    /// <summary>
    /// An XOT peer: ONE transport that dials out when a call is routed to it and also accepts
    /// connections from the same peer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RFC 1613 knows no client and no server. Section 5: a separate TCP connection is used for each
    /// X.25 virtual circuit, all to TCP port 1998, and the connection may be kept after the circuit is
    /// cleared. Section 6.1: the XOT end that opened the TCP connection sends the Call Request. So
    /// either end dials when it has a call to make, and either end accepts. There is no handshake, no
    /// peer identity and no security (RFC 1613 does not discuss it).
    /// </para>
    /// <para>
    /// Dialling: every call opens its own TCP connection, when the call needs it. There is no retry
    /// timer: a failure is reported, and the next call tries again. The connection is closed once the
    /// circuit is cleared.
    /// </para>
    /// <para>
    /// Accepting: the entry listens on its local port through a shared <see cref="XotListenerRegistry"/>,
    /// and a connection whose source address is the entry's peer address is handed to it. Cisco does the
    /// same: an incoming XOT call is matched by the source IP address only. A connection from an address
    /// nobody lists is accepted as an unlisted peer, or refused when
    /// <see cref="XotPeerConfig.AllowOnlyListedPeers"/> is set.
    /// </para>
    /// </remarks>
    public sealed class XotPeerTransport : ITransportInterface, IXotListenerClient, IXotRefusalCounter
    {
        private readonly ILogger _logger;
        private readonly XotPeerConfig _config;
        private readonly object _lock = new object();
        private readonly List<XotPeerLink> _links = new List<XotPeerLink>();
        private readonly TransportStatistics _statistics;
        private readonly Dictionary<string, UnlistedRecord> _unlisted = new Dictionary<string, UnlistedRecord>();
        private XotListenerRegistry? _privateRegistry;
        private XotListenerRegistry? _registry;
        private CancellationTokenSource? _cts;
        private IPAddress? _peerIp;
        private TransportStatus _status = TransportStatus.Stopped;

        // Counters, all under _lock.
        private int _dialAttempts;
        private int _dialFailures;
        private int _dialedOpened;
        private int _dialDrops;
        private string _lastDialError = string.Empty;
        private DateTime? _lastDialConnected;
        private DateTime? _lastDialFailure;
        private long _dialBytesIn;
        private long _dialBytesOut;
        private int _accepted;
        private int _acceptDrops;
        private DateTime? _lastAccepted;
        private long _acceptBytesIn;
        private long _acceptBytesOut;
        private int _callsReceived;
        private int _callsPlaced;
        private int _refusedUnlisted;

        private sealed class UnlistedRecord
        {
            public int Accepted;
            public int OpenNow;
            public long BytesIn;
            public long BytesOut;
            public int Drops;
            public DateTime? LastAcceptedUtc;
        }

        /// <summary>
        /// Creates the peer transport in the stopped state.
        /// </summary>
        /// <param name="logger">Diagnostics logger.</param>
        /// <param name="config">The peer's configuration.</param>
        public XotPeerTransport(ILogger logger, XotPeerConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _statistics = new TransportStatistics
            {
                TransportName = _config.Name,
                TransportType = TransportType.XOT_Peer,
                CurrentStatus = TransportStatus.Stopped,
                StartTime = DateTime.UtcNow,
            };
        }

        /// <summary>
        /// Installed by the runtime: places the call through the connection's X.25 engine once the TCP
        /// connection is up. Arguments: this transport, the new connection, the call.
        /// </summary>
        public Func<XotPeerTransport, XotPeerLink, X25CallRequest, bool>? PacketLayerPlaceCall { get; set; }

        /// <summary>Gets the entry's configuration.</summary>
        public XotPeerConfig Config => _config;

        /// <inheritdoc />
        public string Name => _config.Name;

        /// <inheritdoc />
        public TransportType Type => TransportType.XOT_Peer;

        /// <inheritdoc />
        public TransportStatus Status => _status;

        // IXotListenerClient
        IPAddress? IXotListenerClient.PeerAddress => _peerIp;

        bool IXotListenerClient.RefuseUnlisted => _config.AllowOnlyListedPeers;

        bool IXotListenerClient.HasFreeSlot
        {
            get
            {
                lock (_lock)
                {
                    return _links.Count < _config.MaxConnections;
                }
            }
        }

        // Connections matched to this entry while every slot was taken: open, unread, adopted oldest
        // first when a connection closes.
        private readonly Queue<(TcpClient Client, IPAddress Remote, bool Listed)> _waiting = new Queue<(TcpClient Client, IPAddress Remote, bool Listed)>();

        /// <inheritdoc />
        public event EventHandler<X25CallRequest>? IncomingCall
        {
            // Never raised, on purpose: the connection's X.25 engine raises the incoming call and
            // answers it (the same reason the XOT client never raises it).
            add { }
            remove { }
        }

        /// <inheritdoc />
        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        /// <inheritdoc />
        public event EventHandler<TransportDataEventArgs>? DataReceived;

        /// <summary>
        /// Raised when a connection ends, so the runtime can drop the X.25 engine it kept for it.
        /// </summary>
        public event EventHandler<XotPeerLink>? ConnectionClosed;

        /// <summary>
        /// Starts the entry: resolves the peer address and joins the listener for the local port.
        /// </summary>
        /// <returns>True when the entry is active afterwards.</returns>
        public bool Start()
        {
            if (_status != TransportStatus.Stopped)
            {
                return _status == TransportStatus.Active;
            }

            try
            {
                SetStatus(TransportStatus.Starting, "Starting XOT peer");
                _peerIp = ResolvePeer(_config.PeerAddress);
                _cts = new CancellationTokenSource();
                if (!_config.DialOnly)
                {
                    _registry = _config.Listeners;
                    if (_registry == null)
                    {
                        _privateRegistry = new XotListenerRegistry(_logger);
                        _registry = _privateRegistry;
                    }

                    _registry.Join(_config.ListenAddress, _config.ListenPort, this);
                }

                _statistics.StartTime = DateTime.UtcNow;
                SetStatus(TransportStatus.Active, "XOT peer ready");
                if (_config.DialOnly)
                {
                    _logger.LogInformation("XOT entry {Name} started: dials {Peer}:{PeerPort}, one TCP connection per call, accepts nothing", Name, _config.PeerAddress, _config.PeerPort);
                }
                else
                {
                    _logger.LogInformation("XOT peer {Name} started: dials {Peer}:{PeerPort} per call, accepts on port {Listen}", Name, _config.PeerAddress, _config.PeerPort, _config.ListenPort);
                }

                return true;
            }
            catch (Exception ex)
            {
                SetStatus(TransportStatus.Error, "Startup error: " + ex.Message);
                _logger.LogError(ex, "Error starting XOT peer {Name}", Name);
                return false;
            }
        }

        private IPAddress? ResolvePeer(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (IPAddress.TryParse(text.Trim(), out IPAddress? ip))
            {
                return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
            }

            IPAddress[] found = Dns.GetHostAddresses(text.Trim());
            for (int i = 0; i < found.Length; i++)
            {
                if (found[i].AddressFamily == AddressFamily.InterNetwork)
                {
                    return found[i];
                }
            }

            return found.Length > 0 ? found[0] : null;
        }

        /// <summary>
        /// Stops the entry: leaves the listener and closes every connection.
        /// </summary>
        public void Stop()
        {
            if (_status == TransportStatus.Stopped || _status == TransportStatus.Stopping)
            {
                return;
            }

            SetStatus(TransportStatus.Stopping, "Stopping XOT peer");
            _cts?.Cancel();
            _registry?.Leave(this);
            _privateRegistry?.Dispose();
            _privateRegistry = null;

            XotPeerLink[] links;
            lock (_lock)
            {
                links = _links.ToArray();
            }

            for (int i = 0; i < links.Length; i++)
            {
                links[i].Close();
            }

            lock (_lock)
            {
                // Connections that were waiting for a slot were never served.
                while (_waiting.Count > 0)
                {
                    _waiting.Dequeue().Client.Close();
                }
            }

            SetStatus(TransportStatus.Stopped, "XOT peer stopped");
        }

        /// <summary>
        /// Opens a new TCP connection to the peer for this call and places the call on it.
        /// </summary>
        /// <param name="callRequest">The call to place.</param>
        /// <returns>True when the packet layer took the call; false when the entry is not active, has no peer address, the dial failed or no packet layer is installed.</returns>
        public bool PlaceCall(X25CallRequest callRequest)
        {
            if (_status != TransportStatus.Active)
            {
                _logger.LogWarning("XOT peer {Name} is not active, cannot place call", Name);
                return false;
            }

            string host = _config.PeerAddress ?? string.Empty;
            lock (_lock)
            {
                _dialAttempts++;
                _statistics.OutboundCallsAttempted++;
            }

            if (host.Length == 0)
            {
                NoteDialFailure("no peer address is configured");
                _logger.LogError("XOT peer {Name} has no peer address configured, cannot place call", Name);
                return false;
            }

            if (PacketLayerPlaceCall == null)
            {
                _logger.LogError("XOT peer {Name}: no packet layer installed to place the call", Name);
                return false;
            }

            // RFC 1613 section 5: a separate TCP connection for each virtual circuit.
            _logger.LogInformation("XOT peer {Name}: opening a TCP connection to {Host}:{Port} for the call to {Called}", Name, host, _config.PeerPort, callRequest.CalledAddress);
            TcpClient tcp = new TcpClient();
            try
            {
                Task<bool> connect = ConnectAsync(tcp, host, _config.PeerPort);
                if (!connect.Wait(_config.ConnectTimeoutMs > 0 ? _config.ConnectTimeoutMs : 5000) || !connect.Result)
                {
                    string reason = connect.IsCompleted && connect.Exception != null
                        ? connect.Exception.GetBaseException().Message
                        : "no answer in " + _config.ConnectTimeoutMs + " ms";
                    tcp.Close();
                    NoteDialFailure(reason);
                    _logger.LogWarning("XOT peer {Name}: could not connect to {Host}:{Port}: {Reason}. No automatic retry: the next try is made by the next call", Name, host, _config.PeerPort, reason);
                    return false;
                }
            }
            catch (Exception ex)
            {
                tcp.Close();
                NoteDialFailure(ex.GetBaseException().Message);
                _logger.LogWarning("XOT peer {Name}: could not connect to {Host}:{Port}: {Reason}. No automatic retry: the next try is made by the next call", Name, host, _config.PeerPort, ex.GetBaseException().Message);
                return false;
            }

            ApplyKeepalive(tcp);
            IPAddress? remote = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address;
            XotPeerLink link = new XotPeerLink(_logger, this, tcp, true, remote, true);
            if (!Register(link))
            {
                link.Close();
                return false;
            }

            lock (_lock)
            {
                _dialedOpened++;
                _lastDialConnected = DateTime.UtcNow;
                _statistics.LastConnectedUtc = _lastDialConnected;
            }

            link.StartReceiving();

            bool placed;
            try
            {
                placed = PacketLayerPlaceCall(this, link, callRequest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error placing XOT call via {Name}", Name);
                placed = false;
            }

            if (!placed)
            {
                link.Close();
                return false;
            }

            lock (_lock)
            {
                _callsPlaced++;
                _statistics.OutboundCallsSucceeded++;
            }

            return true;
        }

        private static async Task<bool> ConnectAsync(TcpClient tcp, string host, int port)
        {
            await tcp.ConnectAsync(host, port).ConfigureAwait(false);
            return true;
        }

        private void ApplyKeepalive(TcpClient tcp)
        {
            if (!_config.TcpKeepalive)
            {
                return;
            }

            try
            {
                tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "XOT peer {Name}: could not switch on TCP keep-alive", Name);
            }
        }

        private void NoteDialFailure(string reason)
        {
            lock (_lock)
            {
                _dialFailures++;
                _lastDialError = reason;
                _lastDialFailure = DateTime.UtcNow;
                _statistics.ConnectionErrors++;
                _statistics.LastError = reason;
                _statistics.LastFailureUtc = _lastDialFailure;
            }
        }

        /// <summary>
        /// Called by the listener with a connection from the peer (or from an unlisted address).
        /// </summary>
        void IXotListenerClient.OnConnection(TcpClient client, IPAddress remote, bool listed)
        {
            if (_status != TransportStatus.Active)
            {
                client.Close();
                return;
            }

            lock (_lock)
            {
                if (_links.Count >= _config.MaxConnections)
                {
                    _waiting.Enqueue((client, remote, listed));
                    _logger.LogInformation("XOT peer {Name}: all {Count} connection slots are taken; {Remote} waits for one", Name, _config.MaxConnections, remote);
                    return;
                }
            }

            ApplyKeepalive(client);
            XotPeerLink link = new XotPeerLink(_logger, this, client, false, remote, listed);
            if (!Register(link))
            {
                link.Close();
                return;
            }

            lock (_lock)
            {
                _accepted++;
                _lastAccepted = DateTime.UtcNow;
                _statistics.LastConnectedUtc = _lastAccepted;
                if (!listed)
                {
                    UnlistedRecord rec = UnlistedFor(remote);
                    rec.Accepted++;
                    rec.OpenNow++;
                    rec.LastAcceptedUtc = _lastAccepted;
                }
            }

            if (listed)
            {
                _logger.LogInformation("XOT peer {Name}: accepted a connection from {Remote}", Name, link.RemoteText);
            }
            else
            {
                _logger.LogInformation("XOT peer {Name}: accepted a connection from {Remote}, which is not a listed peer (default settings)", Name, link.RemoteText);
            }

            link.StartReceiving();
        }

        /// <inheritdoc />
        public void NoteRefused(IPAddress remote)
        {
            lock (_lock)
            {
                _refusedUnlisted++;
                _statistics.ConnectionErrors++;
            }
        }

        private UnlistedRecord UnlistedFor(IPAddress remote)
        {
            string key = remote.ToString();
            if (!_unlisted.TryGetValue(key, out UnlistedRecord? rec))
            {
                rec = new UnlistedRecord();
                _unlisted[key] = rec;
            }

            return rec;
        }

        private bool Register(XotPeerLink link)
        {
            lock (_lock)
            {
                if (_links.Count >= _config.MaxConnections)
                {
                    _logger.LogWarning("XOT peer {Name}: connection limit {Max} reached, {Remote} refused", Name, _config.MaxConnections, link.RemoteText);
                    return false;
                }

                _links.Add(link);
            }

            link.PacketReceived += OnLinkPacket;
            link.Closed += OnLinkClosed;
            return true;
        }

        private void OnLinkPacket(XotPeerLink link, byte[] packet)
        {
            lock (_lock)
            {
                link.BytesIn += packet.Length;
                _statistics.BytesReceived += packet.Length;
                if (link.Dialed)
                {
                    _dialBytesIn += packet.Length;
                }
                else
                {
                    _acceptBytesIn += packet.Length;
                    if (!link.Listed && link.RemoteAddress != null)
                    {
                        UnlistedFor(link.RemoteAddress).BytesIn += packet.Length;
                    }
                }

                byte pti = packet[2];
                if (pti == 0x0B)
                {
                    link.OpenCircuits++;
                    if (!link.Dialed)
                    {
                        _callsReceived++;
                        _statistics.InboundCallsReceived++;
                    }
                }
                else if (pti == 0x17 && link.OpenCircuits > 0)
                {
                    link.OpenCircuits--;
                }
            }

            DataReceived?.Invoke(this, new TransportDataEventArgs
            {
                Data = packet,
                Length = packet.Length,
                Transport = this,
                Connection = link,
            });
        }

        internal void NoteSent(XotPeerLink link, byte[] packet)
        {
            lock (_lock)
            {
                link.BytesOut += packet.Length;
                _statistics.BytesTransmitted += packet.Length;
                if (link.Dialed)
                {
                    _dialBytesOut += packet.Length;
                }
                else
                {
                    _acceptBytesOut += packet.Length;
                    if (!link.Listed && link.RemoteAddress != null)
                    {
                        UnlistedFor(link.RemoteAddress).BytesOut += packet.Length;
                    }
                }

                if (packet.Length >= 3)
                {
                    byte pti = packet[2];
                    if (pti == 0x0B)
                    {
                        link.OpenCircuits++;
                    }
                    else if (pti == 0x17 && link.OpenCircuits > 0)
                    {
                        link.OpenCircuits--;
                    }
                    else if (pti == 0x0F && !link.Dialed)
                    {
                        _statistics.InboundCallsAccepted++;
                    }
                }
            }
        }

        private void OnLinkClosed(XotPeerLink link)
        {
            lock (_lock)
            {
                _links.Remove(link);
                bool dropped = !link.ClosedByUs && link.OpenCircuits > 0;
                if (dropped)
                {
                    _statistics.Disconnects++;
                    if (link.Dialed)
                    {
                        _dialDrops++;
                    }
                    else
                    {
                        _acceptDrops++;
                    }
                }

                if (!link.Dialed && !link.Listed && link.RemoteAddress != null)
                {
                    UnlistedRecord rec = UnlistedFor(link.RemoteAddress);
                    if (rec.OpenNow > 0)
                    {
                        rec.OpenNow--;
                    }

                    if (dropped)
                    {
                        rec.Drops++;
                    }
                }

                if (dropped && (_status == TransportStatus.Active))
                {
                    _logger.LogWarning("XOT peer {Name}: connection {Remote} ended while a call was still up", Name, link.RemoteText);
                }
            }

            ConnectionClosed?.Invoke(this, link);

            // A slot is free: a connection waiting for one gets it, and the shared socket is told there
            // is room so it can accept the next connection from the TCP backlog.
            TcpClient? nextClient = null;
            IPAddress? nextRemote = null;
            bool nextListed = false;
            lock (_lock)
            {
                if (_waiting.Count > 0 && _links.Count < _config.MaxConnections)
                {
                    (nextClient, nextRemote, nextListed) = _waiting.Dequeue();
                }
            }

            if (nextClient != null && nextRemote != null)
            {
                ((IXotListenerClient)this).OnConnection(nextClient, nextRemote, nextListed);
            }

            _registry?.SlotFreed();
        }

        /// <summary>
        /// Takes a snapshot of the entry, dialling and accepting counted apart.
        /// </summary>
        /// <returns>The snapshot.</returns>
        public XotPeerReport GetPeerReport()
        {
            XotPeerReport report = new XotPeerReport
            {
                Name = Name,
                PeerAddress = _config.PeerAddress ?? string.Empty,
                PeerPort = _config.PeerPort,
                ListenPort = _config.ListenPort,
            };

            lock (_lock)
            {
                report.DialAttempts = _dialAttempts;
                report.DialFailures = _dialFailures;
                report.DialedOpened = _dialedOpened;
                report.DialDrops = _dialDrops;
                report.LastDialError = _lastDialError;
                report.LastDialConnectedUtc = _lastDialConnected;
                report.LastDialFailureUtc = _lastDialFailure;
                report.DialBytesIn = _dialBytesIn;
                report.DialBytesOut = _dialBytesOut;
                report.Accepted = _accepted;
                report.AcceptDrops = _acceptDrops;
                report.LastAcceptedUtc = _lastAccepted;
                report.AcceptBytesIn = _acceptBytesIn;
                report.AcceptBytesOut = _acceptBytesOut;
                report.CallsReceived = _callsReceived;
                report.CallsPlaced = _callsPlaced;
                report.RefusedUnlisted = _refusedUnlisted;

                for (int i = 0; i < _links.Count; i++)
                {
                    if (_links[i].Dialed)
                    {
                        report.DialedOpenNow++;
                    }
                    else
                    {
                        report.AcceptedOpenNow++;
                    }
                }

                foreach (KeyValuePair<string, UnlistedRecord> kv in _unlisted)
                {
                    report.Unlisted.Add(new XotUnlistedPeerReport
                    {
                        Address = kv.Key,
                        Accepted = kv.Value.Accepted,
                        OpenNow = kv.Value.OpenNow,
                        BytesIn = kv.Value.BytesIn,
                        BytesOut = kv.Value.BytesOut,
                        Drops = kv.Value.Drops,
                        LastAcceptedUtc = kv.Value.LastAcceptedUtc,
                    });
                }
            }

            return report;
        }

        /// <summary>
        /// Gets the transport statistics (the dialling and accepting counts added together).
        /// </summary>
        /// <returns>The transport's own statistics object.</returns>
        public TransportStatistics GetStatistics()
        {
            lock (_lock)
            {
                _statistics.CurrentStatus = _status;
                _statistics.ActiveConnections = _links.Count;
            }

            return _statistics;
        }

        /// <summary>
        /// Sets the counters back to zero.
        /// </summary>
        public void ResetStatistics()
        {
            lock (_lock)
            {
                _statistics.ResetCounters();
                _dialAttempts = 0;
                _dialFailures = 0;
                _dialedOpened = 0;
                _dialDrops = 0;
                _dialBytesIn = 0;
                _dialBytesOut = 0;
                _accepted = 0;
                _acceptDrops = 0;
                _acceptBytesIn = 0;
                _acceptBytesOut = 0;
                _callsReceived = 0;
                _callsPlaced = 0;
                _refusedUnlisted = 0;
                _unlisted.Clear();
            }
        }

        /// <summary>
        /// Not supported: the engine of the connection answers the call.
        /// </summary>
        public bool AcceptCall(X25CallRequest callRequest)
        {
            throw new NotSupportedException("XotPeerTransport.AcceptCall is not used: the connection's X.25 engine answers the call.");
        }

        /// <summary>
        /// Not supported: the engine of the connection clears the call.
        /// </summary>
        public bool RejectCall(X25CallRequest callRequest, byte cause)
        {
            throw new NotSupportedException("XotPeerTransport.RejectCall is not used: the connection's X.25 engine clears the call.");
        }

        /// <summary>
        /// True when the connection belongs to this entry.
        /// </summary>
        public bool Owns(XotPeerLink link)
        {
            return ReferenceEquals(link.Owner, this);
        }

        private void SetStatus(TransportStatus newStatus, string reason)
        {
            TransportStatus old = _status;
            _status = newStatus;
            _statistics.CurrentStatus = newStatus;
            _logger.LogDebug("XOT peer {Name} status changed: {Old} -> {New} ({Reason})", Name, old, newStatus, reason);
            StatusChanged?.Invoke(this, new TransportStatusEventArgs { OldStatus = old, NewStatus = newStatus, Reason = reason });
        }
    }
}
