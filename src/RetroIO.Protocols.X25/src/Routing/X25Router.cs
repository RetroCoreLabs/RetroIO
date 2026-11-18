//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.PacketLayer;
using RetroIO.Protocols.X25.Routing;

namespace RetroIO.Protocols.X25.Transport
{
    /// <summary>
    /// X.25 routing engine that matches X.121 addresses to transport interfaces
    /// Implements Cisco XOT-style routing with priority-based selection and failover
    /// </summary>
    public class X25Router
    {
        private readonly ILogger _logger;
        private readonly Dictionary<string, ITransportInterface> _interfaces;
        private readonly object _routeLock = new object();

        // The one routing table (RetroIO.Protocols.X25.Routing), shared in kind with the switch. The runtime has no
        // port numbers of its own, so every name a route uses (a transport, or the IP address of an XOT_Client
        // route) is given a number here the first time it is seen, and the table carries the name.
        private readonly X121RoutingTable _table = new X121RoutingTable();
        private readonly Dictionary<string, int> _portIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Port numbers that stand for an IP address of an XOT_Client route: such a "port" is reached by dialling a
        // new XOT connection (PlaceXotCall), and is in service while any XOT client transport is active.
        private readonly HashSet<int> _dynamicXotPorts = new HashSet<int>();

        /// <summary>
        /// Creates an empty router.
        /// </summary>
        /// <param name="logger">
        /// Diagnostics logger.
        /// </param>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="logger"/> is null.
        /// </exception>
        public X25Router(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _interfaces = new Dictionary<string, ITransportInterface>();
        }

        /// <summary>
        /// The one routing table every decision of this router is made from. The routing screen shows it; to
        /// change it use <see cref="LoadNetwork"/>, <see cref="AddRoute"/> or <see cref="RemoveRoute"/>.
        /// </summary>
        public X121RoutingTable Table => _table;

        /// <summary>
        /// The number the table uses for a port name, given on first use.
        /// </summary>
        /// <param name="name">
        /// A transport name, an IP address of an XOT_Client route, or <c>local</c>.
        /// </param>
        /// <returns>
        /// The number; <see cref="X121RoutingTable.LocalPort"/> for <c>local</c>.
        /// </returns>
        public int PortId(string name)
        {
            string n = (name ?? string.Empty).Trim();
            if (string.Equals(n, X121RoutingTable.LocalPortName, StringComparison.OrdinalIgnoreCase))
            {
                return X121RoutingTable.LocalPort;
            }

            lock (_routeLock)
            {
                if (!_portIds.TryGetValue(n, out int id))
                {
                    id = _portIds.Count;
                    _portIds[n] = id;
                    _table.SetPortName(id, n);
                }

                return id;
            }
        }

        /// <summary>
        /// Replaces the table with a written one: the own network and every route, ports by name.
        /// </summary>
        /// <param name="network">
        /// The written table.
        /// </param>
        /// <exception cref="System.IO.InvalidDataException">
        /// A route is not valid; the message names it. The table is left as it was.
        /// </exception>
        public void LoadNetwork(X121NetworkConfig network)
        {
            ArgumentNullException.ThrowIfNull(network);
            lock (_routeLock)
            {
                _dynamicXotPorts.Clear();
                X121TableBuilder.Fill(_table, network, PortId);
            }

            _logger.LogInformation("X.25 routing table loaded: {Count} routes, own DNIC {Dnic}", _table.Routes.Count,
                _table.OwnDnics.Count > 0 ? _table.OwnDnics[0] : "(none)");
        }

        /// <summary>
        /// The one lookup for a call through this node's lines (<see cref="X121RoutingTable.Resolve"/>), with
        /// each port's state taken from its transport.
        /// </summary>
        /// <param name="called">
        /// The called number.
        /// </param>
        /// <param name="arrivalPort">
        /// The name of the line the call came in on, never chosen; null for a call placed here.
        /// </param>
        /// <param name="direction">
        /// Whether the call comes in or is placed here.
        /// </param>
        /// <param name="callUserData">
        /// The call user data, or null.
        /// </param>
        /// <returns>
        /// The decision, with the route, the port and why.
        /// </returns>
        public X121RouteResult ResolveCall(string called, string? arrivalPort, X121RouteDirection direction, byte[]? callUserData)
        {
            int inPort = -1;
            if (!string.IsNullOrEmpty(arrivalPort))
            {
                lock (_routeLock)
                {
                    if (_portIds.TryGetValue(arrivalPort, out int id))
                    {
                        inPort = id;
                    }
                }
            }

            return _table.Resolve(called ?? string.Empty, inPort, PortStatus, direction, callUserData);
        }

