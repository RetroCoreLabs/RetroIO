//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using RetroIO.Link;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.PacketLayer;
using RetroIO.Protocols.X25.Runtime.Transport;
using RetroIO.Protocols.X25.Transport;

namespace RetroIO.Protocols.X25.Runtime
{
    /// <summary>
    /// The runtime's XOT path on the one X.25 engine: one <see cref="X25Endpoint"/> per XOT
    /// connection, and the tables that map the runtime's "local LCN" (the key every module,
    /// service and history view holds) to the circuit that carries it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until 2026-09-06 this file's job was done by <c>OnXotDataReceived</c> decoding packet types by
    /// hand, a private <c>XotFlowControl</c> keeping P(S)/P(R) per local LCN, a remote-to-local LCN
    /// map, and - on the server side - the OLD engine sending the Call Accepted while the
    /// hand-rolled code did data and flow control on the same circuit. The audit of the C emulator
    /// found five silent-discard shapes; this had the same ones plus two engines per call.
    /// </para>
    /// <para>
    /// Now the engine owns sequence numbers, windows, timers and every protocol reaction (Annex C).
    /// The runtime only: creates an endpoint when a connection first speaks, feeds it packets, maps
    /// local LCNs, and routes the engine's events to whoever is bound to the local LCN.
    /// </para>
    /// <para>
    /// Locking. <c>_routingLock</c> guards the tables. An endpoint's engine is touched only
    /// inside <see cref="X25Endpoint.Sync"/>; the engine's events arrive inside that lock, so the
    /// handlers here take <c>_routingLock</c> briefly for table lookups and never call another
    /// endpoint while holding either.
    /// </para>
    /// </remarks>
    public partial class ProtocolRuntime
    {
        private const int XotSendTimeoutMs = 2000;
        private const int EndpointTickMs = 250;

        private readonly Dictionary<object, X25Endpoint> _endpoints = new Dictionary<object, X25Endpoint>();

        /// <summary>
        /// Requests from services waiting to be sent, per local LCN. See <see cref="DeliverServiceOutput"/>.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, ServiceOutbox> _serviceOutboxes = new System.Collections.Concurrent.ConcurrentDictionary<ushort, ServiceOutbox>();
        private readonly Dictionary<ushort, (X25Endpoint Endpoint, ushort Lci)> _circuitByLocalLcn = new Dictionary<ushort, (X25Endpoint, ushort)>();
        private readonly Dictionary<X25Endpoint, Dictionary<ushort, ushort>> _localLcnByCircuit = new Dictionary<X25Endpoint, Dictionary<ushort, ushort>>();
        private Timer? _endpointTicker;

        /// <summary>
        /// Test seam: called on the placing thread after the engine has taken an outgoing XOT call
        /// and its lock is released, before <see cref="PlaceCallOnEndpoint"/> returns.
        /// </summary>
        /// <remarks>
        /// From that point on the connection's reader thread is free to feed the engine the peer's
        /// answer. The seam lets a test hold the placing thread there until the answer has been
        /// taken. That is the order that lost the Call Connected while the local LCN was still
        /// mapped after the lock instead of inside it (ProtocolRuntimeWirePinTests). Null outside
        /// tests.
        /// </remarks>
        internal Action<X25Endpoint, ushort>? CallPlacedOnEndpointForTest { get; set; }

        // ------------------------------------------------------------------ endpoints

        /// <summary>
        /// The endpoint for a connection, created on first use.
        /// </summary>
        /// <param name="connection">
        /// The connection object the transport reports: an <see cref="XotPeerLink"/> (one
        /// connection each) or an <see cref="XotServerConnection"/>.
        /// </param>
        /// <param name="name">
        /// Name for logs.
        /// </param>
        /// <param name="send">
        /// Writes one complete X.25 packet to that connection.
        /// </param>
        /// <param name="isDte">
        /// True for a connection this side dialled (it calls the switch as a DTE), false for one
        /// accepted by the XOT server.
        /// </param>
        /// <param name="lciBase">
        /// The first logical channel number this endpoint may use.
        /// </param>
        /// <param name="lciCount">
        /// How many logical channel numbers it may use, counting from <paramref name="lciBase"/>.
        /// </param>
        private X25Endpoint EndpointFor(object connection, string name, Func<byte[], bool> send, bool isDte, ushort lciBase = 1, ushort lciCount = 4095)
        {
            // An XOT connection uses the window and packet size of ITS entry when the entry sets them (the peer's own
            // settings, Cisco's "profile"); otherwise the X.25 section of the configuration.
            byte windowSize = _config.X25.DefaultPacketWindowSize;
            int packetSize = _config.X25.MaxPacketSize;
            if (connection is XotPeerLink || connection is XotServerConnection)
            {
                XotInterfaceConfig? entry = FindXotEntry(name);
                if (entry != null)
                {
                    windowSize = entry.RequestedWindowSize ?? windowSize;
                    packetSize = entry.RequestedPacketSize ?? packetSize;
                }
            }

            lock (_routingLock)
            {
                if (_endpoints.TryGetValue(connection, out X25Endpoint? existing))
                {
                    return existing;
                }

                PacketLayer.X25Config config = new PacketLayer.X25Config
                {
                    LocalAddress = _config.X25.LocalMachineId,
                    IsDte = isDte,
                    LciBase = lciBase,
                    LciCount = lciCount,
                    DefaultWindowSize = windowSize,
                    DefaultPacketSize = (ushort)packetSize,
                    CallRequestTimeoutMs = (int)_config.X25.T21Timeout.TotalMilliseconds,
                    IncomingCallTimeoutMs = (int)_config.X25.T11Timeout.TotalMilliseconds,
                    ResetRequestTimeoutMs = (int)_config.X25.T22Timeout.TotalMilliseconds,
                    ClearRequestTimeoutMs = (int)_config.X25.T23Timeout.TotalMilliseconds,
                    RestartTimeoutMs = (int)_config.X25.T20Timeout.TotalMilliseconds,
                };

                // An XOT circuit has no LAPB or HDLC under it, so the serial path's packet events never
                // fire for it. Record what it sends here, the one place every outgoing packet passes.
                // The record is made BEFORE the send: the peer's answer can arrive on another thread
                // before the send returns, and the history must show the request first.
                Func<byte[], bool> sendAndRecord = send;
                if (connection is XotServerConnection || connection is XotPeerLink)
                {
                    sendAndRecord = bytes =>
                    {
                        RecordXotPacket(bytes, Direction.TX);
                        return send(bytes);
                    };
                }

                X25Endpoint endpoint = new X25Endpoint(name, sendAndRecord, config, _logger);

                // A connection WE dialled over XOT may be answered on another channel number than the call used
                // (RFC 1613 section 6); see X25Endpoint.TranslatesChannels. A peer link carries one circuit.
                endpoint.TranslatesChannels = connection is XotPeerLink dialledLink && dialledLink.Dialed;
                endpoint.Layer.IncomingCall += OnEndpointIncomingCall;
                endpoint.Layer.CallConnected += OnEndpointCallConnected;
                endpoint.Layer.DataReceived += OnEndpointData;
                endpoint.Layer.Cleared += OnEndpointCleared;
                endpoint.Layer.Reset += OnEndpointReset;
                endpoint.Layer.RestartOccurred += OnEndpointRestart;
                endpoint.Layer.RestartTimedOut += OnEndpointRestartTimedOut;

                _endpoints[connection] = endpoint;
                _localLcnByCircuit[endpoint] = new Dictionary<ushort, ushort>();
                _endpointTicker ??= new Timer(TickEndpoints, null, EndpointTickMs, EndpointTickMs);
                _logger.LogInformation("X.25 endpoint created for {Name} ({Role})", name, isDte ? "DTE" : "DCE");
                return endpoint;
            }
        }

