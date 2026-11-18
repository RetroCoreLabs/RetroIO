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
using Microsoft.Extensions.Logging.Abstractions;
using RetroIO.Link.Framing;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.PacketLayer;

namespace RetroIO.Protocols.X25.Switch.Ports
{
    /// <summary>
    /// A switch port that carries X.25 over TCP (XOT, RFC 1613).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first switch port with a real transport under it. Until this existed the switch could
    /// only be driven by <see cref="LoopbackSwitchPort"/>, so nothing outside the process could call
    /// into it and the C emulator's client had nothing to talk to.
    /// </para>
    /// <para>
    /// It is built out of the same two pieces as everything else that speaks X.25 here: one
    /// <see cref="X25Layer"/> for the packet layer and one <see cref="XotFrameCodec"/> for each
    /// connection's framing. Nothing about X.25 is decoded in this file, except the two octets that
    /// carry the logical channel number. The port's job is to own the sockets, to turn the engine's
    /// events into the port contract's events, and to answer a send with what the engine actually did.
    /// </para>
    /// <para><b>A connection for each circuit</b></para>
    /// <para>
    /// RFC 1613 section 5: "A separate TCP connection MUST be
    /// used for each X.25 virtual circuit", and the logical channel number on the wire "has no
    /// significance and has arbitrary values". So a port has many connections, not one. A listening
    /// (DCE) port accepts as many as it has channels. A dialling (DTE) port dials one connection for
    /// each call it places, and keeps one spare connection open so that a peer that can only listen has
    /// a way to place a call to it. The connection a call arrives on is the connection it stays on.
    /// A connection that has carried a call is closed when that call ends, whichever end opened it, and
    /// no call is ever put on it again (the same rule x25emu follows since 2026-11-18). A call going out
    /// of a listening port goes only on a connection that has never carried one; with none, it is refused.
    /// </para>
    /// <para><b>Channel numbers are ours</b></para>
    /// <para>
    /// The number a peer puts on the wire is not trusted to be unique
    /// across connections, or to be inside this port's range. Each connection's circuits are given a
    /// channel number from the port's own range for the engine, and the packet is renumbered on the way
    /// in and again on the way out. When the number the peer used is free and in range it is kept, so
    /// for a peer that behaves nothing is rewritten. A peer that puts several of its own calls on one
    /// connection is still served (each is a circuit of its own), but this port never does that.
    /// </para>
    /// <para>
    /// The range is a limit on how many circuits the port carries at once, not a list of numbers the
    /// peer must use. A connection beyond the number of channels is closed, as the C emulator's switch
    /// does by not accepting it. A call on a connection that already carries circuits, when every
    /// channel is taken, is refused by the packet layer with a Diagnostic.
    /// </para>
    /// <para>
    /// <see cref="ISwitchPort.PeerConnected"/> is true while at least one connection is up. When one
    /// connection is lost only the circuits on it end; the others are not touched.
    /// </para>
    /// <para>
    /// Threading: each connection's reader thread, the tick timer and the switch's own calls all go
    /// through one lock. Events are raised inside it, so a handler must not block or call back into the
    /// same port. That is the same rule <c>LapbStation</c> follows for the link layer.
    /// </para>
    /// <para>
    /// What a monitoring switch needs from it: <see cref="PacketCaptured"/> shows every packet in
    /// both directions as it crossed the wire, <see cref="Counters"/> carries the packet and byte
    /// totals, and <see cref="ConnectAttempts"/>, <see cref="LastConnectedMs"/> and
    /// <see cref="LastDisconnectedMs"/> describe the links. They are counted as the C emulator counts
    /// them, so the two report the same numbers for the same traffic. The optional logger gets one line
    /// for each thing that happens to a connection: listening, dialling, connection up, connection down,
    /// and a port that cannot start.
    /// </para>
    /// </remarks>
    public sealed class XotSwitchPort : IMonitoredSwitchPort, IDisposable
    {
        /// <summary>
        /// How often the packet layer's timers are given the time.
        /// </summary>
        private const int TickIntervalMs = 100;

        /// <summary>
        /// How long to wait before dialling again after a dial failed.
        /// </summary>
        private const int RedialDelayMs = 2000;

        /// <summary>
        /// How long a dial for a call may take before it is given up, in milliseconds.
        /// </summary>
        private const int DialTimeoutMs = 5000;

        /// <summary>
        /// The packet type octet of a Call Request.
        /// </summary>
        private const byte CallRequestType = 0x0B;
        private const byte ClearRequestType = 0x13;
        private const byte CallAcceptedType = 0x0F;
        private const byte ClearConfirmationType = 0x17;

        /// <summary>
        /// The clearing cause reported for a circuit whose connection was lost: out of order.
        /// </summary>
        private const byte CauseOutOfOrder = 0x09;

        // ONE lock for every port, not one per port.
        //
        // A port decodes a packet under its lock and, still inside that call, hands it to the switch,
        // which sends it on by calling SendData on the OTHER port - and that takes the other port's
        // lock. With a lock per port, two ports that each receive a packet at the same moment each hold
        // their own lock and wait for the other's, and the switch stops for good with nothing in the log.
        // (Found 2026-11-10 with a Cisco router on one side and x25traffic on the other; a stack dump of
        // the frozen process showed both reader threads in
        // OnLayerData -> SwitchCore.Forward -> XotSwitchPort.SendData, blocked on Monitor.Enter.)
        //
        // With a single shared lock there is nothing to wait for in a circle: the thread that holds it
        // takes it again on the way into the other port (a lock can be taken again by its own thread),
        // and the other reader waits its turn. The work done under the lock is decoding, routing and
        // writing one packet, so one lock does not make the switch slower in any way that matters.
        //
        // The processing stays synchronous on purpose. The packet layer acknowledges received data only
        // after the switch has dealt with it, and the switch relies on that to hold the sender back
        // (RNR) when the far leg is full. Handing packets to another thread (tried first) lets the
        // layer acknowledge at once, the sender is never slowed down, and the switch's hold queue
        // overflows into a reset.
        //
        // Static: ports are created and started before they are attached to a switch node, so the lock
        // cannot be chosen per node. Several switches in one process share it, which costs only waiting.
        //
        // The lock itself now lives in PortLock, so the HDLC ports share it with this one: a switch can have
        // XOT and HDLC ports side by side and a packet can cross from one kind to the other.
        private static readonly object SharedSync = PortLock.Shared;

        private readonly object _sync = SharedSync;
        private readonly PortConfig _config;
        private readonly ILogger _logger;
        private readonly X25Layer _layer;

        /// <summary>Reset cause "Out of order" (X.25 Table 5-8, 0x01) - not the clearing cause of the same name, 0x09.</summary>
        private const byte PvcResetCauseOutOfOrder = 0x01;

        /// <summary>Reset cause "Remote DTE operational" (X.25 Table 5-8, 0x09).</summary>
        private const byte PvcResetCauseRemoteDteOperational = 0x09;

        /// <summary>This port's PVC channels; a lost connection resets these and never clears them.</summary>
        private readonly HashSet<ushort> _pvcChannels = new HashSet<ushort>();

        /// <summary>The RFC 1613 6.5 responder's decisions for this port's PVCs.</summary>
        private readonly XotPvcResponder _pvcResponder;
        private readonly Timer _ticker;

        private readonly List<XotConnection> _connections = new List<XotConnection>();
        private readonly Dictionary<ushort, CircuitRoute> _routes = new Dictionary<ushort, CircuitRoute>();
        private readonly Dictionary<long, ushort> _inbound = new Dictionary<long, ushort>();

        private TcpListener? _listener;
        private Thread? _supervisor;
        private volatile bool _running;
        private bool _active;
        private bool _peerConnected;
        private bool _disposed;
        private int _nextConnectionId;
        private int _connectAttempts;
        private long _lastConnectedMs;
        private long _lastDisconnectedMs;
        private int _connectFailures;
        private int _failuresSinceUp;
        private int _disconnects;
        private long _lastFailureMs;
        private string _lastError = string.Empty;

        /// <summary>
        /// The connection a packet came in on, while the engine is working on it. Anything the engine
        /// writes in answer to a packet that has no circuit yet goes back out on it.
        /// </summary>
        private XotConnection? _currentInbound;

        /// <summary>
        /// Calls refused for want of a free channel, for the log. Read and written with the lock held.
        /// </summary>
        private long _callsRefused;

        /// <summary>
        /// The connection just dialled for the call being placed, until the call has a channel.
        /// </summary>
        private XotConnection? _pendingDial;