        // What the lookup needs to know of a port: a registered transport is in service while it is active; an
        // XOT_Client address while any XOT client transport is active; any other name is not a port of this node.
        private X121PortStatus PortStatus(int id) => PortStatusOf(_table.GetPortName(id));

        /// <summary>
        /// The state of a port by its name, as the lookup sees it: a registered transport is in service while it
        /// is active; the IP address of an XOT_Client route while any XOT client transport is active; any other
        /// name is not a port of this node.
        /// </summary>
        /// <param name="name">
        /// The port name.
        /// </param>
        /// <returns>
        /// The state. The routing screen uses this to try a table it has not saved yet against the real lines.
        /// </returns>
        public X121PortStatus PortStatusOf(string name)
        {
            name ??= string.Empty;
            if (_interfaces.TryGetValue(name, out ITransportInterface? t))
            {
                return new X121PortStatus(true, t.Status == TransportStatus.Active, X121AddressForm.AsReceived);
            }

            bool dynamic;
            lock (_routeLock)
            {
                dynamic = _portIds.TryGetValue(name, out int id) && _dynamicXotPorts.Contains(id);
            }

            if (dynamic)
            {
                return new X121PortStatus(true, ActiveXotClient() != null, X121AddressForm.AsReceived);
            }

            return X121PortStatus.Missing;
        }

        private XotClientTransport? ActiveXotClient()
        {
            foreach (ITransportInterface iface in _interfaces.Values)
            {
                if (iface is XotClientTransport xot && xot.Status == TransportStatus.Active)
                {
                    return xot;
                }
            }

            return null;
        }

        #region Route Management

        /// <summary>
        /// Adds a route written in the older form (an address pattern, a type, a destination, a priority) to
        /// the one table. A route with the same name is replaced. The pattern is read by
        /// <see cref="X121LegacyPattern"/>; the priority only decides between two otherwise equal routes.
        /// </summary>
        /// <param name="route">
        /// The route.
        /// </param>
        /// <exception cref="ArgumentException">
        /// The route is not valid (for example a pattern that is not a regular expression).
        /// </exception>
        public void AddRoute(X25Route route)
        {
            ArgumentNullException.ThrowIfNull(route);

            X121Route r = ToTableRoute(route);
            if (!X121RouteRules.Check(r, out string error))
            {
                throw new ArgumentException("route " + route.Name + ": " + error, nameof(route));
            }

            lock (_routeLock)
            {
                RemoveByName(route.Name);
                _table.AddRoute(r);
            }

            _logger.LogInformation("X.25 route added: {Route} ({Kind} {Match} -> {Target})", route.Name,
                X121RouteText.KindName(r.Type), r.MatchText, X121RouteText.Target(r, _table));
        }

        private X121Route ToTableRoute(X25Route route)
        {
            X121LegacyPattern.Convert(route.X121AddressPattern, out X121RouteType type, out string match);
            X121Route r = new X121Route
            {
                Name = route.Name,
                Note = route.Description,
                Enabled = route.Enabled,
                Type = type,
                Priority = route.Priority,
                CallUserDataPattern = route.CallUserDataPattern ?? string.Empty,
                Direction = route.Direction == RouteDirection.Inbound ? X121RouteDirection.Incoming
                    : route.Direction == RouteDirection.Outbound ? X121RouteDirection.Outgoing : X121RouteDirection.Both,
            };

            switch (type)
            {
                case X121RouteType.Exact:
                    r.Address = match;
                    break;
                case X121RouteType.Prefix:
                    r.Address = match;
                    r.PrefixLen = match.Length;
                    break;
                case X121RouteType.Regex:
                    r.Pattern = match;
                    break;
            }

            r.DestPort = route.Type == RouteType.Local ? X121RoutingTable.LocalPort : PortId(route.Destination);
            if (route.Type == RouteType.XOT_Client && !_interfaces.ContainsKey(route.Destination))
            {
                lock (_routeLock)
                {
                    _dynamicXotPorts.Add(r.DestPort);
                }
            }

            for (int i = 0; i < route.BackupDestinations.Count; i++)
            {
                r.BackupPorts.Add(PortId(route.BackupDestinations[i]));
            }

            return r;
        }