        /// <summary>
        /// The X.25 engine for one LinkManager link: LAPB, HDLC or serial traffic, as opposed to the
        /// XOT connections above.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The link itself is the key, so every circuit on it shares one engine, one restart state
        /// and one logical channel range - which is what X.25 clause 4 describes. Before this, link
        /// traffic went into a single process-wide engine whose replies were bounced back into the
        /// process and never reached a link at all.
        /// </para>
        /// <para>
        /// The role is DTE. Neither <c>ILink</c> nor the runtime configuration carries an X.25
        /// role today, and every link here dials a switch or a front end, which is the DCE. It
        /// decides the direction the engine searches for a free channel (Annex A) and which side
        /// wins a call collision (clause 4.1.3), so it has to be stated rather than left to chance.
        /// </para>
        /// </remarks>
        /// <param name="link">
        /// The link the payload arrived on.
        /// </param>
        private X25Endpoint EndpointForLink(ILink link)
        {
            return FindEndpoint(link) ?? EndpointFor(link, link.Name, payload => _linkManager.SendViaLink(link.Name, payload), isDte: true);
        }

        private XotInterfaceConfig? FindXotEntry(string name)
        {
            List<XotInterfaceConfig>? entries = _config.Routing?.XotInterfaces;
            for (int i = 0; entries != null && i < entries.Count; i++)
            {
                if (string.Equals(entries[i].Name, name, StringComparison.Ordinal))
                {
                    return entries[i];
                }
            }

            return null;
        }

        private X25Endpoint? FindEndpoint(object connection)
        {
            lock (_routingLock)
            {
                return _endpoints.TryGetValue(connection, out X25Endpoint? e) ? e : null;
            }
        }

        private void TickEndpoints(object? state)
        {
            X25Endpoint[] endpoints;
            lock (_routingLock)
            {
                endpoints = new X25Endpoint[_endpoints.Count];
                _endpoints.Values.CopyTo(endpoints, 0);
            }

            long now = Environment.TickCount64;
            for (int i = 0; i < endpoints.Length; i++)
            {
                try
                {
                    endpoints[i].Tick(now);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "X.25 endpoint {Name}: tick failed", endpoints[i].Name);
                }
            }
        }

        /// <summary>
        /// Drops an endpoint whose connection is gone: every local LCN on it is reported cleared to
        /// its owner and unbound.
        /// </summary>
        private void DropEndpoint(object connection)
        {
            X25Endpoint? endpoint;
            List<(ushort LocalLcn, ushort Lci)> circuits = new List<(ushort, ushort)>();
            lock (_routingLock)
            {
                if (!_endpoints.TryGetValue(connection, out endpoint))
                {
                    return;
                }

                _endpoints.Remove(connection);
                if (_localLcnByCircuit.TryGetValue(endpoint, out Dictionary<ushort, ushort>? map))
                {
                    foreach (KeyValuePair<ushort, ushort> kv in map)
                    {
                        circuits.Add((kv.Value, kv.Key));
                    }

                    _localLcnByCircuit.Remove(endpoint);
                }

                for (int i = 0; i < circuits.Count; i++)
                {
                    _circuitByLocalLcn.Remove(circuits[i].LocalLcn);
                }
            }

            ForgetOfferedCalls(endpoint, 0);
            endpoint.MarkLinkDown();
            for (int i = 0; i < circuits.Count; i++)
            {
                DeliverCleared(circuits[i].LocalLcn, X25Cause.OutOfOrder, X25Diagnostic.None);
            }

            endpoint.Dispose();
            _logger.LogInformation("X.25 endpoint {Name} dropped with {Count} circuit(s)", endpoint.Name, circuits.Count);
        }

        private void DisposeEndpoints()
        {
            _endpointTicker?.Dispose();
            _endpointTicker = null;
            object[] connections;
            lock (_routingLock)
            {
                connections = new object[_endpoints.Count];
                _endpoints.Keys.CopyTo(connections, 0);
            }

            for (int i = 0; i < connections.Length; i++)
            {
                DropEndpoint(connections[i]);
            }
        }

        // ------------------------------------------------------------------ local LCN mapping

        private void MapCircuit(ushort localLcn, X25Endpoint endpoint, ushort lci)
        {
            lock (_routingLock)
            {
                _circuitByLocalLcn[localLcn] = (endpoint, lci);
                if (!_localLcnByCircuit.TryGetValue(endpoint, out Dictionary<ushort, ushort>? map))
                {
                    map = new Dictionary<ushort, ushort>();
                    _localLcnByCircuit[endpoint] = map;
                }

                map[lci] = localLcn;
            }
        }

        private void UnmapCircuit(ushort localLcn)
        {
            lock (_routingLock)
            {
                if (_circuitByLocalLcn.TryGetValue(localLcn, out (X25Endpoint Endpoint, ushort Lci) c))
                {
                    _circuitByLocalLcn.Remove(localLcn);
                    if (_localLcnByCircuit.TryGetValue(c.Endpoint, out Dictionary<ushort, ushort>? map))
                    {
                        map.Remove(c.Lci);
                    }
                }
            }
        }

        private bool TryLocalLcn(X25Endpoint endpoint, ushort lci, out ushort localLcn)
        {
            lock (_routingLock)
            {
                if (_localLcnByCircuit.TryGetValue(endpoint, out Dictionary<ushort, ushort>? map) && map.TryGetValue(lci, out localLcn))
                {
                    return true;
                }
            }

            localLcn = 0;
            return false;
        }

