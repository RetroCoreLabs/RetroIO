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
    /// </para>
    /// <para><b>Channel numbers are ours</b></para>
    /// <para>
    /// The number a peer puts on the wire is not trusted to be unique
    /// across connections, or to be inside this port's range. Each connection's circuits are given a
    /// channel number from the port's own range for the engine, and the packet is renumbered on the way
    /// in and again on the way out. When the number the peer used is free and in range it is kept, so
    /// for a peer that behaves nothing is rewritten. One connection may also carry several circuits,
    /// which is how a peer that multiplexes (RetroIO's own client) works with this port.
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
    public sealed class XotSwitchPort : ISwitchPort, IDisposable
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

        /// <summary>
        /// The clearing cause reported for a circuit whose connection was lost: out of order.
        /// </summary>
        private const byte CauseOutOfOrder = 0x09;

        private readonly object _sync = new object();
        private readonly PortConfig _config;
        private readonly ILogger _logger;
        private readonly X25Layer _layer;
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

        /// <summary>
        /// The connection a packet came in on, while the engine is working on it. Anything the engine
        /// writes in answer to a packet that has no circuit yet goes back out on it.
        /// </summary>
        private XotConnection? _currentInbound;

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
        }

        /// <inheritdoc/>
        public int Id { get; }

        /// <inheritdoc/>
        public PortConfig Config => _config;

        /// <inheritdoc/>
        public bool Active
        {
            get { lock (_sync) { return _active; } }
        }

        /// <inheritdoc/>
        public bool PeerConnected
        {
            get { lock (_sync) { return _peerConnected; } }
        }

        /// <inheritdoc/>
        public PortCounters Counters { get; } = new PortCounters();

        /// <inheritdoc/>
        public event PortIncomingCall? IncomingCall;

        /// <inheritdoc/>
        public event PortCallConnected? CallConnected;

        /// <inheritdoc/>
        public event PortDataReceived? DataReceived;

        /// <inheritdoc/>
        public event PortCallCleared? CallCleared;

        /// <inheritdoc/>
        public event PortReset? CircuitReset;

        /// <inheritdoc/>
        public event PortInterrupt? InterruptReceived;

        /// <inheritdoc/>
        public event PortReadyToSend? ReadyToSend;

        /// <inheritdoc/>
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

        /// <inheritdoc/>
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

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public ushort PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData)
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
                    lci = _layer.PlaceCall(calledAddress, callingAddress, callUserData);
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

        /// <inheritdoc/>
        public void AcceptCall(ushort lci)
        {
            lock (_sync)
            {
                _layer.AcceptCall(lci);
            }
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public void ClearCall(ushort lci, byte cause, byte diagnostic)
        {
            lock (_sync)
            {
                _layer.ClearCall(lci, cause, diagnostic);
            }
        }

        /// <inheritdoc/>
        public void ResetCircuit(ushort lci, byte cause, byte diagnostic)
        {
            lock (_sync)
            {
                _layer.ResetCall(lci, cause, diagnostic);
            }
        }

        /// <inheritdoc/>
        public void SendInterrupt(ushort lci, byte[] userData)
        {
            lock (_sync)
            {
                _layer.SendInterrupt(lci, userData);
            }
        }

        /// <inheritdoc/>
        public void SetLocalBusy(ushort lci, bool busy)
        {
            lock (_sync)
            {
                _layer.SetBusy(lci, busy);
            }
        }

        /// <inheritdoc/>
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
                    if (_connections[i].Circuits.Count == 0 && !_connections[i].Closed)
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
                // A peer that is down is ordinary and is retried, so this is not worth a line at the
                // default level every two seconds.
                _logger.LogDebug("Port {Id}: dial to {Host}:{Port} {Why} failed ({Reason})", Id, _config.Host, _config.Port, why, ex.InnerException?.Message ?? ex.Message);
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
            }

            Interlocked.Exchange(ref _lastConnectedMs, Environment.TickCount64);
            _logger.LogInformation("Port {Id} ({Mode} {Type}): connection up ({How}, connection {Conn})", Id, _config.Mode, _config.Type, how, connection.Number);

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
            _logger.LogInformation("Port {Id} ({Mode} {Type}): connection down ({Reason}, connection {Conn})", Id, _config.Mode, _config.Type, reason, connection.Number);

            // The circuits that were on this connection are gone with it. Nothing is written to them -
            // nobody is there to answer - and the switch clears the far leg of each.
            for (int i = 0; i < lost.Length; i++)
            {
                lock (_sync)
                {
                    _layer.AbortCircuit(lost[i], CauseOutOfOrder, 0);
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
        /// <param name="channel">
        /// The channel for the engine.
        /// </param>
        /// <returns>
        /// True if the packet belongs to a circuit that has a route; false for a packet that starts
        /// nothing and belongs to no circuit, which the engine will refuse.
        /// </returns>
        private bool MapInbound(XotConnection connection, ushort wireChannel, bool isCallRequest, out ushort channel)
        {
            if (_inbound.TryGetValue(InboundKey(connection, wireChannel), out channel))
            {
                return true;
            }

            channel = wireChannel;
            if (!isCallRequest)
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
        /// RFC 1613 section 5 gives each circuit its own connection, so an idle one is used when there
        /// is one. When every connection already carries a circuit the call is put on one of them
        /// anyway: RetroIO's own client carries several circuits on one connection, and refusing the
        /// call would break it. A peer that does not accept that clears the call.
        /// </remarks>
        private XotConnection? PickConnectionForNewCall()
        {
            for (int i = 0; i < _connections.Count; i++)
            {
                if (_connections[i].Circuits.Count == 0 && !_connections[i].Closed)
                {
                    return _connections[i];
                }
            }

            return _connections.Count > 0 ? _connections[0] : null;
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
            PortPacketCaptured? tap = PacketCaptured;
            if (tap != null)
            {
                tap(Id, true, frame.ToArray());
            }

            ushort wire = ChannelOf(frame);
            ReadOnlySpan<byte> forEngine = frame;
            if (wire != 0)
            {
                bool isCallRequest = frame.Length >= 3 && frame[2] == CallRequestType;
                if (MapInbound(connection, wire, isCallRequest, out ushort channel) && channel != wire)
                {
                    byte[] renumbered = frame.ToArray();
                    SetChannel(renumbered, channel);
                    forEngine = renumbered;
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

            try
            {
                connection.Codec.SendFrame(onWire);
                Counters.IncrementPacketsOut();
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
            Counters.IncrementCallsIn();
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
            Counters.IncrementPacketsIn();
            DataReceived?.Invoke(this, lci, data, length, qBit, mBit, dBit);
        }

        private void OnLayerCleared(X25Layer layer, ushort lci, byte cause, byte diagnostic)
        {
            Counters.IncrementCallsCleared();
            _logger.LogInformation("Port {Id}: clear LCI={Lci} cause={Cause} diag={Diag}", Id, lci, cause, diagnostic);

            // The circuit is over, so it gives up its route. A connection this port dialled for the
            // call has nothing left to carry and is closed (RFC 1613 section 5: one connection for
            // one circuit). A connection the peer opened is the peer's to close, and a connection
            // that carries other circuits stays.
            XotConnection? closeAfter = null;
            if (_routes.TryGetValue(lci, out CircuitRoute? route))
            {
                XotConnection connection = route.Connection;
                ReleaseRoute(lci);
                if (connection.DialledForCall && connection.Circuits.Count == 0)
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

            /// <inheritdoc/>
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

            /// <inheritdoc/>
            public void Send(ReadOnlySpan<byte> bytes)
            {
                _stream.Write(bytes);
            }
        }
    }
}