        private bool RemoveByName(string name)
        {
            bool removed = false;
            for (int i = _table.Routes.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_table.Routes[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    _table.DeleteRouteAt(i);
                    removed = true;
                }
            }

            return removed;
        }

        /// <summary>
        /// Removes every route with this name.
        /// </summary>
        /// <param name="routeName">
        /// The route name.
        /// </param>
        /// <returns>
        /// True when one was removed.
        /// </returns>
        public bool RemoveRoute(string routeName)
        {
            bool removed;
            lock (_routeLock)
            {
                removed = RemoveByName(routeName);
            }

            if (removed)
            {
                _logger.LogInformation("X.25 route removed: {RouteName}", routeName);
            }

            return removed;
        }

        /// <summary>
        /// Removes every route and hunt group; the own network stays.
        /// </summary>
        public void ClearRoutes()
        {
            lock (_routeLock)
            {
                int count = _table.Routes.Count;
                _table.ClearRoutes();
                _dynamicXotPorts.Clear();
                _logger.LogInformation("Cleared {Count} X.25 routes", count);
            }
        }

        #endregion
        #region Transport Interface Management

        /// <summary>
        /// Register a transport interface
        /// </summary>
        public void RegisterInterface(ITransportInterface transportInterface)
        {
            if (transportInterface == null)
                throw new ArgumentNullException(nameof(transportInterface));

            _interfaces[transportInterface.Name] = transportInterface;

            // The router does not listen to the transport's IncomingCall event. That event is the
            // offer to the program, and it is raised only after the router passed the call: the
            // runtime asks DecideIncomingCall first (decided by Ronny, 2026-11-17). Hearing the event
            // as well made the router refuse calls the program was still deciding on.

            _logger.LogInformation("X.25 transport interface registered: {InterfaceName} ({Type})", 
                                  transportInterface.Name, transportInterface.Type);
        }

