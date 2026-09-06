using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.PacketLayer;
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
    /// <b>Locking.</b> <c>_routingLock</c> guards the tables. An endpoint's engine is touched only
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
        private readonly Dictionary<ushort, (X25Endpoint Endpoint, ushort Lci)> _circuitByLocalLcn = new Dictionary<ushort, (X25Endpoint, ushort)>();
        private readonly Dictionary<X25Endpoint, Dictionary<ushort, ushort>> _localLcnByCircuit = new Dictionary<X25Endpoint, Dictionary<ushort, ushort>>();
        private Timer? _endpointTicker;

        // ------------------------------------------------------------------ endpoints

        /// <summary>
        /// The endpoint for a connection, created on first use.
        /// </summary>
        /// <param name="connection">
        /// The connection object the transport reports: an <see cref="XotClientTransport"/> (one
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
        private X25Endpoint EndpointFor(object connection, string name, Func<byte[], bool> send, bool isDte, ushort lciBase = 1, ushort lciCount = 4095)
        {
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
                    DefaultWindowSize = _config.X25.DefaultPacketWindowSize,
                    DefaultPacketSize = _config.X25.MaxPacketSize,
                    CallRequestTimeoutMs = (int)_config.X25.T21Timeout.TotalMilliseconds,
                    ResetRequestTimeoutMs = (int)_config.X25.T22Timeout.TotalMilliseconds,
                    ClearRequestTimeoutMs = (int)_config.X25.T23Timeout.TotalMilliseconds,
                    RestartTimeoutMs = (int)_config.X25.T20Timeout.TotalMilliseconds,
                };

                X25Endpoint endpoint = new X25Endpoint(name, send, config, _logger);
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
            if (e.Connection is XotServerConnection serverConnection)
            {
                endpoint = FindEndpoint(serverConnection)
                    ?? EndpointFor(serverConnection, (sender as ITransportInterface)?.Name ?? "xot-server", serverConnection.SendPacket, isDte: false);
            }
            else if (sender is XotClientTransport client)
            {
                endpoint = FindEndpoint(client)
                    ?? EndpointFor(client, client.Name, client.SendPacket, isDte: true,
                        client.InterfaceConfig?.LciBase ?? (ushort)1, client.InterfaceConfig?.LciCount ?? (ushort)16);
            }
            else
            {
                _logger.LogWarning("XOT data from an unknown connection type {Type}", sender?.GetType().Name);
                return;
            }

            endpoint.Receive(packet);
        }

        // ------------------------------------------------------------------ calls

        /// <summary>
        /// Places an outgoing call over a dialled XOT connection: the endpoint restarts the link the
        /// first time (X.25 4.1; the engine holds the Call Request until the Restart Confirmation),
        /// then places the call. The engine picks the logical channel; the runtime's local LCN
        /// (<see cref="X25CallRequest.SourceLcn"/>) is mapped onto it.
        /// </summary>
        private bool PlaceCallViaEndpoint(XotClientTransport transport, X25CallRequest request)
        {
            X25Endpoint endpoint = FindEndpoint(transport)
                ?? EndpointFor(transport, transport.Name, transport.SendPacket, isDte: true,
                    transport.InterfaceConfig?.LciBase ?? (ushort)1, transport.InterfaceConfig?.LciCount ?? (ushort)16);

            ushort lci;
            lock (endpoint.Sync)
            {
                if (!endpoint.RestartSent)
                {
                    endpoint.RestartSent = true;
                    endpoint.Layer.Restart(0, 0);
                }

                byte[]? cud = request.CallUserData.Length == 0 ? null : request.CallUserData;
                lci = endpoint.Layer.PlaceCall(request.CalledAddress, request.CallingAddress, cud);
            }

            if (lci == 0)
            {
                _logger.LogWarning("XOT call to {Called} via {Name} refused by the packet layer", request.CalledAddress, transport.Name);
                return false;
            }

            if (request.SourceLcn != 0)
            {
                MapCircuit(request.SourceLcn, endpoint, lci);
            }

            request.UsedTransport = transport;
            _logger.LogInformation("XOT call to {Called} placed on {Name} channel {Lci} (local LCN {Local})",
                request.CalledAddress, transport.Name, lci, request.SourceLcn);
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

            // The routing layer allocates the local LCN, binds the service and calls back into
            // RegisterXotConnection with (localLcn, connection, lci), which maps the circuit.
            bool accepted = transport != null && RoutingLayer.HandleIncomingCall(request, transport);
            if (accepted)
            {
                if (!layer.AcceptCall(lci))
                {
                    _logger.LogWarning("XOT incoming call on {Name} channel {Lci}: Call Accepted refused by the link", endpoint.Name, lci);
                }

                return;
            }

            _logger.LogWarning("XOT incoming call to {Called} on {Name} not routed - clearing", called, endpoint.Name);
            layer.ClearCall(lci, X25Cause.NotObtainable, X25Diagnostic.None);
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
                service.HandleData(localLcn, userData);
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
            if (endpoint == null || !TryLocalLcn(endpoint, lci, out ushort localLcn))
            {
                return;
            }

            UnmapCircuit(localLcn);
            DeliverCleared(localLcn, cause, diagnostic);
        }

        /// <summary>
        /// Tells whoever holds a local LCN that its call is over, and unbinds it.
        /// </summary>
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
        public XotSendOutcome SendDataViaXotIfMapped(ushort lcn, byte[] data, int length)
        {
            if (!TryCircuit(lcn, out X25Endpoint endpoint, out ushort lci))
            {
                return XotSendOutcome.NotXotMapped;
            }

            byte[] payload = data.Length == length ? data : data.AsSpan(0, length).ToArray();
            return endpoint.SendBlocking(lci, payload, XotSendTimeoutMs) ? XotSendOutcome.Sent : XotSendOutcome.RefusedByFlowControl;
        }

        /// <summary>
        /// Clears a call on a local LCN carried by an XOT endpoint. False when the LCN is not XOT-mapped.
        /// </summary>
        public bool ClearXotCall(ushort lcn, byte cause, byte diagnostic)
        {
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
        /// Translates a packet built by the old engine for an XOT-carried channel into the
        /// corresponding operation on the endpoint's engine. Data and Clear are the only two that
        /// mean anything; a Call Accepted was already sent by the endpoint when the call was
        /// accepted, and flow control belongs to the engine alone.
        /// </summary>
        private void ForwardOldLayerPacketToEndpoint(ushort localLcn, X25PacketEventArgs eventArgs)
        {
            X25Packet packet = eventArgs.Packet;
            switch (packet.PacketType)
            {
                case X25PacketType.Data:
                    // Off the routing lock: SendBlocking may wait for the window.
                    ThreadPool.QueueUserWorkItem(_ => SendDataViaXotIfMapped(localLcn, packet.Data, packet.Data.Length));
                    break;
                case X25PacketType.ClearRequest:
                    byte cause = packet.Data.Length > 0 ? packet.Data[0] : (byte)0;
                    byte diag = packet.Data.Length > 1 ? packet.Data[1] : (byte)0;
                    ThreadPool.QueueUserWorkItem(_ => ClearXotCall(localLcn, cause, diag));
                    break;
                case X25PacketType.CallAccepted:
                    break;
                default:
                    _logger.LogDebug("Old-layer {Type} for XOT-carried LCN {LCN} dropped: the endpoint's engine owns that", packet.PacketType, localLcn);
                    break;
            }
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
