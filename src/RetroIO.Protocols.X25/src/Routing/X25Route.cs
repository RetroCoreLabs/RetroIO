//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Transport
{
    /// <summary>
    /// A route in the older written form: an X.121 address pattern (in the style of Cisco's <c>x25 route</c>),
    /// a type, a destination and a priority. <see cref="X25Router.AddRoute"/> reads it into the one table.
    /// </summary>
    public class X25Route
    {
        /// <summary>
        /// Unique identifier for this route (e.g., "Bank-Primary", "Local-Serial")
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable description of this route
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Regular expression pattern for matching X.121 addresses
        /// Examples:
        /// - "^31064.*" - DNIC 31064 prefix match
        /// - "^2340123.*" - Specific network prefix
        /// - "^31064(001|002|003).*" - Multiple service prefixes
        /// - ".*" - Wildcard/default route
        /// </summary>
        public string X121AddressPattern { get; set; } = string.Empty;

        /// <summary>
        /// Type of transport for this route
        /// </summary>
        public RouteType Type { get; set; }

        /// <summary>
        /// Destination for this route:
        /// - Serial: Interface name (e.g., "Serial0")
        /// - XOT_Client: IP address (e.g., "192.168.1.100")
        /// - XOT_Server: Listener name (e.g., "XOT-Listener")
        /// </summary>
        public string Destination { get; set; } = string.Empty;

        /// <summary>
        /// Route priority for selection (higher values = higher priority)
        /// Used when multiple routes match the same X.121 address
        /// Enables load balancing and failover scenarios
        /// </summary>
        public int Priority { get; set; } = 100;

        /// <summary>
        /// Enable/disable this route
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Direction(s) this route supports
        /// </summary>
        public RouteDirection Direction { get; set; } = RouteDirection.Bidirectional;

        /// <summary>
        /// Optional: Alternative destinations for failover/load balancing
        /// When primary destination fails, try these in order
        /// </summary>
        public List<string> BackupDestinations { get; set; } = new List<string>();

        /// <summary>
        /// Optional: Call User Data pattern matching
        /// Additional routing criteria based on CUD field in X.25 call request
        /// </summary>
        public string? CallUserDataPattern { get; set; }

        // The matching that stood here (Matches, an unanchored regular expression search ranked only by Priority) was
        // removed on 2026-11-18. An X25Route is now only the older written form of a route: X25Router.AddRoute turns
        // it into a route of the one table (RetroIO.Protocols.X25.Routing, X121LegacyPattern), and every decision is
        // made there.

        /// <summary>
        /// Returns a one-line description of the route.
        /// </summary>
        /// <returns>
        /// The route name, its X.121 address pattern, its type with the primary destination in
        /// brackets, and its priority, as in <c>name: pattern -> type(destination) [Pri:priority]</c>.
        /// </returns>
        public override string ToString()
        {
            return $"{Name}: {X121AddressPattern} -> {Type}({Destination}) [Pri:{Priority}]";
        }
    }

    /// <summary>
    /// Types of transport interfaces for X.25 routing
    /// </summary>
    public enum RouteType
    {
        /// <summary>
        /// Serial interface (traditional X.25 over LAPB)
        /// </summary>
        Serial,

        /// <summary>
        /// XOT client (outbound X.25 over TCP connections)
        /// </summary>
        XOT_Client,

        /// <summary>
        /// XOT server (inbound X.25 over TCP listener)
        /// </summary>
        XOT_Server,

        /// <summary>
        /// Gateway route (multi-hop routing through another machine)
        /// </summary>
        Gateway,

        /// <summary>
        /// Local protocol service (built-in TAD, XMSG, etc.)
        /// </summary>
        Local
    }

    /// <summary>
    /// Direction constraints for routing
    /// </summary>
    public enum RouteDirection
    {
        /// <summary>
        /// Route supports outbound calls only (local to remote)
        /// </summary>
        Outbound,

        /// <summary>
        /// Route supports inbound calls only (remote to local)
        /// </summary>
        Inbound,

        /// <summary>
        /// Route supports both inbound and outbound calls
        /// </summary>
        Bidirectional
    }
}