        /// <summary>
        /// Creates the port. Nothing happens on the network until <see cref="Start"/>.
        /// </summary>
        /// <param name="config">
        /// The port's configuration. <c>Mode</c> decides whether it listens or dials, <c>Port</c>
        /// and <c>Host</c> say where (for a listening port <c>Host</c> is the local address to bind,
        /// and empty or <c>0.0.0.0</c> means every address), and <c>LciBase</c> and <c>LciCount</c>
        /// give the engine its range of logical channels.
        /// </param>
        /// <param name="logger">
        /// Where the port reports what happens to its connections. Null reports nothing.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="config"/> is null.
        /// </exception>
        public XotSwitchPort(PortConfig config, ILogger? logger = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? NullLogger.Instance;
            Id = config.Id;

            X25Config layerConfig = new X25Config
            {
                // A DCE port faces a calling DTE, so it is the DCE; a DTE port dials a network.
                // This decides the direction the engine searches for a free channel (Annex A) and
                // which side wins a call collision (4.1.3), so it follows the port's own mode
                // rather than being assumed.
                IsDte = config.Mode != PortMode.Dce,
                LciBase = config.LciBase,
                LciCount = config.LciCount,
                DefaultWindowSize = config.DefaultWindowSize,
                DefaultPacketSize = config.DefaultPacketSize,
            };

            _layer = new X25Layer(new PacketSink(this), layerConfig);
            _layer.IncomingCall += OnLayerIncomingCall;
            _layer.CallConnected += OnLayerCallConnected;
            _layer.DataReceived += OnLayerData;
            _layer.Cleared += OnLayerCleared;
            _layer.Reset += OnLayerReset;
            _layer.InterruptReceived += OnLayerInterrupt;
            _layer.WindowOpened += OnLayerWindowOpened;

            _ticker = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);

            // The largest packet one XOT frame carries: a power of two that fits after the 3-octet packet header.
            int largest = 16;
            while (largest * 2 <= 4096 && largest * 2 + 3 <= XotFrameCodec.FrameMaxPayload)
            {
                largest *= 2;
            }

            _pvcResponder = new XotPvcResponder(config.InterfaceName, (byte)config.DefaultWindowSize, config.DefaultPacketSize, largest);
        }

        /// <summary>
        /// Adds a permanent virtual circuit on this port. Over XOT it carries nothing until an RFC 1613 6.5 setup
        /// exchange binds it to a TCP connection; on a responder port the far XOT asks for it by channel.
        /// </summary>
        /// <param name="lci">The PVC's channel on this port.</param>
        /// <returns>True.</returns>
        public bool AddPvc(ushort lci)
        {
            lock (_sync)
            {
                _layer.AddPvc(lci, _config.DefaultWindowSize, _config.DefaultPacketSize);
                _pvcChannels.Add(lci);
                _pvcResponder.AddPvc(lci);
                return true;
            }
        }

        /// <summary>
        /// How long the initiator waits before trying a PVC setup again after it failed. RFC 1613 6.5: "An XOT MAY retry
        /// a failed PVC setup; if implemented the XOT SHOULD wait between attempts (5 minutes is suggested)."
        /// </summary>
        public int PvcSetupRetryMs { get; set; } = 5 * 60 * 1000;

        /// <summary>
        /// As responder: whether a PVC's destination is up, so a setup for it is answered 0x14 "Destination interface is
        /// not up" when it is not (RFC 1613 6.5). Set by the switch; null means always up.
        /// </summary>
        public Func<ushort, bool>? PvcDestinationUp
        {
            get => _pvcResponder.InterfaceUp;
            set => _pvcResponder.InterfaceUp = value;
        }

        /// <summary>For each PVC this initiator has joined: how to set it up again when its connection is lost.</summary>
        private readonly Dictionary<ushort, Action> _pvcRetries = new Dictionary<ushort, Action>();

        /// <summary>The setups this initiator has sent and not yet had answered, by connection.</summary>
        private readonly Dictionary<XotConnection, (ushort Lci, ushort FarLci, Action Retry)> _pendingSetups =
            new Dictionary<XotConnection, (ushort, ushort, Action)>();

