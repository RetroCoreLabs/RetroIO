//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text.RegularExpressions;

namespace RetroIO.Protocols.X25.Transport
{
    /// <summary>
    /// X.25 routing entry that maps X.121 address patterns to transport destinations
    /// Based on Cisco XOT routing model with regex pattern matching
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

        /// <summary>
        /// Compiled regex pattern for performance (cached after first use)
        /// </summary>
        private Regex? _compiledPattern;

        /// <summary>
        /// Check if this route matches the given X.121 address and direction
        /// </summary>
        /// <param name="x121Address">
        /// X.121 address to match
        /// </param>
        /// <param name="direction">
        /// Call direction to check
        /// </param>
        /// <param name="callUserData">
        /// Optional call user data for additional matching
        /// </param>
        /// <returns>
        /// True if route matches, false otherwise
        /// </returns>
        public bool Matches(string x121Address, RouteDirection direction, byte[]? callUserData = null)
        {
            if (!Enabled)
                return false;

            // Check direction compatibility
            if (!SupportsDirection(direction))
                return false;

            // Check X.121 address pattern
            if (!MatchesX121Pattern(x121Address))
                return false;

            // Check Call User Data pattern if specified
            if (!string.IsNullOrEmpty(CallUserDataPattern) && callUserData != null)
            {
                var cudString = System.Text.Encoding.ASCII.GetString(callUserData);
                if (!Regex.IsMatch(cudString, CallUserDataPattern))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Check if this route supports the specified direction
        /// </summary>
        public bool SupportsDirection(RouteDirection direction)
        {
            return Direction == RouteDirection.Bidirectional ||
                   Direction == direction;
        }

        /// <summary>
        /// Check if X.121 address matches the pattern
        /// </summary>
        private bool MatchesX121Pattern(string x121Address)
        {
            try
            {
                // Cache compiled regex for performance
                _compiledPattern ??= new Regex(X121AddressPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                
                return _compiledPattern.IsMatch(x121Address);
            }
            catch (ArgumentException)
            {
                // Invalid regex pattern
                return false;
            }
        }

        /// <summary>
        /// Get all destinations for this route (primary + backups)
        /// </summary>
        public IEnumerable<string> GetAllDestinations()
        {
            yield return Destination;
            
            foreach (var backup in BackupDestinations)
            {
                yield return backup;
            }
        }

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