        private bool TryCircuit(ushort localLcn, out X25Endpoint endpoint, out ushort lci)
        {
            lock (_routingLock)
            {
                if (_circuitByLocalLcn.TryGetValue(localLcn, out (X25Endpoint Endpoint, ushort Lci) c))
                {
                    endpoint = c.Endpoint;
                    lci = c.Lci;
                    return true;
                }
            }

            endpoint = null!;
            lci = 0;
            return false;
        }

        // ------------------------------------------------------------------ receive

        /// <summary>
        /// Every packet from an XOT connection goes to that connection's engine. Nothing is decoded
        /// here any more.
        /// </summary>
        private void OnXotDataReceived(object? sender, TransportDataEventArgs e)
        {
            byte[]? data = e.Data;
            if (data == null || e.Length < 3)
            {
                return;
            }

            byte[] packet = data.Length == e.Length ? data : data.AsSpan(0, e.Length).ToArray();

            X25Endpoint? endpoint;
            if (e.Connection is XotPeerLink peerLink)
            {
                endpoint = FindEndpoint(peerLink) ?? PeerEndpointFor(peerLink);
            }
            else if (e.Connection is XotServerConnection serverConnection)
            {
                endpoint = FindEndpoint(serverConnection)
                    ?? EndpointFor(serverConnection, (sender as ITransportInterface)?.Name ?? "xot-server", serverConnection.SendPacket, isDte: false);
            }
            else
            {
                _logger.LogWarning("XOT data from an unknown connection type {Type}", sender?.GetType().Name);
                return;
            }

            RecordXotPacket(packet, Direction.RX);
            endpoint.Receive(packet);
        }

        /// <summary>
        /// Puts one packet of an XOT circuit in the history (what the packet window shows) and in the
        /// X.25 counters (what the status window shows).
        /// </summary>
        /// <param name="packet">
        /// The X.25 packet, without the XOT header.
        /// </param>
        /// <param name="direction">
        /// TX for a packet we send, RX for one we received.
        /// </param>
        /// <remarks>
        /// The serial path records its packets from the engine's own events and counts them in
        /// <c>OnX25PacketReceived</c>. XOT has no such events, so before this the packet window and
        /// the X.25 counters stayed empty for every XOT call (found 2026-11-11: a call to R1 worked
        /// and both windows showed zero). A packet too short to be an X.25 packet is not recorded;
        /// the engine rejects it on its own.
        /// </remarks>
        private void RecordXotPacket(byte[] packet, Direction direction)
        {
            if (packet == null || packet.Length < 3)
            {
                return;
            }

            X25Packet parsed;
            try
            {
                parsed = new X25Packet(packet);
            }
            catch (ArgumentException)
            {
                return;
            }

            long id = GetNextFrameId();
            X25HistoryItem item = new X25HistoryItem(direction, parsed, id);
            AddPacketWithChainTracking(id, "X25", direction);
            AddToHistory(item);

            lock (_statsLock)
            {
                bool received = direction == Direction.RX;
                if (received)
                {
                    _currentStats.X25.PacketsReceived++;
                }
                else
                {
                    _currentStats.X25.PacketsTransmitted++;
                }

                switch (parsed.PacketType)
                {
                    case X25PacketType.Data:
                        if (received)
                        {
                            _currentStats.X25.DataPacketsReceived++;
                        }
                        else
                        {
                            _currentStats.X25.DataPacketsTransmitted++;
                        }

                        break;
                    case X25PacketType.CallRequest:
                        if (received)
                        {
                            _currentStats.X25.CallRequestsReceived++;
                        }
                        else
                        {
                            _currentStats.X25.CallRequestsTransmitted++;
                        }

                        break;
                }

                StatisticsUpdated?.Invoke(this, _currentStats.Clone());
            }
        }

        /// <summary>
        /// Every X.25 packet taken out of a LAPB I frame on a serial line goes to that line's engine,
        /// the same as <see cref="OnXotDataReceived"/> does for an XOT connection.
        /// </summary>
        /// <remarks>
        /// The engine decodes a Call Request and raises the incoming call once, through
        /// <see cref="OnEndpointIncomingCall"/>, which routes it once and answers through the engine.
        /// The serial transport's own <c>IncomingCall</c> is not raised, so nothing routes it twice.
        /// The role comes from the transport's options: a DTE side is a DTE for the engine, a DCE
        /// side is the DCE. The engine has no default role here because the transport has none.
        /// </remarks>
        private void OnSerialPacketReceived(object? sender, TransportDataEventArgs e)
        {
            if (sender is not SerialTransport serial || serial.LinkRole == null)
            {
                return;
            }

            byte[]? data = e.Data;
            if (data == null || e.Length < 3)
            {
                return;
            }

            byte[] packet = data.Length == e.Length ? data : data.AsSpan(0, e.Length).ToArray();
            SerialEndpointFor(serial).Receive(packet);
        }

        /// <summary>
        /// The one X.25 engine of a serial line, created on first use. Incoming packets
        /// (<see cref="OnSerialPacketReceived"/>) and outgoing calls (<see cref="PlaceCallOnSerial"/>)
        /// both use it, so a line has one restart state and one channel range (X.25 clause 4).
        /// </summary>
        /// <param name="serial">
        /// A serial transport whose <see cref="SerialTransport.LinkRole"/> is set.
        /// </param>
        private X25Endpoint SerialEndpointFor(SerialTransport serial)
        {
            return FindEndpoint(serial)
                ?? EndpointFor(serial, serial.Name, serial.SendPacket, isDte: serial.LinkRole == SerialX25LinkRole.Dte);
        }

        /// <summary>
        /// Places an outgoing call on a serial line through the line's own X.25 engine, the same way
        /// <see cref="PlaceCallViaPeerLink"/> does for an XOT connection.
        /// </summary>
        /// <remarks>
        /// The Call Request goes out in a LAPB I frame through <see cref="SerialTransport.SendPacket"/>;
        /// the peer's Call Connected or Clear Indication comes back through
        /// <see cref="OnSerialPacketReceived"/> into the same engine. A call is refused while the line
        /// runs no X.25 (no complete link options) or its data link is not set up: X.25 2.4.4.1 lets
        /// information frames go only once the link is set up, so there is nothing to carry the call.
        /// Unlike XOT, the restart rules of X.25 4.1.1 apply here unchanged: while the engine's own
        /// Restart Request is unconfirmed, the engine holds the call and sends it after the restart.
        /// </remarks>
        /// <param name="serial">
        /// The serial transport the call is placed on.
        /// </param>
        /// <param name="request">
        /// The call to place.
        /// </param>
        /// <returns>
        /// True when the engine took the call; false when the line cannot carry it now.
        /// </returns>
        internal bool PlaceCallOnSerial(SerialTransport serial, X25CallRequest request)
        {
            if (serial.LinkRole == null || !serial.IsLinkUp)
            {
                _logger.LogWarning("Serial call to {Called} via {Name} refused: the X.25 data link is not up", request.CalledAddress, serial.Name);
                return false;
            }

            return PlaceCallOnEndpoint(SerialEndpointFor(serial), serial, serial.Name, request);
        }