        /// <summary>
        /// Unregister a transport interface
        /// </summary>
        public bool UnregisterInterface(string interfaceName)
        {
            if (_interfaces.TryGetValue(interfaceName, out var transportInterface))
            {
                _interfaces.Remove(interfaceName);
                
                _logger.LogInformation("X.25 transport interface unregistered: {InterfaceName}", interfaceName);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Get registered transport interface
        /// </summary>
        public ITransportInterface? GetInterface(string interfaceName)
        {
            _interfaces.TryGetValue(interfaceName, out var transportInterface);
            return transportInterface;
        }

        #endregion

        #region Call Routing

        /// <summary>
        /// Places a call from this node: the one lookup picks the port (its backups when it is out of order),
        /// and if placing the call there fails the remaining ports of every route that takes the number are
        /// tried in lookup order (<see cref="X121RoutingTable.LookupMulti(string, int, int, X121RouteDirection, byte[])"/>).
        /// </summary>
        /// <param name="callRequest">
        /// X.25 call request to route
        /// </param>
        /// <returns>
        /// True if call was successfully routed, false otherwise
        /// </returns>
        public bool RouteOutboundCall(X25CallRequest callRequest)
        {
            ArgumentNullException.ThrowIfNull(callRequest);

            X121RouteResult first = ResolveCall(callRequest.CalledAddress, null, X121RouteDirection.Outgoing, callRequest.CallUserData);
            _logger.LogInformation("Outbound call to {CalledAddress}: {Why}", callRequest.CalledAddress, first.Explanation);
            if (first.Outcome == X121RouteOutcome.Local)
            {
                // A route to this node is not a line a call can be placed on.
                return false;
            }

            List<int> tried = new List<int>();
            if (first.Outcome == X121RouteOutcome.Forward)
            {
                tried.Add(first.Port);
                if (TryPlace(first.Port, callRequest))
                {
                    return true;
                }
            }

            IReadOnlyList<int> all = _table.LookupMulti(callRequest.CalledAddress, -1, 16, X121RouteDirection.Outgoing, callRequest.CallUserData);
            for (int i = 0; i < all.Count; i++)
            {
                int port = all[i];
                if (tried.Contains(port) || port == X121RoutingTable.LocalPort || !PortStatus(port).InService)
                {
                    continue;
                }

                tried.Add(port);
                if (TryPlace(port, callRequest))
                {
                    return true;
                }
            }

            _logger.LogError("All routing attempts failed for outbound call to X.121 address: {CalledAddress}", callRequest.CalledAddress);
            return false;
        }

        private bool TryPlace(int port, X25CallRequest callRequest)
        {
            string name = _table.GetPortName(port);
            try
            {
                if (_interfaces.TryGetValue(name, out ITransportInterface? transportInterface))
                {
                    if (transportInterface.PlaceCall(callRequest))
                    {
                        callRequest.UsedTransport = transportInterface;
                        _logger.LogInformation("Outbound call to {CalledAddress} placed on {Port}", callRequest.CalledAddress, name);
                        return true;
                    }

                    _logger.LogDebug("Call to {CalledAddress} failed on {Port}, trying the next one", callRequest.CalledAddress, name);
                    return false;
                }

                bool dynamic;
                lock (_routeLock)
                {
                    dynamic = _dynamicXotPorts.Contains(port);
                }

                if (dynamic)
                {
                    return PlaceXotCall(name, callRequest);
                }

                _logger.LogWarning("Transport interface not found for destination: {Destination}", name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error placing call to {CalledAddress} on {Port}", callRequest.CalledAddress, name);
            }

            return false;
        }
        /// <summary>
        /// Says whether a local service takes calls to a called address with given call user data.
        /// Set by the runtime, which owns the services. Null means no local service exists.
        /// </summary>
        public Func<string, byte[], bool>? LocalServiceAvailable { get; set; }

        /// <summary>
        /// The one routing decision for a call that came in on any line of this node: an XOT server
        /// connection, an XOT peer connection, a serial line or a link manager link. The line never
        /// decides; it hands the call here first and carries out the answer - a refusal becomes a Clear
        /// Request with the returned cause and diagnostic, sent by the line's own X.25 engine on the
        /// call's channel (X.25 4.1.7).
        /// </summary>
        /// <remarks>
        /// <para>
        /// In this order:
        /// </para>
        ///  - A rule of the line bars the called address (an XOT server entry that lists the addresses it
        ///    takes calls for): Access barred, diagnostic 70 (X.25 Table C.1 row 25).
        ///  - No inbound route matches the called address: Not obtainable, diagnostic 67 (Table C.1 row 24,
        ///    "Unknown number").
        ///  - The line waits for a program to answer: delivered locally, to that program.
        ///  - A local service takes calls to the address: delivered locally, to that service.
        ///  - Nothing here takes it: Not obtainable, diagnostic 67, as above - the number has no one to
        ///    answer at this node.
        /// <para>
        /// The causes and their citations are on <see cref="X25CallDecision"/>. Until 2026-11-18 this
        /// method was <c>AnswerIncomingCall</c>: it refused a call with no route itself through the
        /// transport's RejectCall with 0x0B, and only the XOT server and serial lines asked it; an XOT
        /// peer link and a link manager link went straight to the local service lookup, which cleared
        /// with 0x0D.
        /// </para>
        /// <para>
        /// An inbound route whose destination is another line is treated like a local route: this node
        /// does not forward an incoming call out of another line (see src/BUGS.md).
        /// </para>
        /// </remarks>
        /// <param name="callRequest">
        /// The call.
        /// </param>
        /// <param name="sourceInterface">
        /// The transport of the line the call came in on; null for a link manager link, which has none.
        /// </param>
        /// <param name="programWaits">
        /// True when the line is set to offer its calls to a program and wait for its answer.
        /// </param>
        /// <returns>
        /// Deliver locally, or a refusal.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="callRequest"/> is null.
        /// </exception>
        public X25CallDecision DecideIncomingCall(X25CallRequest callRequest, ITransportInterface? sourceInterface, bool programWaits)
        {
            ArgumentNullException.ThrowIfNull(callRequest);

            string called = callRequest.CalledAddress;
            string line = sourceInterface?.Name ?? "a link";

            if (sourceInterface is XotServerTransport xotServer && !xotServer.AcceptsX121Address(called))
            {
                _logger.LogWarning("Incoming call to {Called} on {Line}: the entry does not take calls for that address - access barred", called, line);
                return X25CallDecision.Barred();
            }

            // Any enabled incoming route that takes the number lets the call in (the one table's ranking picks
            // which one is logged). Where it points is not used: see the remarks.
            X121Route? winner = _table.FindWinner(called, -1, X121RouteDirection.Incoming, callRequest.CallUserData);
            if (winner == null)
            {
                _logger.LogWarning("Incoming call to {Called} on {Line}: no inbound route - not obtainable", called, line);
                return X25CallDecision.NoRoute();
            }

            if (programWaits)
            {
                _logger.LogInformation("Incoming call to {Called} on {Line} passed by route {Route}; offered to the program", called, line, winner.Name);
                return X25CallDecision.DeliverLocally();
            }

            Func<string, byte[], bool>? serviceAvailable = LocalServiceAvailable;
            if (serviceAvailable == null || !serviceAvailable(called, callRequest.CallUserData ?? Array.Empty<byte>()))
            {
                _logger.LogWarning("Incoming call to {Called} on {Line}: route {Route} matches but no local service takes the address - not obtainable", called, line, winner.Name);
                return X25CallDecision.NoRoute();
            }

            _logger.LogInformation("Incoming call to {Called} on {Line} passed by route {Route}; delivered to the local service", called, line, winner.Name);
            return X25CallDecision.DeliverLocally();
        }
        #endregion

        #region XOT Specific Methods

        /// <summary>
        /// Place an XOT call to specified IP address
        /// This creates a dynamic XOT client connection
        /// </summary>
        private bool PlaceXotCall(string ipAddress, X25CallRequest callRequest)
        {
            _logger.LogInformation("Placing XOT call to {IpAddress} for X.121 address {CalledAddress}",
                                  ipAddress, callRequest.CalledAddress);

            // Find any active XOT_Client interface to handle the call
            XotClientTransport? xotClient = null;
            foreach (var iface in _interfaces.Values)
            {
                if (iface is XotClientTransport xot && xot.Status == TransportStatus.Active)
                {
                    xotClient = xot;
                    break;
                }
            }

            if (xotClient == null)
            {
                _logger.LogWarning("No active XOT client interface available for call to {IpAddress}", ipAddress);
                return false;
            }

            try
            {
                // Establish connection to the target IP if not already connected
                var connected = xotClient.ConnectToXotGateway(ipAddress);
                if (!connected)
                {
                    _logger.LogWarning("Failed to establish XOT connection to {IpAddress}", ipAddress);
                    return false;
                }

                // Create X.25 call request packet
                var x25CallPacket = CreateX25CallRequestPacket(callRequest);
                if (x25CallPacket == null)
                {
                    _logger.LogError("Failed to create X.25 call request packet");
                    return false;
                }

                // Send X.25 call request over XOT connection
                var success = xotClient.SendX25Packet(ipAddress, x25CallPacket);
                if (success)
                {
                    _logger.LogInformation("XOT call request sent to {IpAddress} for {CalledAddress}", ipAddress, callRequest.CalledAddress);
                    
                    // TODO: Wait for call accepted/cleared response
                    // For now, assume success
                    return true;
                }
                else
                {
                    _logger.LogWarning("Failed to send XOT call request to {IpAddress}", ipAddress);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error placing XOT call to {IpAddress}", ipAddress);
                return false;
            }
        }

        /// <summary>
        /// Create X.25 Call Request packet from X25CallRequest
        /// </summary>
        private byte[]? CreateX25CallRequestPacket(X25CallRequest callRequest)
        {
            try
            {
                // Basic X.25 Call Request packet format:
                // [GFI & LCN (3 bytes)] [Packet Type (1 byte)] [Called/Calling Address] [Facilities] [Call User Data]
                
                using var ms = new System.IO.MemoryStream();
                
                // General Format Identifier and Logical Channel Number (placeholder - LCN 1)
                ms.WriteByte(0x10); // GFI = Q=0, D=0, M=0, Q=1 (standard modulo 8)
                ms.WriteByte(0x00); // LCN high nibble = 0
                ms.WriteByte(0x01); // LCN low byte = 1
                
                // Packet Type Identifier - Call Request
                ms.WriteByte(0x0B);
                
                // Calling and Called address lengths
                var calledBytes = System.Text.Encoding.ASCII.GetBytes(callRequest.CalledAddress);
                var callingBytes = System.Text.Encoding.ASCII.GetBytes(callRequest.CallingAddress ?? "");
                
                // Address length byte (4 bits called, 4 bits calling)
                var addressLengthByte = (byte)((calledBytes.Length & 0x0F) << 4 | (callingBytes.Length & 0x0F));
                ms.WriteByte(addressLengthByte);
                
                // Called address (BCD encoded, but using ASCII for simplicity)
                ms.Write(calledBytes);
                
                // Calling address
                ms.Write(callingBytes);
                
                // Facilities (optional)
                if (callRequest.Facilities != null && callRequest.Facilities.Length > 0)
                {
                    ms.WriteByte((byte)callRequest.Facilities.Length);
                    ms.Write(callRequest.Facilities);
                }
                else
                {
                    ms.WriteByte(0x00); // No facilities
                }
                
                // Call User Data (optional)
                if (callRequest.CallUserData != null && callRequest.CallUserData.Length > 0)
                {
                    ms.Write(callRequest.CallUserData);
                }
                
                var packet = ms.ToArray();
                _logger.LogDebug("Created X.25 Call Request packet: {PacketLength} bytes for {CalledAddress}", 
                               packet.Length, callRequest.CalledAddress);
                
                return packet;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating X.25 Call Request packet");
                return null;
            }
        }

        #endregion

        // The IncomingCallRouted event that stood here was removed on 2026-11-17. It was raised only
        // from the router's own handler of the transport's IncomingCall event, which is gone: the
        // router now decides a call when the runtime asks it (DecideIncomingCall), and nothing
        // subscribed to the event.

        #region Statistics and Diagnostics

        /// <summary>
        /// Get routing statistics
        /// </summary>
        public RoutingStatistics GetStatistics()
        {
            lock (_routeLock)
            {
                int enabledCount = 0;
                Dictionary<X121RouteType, int> routesByType = new Dictionary<X121RouteType, int>();
                for (int i = 0; i < _table.Routes.Count; i++)
                {
                    X121Route route = _table.Routes[i];
                    if (route.Enabled)
                    {
                        enabledCount++;
                    }

                    routesByType.TryGetValue(route.Type, out int n);
                    routesByType[route.Type] = n + 1;
                }

                return new RoutingStatistics
                {
                    TotalRoutes = _table.Routes.Count,
                    EnabledRoutes = enabledCount,
                    RegisteredInterfaces = _interfaces.Count,
                    RoutesByType = routesByType
                };
            }
        }
        #endregion
    }

    /// <summary>
    /// Routing statistics
    /// </summary>
    public class RoutingStatistics
    {
        /// <summary>
        /// Gets or sets the total number of routes.
        /// </summary>
        public int TotalRoutes { get; set; }
        /// <summary>
        /// Gets or sets the number of enabled routes.
        /// </summary>
        public int EnabledRoutes { get; set; }
        /// <summary>
        /// Gets or sets the number of registered interfaces.
        /// </summary>
        public int RegisteredInterfaces { get; set; }
        /// <summary>
        /// Gets or sets the number of routes for each route type.
        /// </summary>
        public Dictionary<X121RouteType, int> RoutesByType { get; set; } = new();
    }
}