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

namespace RetroIO.Protocols.X25.Transport
{
    /// <summary>
    /// X.25 routing engine that matches X.121 addresses to transport interfaces
    /// Implements Cisco XOT-style routing with priority-based selection and failover
    /// </summary>
    public class X25Router
    {
        private readonly ILogger _logger;
        private readonly List<X25Route> _routes;
        private readonly Dictionary<string, ITransportInterface> _interfaces;
        private readonly object _routeLock = new object();

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
            _routes = new List<X25Route>();
            _interfaces = new Dictionary<string, ITransportInterface>();
        }

        #region Route Management

        /// <summary>
        /// Add a routing entry
        /// </summary>
        public void AddRoute(X25Route route)
        {
            if (route == null)
                throw new ArgumentNullException(nameof(route));

            lock (_routeLock)
            {
                // Remove existing route with same name
                _routes.RemoveAll(r => r.Name.Equals(route.Name, StringComparison.OrdinalIgnoreCase));
                
                // Add new route
                _routes.Add(route);
                
                // Sort by priority (highest first)
                _routes.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }

            _logger.LogInformation("X.25 route added: {Route}", route);
        }

        /// <summary>
        /// Remove a routing entry by name
        /// </summary>
        public bool RemoveRoute(string routeName)
        {
            lock (_routeLock)
            {
                var removed = _routes.RemoveAll(r => r.Name.Equals(routeName, StringComparison.OrdinalIgnoreCase));
                if (removed > 0)
                {
                    _logger.LogInformation("X.25 route removed: {RouteName}", routeName);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Clear all routes
        /// </summary>
        public void ClearRoutes()
        {
            lock (_routeLock)
            {
                var count = _routes.Count;
                _routes.Clear();
                _logger.LogInformation("Cleared {Count} X.25 routes", count);
            }
        }

        /// <summary>
        /// Get all configured routes
        /// </summary>
        public List<X25Route> GetRoutes()
        {
            lock (_routeLock)
            {
                return new List<X25Route>(_routes);
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

        #region Route Matching

        /// <summary>
        /// Find the best matching route for an X.121 address and direction
        /// Returns routes in priority order for failover scenarios
        /// </summary>
        /// <param name="calledX121Address">
        /// Called X.121 address
        /// </param>
        /// <param name="direction">
        /// Call direction
        /// </param>
        /// <param name="callUserData">
        /// Optional call user data for additional matching
        /// </param>
        /// <returns>
        /// List of matching routes ordered by priority
        /// </returns>
        public List<X25Route> FindRoutes(string calledX121Address, RouteDirection direction, byte[]? callUserData = null)
        {
            if (string.IsNullOrEmpty(calledX121Address))
                return new List<X25Route>();

            lock (_routeLock)
            {
                // Collect matching routes without LINQ
                var matchingRoutes = new List<X25Route>();
                foreach (var route in _routes)
                {
                    if (route.Matches(calledX121Address, direction, callUserData))
                    {
                        matchingRoutes.Add(route);
                    }
                }

                // Sort by priority descending (highest first)
                matchingRoutes.Sort((a, b) => b.Priority.CompareTo(a.Priority));

                // Build route names string without LINQ
                var routeNames = new System.Text.StringBuilder();
                for (int i = 0; i < matchingRoutes.Count; i++)
                {
                    if (i > 0) routeNames.Append(", ");
                    routeNames.Append(matchingRoutes[i].Name);
                }

                _logger.LogDebug("Found {Count} matching routes for X.121 address {Address} ({Direction}): {Routes}",
                               matchingRoutes.Count, calledX121Address, direction, routeNames.ToString());

                return matchingRoutes;
            }
        }

        /// <summary>
        /// Find the best matching route for an X.121 address and direction
        /// </summary>
        public X25Route? FindBestRoute(string calledX121Address, RouteDirection direction, byte[]? callUserData = null)
        {
            var routes = FindRoutes(calledX121Address, direction, callUserData);
            return routes.Count > 0 ? routes[0] : null;
        }

        #endregion

        #region Call Routing

        /// <summary>
        /// Route an outbound X.25 call request
        /// </summary>
        /// <param name="callRequest">
        /// X.25 call request to route
        /// </param>
        /// <returns>
        /// True if call was successfully routed, false otherwise
        /// </returns>
        public bool RouteOutboundCall(X25CallRequest callRequest)
        {
            if (callRequest == null)
                throw new ArgumentNullException(nameof(callRequest));

            _logger.LogInformation("Routing outbound call to X.121 address: {CalledAddress}", 
                                  callRequest.CalledAddress);

            var routes = FindRoutes(callRequest.CalledAddress, RouteDirection.Outbound, callRequest.CallUserData);
            
            if (routes.Count == 0)
            {
                _logger.LogWarning("No route found for outbound call to X.121 address: {CalledAddress}", 
                                  callRequest.CalledAddress);
                return false;
            }

            // Try routes in priority order (including backup destinations)
            foreach (var route in routes)
            {
                foreach (var destination in route.GetAllDestinations())
                {
                    if (_interfaces.TryGetValue(destination, out var transportInterface))
                    {
                        try
                        {
                            _logger.LogDebug("Attempting call via route {RouteName} to destination {Destination}", 
                                           route.Name, destination);

                            var success = transportInterface.PlaceCall(callRequest);
                            if (success)
                            {
                                callRequest.UsedTransport = transportInterface;
                                _logger.LogInformation("Outbound call successfully routed via {RouteName} to {Destination}",
                                                     route.Name, destination);
                                return true;
                            }
                            else
                            {
                                _logger.LogDebug("Call failed via {RouteName} to {Destination}, trying next option", 
                                               route.Name, destination);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error placing call via route {RouteName} to {Destination}", 
                                           route.Name, destination);
                        }
                    }
                    else if (route.Type == RouteType.XOT_Client)
                    {
                        // For XOT client routes, destination is IP address - create dynamic connection
                        try
                        {
                            var success = PlaceXotCall(destination, callRequest);
                            if (success)
                            {
                                _logger.LogInformation("Outbound XOT call successfully placed to {Destination}", destination);
                                return true;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error placing XOT call to {Destination}", destination);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Transport interface not found for destination: {Destination}", destination);
                    }
                }
            }

            _logger.LogError("All routing attempts failed for outbound call to X.121 address: {CalledAddress}", 
                           callRequest.CalledAddress);
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

            List<X25Route> routes = FindRoutes(called, RouteDirection.Inbound, callRequest.CallUserData);
            if (routes.Count == 0)
            {
                _logger.LogWarning("Incoming call to {Called} on {Line}: no inbound route - not obtainable", called, line);
                return X25CallDecision.NoRoute();
            }

            if (programWaits)
            {
                _logger.LogInformation("Incoming call to {Called} on {Line} passed by route {Route}; offered to the program", called, line, routes[0].Name);
                return X25CallDecision.DeliverLocally();
            }

            Func<string, byte[], bool>? serviceAvailable = LocalServiceAvailable;
            if (serviceAvailable == null || !serviceAvailable(called, callRequest.CallUserData ?? Array.Empty<byte>()))
            {
                _logger.LogWarning("Incoming call to {Called} on {Line}: route {Route} matches but no local service takes the address - not obtainable", called, line, routes[0].Name);
                return X25CallDecision.NoRoute();
            }

            _logger.LogInformation("Incoming call to {Called} on {Line} passed by route {Route}; delivered to the local service", called, line, routes[0].Name);
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
                // Count enabled routes without LINQ
                int enabledCount = 0;
                foreach (var route in _routes)
                {
                    if (route.Enabled) enabledCount++;
                }

                // Build routes by type dictionary without LINQ
                var routesByType = new Dictionary<RouteType, int>();
                foreach (var route in _routes)
                {
                    if (routesByType.ContainsKey(route.Type))
                    {
                        routesByType[route.Type]++;
                    }
                    else
                    {
                        routesByType[route.Type] = 1;
                    }
                }

                return new RoutingStatistics
                {
                    TotalRoutes = _routes.Count,
                    EnabledRoutes = enabledCount,
                    RegisteredInterfaces = _interfaces.Count,
                    RoutesByType = routesByType
                };
            }
        }

        /// <summary>
        /// Test route matching without actually placing a call
        /// </summary>
        public List<X25Route> TestRoute(string x121Address, RouteDirection direction)
        {
            return FindRoutes(x121Address, direction);
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
        public Dictionary<RouteType, int> RoutesByType { get; set; } = new();
    }
}