        /// <summary>
        /// A serial data link that is no longer up takes its engine with it, so every call on it is
        /// reported cleared. The next packet after the link is set up again builds a fresh engine.
        /// </summary>
        private void OnSerialLinkStateChanged(object? sender, bool up)
        {
            if (!up && sender is SerialTransport serial)
            {
                DropEndpoint(serial);
            }
        }

        // ------------------------------------------------------------------ calls

        /// <summary>
        /// The engine for one connection of an XOT peer entry. A connection we dialled is a DTE
        /// (it places the call); one the peer opened is answered as the DCE, the same as an accepted
        /// connection of an XOT server entry. Each connection carries one circuit (RFC 1613 section 5),
        /// so each has its own engine.
        /// </summary>
        private X25Endpoint PeerEndpointFor(XotPeerLink link)
        {
            if (link.Dialed)
            {
                XotPeerConfig cfg = link.Owner.Config;
                return EndpointFor(link, link.Owner.Name, link.SendPacket, isDte: true, cfg.LciBase, cfg.LciCount);
            }

            return EndpointFor(link, link.Owner.Name, link.SendPacket, isDte: false);
        }

        /// <summary>
        /// Places an outgoing call on a connection a peer entry has just opened for it.
        /// </summary>
        private bool PlaceCallViaPeerLink(XotPeerTransport transport, XotPeerLink link, X25CallRequest request)
        {
            X25Endpoint endpoint = FindEndpoint(link) ?? PeerEndpointFor(link);
            return PlaceCallOnEndpoint(endpoint, transport, transport.Name, request);
        }

        /// <summary>
        /// A connection of an XOT peer ended: its engine is dropped and any circuit still on it is
        /// reported cleared.
        /// </summary>
        private void OnXotPeerConnectionClosed(object? sender, XotPeerLink link)
        {
            DropEndpoint(link);
        }

        private bool PlaceCallOnEndpoint(X25Endpoint endpoint, ITransportInterface transport, string name, X25CallRequest request)
        {
            ushort lci;
            lock (endpoint.Sync)
            {
                // No Restart is sent here. RFC 1613 section 6.4: Restart, Restart Confirm, DTE Reject, Diagnostic and
                // the registration packets "MUST NOT be sent over the TCP connection" and are silently discarded
                // when received; an XOT circuit has no link under it to restart. The engine used to send one and hold
                // the Call Request until it was confirmed (X.25 4.1.1, T20 = 180 s), which left every call to a peer
                // that sends no confirmation (x25emu, measured 2026-11-12) waiting for ever. The switch port
                // (XotSwitchPort) and X25Client already behave this way.
                byte[]? cud = request.CallUserData.Length == 0 ? null : request.CallUserData;
                lci = endpoint.Layer.PlaceCall(request.CalledAddress, request.CallingAddress, cud);
                if (lci != 0)
                {
                    endpoint.NoteCallPlaced(lci);
                }

                // Map the local LCN BEFORE this lock is released. The moment it is, the
                // connection's reader thread may feed the engine the peer's Call Connected (or a
                // Clear Indication), and the engine's handlers find their owner only through this
                // map. Mapped after the lock, as it was until 2026-10-21, a fast answer found no
                // local LCN and was dropped without a word, so the PAD never learned the call was
                // up (ProtocolRuntimeWirePinTests failed now and then under load). Lock order is
                // Sync then _routingLock, the same order the engine's handlers use.
                if (lci != 0 && request.SourceLcn != 0)
                {
                    MapCircuit(request.SourceLcn, endpoint, lci);
                }
            }

            if (lci == 0)
            {
                _logger.LogWarning("Call to {Called} via {Name} refused by the packet layer", request.CalledAddress, name);
                return false;
            }

            CallPlacedOnEndpointForTest?.Invoke(endpoint, lci);

            request.UsedTransport = transport;
            _logger.LogInformation("Call to {Called} placed on {Name} channel {Lci} (local LCN {Local})",
                request.CalledAddress, name, lci, request.SourceLcn);
            return true;
        }

        private void OnEndpointIncomingCall(X25Layer layer, ushort lci, string called, string calling, byte[] cud)
        {
            X25Endpoint? endpoint = EndpointOf(layer);
            object? connection = ConnectionOf(endpoint);
            if (endpoint == null || connection == null)
            {
                return;
            }

            ITransportInterface? transport = TransportOf(connection);

            X25CallRequest request = new X25CallRequest(called, calling, null, cud)
            {
                SourceLcn = lci,
                SourceConnection = connection,
                UsedTransport = transport,
            };

            // One routing decision for every line (2026-11-18): an XOT server connection, an XOT peer
            // connection, a serial line and a link manager link all hand the call to the router first,
            // and the line only carries out the answer. A refusal goes out as a Clear Request from this
            // connection's own engine, with the router's cause and diagnostic (X.25 4.1.7; the causes
            // are on X25CallDecision, from Table C.1). Until 2026-11-18 only the XOT server and the
            // serial line asked the router (with 0x0B for no route); the XOT server's address list was
            // checked here with diagnostic 0, and a call on an XOT peer link or a link manager link went
            // straight to the local service lookup, which cleared it with 0x0D.
            //
            // A line set to wait for the program's answer: the router decides with that in mind, and a
            // call it passes is offered to the program instead of a service.
            bool waitsForProgram = (transport is XotServerTransport waitingXot && connection is XotServerConnection && !waitingXot.AnswerIncomingCallsAutomatically)
                || (connection is SerialTransport waitingSerial && !waitingSerial.AnswerIncomingCallsAutomatically);
            X25CallDecision decision = _x25Router.DecideIncomingCall(request, transport, waitsForProgram);
            if (decision.IsRefused)
            {
                _logger.LogWarning("Incoming call to {Called} on {Name} channel {Lci} refused by the router: {Decision}", called, endpoint.Name, lci, decision);
                layer.ClearCall(lci, decision.Cause, decision.Diagnostic);
                return;
            }

            // Offered through the transport's IncomingCall event; the call stays in p3 until the program
            // calls AcceptCall or RejectCall (AcceptOfferedCall / RejectOfferedCall below), or until the
            // engine's T11 runs out and clears it (X.25 4.1.4, Table D.1: 180 s). Nothing is routed to a
            // service yet: a local service that took the call now could start sending before the call is
            // accepted. The call is remembered before the offer, so a program that answers inside the
            // event finds it.
            if (waitsForProgram)
            {
                RememberOfferedCall(request, endpoint, lci);
                _logger.LogInformation("Incoming call to {Called} on {Name} channel {Lci} offered to the program; it waits for an answer", called, endpoint.Name, lci);
                if (transport is XotServerTransport offerXot)
                {
                    offerXot.OfferIncomingCall(request);
                }
                else
                {
                    ((SerialTransport)connection).OfferIncomingCall(request);
                }

                return;
            }

            // Delivered to the local service. The routing layer allocates the local LCN, maps the
            // circuit and binds the service, but sends nothing: the engine below owns this circuit, so
            // the answer goes out through it. That is also why a link manager link works here - it has
            // no ITransportInterface.
            X25CallDecision delivered = RoutingLayer.RouteIncomingCall(request, endpoint.Name);
            if (!delivered.IsRefused)
            {
                if (!layer.AcceptCall(lci))
                {
                    _logger.LogWarning("Incoming call on {Name} channel {Lci}: Call Accepted refused by the engine", endpoint.Name, lci);
                }

                return;
            }

            _logger.LogWarning("Incoming call to {Called} on {Name} not taken by the local service: {Decision}", called, endpoint.Name, delivered);
            layer.ClearCall(lci, delivered.Cause, delivered.Diagnostic);
        }
        // ------------------------------------------------------------------ calls waiting for the program