        /// <summary>
        /// Joins one of this port's PVCs to a channel on another XOT, as the RFC 1613 6.5 initiator: opens a TCP
        /// connection of its own to the far XOT, sends the setup packet with status 0x00, and on a 0x00 reply carries the
        /// PVC on that connection. A failure is tried again after <see cref="PvcSetupRetryMs"/>, for as long as the port
        /// runs. Returns at once; the exchange runs in the background.
        /// </summary>
        /// <param name="lci">The PVC's channel on this port (added with <see cref="AddPvc"/>).</param>
        /// <param name="remoteHost">The far XOT, <c>host</c> or <c>host:port</c>; port 1998 when none is given.</param>
        /// <param name="remoteInterface">The far XOT's interface name.</param>
        /// <param name="remoteLci">The PVC's channel on the far interface.</param>
        public void StartPvcSetup(ushort lci, string remoteHost, string remoteInterface, ushort remoteLci)
        {
            string host = remoteHost;
            int tcpPort = 1998;
            int colon = remoteHost.LastIndexOf(':');
            if (colon > 0)
            {
                host = remoteHost.Substring(0, colon);
                tcpPort = int.Parse(remoteHost.Substring(colon + 1), System.Globalization.CultureInfo.InvariantCulture);
            }

            Action attempt = null!;
            Action retry = () => ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(PvcSetupRetryMs);
                if (_running)
                {
                    attempt();
                }
            });

            attempt = () =>
            {
                TcpClient client = new TcpClient();
                try
                {
                    if (!client.ConnectAsync(host, tcpPort).Wait(DialTimeoutMs))
                    {
                        throw new TimeoutException("no answer in " + DialTimeoutMs + " ms");
                    }
                }
                catch (Exception ex)
                {
                    client.Close();
                    _logger.LogWarning("Port {Id}: PVC {Lci} setup: dial to {Host}:{Port} failed ({Reason}); trying again in {S} s",
                        Id, lci, host, tcpPort, ex.InnerException?.Message ?? ex.Message, PvcSetupRetryMs / 1000);
                    retry();
                    return;
                }

                if (!_running)
                {
                    client.Close();
                    return;
                }

                XotConnection connection = AddConnection(client, "dialled for PVC " + lci, dialled: false);
                XotPvcSetup setup = new XotPvcSetup
                {
                    GfiLcn = (ushort)(0x1000 | lci),
                    Status = XotPvcSetup.StatusOk,
                    InitiatorInterface = _config.InterfaceName,
                    InitiatorLci = lci,
                    ResponderInterface = remoteInterface,
                    ResponderLci = remoteLci,
                    IncomingWindow = (byte)_config.DefaultWindowSize,
                    OutgoingWindow = (byte)_config.DefaultWindowSize,
                    IncomingPacketSize = _config.DefaultPacketSize,
                    OutgoingPacketSize = _config.DefaultPacketSize,
                };

                lock (_sync)
                {
                    _pendingSetups[connection] = (lci, remoteLci, retry);
                    SendOnConnection(connection, setup.Build());
                }
            };

            ThreadPool.QueueUserWorkItem(_ => attempt());
        }

        /// <summary>
        /// The initiator reads the responder's reply to its setup. The lock is held.
        /// </summary>
        private void TakeSetupReply(XotConnection connection, ReadOnlySpan<byte> frame)
        {
            if (!_pendingSetups.TryGetValue(connection, out (ushort Lci, ushort FarLci, Action Retry) pending))
            {
                _logger.LogWarning("Port {Id}: PVC setup received on an initiator port with none outstanding; connection closed", Id);
                ThreadPool.QueueUserWorkItem(_ => CloseConnection(connection, "unexpected PVC setup"));
                return;
            }

            _pendingSetups.Remove(connection);
            bool read = XotPvcSetup.TryParse(frame, out XotPvcSetup reply);
            if (!read || reply.Status != XotPvcSetup.StatusOk)
            {
                // "An XOT PVC setup is successful if the responder returns a status of 0x00."
                _logger.LogWarning("Port {Id}: PVC {Lci} setup refused, status 0x{Status:X2}; trying again in {S} s", Id, pending.Lci,
                    read ? reply.Status : XotPvcSetup.StatusProtocolError, PvcSetupRetryMs / 1000);
                ThreadPool.QueueUserWorkItem(_ => CloseConnection(connection, "PVC setup refused"));
                pending.Retry();
                return;
            }

            AddRoute(pending.Lci, connection, pending.FarLci);
            _pvcRetries[pending.Lci] = pending.Retry;
            _logger.LogInformation("Port {Id}: PVC channel {Lci} joined to {Iface} channel {Far} (connection {Conn})", Id,
                pending.Lci, reply.ResponderInterface, pending.FarLci, connection.Number);
            _layer.ResetCall(pending.Lci, PvcResetCauseRemoteDteOperational, 0);
        }

        /// <summary>
        /// Answers an RFC 1613 6.5 PVC setup packet as the responder. The lock is held.
        /// </summary>
        /// <remarks>
        /// "the responder will reply with its own PVC setup packet or by closing the TCP connection." On 0x00 the
        /// connection carries this PVC, and "each XOT MUST complete a Reset procedure on the local interface". A
        /// refusal is sent and the connection closed. An initiator port does not answer setups; it sends them.
        /// </remarks>
        private void AnswerPvcSetup(XotConnection connection, ReadOnlySpan<byte> frame)
        {
            if (_config.XotPvcInitiator)
            {
                TakeSetupReply(connection, frame);
                return;
            }

            byte status = _pvcResponder.Answer(frame, out XotPvcSetup reply);
            SendOnConnection(connection, reply.Build());
            if (status != XotPvcSetup.StatusOk)
            {
                _logger.LogWarning("Port {Id}: PVC setup for channel {Lci} refused, status 0x{Status:X2}", Id, reply.ResponderLci, status);
                ThreadPool.QueueUserWorkItem(_ => CloseConnection(connection, "PVC setup refused"));
                return;
            }

            // The PVC's packets travel on this connection with the far end's channel number on the wire.
            AddRoute(reply.ResponderLci, connection, reply.InitiatorLci);
            _logger.LogInformation("Port {Id}: PVC channel {Lci} joined to {Iface} channel {Far} (connection {Conn})", Id,
                reply.ResponderLci, reply.InitiatorInterface, reply.InitiatorLci, connection.Number);
            // The RFC names no cause; the far end has just become reachable, which X.25 4.6.5 resets with "Remote DTE
            // operational" (Table 5-8, 0x09).
            _layer.ResetCall(reply.ResponderLci, PvcResetCauseRemoteDteOperational, 0);
        }

        /// <summary>
        /// Gets the port number given to the constructor.
        /// </summary>
        public int Id { get; }

        /// <summary>
        /// Gets the configuration this port was created with.
        /// </summary>
        public PortConfig Config => _config;

        /// <summary>
        /// Gets a value indicating whether the port is up: started, and listening or dialling.
        /// </summary>
        /// <remarks>
        /// Read under the port lock. It says nothing about whether a peer is connected; see <see cref="PeerConnected"/>.
        /// </remarks>
        public bool Active
        {
            get { lock (_sync) { return _active; } }
        }

        /// <summary>
        /// Gets a value indicating whether at least one TCP connection is up.
        /// </summary>
        /// <remarks>
        /// When one connection is lost only the circuits on it end, and this stays true while any other connection remains.
        /// </remarks>
        public bool PeerConnected
        {
            get { lock (_sync) { return _peerConnected; } }
        }

        /// <summary>
        /// Gets the packet and byte totals of this port.
        /// </summary>
        public PortCounters Counters { get; } = new PortCounters();

        /// <summary>
        /// Raised when a peer places a call into this port.
        /// </summary>
        /// <remarks>
        /// Raised inside the port lock, so a handler must not block or call back into the same port.
        /// </remarks>
        public event PortIncomingCall? IncomingCall;

        /// <summary>
        /// Raised when a call this port placed has been accepted by the peer.
        /// </summary>
        /// <remarks>
        /// Raised inside the port lock, so a handler must not block or call back into the same port.
        /// </remarks>
        public event PortCallConnected? CallConnected;

        /// <summary>
        /// Raised when data arrives on an established circuit.
        /// </summary>
        /// <remarks>
        /// Raised inside the port lock, so a handler must not block or call back into the same port.
        /// </remarks>
        public event PortDataReceived? DataReceived;

        /// <summary>
        /// Raised when a circuit on this port is cleared, by the peer, by this side, or because its connection was lost.
        /// </summary>
        /// <remarks>
        /// A lost connection clears its circuits with the cause out of order. Raised inside the port lock, so a handler must not block or call back into the same port.
        /// </remarks>
        public event PortCallCleared? CallCleared;

        /// <summary>
        /// Raised when a circuit on this port is reset.
        /// </summary>
        /// <remarks>
        /// Raised inside the port lock, so a handler must not block or call back into the same port.
        /// </remarks>
        public event PortReset? CircuitReset;

        /// <summary>
        /// Raised when an interrupt packet arrives on a circuit.
        /// </summary>
        /// <remarks>
        /// Raised inside the port lock, so a handler must not block or call back into the same port.
        /// </remarks>
        public event PortInterrupt? InterruptReceived;

        /// <summary>
        /// Raised when a circuit that had refused a send, because its window was shut or the peer was busy, can take data again.
        /// </summary>
        /// <remarks>
        /// Raised inside the port lock, so a handler must not block or call back into the same port.
        /// </remarks>
        public event PortReadyToSend? ReadyToSend;

        /// <summary>
        /// Raised when the port goes up or down.
        /// </summary>
        /// <remarks>
        /// Raised inside the port lock, so a handler must not block or call back into the same port.
        /// </remarks>
        public event PortStateChanged? StateChanged;

        /// <summary>
        /// Raised for every X.25 packet the port receives or writes, for a monitor to show.
        /// </summary>
        /// <remarks>
        /// See <see cref="PortPacketCaptured"/> for what the handler may do. The packet is shown as
        /// it crossed the wire, with the channel number the peer used. Nothing is copied while no
        /// handler is attached.
        /// </remarks>
        public event PortPacketCaptured? PacketCaptured;

        /// <summary>
        /// Gets how many times the port has tried to make a connection: each dial for a DTE port
        /// and each accepted connection for a DCE port.
        /// </summary>
        public int ConnectAttempts => Volatile.Read(ref _connectAttempts);

        /// <summary>
        /// Gets the time, as <see cref="Environment.TickCount64"/>, at which a peer last connected;
        /// 0 if none has.
        /// </summary>
        public long LastConnectedMs => Interlocked.Read(ref _lastConnectedMs);

        /// <summary>
        /// Gets the time, as <see cref="Environment.TickCount64"/>, at which a connection last went
        /// away; 0 if none has.
        /// </summary>
        public long LastDisconnectedMs => Interlocked.Read(ref _lastDisconnectedMs);

        /// <summary>
        /// Gets how many dials failed since the port started (the peer refused, did not answer in time,
        /// or the name did not resolve). A DCE port never dials, so this stays 0 for it.
        /// </summary>
        public int ConnectFailures => Volatile.Read(ref _connectFailures);

        /// <summary>
        /// Gets how many connections went away since the port started, whether the peer closed them or
        /// the network dropped them.
        /// </summary>
        public int Disconnects => Volatile.Read(ref _disconnects);

        /// <summary>
        /// Gets the reason the last dial failed, in words (for example "connection refused"); empty if no
        /// dial has failed. It is not cleared when a later dial works: read it together with
        /// <see cref="PeerConnected"/>.
        /// </summary>
        public string LastError => Volatile.Read(ref _lastError);

        /// <summary>
        /// Gets the time, as <see cref="Environment.TickCount64"/>, at which a dial last failed; 0 if none has.
        /// </summary>
        public long LastFailureMs => Interlocked.Read(ref _lastFailureMs);

        /// <summary>
        /// Gets how long a DTE port waits after a failed dial before it dials again, in milliseconds.
        /// </summary>
        public int RedialDelay => RedialDelayMs;

        /// <summary>
        /// Gets how many connections the port has open now.
        /// </summary>
        public int ConnectionCount
        {
            get { lock (_sync) { return _connections.Count; } }
        }

        /// <summary>
        /// The port the listener actually bound to. Zero until a DCE port has started.
        /// </summary>
        /// <remarks>
        /// A configured port of zero asks the operating system to pick one, which is how a test
        /// avoids a fixed port number and the flakes that come with one.
        /// </remarks>
        public ushort BoundPort { get; private set; }

        /// <summary>
        /// Starts the port: a DCE port starts listening, any other port starts dialling, and the background supervisor and the timer start.
        /// </summary>
        /// <remarks>
        /// A second call while the port is running changes nothing and returns <see cref="Active"/>. A port that cannot start (for example the address is in use or the host is not an address) logs an error, stays down, and returns <c>false</c>; it does not throw.
        /// </remarks>
        /// <returns>
        /// <c>true</c> when the port started; <c>false</c> when it could not.
        /// </returns>
        public bool Start()
        {
            lock (_sync)
            {
                if (_running)
                {
                    return _active;
                }

                _running = true;
            }

            try
            {
                if (_config.Mode == PortMode.Dce)
                {
                    TcpListener listener = new TcpListener(ParseBindAddress(_config.Host), _config.Port);
                    listener.Start();
                    _listener = listener;
                    BoundPort = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
                    _logger.LogInformation("Port {Id} ({Mode} {Type}): listening on {Address}:{Port}",
                        Id, _config.Mode, _config.Type, ((IPEndPoint)listener.LocalEndpoint).Address, BoundPort);
                }
                else
                {
                    _logger.LogInformation("Port {Id} ({Mode} {Type}): dialling {Host}:{Port}",
                        Id, _config.Mode, _config.Type, _config.Host, _config.Port);
                }

                _supervisor = new Thread(RunSupervisor) { IsBackground = true, Name = "XotSwitchPort" + Id };
                _supervisor.Start();
                _ticker.Change(TickIntervalMs, TickIntervalMs);

                SetActive(true);
                return true;
            }
            catch (Exception ex)
            {
                // A port that cannot start is reported, not hidden: a switch that goes on without its
                // listener still answers its management console and looks healthy while every call
                // routed to the port goes nowhere.
                _logger.LogError(ex, "Port {Id} ({Mode} {Type}): could NOT start on {Host}:{Port} - the port is down",
                    Id, _config.Mode, _config.Type, _config.Host, _config.Port);
                _running = false;
                SetActive(false);
                return false;
            }
        }

        /// <summary>
        /// Works out the local address a listening port binds to.
        /// </summary>
        /// <param name="host">
        /// The configured <c>Host</c>: empty or <c>0.0.0.0</c> for every address, otherwise an address
        /// such as <c>127.0.0.1</c>.
        /// </param>
        /// <returns>
        /// The address to listen on.
        /// </returns>
        /// <exception cref="FormatException">
        /// Thrown when <paramref name="host"/> is not an address. A host name is not accepted, because
        /// the C emulator takes the same setting as an address.
        /// </exception>
        private static IPAddress ParseBindAddress(string host)
        {
            if (string.IsNullOrEmpty(host) || host == "0.0.0.0")
            {
                return IPAddress.Any;
            }

            if (!IPAddress.TryParse(host, out IPAddress? address))
            {
                throw new FormatException("'" + host + "' is not an address the port can listen on");
            }

            return address;
        }

        /// <summary>
        /// Stops the timer and the listener, closes every connection, waits up to one second for the supervisor thread, and marks the port down.
        /// </summary>
        /// <remarks>
        /// Every circuit on the port ends when its connection closes. Safe to call more than once.
        /// </remarks>
        public void Stop()
        {
            _running = false;
            _ticker.Change(Timeout.Infinite, Timeout.Infinite);

            try
            {
                _listener?.Stop();
            }
            catch (Exception)
            {
                // Already down; the supervisor thread leaves on its own.
            }

            XotConnection[] open;
            lock (_sync)
            {
                open = _connections.ToArray();
            }

            for (int i = 0; i < open.Length; i++)
            {
                CloseConnection(open[i], "port stopped");
            }

            Thread? supervisor = _supervisor;
            _supervisor = null;
            supervisor?.Join(1000);

            SetActive(false);
        }

        /// <summary>
        /// Places an outgoing call and returns the logical channel it was given.
        /// </summary>
        /// <remarks>
        /// A dialling port opens a new TCP connection for the call, because RFC 1613 requires one connection for each virtual circuit; the dial happens before the lock is taken so a slow peer holds up only this call. A listening port places the call on a connection the peer already opened that has never carried a call, and refuses it when there is none. No Restart is sent first (RFC 1613 6.4 says a Restart must not be sent over the connection). A connection opened for a call that could not be placed is closed again.
        /// </remarks>
        /// <param name="calledAddress">
        /// The X.121 address to call.
        /// </param>
        /// <param name="callingAddress">
        /// The X.121 address of the caller.
        /// </param>
        /// <param name="callUserData">
        /// The call user data, or <c>null</c> for none.
        /// </param>
        /// <returns>
        /// The logical channel of the new circuit, or 0 when no peer is connected, the dial failed, or the packet layer refused the call.
        /// </returns>
        public ushort PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData)
        {
            return PlaceCallWith(calledAddress, callingAddress, callUserData, null);
        }

        /// <inheritdoc/>
        public ushort PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData, X25CallParameters requested)
        {
            return PlaceCallWith(calledAddress, callingAddress, callUserData, requested);
        }

        /// <inheritdoc/>
        public bool TryGetCallParameters(ushort lci, out X25CallParameters parameters)
        {
            lock (_sync)
            {
                return _layer.TryGetCallParameters(lci, out parameters);
            }
        }

        /// <inheritdoc/>
        public void AcceptCall(ushort lci, X25CallParameters agreed)
        {
            lock (_sync)
            {
                _layer.AcceptCall(lci, agreed);
            }
        }

        private ushort PlaceCallWith(string calledAddress, string callingAddress, byte[]? callUserData, X25CallParameters? requested)
        {
            XotConnection? dialled = null;

            if (_config.Mode != PortMode.Dce)
            {
                // A call going out of a dialling port gets a connection of its own (RFC 1613 section
                // 5 applies to the end that dials just as it does to the end that listens). The dial
                // is made before the lock is taken, so a peer that is slow to answer holds up this
                // call and not every other circuit on the port.
                if (!PeerConnected)
                {
                    return 0;
                }

                dialled = Dial("for a call");
                if (dialled == null)
                {
                    return 0;
                }
            }

            ushort lci;
            lock (_sync)
            {
                if (!_peerConnected)
                {
                    return 0;
                }

                if (dialled == null)
                {
                    WaitForFreshConnection();
                }

                // No restart first. X.25 4.1 restarts a link before its first call, but this link is a
                // TCP connection, and RFC 1613 6.4 says Restart "MUST NOT be sent over the TCP
                // connection" and, if received, "MUST be silently discarded". A conforming XOT peer
                // therefore never answers one, and the packet layer holds the Call Request until a
                // Restart Confirmation that cannot come. That is how a call from this switch to the C
                // emulator across a trunk failed after five seconds, although the same call worked on
                // the port this one replaces, which never sent a restart.
                _pendingDial = dialled;
                try
                {
                    lci = requested.HasValue
                        ? _layer.PlaceCall(calledAddress, callingAddress, callUserData, requested.Value)
                        : _layer.PlaceCall(calledAddress, callingAddress, callUserData);
                }
                finally
                {
                    _pendingDial = null;
                }
            }

            if (lci == 0 && dialled != null)
            {
                CloseConnection(dialled, "the call could not be placed");
            }

            return lci;
        }

        /// <summary>
        /// Accepts an incoming call by sending a Call Accepted on its circuit.
        /// </summary>
        /// <param name="lci">
        /// The logical channel of the incoming call, as given to the <see cref="IncomingCall"/> event.
        /// </param>
        public void AcceptCall(ushort lci)
        {
            lock (_sync)
            {
                _layer.AcceptCall(lci);
            }
        }

        /// <summary>
        /// Sends data on an established circuit and says what the packet layer did with it.
        /// </summary>
        /// <remarks>
        /// The data is copied. When the packet layer refuses it, the result tells a refusal that waiting can cure (the window is shut or the peer is busy) from one that cannot be cured. The caller has to read the result: dropping a refused packet leaves a gap in the send sequence that resets the circuit.
        /// </remarks>
        /// <param name="lci">
        /// The logical channel to send on.
        /// </param>
        /// <param name="data">
        /// The bytes to send.
        /// </param>
        /// <param name="qBit">
        /// The qualifier bit.
        /// </param>
        /// <param name="mBit">
        /// The more-data bit.
        /// </param>
        /// <param name="dBit">
        /// The delivery-confirmation bit.
        /// </param>
        /// <returns>
        /// <see cref="PortSendResult.Sent"/> when the packet was written; <see cref="PortSendResult.WindowFull"/> when it must wait; <see cref="PortSendResult.Failed"/> when it cannot be sent, including when no peer is connected.
        /// </returns>
        public PortSendResult SendData(ushort lci, ReadOnlySpan<byte> data, bool qBit, bool mBit, bool dBit)
        {
            lock (_sync)
            {
                if (!_peerConnected)
                {
                    return PortSendResult.Failed;
                }

                if (_layer.SendData(lci, data.ToArray(), qBit, mBit, dBit))
                {
                    return PortSendResult.Sent;
                }

                return WouldFitLater(lci, data.Length) ? PortSendResult.WindowFull : PortSendResult.Failed;
            }
        }

        /// <summary>
        /// Clears a circuit by sending a Clear Request.
        /// </summary>
        /// <param name="lci">
        /// The logical channel to clear.
        /// </param>
        /// <param name="cause">
        /// The clearing cause code.
        /// </param>
        /// <param name="diagnostic">
        /// The diagnostic code.
        /// </param>
        public void ClearCall(ushort lci, byte cause, byte diagnostic)
        {
            lock (_sync)
            {
                _layer.ClearCall(lci, cause, diagnostic);
            }
        }

        /// <inheritdoc/>
        public void ClearCall(ushort lci, byte cause, byte diagnostic, byte[] facilities)
        {
            lock (_sync)
            {
                _layer.ClearCall(lci, cause, diagnostic, facilities);
            }
        }

        /// <inheritdoc/>
        public byte[] GetClearFacilities(ushort lci)
        {
            lock (_sync)
            {
                return _layer.GetClearFacilities(lci);
            }
        }

        /// <inheritdoc/>
        public Func<ushort, byte[]?>? ClearConfirmationFacilities
        {
            get { lock (_sync) { return _layer.ClearConfirmationFacilities; } }
            set { lock (_sync) { _layer.ClearConfirmationFacilities = value; } }
        }

        /// <summary>
        /// Resets a circuit: the sequence numbers go back to zero and what was in flight is discarded.
        /// </summary>
        /// <param name="lci">
        /// The logical channel to reset.
        /// </param>
        /// <param name="cause">
        /// The resetting cause code.
        /// </param>
        /// <param name="diagnostic">
        /// The diagnostic code.
        /// </param>
        public void ResetCircuit(ushort lci, byte cause, byte diagnostic)
        {
            lock (_sync)
            {
                _layer.ResetCall(lci, cause, diagnostic);
            }
        }

        /// <summary>
        /// Sends an interrupt on a circuit, past the flow control of the data it overtakes.
        /// </summary>
        /// <param name="lci">
        /// The logical channel to interrupt.
        /// </param>
        /// <param name="userData">
        /// The interrupt user data, 1 to 32 octets.
        /// </param>
        public void SendInterrupt(ushort lci, byte[] userData)
        {
            lock (_sync)
            {
                _layer.SendInterrupt(lci, userData);
            }
        }

        /// <summary>
        /// Tells the far end whether this side can take data on a circuit: Receive Not Ready when busy, Receive Ready when not.
        /// </summary>
        /// <param name="lci">
        /// The logical channel to signal on.
        /// </param>
        /// <param name="busy">
        /// <c>true</c> to send Receive Not Ready; <c>false</c> to send Receive Ready.
        /// </param>
        public void SetLocalBusy(ushort lci, bool busy)
        {
            lock (_sync)
            {
                _layer.SetBusy(lci, busy);
            }
        }

        /// <summary>
        /// Stops the port and disposes its timer.
        /// </summary>
        /// <remarks>
        /// Only the first call does anything.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
            _ticker.Dispose();
        }

        /// <summary>
        /// True when a refused send was refused only because the window is shut or the peer is
        /// busy, so holding the packet and trying again can help.
        /// </summary>
        /// <param name="lci">
        /// The channel the send was refused on.
        /// </param>
        /// <param name="length">
        /// How many bytes were offered.
        /// </param>
        /// <returns>
        /// True for a flow control refusal, false for anything else.
        /// </returns>
        private bool WouldFitLater(ushort lci, int length)
        {
            if (!_layer.Circuits.TryGetValue(lci, out X25Vc? vc))
            {
                return false;
            }

            if (vc.State != X25VcState.DataTransfer && vc.State != X25VcState.PvcReady)
            {
                return false;
            }

            if (length > vc.PacketSizeOut)
            {
                // Too long for what was negotiated. Waiting will not make it fit.
                return false;
            }

            return vc.PeerBusy || vc.PendingSegments.Count > 0 || ((vc.SendSeq - vc.AckedSeq) & 0x07) >= vc.WindowSizeOut;
        }

        // ------------------------------------------------------------------ connections

        /// <summary>
        /// Owns the port's ability to get connections for its whole life: a listening port accepts
        /// them, a dialling port keeps one spare open.
        /// </summary>
        private void RunSupervisor()
        {
            while (_running)
            {
                try
                {
                    if (_config.Mode == PortMode.Dce)
                    {
                        AcceptOne();
                    }
                    else
                    {
                        KeepSpareOpen();
                    }
                }
                catch (Exception ex)
                {
                    // Nothing here is fatal to the port: the next turn accepts or dials again.
                    _logger.LogDebug("Port {Id}: connection supervisor: {Reason}", Id, ex.Message);
                }
            }
        }

        /// <summary>
        /// Waits for one connection on a listening port and starts serving it.
        /// </summary>
        private void AcceptOne()
        {
            TcpListener? listener = _listener;
            if (listener == null)
            {
                Thread.Sleep(100);
                return;
            }

            TcpClient accepted;
            try
            {
                accepted = listener.AcceptTcpClient();
            }
            catch (Exception)
            {
                // Stop() closed the listener.
                return;
            }

            Interlocked.Increment(ref _connectAttempts);

            bool room;
            lock (_sync)
            {
                // A circuit needs a channel, so a port cannot use more connections than it has
                // channels. Refusing here is clearer than accepting a call that would have no channel.
                room = _connections.Count < Math.Max((int)_config.LciCount, 1);
            }

            if (!room)
            {
                _logger.LogWarning("Port {Id}: no room for another connection (channels={Count}); refused", Id, _config.LciCount);
                accepted.Close();
                return;
            }

            AddConnection(accepted, "accepted", dialled: false);
        }

        /// <summary>
        /// A dialling port keeps one idle connection open. It is not for the calls the port places,
        /// which dial their own; it is for the calls the port receives, because a peer that only
        /// listens cannot open a connection back and can place a call only on one that is already open.
        /// </summary>
        private void KeepSpareOpen()
        {
            bool haveIdle;
            lock (_sync)
            {
                haveIdle = false;
                for (int i = 0; i < _connections.Count; i++)
                {
                    if (IsFresh(_connections[i]))
                    {
                        haveIdle = true;
                        break;
                    }
                }
            }

            if (haveIdle)
            {
                Thread.Sleep(100);
                return;
            }

            XotConnection? spare = Dial("spare");
            if (spare != null)
            {
                return;
            }

            // A peer that is down is ordinary and is retried; wait so a port whose peer is down does
            // not spin.
            for (int waited = 0; waited < RedialDelayMs && _running; waited += 100)
            {
                Thread.Sleep(100);
            }
        }

        /// <summary>
        /// Opens a TCP connection to the configured peer and starts serving it.
        /// </summary>
        /// <param name="why">
        /// What the connection is for, for the log.
        /// </param>
        /// <returns>
        /// The connection, or null if the peer could not be reached.
        /// </returns>
        private XotConnection? Dial(string why)
        {
            TcpClient? client = null;
            try
            {
                Interlocked.Increment(ref _connectAttempts);

                // Only the first try after the port started or after a connection was up is logged at
                // Information level: the retries that follow every two seconds would fill the log.
                if (Volatile.Read(ref _failuresSinceUp) == 0)
                {
                    _logger.LogInformation("Port {Id} ({Mode} {Type}): dialling {Host}:{Port} ({Why})", Id, _config.Mode, _config.Type, _config.Host, _config.Port, why);
                }

                client = new TcpClient();
                Task connect = client.ConnectAsync(_config.Host, _config.Port);
                if (!connect.Wait(DialTimeoutMs))
                {
                    client.Close();
                    throw new TimeoutException("no answer in " + DialTimeoutMs + " ms");
                }
            }
            catch (Exception ex)
            {
                string reason = ex.InnerException?.Message ?? ex.Message;
                Interlocked.Increment(ref _connectFailures);
                int inARow = Interlocked.Increment(ref _failuresSinceUp);
                Interlocked.Exchange(ref _lastFailureMs, Environment.TickCount64);
                Volatile.Write(ref _lastError, reason);

                // A peer that is down is ordinary and is retried every RedialDelayMs, so only the first
                // failure and then every fifteenth (about every 30 seconds) is a warning; the rest are Debug.
                if (inARow == 1 || inARow % 15 == 0)
                {
                    _logger.LogWarning("Port {Id} ({Mode} {Type}): dial to {Host}:{Port} ({Why}) failed: {Reason}. Failure {Count} in a row; the next try is in {Delay} seconds, and then every {Delay} seconds until it connects",
                        Id, _config.Mode, _config.Type, _config.Host, _config.Port, why, reason, inARow, RedialDelayMs / 1000, RedialDelayMs / 1000);
                }
                else
                {
                    _logger.LogDebug("Port {Id}: dial to {Host}:{Port} {Why} failed ({Reason}), failure {Count} in a row", Id, _config.Host, _config.Port, why, reason, inARow);
                }

                client?.Close();
                return null;
            }

            if (!_running)
            {
                client.Close();
                return null;
            }

            return AddConnection(client, "dialled " + why, dialled: why != "spare");
        }

        /// <summary>
        /// Registers a connected socket as a connection of this port and starts its reader.
        /// </summary>
        /// <param name="client">
        /// The connected socket.
        /// </param>
        /// <param name="how">
        /// How it came about, for the log.
        /// </param>
        /// <param name="dialled">
        /// True when the port dialled it for one call, so that it is closed when the call ends.
        /// </param>
        /// <returns>
        /// The new connection.
        /// </returns>
        private XotConnection AddConnection(TcpClient client, string how, bool dialled)
        {
            // Nagle's algorithm off. A switch passes small packets - receive-ready acknowledgements,
            // call set-up, a few bytes of terminal data - and every hop that holds one back until the
            // previous segment is acknowledged adds its delay to the whole call. Every other XOT link
            // in RetroIO sets this, as does the C emulator, and the port this one replaces did too.
            client.NoDelay = true;

            XotConnection connection = new XotConnection(this, Interlocked.Increment(ref _nextConnectionId), client, dialled);

            lock (_sync)
            {
                _connections.Add(connection);
                _peerConnected = true;

                // A call waiting for a fresh connection (see WaitForFreshConnection) may go now.
                Monitor.PulseAll(_sync);
            }

            Interlocked.Exchange(ref _lastConnectedMs, Environment.TickCount64);
            int failedBefore = Interlocked.Exchange(ref _failuresSinceUp, 0);
            _logger.LogInformation("Port {Id} ({Mode} {Type}): connection up ({How}, connection {Conn}){After}", Id, _config.Mode, _config.Type, how, connection.Number,
                failedBefore > 0 ? " after " + failedBefore + " failed dial(s)" : string.Empty);

            connection.Reader = new Thread(() => ReadUntilClosed(connection)) { IsBackground = true, Name = "XotSwitchPort" + Id + "." + connection.Number };
            connection.Reader.Start();
            return connection;
        }

        /// <summary>
        /// Reads one connection until it ends, then closes it.
        /// </summary>
        /// <param name="connection">
        /// The connection to serve.
        /// </param>
        private void ReadUntilClosed(XotConnection connection)
        {
            string reason = "closed by peer";
            byte[] buffer = new byte[XotFrameCodec.FrameMaxPayload];

            try
            {
                NetworkStream stream = connection.Stream;
                while (_running && !connection.Closed)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    lock (_sync)
                    {
                        if (connection.Closed)
                        {
                            break;
                        }

                        connection.Codec.ProcessBytes(new ReadOnlySpan<byte>(buffer, 0, read));
                    }
                }
            }
            catch (Exception ex)
            {
                // A dropped connection is ordinary; the reason is kept for the "connection down" line.
                reason = ex.Message;
            }

            CloseConnection(connection, reason);
        }

        /// <summary>
        /// Drops one connection, if it is still up, and ends the circuits that were on it.
        /// </summary>
        /// <param name="connection">
        /// The connection to close.
        /// </param>
        /// <param name="reason">
        /// Why the connection ended, for the log line.
        /// </param>
        /// <remarks>
        /// Several paths see the same drop; only the first does anything. The other connections of the
        /// port, and the circuits on them, are not touched.
        /// </remarks>
        private void CloseConnection(XotConnection connection, string reason)
        {
            ushort[] lost;
            bool anyLeft;

            lock (_sync)
            {
                if (connection.Closed)
                {
                    return;
                }

                connection.Closed = true;
                _connections.Remove(connection);
                if (ReferenceEquals(_currentInbound, connection))
                {
                    _currentInbound = null;
                }

                // A responder that closes instead of replying refused the setup ("reply ... or by closing the TCP
                // connection"): the initiator tries again later.
                if (_pendingSetups.TryGetValue(connection, out (ushort Lci, ushort FarLci, Action Retry) unanswered))
                {
                    _pendingSetups.Remove(connection);
                    if (_running)
                    {
                        unanswered.Retry();
                    }
                }

                lost = connection.Circuits.ToArray();
                for (int i = 0; i < lost.Length; i++)
                {
                    ReleaseRoute(lost[i]);
                }

                anyLeft = _connections.Count > 0;
                _peerConnected = anyLeft;
            }

            try
            {
                connection.Client.Close();
            }
            catch (Exception)
            {
                // Already closed.
            }

            Interlocked.Exchange(ref _lastDisconnectedMs, Environment.TickCount64);
            Interlocked.Increment(ref _disconnects);

            // A connection that goes away while the port is meant to be running is a fault, so a warning;
            // one closed because the port is stopping is ordinary. A DTE port dials again by itself.
            if (_running)
            {
                _logger.LogWarning("Port {Id} ({Mode} {Type}): connection down ({Reason}, connection {Conn}){Retry}", Id, _config.Mode, _config.Type, reason, connection.Number,
                    _config.Mode == PortMode.Dte && !anyLeft ? "; dialling again in " + RedialDelayMs / 1000 + " seconds" : string.Empty);
            }
            else
            {
                _logger.LogInformation("Port {Id} ({Mode} {Type}): connection down ({Reason}, connection {Conn})", Id, _config.Mode, _config.Type, reason, connection.Number);
            }

            // The circuits that were on this connection are gone with it. Nothing is written to them -
            // nobody is there to answer - and the switch clears the far leg of each.
            for (int i = 0; i < lost.Length; i++)
            {
                lock (_sync)
                {
                    if (_pvcChannels.Contains(lost[i]))
                    {
                        // "An XOT PVC connection is broken by simply closing the TCP connection." A PVC is never cleared
                        // (X.25 4.2); it is reset "Out of order" (4.6.3) and waits for a new setup.
                        _pvcResponder.Disconnected(lost[i]);
                        _layer.ResetCall(lost[i], PvcResetCauseOutOfOrder, 0);
                        if (_running && _pvcRetries.TryGetValue(lost[i], out Action? again))
                        {
                            _pvcRetries.Remove(lost[i]);
                            again();
                        }
                    }
                    else
                    {
                        _layer.AbortCircuit(lost[i], CauseOutOfOrder, 0);
                    }
                }
            }

            if (!anyLeft)
            {
                StateChanged?.Invoke(this, false);
                StateChanged?.Invoke(this, Active);
            }
        }

        private void SetActive(bool active)
        {
            bool changed;
            lock (_sync)
            {
                changed = _active != active;
                _active = active;
            }

            if (changed)
            {
                StateChanged?.Invoke(this, active);
            }
        }

        private void OnTick(object? state)
        {
            lock (_sync)
            {
                _layer.Tick(Environment.TickCount64);
            }
        }

        // ------------------------------------------------------------------ channel numbers

        /// <summary>
        /// Reads the logical channel number from the first two octets of a packet.
        /// </summary>
        /// <param name="packet">
        /// The packet.
        /// </param>
        /// <returns>
        /// The group and channel as one number, 0 to 4095; 0 also for a packet too short to have one.
        /// </returns>
        private static ushort ChannelOf(ReadOnlySpan<byte> packet)
        {
            if (packet.Length < 2)
            {
                return 0;
            }

            return (ushort)(((packet[0] & 0x0F) << 8) | packet[1]);
        }

        /// <summary>
        /// Writes a logical channel number into the first two octets of a packet.
        /// </summary>
        /// <param name="packet">
        /// The packet to change.
        /// </param>
        /// <param name="channel">
        /// The group and channel as one number, 0 to 4095.
        /// </param>
        private static void SetChannel(byte[] packet, ushort channel)
        {
            packet[0] = (byte)((packet[0] & 0xF0) | ((channel >> 8) & 0x0F));
            packet[1] = (byte)(channel & 0xFF);
        }

        /// <summary>
        /// Builds the key that names one channel number on one connection.
        /// </summary>
        private static long InboundKey(XotConnection connection, ushort wireChannel)
        {
            return ((long)connection.Number << 16) | wireChannel;
        }

        /// <summary>
        /// Finds the channel the engine knows a connection's circuit by, giving it one if this is a
        /// new circuit.
        /// </summary>
        /// <param name="connection">
        /// The connection the packet came in on.
        /// </param>
        /// <param name="wireChannel">
        /// The channel number the peer put on the packet.
        /// </param>
        /// <param name="isCallRequest">
        /// True when the packet is a Call Request, the only packet that starts a circuit.
        /// </param>
        /// <param name="packetType">
        /// The packet type octet. A Call Accepted or Clear Request on an unknown channel is the answer to the call dialled
        /// for this connection, and the peer's channel number is bound to that call.
        /// </param>
        /// <param name="channel">
        /// The channel for the engine.
        /// </param>
        /// <returns>
        /// True if the packet belongs to a circuit that has a route; false for a packet that starts
        /// nothing and belongs to no circuit, which the engine will refuse.
        /// </returns>
        private bool MapInbound(XotConnection connection, ushort wireChannel, bool isCallRequest, byte packetType, out ushort channel)
        {
            if (_inbound.TryGetValue(InboundKey(connection, wireChannel), out channel))
            {
                return true;
            }

            // RFC 1613 section 6: the channel number in an XOT packet "has no significance and has arbitrary values", and a
            // forwarding end sets it to the number of its own outgoing interface. So the answer to a call we dialled a
            // connection for may come back on another number than our Call Request went out on (x25emu answers a call sent on
            // channel 16 with a Call Accepted on channel 1). A connection carries one circuit (section 5), so a Call Accepted
            // or a Clear Request on an unknown channel can only belong to that circuit: bind the peer's number to it.
            //
            // This holds whoever opened the connection. Until 2026-11-17 it was done only for a connection this port had
            // dialled (DialledForCall); on a listening port, where a call goes out on a connection the peer opened, the
            // acceptance was dropped and answered with a clear (diagnostic 20). Seen with SINTRAN's PAD calling x25emu's
            // x25tadsrv, whose client renumbers every arriving call to channel 1.
            if (!isCallRequest && (packetType == CallAcceptedType || packetType == ClearRequestType)
                && connection.Circuits.Count == 1)
            {
                ushort only = connection.Circuits[0];

                if (_routes.TryGetValue(only, out CircuitRoute? route) && route.Connection == connection)
                {
                    _inbound.Remove(InboundKey(connection, route.WireChannel));
                    _routes[only] = new CircuitRoute(connection, wireChannel);
                    _inbound[InboundKey(connection, wireChannel)] = only;
                    channel = only;
                    return true;
                }
            }

            channel = wireChannel;
            if (!isCallRequest)
            {
                return false;
            }

            // A connection whose call has ended is being closed (see OnLayerCleared) and takes no new call
            // (RFC 1613 section 5). The engine answers the Call Request with a clear.
            if (connection.CarriedCall && connection.Circuits.Count == 0)
            {
                return false;
            }

            ushort chosen = wireChannel;
            if (!ChannelIsUsable(wireChannel))
            {
                if (!FindFreeChannel(out chosen))
                {
                    // Every channel is in use. The engine answers the call with the right clear.
                    return false;
                }
            }

            channel = chosen;
            AddRoute(chosen, connection, wireChannel);
            return true;
        }

        /// <summary>
        /// True if a channel is inside the port's range and no circuit has it.
        /// </summary>
        private bool ChannelIsUsable(ushort channel)
        {
            if (channel < _config.LciBase || channel >= _config.LciBase + _config.LciCount)
            {
                return false;
            }

            if (_routes.ContainsKey(channel))
            {
                return false;
            }

            return !_layer.Circuits.TryGetValue(channel, out X25Vc? vc) || vc.State == X25VcState.Ready;
        }

        /// <summary>
        /// Picks a free channel from the port's range, from the end the port's role searches from
        /// (X.25 Annex A: a DTE from the highest, a DCE from the lowest).
        /// </summary>
        private bool FindFreeChannel(out ushort channel)
        {
            int first = _config.LciBase;
            int last = _config.LciBase + _config.LciCount - 1;
            bool fromTop = _config.Mode != PortMode.Dce;

            for (int n = 0; n < _config.LciCount; n++)
            {
                ushort candidate = (ushort)(fromTop ? last - n : first + n);
                if (ChannelIsUsable(candidate))
                {
                    channel = candidate;
                    return true;
                }
            }

            channel = 0;
            return false;
        }

        private void AddRoute(ushort channel, XotConnection connection, ushort wireChannel)
        {
            _routes[channel] = new CircuitRoute(connection, wireChannel);
            _inbound[InboundKey(connection, wireChannel)] = channel;
            connection.Circuits.Add(channel);
            connection.CarriedCall = true;
        }

        private void ReleaseRoute(ushort channel)
        {
            if (!_routes.TryGetValue(channel, out CircuitRoute? route))
            {
                return;
            }

            _routes.Remove(channel);
            _inbound.Remove(InboundKey(route.Connection, route.WireChannel));
            route.Connection.Circuits.Remove(channel);
        }

        /// <summary>
        /// Chooses the connection a packet from the engine goes out on, and the channel number it
        /// carries on the wire.
        /// </summary>
        /// <param name="channel">
        /// The channel the engine put on the packet.
        /// </param>
        /// <param name="isCallRequest">
        /// True when the packet is a Call Request, which starts a circuit.
        /// </param>
        /// <param name="wireChannel">
        /// The channel number to write.
        /// </param>
        /// <returns>
        /// The connection, or null if the port has none to send on.
        /// </returns>
        private XotConnection? ConnectionFor(ushort channel, bool isCallRequest, out ushort wireChannel)
        {
            wireChannel = channel;

            if (channel != 0 && _routes.TryGetValue(channel, out CircuitRoute? route))
            {
                wireChannel = route.WireChannel;
                return route.Connection;
            }

            // Anything written in answer to a packet that has no circuit goes back the way it came.
            if (_currentInbound != null)
            {
                return _currentInbound;
            }

            if (channel == 0)
            {
                return _connections.Count > 0 ? _connections[0] : null;
            }

            XotConnection? chosen = _pendingDial;
            if (chosen == null)
            {
                chosen = PickConnectionForNewCall();
            }

            if (chosen != null && isCallRequest)
            {
                AddRoute(channel, chosen, channel);
            }

            return chosen;
        }

        /// <summary>
        /// Picks the connection for a call going out of a port that did not dial one for it.
        /// </summary>
        /// <remarks>
        /// RFC 1613 section 5: "A separate TCP connection MUST be used for each X.25 virtual circuit."
        /// So the call goes only on a connection that has never carried one. When there is none the
        /// call is refused (null: the engine reports the link down and the call is not placed).
        /// Until 2026-11-18 the call was then put on a busy connection, and a connection whose call
        /// had ended was used again; x25emu closes such a connection, so that call was lost.
        /// </remarks>
        private XotConnection? PickConnectionForNewCall()
        {
            for (int i = 0; i < _connections.Count; i++)
            {
                if (IsFresh(_connections[i]))
                {
                    return _connections[i];
                }
            }

            return null;
        }

        /// <summary>
        /// How long a call going out of a listening port waits for the peer to open a fresh connection, in
        /// milliseconds. The same as x25emu's switch (SW_UNDELIVERABLE_MS in tools/x25switch.h), which holds such a
        /// call for this long and then clears it.
        /// </summary>
        private const int FreshConnectionWaitMs = 5000;

        /// <summary>
        /// Waits, with the lock held (Monitor.Wait gives it up while waiting), until the port has a connection that
        /// has never carried a call, or <see cref="FreshConnectionWaitMs"/> has passed.
        /// </summary>
        /// <remarks>
        /// A listening port cannot dial: a call can go out only on a connection the peer opened and has not used. A
        /// peer that keeps one spare connection opens the next one only after a call has taken the last, so a second
        /// call that comes right after the first finds none for a moment. x25emu holds such a call until a connection
        /// comes (tools/x25switch.c, conn_for_tx: "a DTE that has no idle connection at this instant may open one a
        /// moment later, so the caller of this holds the packet rather than dropping it"). This does the same by
        /// waiting; the accept path pulses the lock when a connection is added. The thread that waits is the one
        /// placing the call (another port's reader, through the switch core).
        /// </remarks>
        private void WaitForFreshConnection()
        {
            long deadline = Environment.TickCount64 + FreshConnectionWaitMs;
            while (_running && _peerConnected)
            {
                for (int i = 0; i < _connections.Count; i++)
                {
                    if (IsFresh(_connections[i]))
                    {
                        return;
                    }
                }

                long left = deadline - Environment.TickCount64;
                if (left <= 0)
                {
                    return;
                }

                Monitor.Wait(_sync, (int)left);
            }
        }

        /// <summary>
        /// True for a connection that is open and has never carried a call: the only kind a new call may go on.
        /// </summary>
        private static bool IsFresh(XotConnection connection)
        {
            return !connection.Closed && !connection.CarriedCall && connection.Circuits.Count == 0;
        }

        // ------------------------------------------------------------------ packets

        private void OnXotFrame(XotConnection connection, ReadOnlySpan<byte> frame)
        {
            // Already inside the lock: the reader thread takes it before calling the codec.
            //
            // The packet is counted and shown to the tap before the engine sees it, so a packet the
            // engine then refuses still appears in the monitor and in the byte total, as it crossed
            // the wire.
            Counters.AddBytesIn(frame.Length);
            Counters.IncrementAllPacketsIn();
            PortPacketCaptured? tap = PacketCaptured;
            if (tap != null)
            {
                tap(Id, true, frame.ToArray());
            }

            // RFC 1613 6.5: the setup packet's type was "selected from the unused X.25 PTI values" so it can be told
            // apart before anything else; it never reaches the packet layer.
            if (XotPvcSetup.IsSetup(frame))
            {
                AnswerPvcSetup(connection, frame);
                return;
            }

            ushort wire = ChannelOf(frame);
            ReadOnlySpan<byte> forEngine = frame;
            if (wire != 0)
            {
                byte packetType = frame.Length >= 3 ? frame[2] : (byte)0;

                // A channel this port refused a call on belongs to no circuit the engine knows; the
                // few packets the caller can still send on it are answered here.
                if (connection.Refused.Contains(wire))
                {
                    AnswerOnRefusedChannel(connection, wire, packetType);
                    return;
                }

                bool isCallRequest = packetType == CallRequestType;
                if (MapInbound(connection, wire, isCallRequest, packetType, out ushort channel))
                {
                    if (channel != wire)
                    {
                        byte[] renumbered = frame.ToArray();
                        SetChannel(renumbered, channel);
                        forEngine = renumbered;
                    }
                }
                else if (isCallRequest)
                {
                    // MapInbound only fails a Call Request when every channel of the port is in use.
                    RefuseCall(connection, wire);
                    return;
                }
            }

            _currentInbound = connection;
            try
            {
                _layer.ProcessPacket(forEngine);
            }
            finally
            {
                _currentInbound = null;
            }
        }

        /// <summary>
        /// Clears a call the port has no channel for, with cause 5, network congestion (X.25 Table
        /// 5-7), on the channel number the caller used.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The port renumbers a caller's channels into its own range, so a call that finds the range
        /// full has a channel number the engine does not know. Handed to the engine, the packet was
        /// answered with a Diagnostic on channel 0 (Table C.1: a channel outside every configured range
        /// is unassigned, diagnostic 36). A Diagnostic is not an answer to a call: the caller ignores
        /// it and waits for the call to be accepted or cleared, and the call just hangs. The caller
        /// is told, as X.25 has it, that the network cannot take the call now.
        /// </para>
        /// <para>
        /// The channel is remembered in <see cref="XotConnection.Refused"/> until the caller confirms
        /// the clear, so its Clear Confirmation (or a Clear Request of its own, if it gives up first)
        /// does not reach the engine as a packet on an unassigned channel.
        /// </para>
        /// </remarks>
        private void RefuseCall(XotConnection connection, ushort wire)
        {
            // A caller that sends thousands of calls at a full port would otherwise write a line each.
            _callsRefused++;
            if (_callsRefused == 1 || _callsRefused % 100 == 0)
            {
                _logger.LogWarning("Port {Id}: no free channel for a call on channel {Wire}; cleared with network congestion ({Count} refused so far)", Id, wire, _callsRefused);
            }

            // This call never reaches the engine or the switch core, so nobody else will count it: it arrived, and it
            // ends here (see PortCounters).
            connection.Refused.Add(wire);
            Counters.IncrementCallsIn();
            Counters.IncrementCallsCleared();
            SendOnConnection(connection, X25PacketBuilder.CreateClearRequest(wire, X25Cause.NetworkCongestion, 0));
        }

        /// <summary>
        /// Answers a packet on a channel this port refused a call on.
        /// </summary>
        private void AnswerOnRefusedChannel(XotConnection connection, ushort wire, byte packetType)
        {
            if (packetType == ClearConfirmationType)
            {
                connection.Refused.Remove(wire);
            }
            else if (packetType == ClearRequestType)
            {
                // The caller cleared first: both ends have now said clear.
                connection.Refused.Remove(wire);
                SendOnConnection(connection, X25PacketBuilder.CreateClearConfirmation(wire));
            }

            // Anything else (a second Call Request, data) is a packet on a channel in clear
            // indication state and is dropped, as X.25 has the network do.
        }

        private void OnFramingFailed(XotConnection connection, string reason)
        {
            // RFC 1613 has no marker to resynchronize on. Closing the socket from here would
            // deadlock against the reader thread's own lock, so the connection is simply dropped by
            // closing the client; the reader's next read returns and the loop tidies up.
            try
            {
                connection.Client.Close();
            }
            catch (Exception)
            {
                // Already going.
            }
        }

        /// <summary>
        /// Writes one packet from the engine to the connection it belongs to. Only the packet sink
        /// calls this, with the lock held.
        /// </summary>
        private X25SendResult WritePacket(ReadOnlySpan<byte> packet)
        {
            bool isCallRequest = packet.Length >= 3 && packet[2] == CallRequestType;

            // A PVC travels only on the connection its RFC 1613 6.5 setup joined it to; until then it has no way out.
            ushort pvc = ChannelOf(packet);
            if (pvc != 0 && _pvcChannels.Contains(pvc) && !_routes.ContainsKey(pvc))
            {
                return X25SendResult.LinkDown;
            }

            XotConnection? connection = ConnectionFor(ChannelOf(packet), isCallRequest, out ushort wire);
            if (connection == null || connection.Closed)
            {
                return X25SendResult.LinkDown;
            }

            byte[] onWire = packet.ToArray();
            if (packet.Length >= 2 && wire != ChannelOf(packet))
            {
                SetChannel(onWire, wire);
            }

            return SendOnConnection(connection, onWire);
        }

        /// <summary>
        /// Writes one finished packet to a connection, and counts and shows it. The lock is held.
        /// </summary>
        private X25SendResult SendOnConnection(XotConnection connection, byte[] onWire)
        {
            try
            {
                connection.Codec.SendFrame(onWire);
                Counters.IncrementAllPacketsOut();
                Counters.AddBytesOut(onWire.Length);

                // Only a packet that was written is shown and counted as sent.
                PortPacketCaptured? tap = PacketCaptured;
                if (tap != null)
                {
                    tap(Id, false, onWire);
                }

                return X25SendResult.Sent;
            }
            catch (Exception)
            {
                return X25SendResult.LinkDown;
            }
        }

        // ------------------------------------------------------------------ engine events

        private void OnLayerIncomingCall(X25Layer layer, ushort lci, string called, string calling, byte[] cud)
        {
            // Not counted here: SwitchCore counts the call when it gets this event (see PortCounters).
            _logger.LogInformation("Port {Id}: incoming call {Calling} -> {Called} LCI={Lci}", Id, calling, called, lci);
            IncomingCall?.Invoke(this, lci, called, calling, cud);
        }

        private void OnLayerCallConnected(X25Layer layer, ushort lci)
        {
            _logger.LogInformation("Port {Id}: call accepted LCI={Lci}", Id, lci);
            CallConnected?.Invoke(this, lci);
        }

        private void OnLayerData(X25Layer layer, ushort lci, byte[] data, int length, bool qBit, bool mBit, bool dBit)
        {
            // Not counted here: SwitchCore counts the data packet when it gets this event (see PortCounters).
            DataReceived?.Invoke(this, lci, data, length, qBit, mBit, dBit);
        }

        private void OnLayerCleared(X25Layer layer, ushort lci, byte cause, byte diagnostic)
        {
            // Not counted here: SwitchCore counts the clear when it gets this event (see PortCounters).
            _logger.LogInformation("Port {Id}: clear LCI={Lci} cause={Cause} diag={Diag}", Id, lci, cause, diagnostic);

            // The circuit is over, so it gives up its route. The connection it went on has nothing
            // left to carry and is closed (RFC 1613 section 5: one connection for one circuit),
            // whichever end opened it: it may never carry another call, so keeping it only lets the
            // other end put a call on it that this end will not take. A connection that still
            // carries other circuits (a peer that puts several on one) stays until the last ends.
            XotConnection? closeAfter = null;
            if (_routes.TryGetValue(lci, out CircuitRoute? route))
            {
                XotConnection connection = route.Connection;
                ReleaseRoute(lci);
                if (connection.CarriedCall && connection.Circuits.Count == 0)
                {
                    closeAfter = connection;
                }
            }

            CallCleared?.Invoke(this, lci, cause, diagnostic);

            if (closeAfter != null)
            {
                // Outside the engine's callback: closing may raise events of its own. The packet that
                // ended the call has already been written; a close sends what is queued first.
                ThreadPool.QueueUserWorkItem(_ => CloseConnection(closeAfter, "the call ended"));
            }
        }

        private void OnLayerReset(X25Layer layer, ushort lci, byte cause, byte diagnostic)
        {
            CircuitReset?.Invoke(this, lci, cause, diagnostic);
        }

        private void OnLayerInterrupt(X25Layer layer, ushort lci, byte[] userData)
        {
            InterruptReceived?.Invoke(this, lci, userData);
        }

        private void OnLayerWindowOpened(X25Layer layer, ushort lci)
        {
            ReadyToSend?.Invoke(this, lci);
        }

        // ------------------------------------------------------------------ helper types

        /// <summary>
        /// One TCP connection of the port, with the framing state of its stream.
        /// </summary>
        private sealed class XotConnection
        {
            /// <summary>
            /// Creates the connection around a connected socket.
            /// </summary>
            /// <param name="owner">
            /// The port the connection belongs to.
            /// </param>
            /// <param name="number">
            /// A number for the log and for the route table, unique within the port.
            /// </param>
            /// <param name="client">
            /// The connected socket.
            /// </param>
            /// <param name="dialledForCall">
            /// True when the port dialled the connection for one call and closes it when the call ends.
            /// </param>
            public XotConnection(XotSwitchPort owner, int number, TcpClient client, bool dialledForCall)
            {
                Number = number;
                Client = client;
                Stream = client.GetStream();
                DialledForCall = dialledForCall;
                Codec = new XotFrameCodec("port" + owner.Id + "." + number, new SocketSink(Stream));
                Codec.FrameReceived += (linkId, frame) => owner.OnXotFrame(this, frame);
                Codec.FramingFailed += (linkId, reason) => owner.OnFramingFailed(this, reason);
            }

            /// <summary>
            /// Gets the connection's number within the port.
            /// </summary>
            public int Number { get; }

            /// <summary>
            /// Gets the socket.
            /// </summary>
            public TcpClient Client { get; }

            /// <summary>
            /// Gets the socket's stream.
            /// </summary>
            public NetworkStream Stream { get; }

            /// <summary>
            /// Gets the framing for this connection's stream.
            /// </summary>
            public XotFrameCodec Codec { get; }

            /// <summary>
            /// Gets whether the port dialled this connection for one call.
            /// </summary>
            public bool DialledForCall { get; }

            /// <summary>
            /// Gets or sets the thread that reads the connection.
            /// </summary>
            public Thread? Reader { get; set; }

            /// <summary>
            /// Gets or sets whether the connection has been closed.
            /// </summary>
            public bool Closed { get; set; }

            /// <summary>
            /// Gets the channels of the circuits this connection carries, as the engine knows them.
            /// </summary>
            public List<ushort> Circuits { get; } = new List<ushort>();

            /// <summary>
            /// Gets or sets whether a call has ever been put on this connection, by either end. RFC 1613 section 5
            /// gives each virtual circuit its own connection, so once this is set the connection never takes
            /// another call, and it is closed when its call ends.
            /// </summary>
            public bool CarriedCall { get; set; }

            /// <summary>
            /// Gets the channel numbers (as the caller numbers them) of calls the port refused for
            /// want of a free channel and whose clearing the caller has not yet confirmed.
            /// </summary>
            public HashSet<ushort> Refused { get; } = new HashSet<ushort>();
        }

        /// <summary>
        /// Where one circuit goes: its connection, and the channel number the peer knows it by.
        /// </summary>
        private sealed class CircuitRoute
        {
            /// <summary>
            /// Creates the route.
            /// </summary>
            /// <param name="connection">
            /// The connection that carries the circuit.
            /// </param>
            /// <param name="wireChannel">
            /// The channel number used on that connection.
            /// </param>
            public CircuitRoute(XotConnection connection, ushort wireChannel)
            {
                Connection = connection;
                WireChannel = wireChannel;
            }

            /// <summary>
            /// Gets the connection that carries the circuit.
            /// </summary>
            public XotConnection Connection { get; }

            /// <summary>
            /// Gets the channel number used on that connection.
            /// </summary>
            public ushort WireChannel { get; }
        }

        /// <summary>
        /// Where the packet layer's output goes: one complete X.25 packet, renumbered, framed by the
        /// connection's codec and written to its socket.
        /// </summary>
        private sealed class PacketSink : IX25PacketSink
        {
            private readonly XotSwitchPort _owner;

            /// <summary>
            /// Creates the sink.
            /// </summary>
            /// <param name="owner">
            /// The port that owns the connections.
            /// </param>
            public PacketSink(XotSwitchPort owner)
            {
                _owner = owner;
            }

            /// <summary>
            /// Writes one packet from the packet layer to the connection it belongs to, through <c>WritePacket</c> on the owning port.
            /// </summary>
            /// <param name="packet">
            /// The X.25 packet to write.
            /// </param>
            /// <returns>
            /// What the owning port did with the packet.
            /// </returns>
            public X25SendResult SendPacket(ReadOnlySpan<byte> packet)
            {
                return _owner.WritePacket(packet);
            }
        }

        /// <summary>
        /// Where a codec's output goes: XOT-framed bytes, straight to its connection's socket.
        /// </summary>
        private sealed class SocketSink : IFrameTransport
        {
            private readonly NetworkStream _stream;

            /// <summary>
            /// Creates the sink.
            /// </summary>
            /// <param name="stream">
            /// The connection's stream.
            /// </param>
            public SocketSink(NetworkStream stream)
            {
                _stream = stream;
            }

            /// <summary>
            /// Writes the bytes straight to the connection's network stream.
            /// </summary>
            /// <param name="bytes">
            /// The XOT-framed bytes to write.
            /// </param>
            /// <exception cref="System.IO.IOException">
            /// Thrown when the stream cannot be written, for example when the connection is closed.
            /// </exception>
            public void Send(ReadOnlySpan<byte> bytes)
            {
                _stream.Write(bytes);
            }
        }
    }
}
