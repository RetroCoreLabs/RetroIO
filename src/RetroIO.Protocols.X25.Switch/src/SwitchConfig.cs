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
        /// The most calls in progress on the whole switch at once (<c>max_calls</c> in the <c>[switch]</c> section).
        /// 0, the default, and an absent key both mean no limit. A call is counted once, from the moment it is
        /// routed until it is cleared, even though it uses two ports of the switch (the leg in and the leg out).
        /// When the count has reached the limit a new incoming call is cleared with Network congestion
        /// (<see cref="SwitchCore.CauseNetworkCongestion"/>, diagnostic 0) and is never offered onward.
        /// </summary>
        public int MaxCalls { get; set; }

        /// <summary>
        /// Telnet management listener port; 0 disables it. The default is 9000.
        /// </summary>
        public ushort MgmtPort { get; set; } = 9000;

        /// <summary>
        /// <c>mgmt_open</c>: true lets the telnet console run commands with no login, at full rights - x25emu's key of
        /// the same name, meant for a demonstration. False (the default) asks for a <c>[user.N]</c> login.
        /// </summary>
        public bool MgmtOpen { get; set; }

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
        /// Path to the price list (<c>tariff_file</c>) that <c>show x25 invoice</c> prices the call records with; blank
        /// means the switch prints no bills. Read when the command runs, so a changed price list needs no restart.
        /// </summary>
        public string TariffFile { get; set; } = string.Empty;

        /// <summary>
        /// The folder <c>write memory</c> writes into (config key <c>config_dir</c>, the same as x25emu). When the file
        /// names none, <see cref="SwitchConfigLoader.LoadFile"/> sets the folder the file was loaded from; empty for a
        /// configuration built in code, and then <c>write memory</c> is refused rather than writing somewhere unchosen.
        /// </summary>
        public string ConfigDir { get; set; } = string.Empty;

        /// <summary>
        /// Configured ports, in order.
        /// </summary>
        public List<PortConfig> Ports { get; } = new List<PortConfig>();

        /// <summary>
        /// The routing table (routes + hunt groups), including PVC entries.
        /// </summary>
        public X121RoutingTable Routing { get; } = new X121RoutingTable();

        /// <summary>
        /// The permanent virtual circuits the switch joins at start (<c>[pvc.N]</c>), checked by the loader.
        /// </summary>
        public List<PvcConfig> Pvcs { get; } = new List<PvcConfig>();

        /// <summary>
        /// How often each PVC writes a billing record, in minutes (<c>[switch] pvc_record_minutes</c>, the same key and
        /// default as x25emu); 0 turns the periodic line off and leaves only the lines a reset or an outage writes.
        /// </summary>
        public int PvcRecordMinutes { get; set; } = 15;

        /// <summary>
        /// Octets in one charging segment (<c>[switch] segment_size</c>, the same key, default and limits as x25emu):
        /// D.12's 64 by default; X.25 7.3.8.3 leaves it to the Administration for national calls. 1 to 4096.
        /// </summary>
        public int SegmentSize { get; set; } = Model.SegmentCounter.SegmentOctets;

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