        /// <summary>
        /// One incoming call offered to the program and not answered yet: the request object the
        /// program was given, and the engine and channel the answer goes to.
        /// </summary>
        private sealed class OfferedCall
        {
            public OfferedCall(X25CallRequest request, X25Endpoint endpoint, ushort lci)
            {
                Request = request;
                Endpoint = endpoint;
                Lci = lci;
            }

            public X25CallRequest Request { get; }

            public X25Endpoint Endpoint { get; }

            public ushort Lci { get; }
        }

        /// <summary>
        /// Calls offered to the program and not answered yet. Guarded by <c>_routingLock</c>. Short:
        /// one entry per call in p3 on a waiting entry.
        /// </summary>
        private readonly List<OfferedCall> _offeredCalls = new List<OfferedCall>();

        private void RememberOfferedCall(X25CallRequest request, X25Endpoint endpoint, ushort lci)
        {
            lock (_routingLock)
            {
                _offeredCalls.Add(new OfferedCall(request, endpoint, lci));
            }
        }

        /// <summary>
        /// Takes an offered call out of the list by the request object the program was given.
        /// </summary>
        private OfferedCall? TakeOfferedCall(X25CallRequest request)
        {
            lock (_routingLock)
            {
                for (int i = 0; i < _offeredCalls.Count; i++)
                {
                    if (ReferenceEquals(_offeredCalls[i].Request, request))
                    {
                        OfferedCall found = _offeredCalls[i];
                        _offeredCalls.RemoveAt(i);
                        return found;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Forgets offered calls whose channel was cleared (by the caller, or by the engine at T11);
        /// with <paramref name="lci"/> 0 every offered call of the endpoint is forgotten (the
        /// connection is gone).
        /// </summary>
        private void ForgetOfferedCalls(X25Endpoint endpoint, ushort lci)
        {
            lock (_routingLock)
            {
                for (int i = _offeredCalls.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(_offeredCalls[i].Endpoint, endpoint) && (lci == 0 || _offeredCalls[i].Lci == lci))
                    {
                        _offeredCalls.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>
        /// The program's "accept" for a call offered by <see cref="OnEndpointIncomingCall"/>: the
        /// call is routed to its local service the same way an automatic entry routes it, and the
        /// engine sends the Call Accepted (X.25 4.1.4), which puts the channel in data transfer (p4).
        /// A call no local service takes is cleared with the cause the routing layer gives
        /// (<see cref="RetroIO.Protocols.X25.Layers.X25RoutingLayer.RouteIncomingCall"/>), as on an automatic entry.
        /// </summary>
        /// <param name="request">
        /// The request object the program was given by the transport's IncomingCall event.
        /// </param>
        /// <returns>
        /// True when the Call Accepted went out; false when the call is not waiting for an answer
        /// (never offered, already answered, or cleared meanwhile - by the caller or by T11), when no
        /// service took it, or when the link refused the packet.
        /// </returns>
        internal bool AcceptOfferedCall(X25CallRequest request)
        {
            OfferedCall? offered = TakeOfferedCall(request);
            if (offered == null)
            {
                _logger.LogWarning("Accept for a call to {Called} refused: the call is not waiting for an answer", request.CalledAddress);
                return false;
            }

            X25Endpoint endpoint = offered.Endpoint;
            lock (endpoint.Sync)
            {
                if (!endpoint.Layer.Circuits.TryGetValue(offered.Lci, out PacketLayer.X25Vc? vc) || vc.State != PacketLayer.X25VcState.CallIncoming)
                {
                    _logger.LogWarning("Accept for {Name} channel {Lci} refused: the call is no longer incoming", endpoint.Name, offered.Lci);
                    return false;
                }

                X25CallDecision delivered = RoutingLayer.RouteIncomingCall(request, endpoint.Name);
                if (delivered.IsRefused)
                {
                    _logger.LogWarning("Incoming call to {Called} on {Name} accepted by the program but not taken by the local service: {Decision}", request.CalledAddress, endpoint.Name, delivered);
                    endpoint.Layer.ClearCall(offered.Lci, delivered.Cause, delivered.Diagnostic);
                    return false;
                }

                if (!endpoint.Layer.AcceptCall(offered.Lci))
                {
                    _logger.LogWarning("Incoming call on {Name} channel {Lci}: Call Accepted refused by the engine", endpoint.Name, offered.Lci);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The program's "refuse" for a call offered by <see cref="OnEndpointIncomingCall"/>: the
        /// engine sends a Clear Request with the given cause and diagnostic 0 (X.25 4.1.4 and 4.1.7).
        /// </summary>
        /// <param name="request">
        /// The request object the program was given by the transport's IncomingCall event.
        /// </param>
        /// <param name="cause">
        /// The clearing cause (X.25 Table 5-7).
        /// </param>
        /// <returns>
        /// True when the Clear Request went out; false when the call is not waiting for an answer.
        /// </returns>
        internal bool RejectOfferedCall(X25CallRequest request, byte cause)
        {
            OfferedCall? offered = TakeOfferedCall(request);
            if (offered == null)
            {
                _logger.LogWarning("Reject for a call to {Called} refused: the call is not waiting for an answer", request.CalledAddress);
                return false;
            }

            X25Endpoint endpoint = offered.Endpoint;
            lock (endpoint.Sync)
            {
                if (!endpoint.Layer.Circuits.TryGetValue(offered.Lci, out PacketLayer.X25Vc? vc) || vc.State != PacketLayer.X25VcState.CallIncoming)
                {
                    _logger.LogWarning("Reject for {Name} channel {Lci} refused: the call is no longer incoming", endpoint.Name, offered.Lci);
                    return false;
                }

                _logger.LogInformation("Incoming call to {Called} on {Name} channel {Lci} refused by the program, cause 0x{Cause:X2}",
                    request.CalledAddress, endpoint.Name, offered.Lci, cause);
                return endpoint.Layer.ClearCall(offered.Lci, cause, X25Diagnostic.None);
            }
        }

        /// <summary>
        /// Test seam: the X.25 engine the runtime keeps for a connection (an XOT connection, or a
        /// serial transport), or null when none exists yet. Lets a test drive the engine's clock.
        /// </summary>
        internal X25Endpoint? EndpointForTest(object connection)
        {
            return FindEndpoint(connection);
        }

        private void OnEndpointCallConnected(X25Layer layer, ushort lci)
        {
            X25Endpoint? endpoint = EndpointOf(layer);
            if (endpoint == null || !TryLocalLcn(endpoint, lci, out ushort localLcn))
            {
                return;
            }

            Modules.PadClient? pad = null;
            lock (_routingLock)
            {
                if (_lcnBindings.TryGetValue(localLcn, out IProtocolModule? module) && module is Modules.PadClient pc)
                {
                    pad = pc;
                }
            }

            pad?.NotifyCallAccepted(localLcn);
        }

        private void OnEndpointData(X25Layer layer, ushort lci, byte[] data, int length, bool qBit, bool mBit, bool dBit)
        {
            X25Endpoint? endpoint = EndpointOf(layer);
            if (endpoint == null || !TryLocalLcn(endpoint, lci, out ushort localLcn))
            {
                return;
            }

            byte[] userData = data.Length == length ? data : data.AsSpan(0, length).ToArray();

            Services.IProtocolService? service = FindServiceForLcn(localLcn);
            if (service != null)
            {
                service.HandleData(localLcn, userData, qBit);
                return;
            }

            Modules.PadClient? pad = null;
            lock (_routingLock)
            {
                if (_lcnBindings.TryGetValue(localLcn, out IProtocolModule? module) && module is Modules.PadClient pc)
                {
                    pad = pc;
                }
                else
                {
                    _logger.LogWarning("No service or module bound to LCN {LCN} for incoming data", localLcn);
                }
            }

            if (pad != null)
            {
                // The PAD reads the user data and the Q bit out of an X.25 packet keyed by the local
                // LCN; the sequence numbers it sees are not the wire's, and it does not use them.
                byte[] shaped = X25PacketBuilder.CreateDataPacket(localLcn, 0, 0, userData, mBit, qBit, dBit);
                X25Packet packet = new X25Packet(shaped) { LCN = localLcn };
                pad.OnX25Event(new X25PacketEventArgs
                {
                    Packet = packet,
                    Direction = PacketDirection.Receive,
                    Timestamp = DateTime.UtcNow,
                });
            }
        }

        private void OnEndpointCleared(X25Layer layer, ushort lci, byte cause, byte diagnostic)
        {
            X25Endpoint? endpoint = EndpointOf(layer);
            if (endpoint == null)
            {
                return;
            }

            // The engine reports Cleared inside its lock, so Sync is already held here.
            endpoint.ForgetCircuit(lci);

            // A call still waiting for the program's answer is gone (the caller cleared it, or T11
            // ran out): a late AcceptCall or RejectCall for it must find nothing.
            ForgetOfferedCalls(endpoint, lci);
            if (!TryLocalLcn(endpoint, lci, out ushort localLcn))
            {
                return;
            }

            UnmapCircuit(localLcn);
            DeliverCleared(localLcn, cause, diagnostic);
            CloseDialledLinkAfterClear(endpoint);
        }

        /// <summary>
        /// An XOT connection is closed once its circuit is cleared, whichever end opened it. RFC 1613
        /// section 5: "A separate TCP connection MUST be used for each X.25 virtual circuit", so a
        /// connection that has carried a call never carries another; keeping it only lets the other
        /// end put a call on it. (Section 5 also lets an end keep a connection after the circuit is
        /// cleared, but x25emu and our own switch both close it since 2026-11-18, and an end that
        /// kept it would race that close.) Until 2026-11-18 a connection the peer opened was left
        /// for the peer to close. It is closed on another thread: this runs inside the engine's lock.
        /// </summary>
        private void CloseDialledLinkAfterClear(X25Endpoint endpoint)
        {
            if (ConnectionOf(endpoint) is XotPeerLink link)
            {
                ThreadPool.QueueUserWorkItem(_ => link.CloseGracefully());
            }
        }

        /// <summary>
        /// Tells whoever holds a local LCN that its call is over, and unbinds it.
        /// </summary>
        /// <summary>
        /// Clears a call by its local channel number, wherever the circuit lives.
        /// </summary>
        /// <remarks>
        /// A circuit carried by a connection - an XOT connection or a LinkManager link - is cleared
        /// by that connection's engine, which puts the Clear Request on the wire and runs T23. A
        /// circuit that is in process, a PAD calling a local service, has no wire and no engine, so
        /// the owner is simply told the call is gone. Before this, both went to the retired
        /// process-wide engine, which knew about neither.
        /// </remarks>
        /// <param name="lcn">
        /// The runtime's local channel number.
        /// </param>
        /// <param name="cause">
        /// Clearing cause (X.25 Table 5-7).
        /// </param>
        /// <param name="diagnostic">
        /// Diagnostic code (Annex E).
        /// </param>
        public void ClearCallByLocalLcn(ushort lcn, byte cause, byte diagnostic)
        {
            if (ClearMappedCall(lcn, cause, diagnostic))
            {
                return;
            }

            ClearLocally(lcn, cause, diagnostic);
        }

        /// <summary>
        /// Clears a circuit that runs in process: a PAD talking to a local service, with no link
        /// and no engine under it.
        /// </summary>
        /// <remarks>
        /// The same shape as <see cref="DeliverLocally"/>. A Clear Request is built so the history
        /// and the packet views show the circuit ending the way they would for a real one, the bound
        /// module is told, and the local terminal service closes its session. Before 2026-09-06 the
        /// clear went to the retired process-wide engine, whose reply was bounced back into the
        /// process and reached the service that way.
        /// </remarks>
        /// <param name="lcn">
        /// The runtime's local channel number.
        /// </param>
        /// <param name="cause">
        /// Clearing cause (X.25 Table 5-7).
        /// </param>
        /// <param name="diagnostic">
        /// Diagnostic code (Annex E).
        /// </param>
        private void ClearLocally(ushort lcn, byte cause, byte diagnostic)
        {
            byte[] built = X25PacketBuilder.CreateClearRequest(lcn, cause, diagnostic);

            PacketHistory.FirePacketTransmitted(new X25PacketEventArgs
            {
                Packet = new X25Packet(built) { Direction = PacketDirection.Transmit, LCN = lcn },
                Direction = PacketDirection.Transmit,
                Timestamp = DateTime.UtcNow,
                PacketId = GetNextFrameId(),
            });

            X25PacketEventArgs received = new X25PacketEventArgs
            {
                Packet = new X25Packet(built) { Direction = PacketDirection.Receive, LCN = lcn },
                Direction = PacketDirection.Receive,
                Timestamp = DateTime.UtcNow,
                PacketId = GetNextFrameId(),
            };

            RouteX25EventToBoundModule(lcn, received);
            LocalTad.HandleCallClear(lcn);

            DeliverCleared(lcn, cause, diagnostic);
        }

        private void DeliverCleared(ushort localLcn, byte cause, byte diagnostic)
        {
            Services.IProtocolService? service = FindServiceForLcn(localLcn);
            service?.HandleClear(localLcn, cause, diagnostic);

            Modules.PadClient? pad = null;
            lock (_routingLock)
            {
                if (_lcnBindings.TryGetValue(localLcn, out IProtocolModule? module) && module is Modules.PadClient pc)
                {
                    pad = pc;
                }

                _lcnServiceBindings.Remove(localLcn);
                _lcnBindings.Remove(localLcn);
            }

            if (pad != null)
            {
                X25Packet packet = new X25Packet(X25PacketBuilder.CreateClearRequest(localLcn, cause, diagnostic)) { LCN = localLcn };
                pad.OnX25Event(new X25PacketEventArgs
                {
                    Packet = packet,
                    Direction = PacketDirection.Receive,
                    Timestamp = DateTime.UtcNow,
                });
            }
        }

        private void OnEndpointReset(X25Layer layer, ushort lci, byte cause, byte diagnostic)
        {
            X25Endpoint? endpoint = EndpointOf(layer);
            if (endpoint == null || !TryLocalLcn(endpoint, lci, out ushort localLcn))
            {
                return;
            }

            _logger.LogWarning("X.25 circuit reset on {Name} channel {Lci} (local LCN {Local}): cause 0x{Cause:X2} diag 0x{Diag:X2}",
                endpoint.Name, lci, localLcn, cause, diagnostic);
        }

        private void OnEndpointRestart(X25Layer layer, byte cause, byte diagnostic)
        {
            // The engine has already raised Cleared for every call it freed; the mappings went with
            // them. Nothing else to do but say so.
            X25Endpoint? endpoint = EndpointOf(layer);
            _logger.LogInformation("X.25 restart on {Name}: cause 0x{Cause:X2} diag 0x{Diag:X2}", endpoint?.Name, cause, diagnostic);
        }

        private void OnEndpointRestartTimedOut(X25Layer layer)
        {
            X25Endpoint? endpoint = EndpointOf(layer);
            _logger.LogError("X.25 restart on {Name} was never confirmed - the link is most likely dead", endpoint?.Name);
        }

        // ------------------------------------------------------------------ send / clear from the modules

        /// <summary>
        /// Sends application data on a local LCN that is carried by an XOT endpoint, waiting up to
        /// two seconds for a closed window or a busy peer as the PAD has always expected.
        /// </summary>
        public MappedSendOutcome SendMappedData(ushort lcn, byte[] data, int length)
        {
            byte[] payload = data.Length == length ? data : data.AsSpan(0, length).ToArray();

            // A call the router placed itself on an XOT_Client route (FinalizeOutboundXotCall). Its send does not wait
            // for the window: a shut window is reported as a refusal, which the caller already handles.
            if (TryRouterCall(lcn, out XotOutgoingCall routerCall))
            {
                return routerCall.Send(payload) ? MappedSendOutcome.Sent : MappedSendOutcome.RefusedByFlowControl;
            }

            if (!TryCircuit(lcn, out X25Endpoint endpoint, out ushort lci))
            {
                return MappedSendOutcome.NotMapped;
            }

            return endpoint.SendBlocking(lci, payload, XotSendTimeoutMs) ? MappedSendOutcome.Sent : MappedSendOutcome.RefusedByFlowControl;
        }

        /// <summary>
        /// Clears a call on a local LCN carried by an XOT endpoint. False when the LCN is not XOT-mapped.
        /// </summary>
        public bool ClearMappedCall(ushort lcn, byte cause, byte diagnostic)
        {
            if (TryRouterCall(lcn, out XotOutgoingCall routerCall))
            {
                return routerCall.Clear(cause, diagnostic);
            }

            if (!TryCircuit(lcn, out X25Endpoint endpoint, out ushort lci))
            {
                return false;
            }

            lock (endpoint.Sync)
            {
                endpoint.Layer.ClearCall(lci, cause, diagnostic);
            }

            return true;
        }

        /// <summary>
        /// Carries out a request from a built-in service (the TAD). See
        /// <see cref="IProtocolRuntime.DeliverServiceOutput"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A call with a module bound to its channel is a PAD client in this same process: the
        /// module is given the packet the exchange would have been on a wire, as
        /// <see cref="DeliverLocally"/> does for the other direction. Every other call is carried by
        /// an XOT endpoint, and the request becomes an operation on that endpoint's one engine: the
        /// engine numbers the packet, and the Q, M and D bits the service asked for go out with it.
        /// A Call Accepted needs nothing there; the endpoint sent it when the call was accepted.
        /// </para>
        /// <para>
        /// Requests of one call are queued and sent in order by one worker at a time, because a send
        /// can wait for the window and must not run on the thread that delivers the acknowledgements
        /// that open it - and a thread-pool item per request would let two of them overtake each
        /// other.
        /// </para>
        /// </remarks>
        public void DeliverServiceOutput(TadOutputEventArgs output)
        {
            bool bound;
            lock (_routingLock)
            {
                bound = _lcnBindings.ContainsKey(output.Lcn);
            }

            if (bound)
            {
                DeliverServiceOutputLocally(output);
                return;
            }

            if (!TryCircuit(output.Lcn, out _, out _))
            {
                _logger.LogWarning("Service output {Kind} for LCN {LCN} dropped: no module is bound and no XOT circuit carries it", output.Kind, output.Lcn);
                return;
            }

            if (output.Kind == TadOutputKind.CallAccepted)
            {
                return;
            }

            ServiceOutbox outbox = _serviceOutboxes.GetOrAdd(output.Lcn, _ => new ServiceOutbox());
            bool startWorker;
            lock (outbox.Gate)
            {
                outbox.Pending.Enqueue(output);
                startWorker = !outbox.Draining;
                outbox.Draining = true;
            }

            if (startWorker)
            {
                ThreadPool.QueueUserWorkItem(_ => DrainServiceOutbox(output.Lcn, outbox));
            }
        }

        private void DrainServiceOutbox(ushort lcn, ServiceOutbox outbox)
        {
            while (true)
            {
                TadOutputEventArgs item;
                lock (outbox.Gate)
                {
                    if (!outbox.Pending.TryDequeue(out TadOutputEventArgs? next))
                    {
                        outbox.Draining = false;
                        return;
                    }

                    item = next;
                }

                if (item.Kind == TadOutputKind.Data)
                {
                    if (TryCircuit(lcn, out X25Endpoint endpoint, out ushort lci))
                    {
                        endpoint.SendBlocking(lci, item.Data, XotSendTimeoutMs, item.QBit, item.MBit, item.DBit);
                    }
                }
                else if (item.Kind == TadOutputKind.Clear)
                {
                    ClearMappedCall(lcn, item.Cause, item.Diagnostic);
                    _serviceOutboxes.TryRemove(lcn, out _);
                }
            }
        }

        /// <summary>
        /// The requests of one call waiting to be sent, and whether a worker is draining them.
        /// </summary>
        private sealed class ServiceOutbox
        {
            public readonly object Gate = new object();
            public readonly Queue<TadOutputEventArgs> Pending = new Queue<TadOutputEventArgs>();
            public bool Draining;
        }

        /// <summary>
        /// Gives a request from a service to the module bound to the channel, and to the packet
        /// history, as the packet the exchange would have been on a wire.
        /// </summary>
        private void DeliverServiceOutputLocally(TadOutputEventArgs output)
        {
            byte[] built;
            switch (output.Kind)
            {
                case TadOutputKind.CallAccepted:
                    built = X25PacketBuilder.CreateCallAccepted(output.Lcn, null);
                    break;
                case TadOutputKind.Data:
                    built = X25PacketBuilder.CreateDataPacket(output.Lcn, 0, 0, output.Data, output.MBit, output.QBit, output.DBit);
                    break;
                default:
                    built = X25PacketBuilder.CreateClearRequest(output.Lcn, output.Cause, output.Diagnostic);
                    break;
            }

            X25PacketEventArgs received = new X25PacketEventArgs
            {
                Packet = new X25Packet(built) { Direction = PacketDirection.Receive, LCN = output.Lcn },
                Direction = PacketDirection.Receive,
                Timestamp = DateTime.UtcNow,
                PacketId = GetNextFrameId(),
            };

            PacketHistory.FirePacketReceived(received);
            RouteX25EventToBoundModule(output.Lcn, received);
        }

        /// <summary>
        /// Delivers data on a logical channel that has no link under it: a PAD client talking to a
        /// service in this same process.
        /// </summary>
        /// <remarks>
        /// <para>
        /// There is no X.25 link here and never was. Until 2026-09-06 this path ran through the old
        /// packet-layer engine, which built a data packet, raised <c>DataToSend</c>, and was answered
        /// by <c>IsLocalConnection</c> returning <c>true</c> unconditionally - so the packet was
        /// immediately unwrapped and handed back into the process. The engine was doing nothing but
        /// packing and unpacking bytes.
        /// </para>
        /// <para>
        /// The packet is still BUILT, because the history and the desktop packet views want to show
        /// what the exchange would look like on a wire, and the bound module is still given the
        /// receive-direction event it has always had. What is gone is the pretence that a packet
        /// layer was involved.
        /// </para>
        /// </remarks>
        private void DeliverLocally(ushort lcn, byte[] data, bool mBit, bool qBit, bool dBit)
        {
            byte[] built = X25PacketBuilder.CreateDataPacket(lcn, 0, 0, data, mBit, qBit, dBit);

            PacketHistory.FirePacketTransmitted(new X25PacketEventArgs
            {
                Packet = new X25Packet(built) { Direction = PacketDirection.Transmit, LCN = lcn },
                Direction = PacketDirection.Transmit,
                Timestamp = DateTime.UtcNow,
                PacketId = GetNextFrameId(),
            });

            X25PacketEventArgs received = new X25PacketEventArgs
            {
                Packet = new X25Packet(built) { Direction = PacketDirection.Receive, LCN = lcn },
                Direction = PacketDirection.Receive,
                Timestamp = DateTime.UtcNow,
                PacketId = GetNextFrameId(),
            };

            RouteX25EventToBoundModule(lcn, received);
            LocalTad.HandleIncomingData(lcn, data);
        }

        // ------------------------------------------------------------------ lookups

        private X25Endpoint? EndpointOf(X25Layer layer)
        {
            lock (_routingLock)
            {
                foreach (X25Endpoint e in _endpoints.Values)
                {
                    if (ReferenceEquals(e.Layer, layer))
                    {
                        return e;
                    }
                }
            }

            return null;
        }

        private object? ConnectionOf(X25Endpoint? endpoint)
        {
            if (endpoint == null)
            {
                return null;
            }

            lock (_routingLock)
            {
                foreach (KeyValuePair<object, X25Endpoint> kv in _endpoints)
                {
                    if (ReferenceEquals(kv.Value, endpoint))
                    {
                        return kv.Key;
                    }
                }
            }

            return null;
        }

        private ITransportInterface? TransportOf(object connection)
        {
            if (connection is ITransportInterface t)
            {
                return t;
            }

            if (connection is XotPeerLink peerLink)
            {
                return peerLink.Owner;
            }

            if (connection is XotServerConnection sc)
            {
                foreach (ITransportInterface transport in _transportInterfaces.Values)
                {
                    if (transport is XotServerTransport server && server.Owns(sc))
                    {
                        return server;
                    }
                }
            }

            return null;
        }
    }
}
