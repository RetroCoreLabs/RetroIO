//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using RetroIO.Protocols.X25.Routing;

namespace RetroIO.Protocols.X25.Switch
{
    /// <summary>
    /// The fully-parsed configuration of a switch: identity, ports, routing table, accounts, and management
    /// users.
    /// </summary>
    /// <remarks>
    /// Routing reuses the generic <see cref="X121RoutingTable"/> (routes + hunt groups) rather than a
    /// switch-specific route class, so there is one routing model across the codebase.
    /// </remarks>
    public sealed class SwitchConfig
    {
        /// <summary>
        /// This switch's DNIC. Defaults to <c>2342</c> when the config omits it.
        /// </summary>
        public string Dnic { get; set; } = "2342";

        /// <summary>
        /// Loop-prevention hop limit. The default is 15.
        /// </summary>
        public int MaxHops { get; set; } = 15;

        /// <summary>
        /// Telnet management listener port; 0 disables it. The default is 9000.
        /// </summary>
        public ushort MgmtPort { get; set; } = 9000;

        /// <summary>
        /// TCP port for the packet-monitoring stream that remote viewers subscribe to; 0 disables it.
        /// </summary>
        public ushort MonitorPort { get; set; }

        /// <summary>
        /// Human-readable switch name (blank if unset).
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Switch location (blank if unset).
        /// </summary>
        public string Location { get; set; } = string.Empty;

        /// <summary>
        /// Switch coordinates (blank if unset).
        /// </summary>
        public string Coordinates { get; set; } = string.Empty;

        /// <summary>
        /// Administrative contact (blank if unset).
        /// </summary>
        public string Contact { get; set; } = string.Empty;

        /// <summary>
        /// Reserved management X.25 address; blank disables the PAD/NMC management surface.
        /// </summary>
        public string MgmtAddr { get; set; } = string.Empty;

        /// <summary>
        /// Path to the Call Detail Record file (appended); blank disables CDR logging.
        /// </summary>
        public string CdrFile { get; set; } = string.Empty;

        /// <summary>
        /// Configured ports, in order.
        /// </summary>
        public List<PortConfig> Ports { get; } = new List<PortConfig>();

        /// <summary>
        /// The routing table (routes + hunt groups), including PVC entries.
        /// </summary>
        public X121RoutingTable Routing { get; } = new X121RoutingTable();

        /// <summary>
        /// Configured accounts.
        /// </summary>
        public List<AccountConfig> Accounts { get; } = new List<AccountConfig>();

        /// <summary>
        /// Configured management users.
        /// </summary>
        public List<UserConfig> Users { get; } = new List<UserConfig>();
    }
}
