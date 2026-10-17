//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Link;

// NOTE: These XOT-specific config types were extracted from RuntimeConfig.cs so they live
// in the X25 assembly alongside the XOT transports that consume them (XotClientTransport,
// XotServerTransport, XotSerialBridge). RuntimeConfig (in the top Runtime assembly) still
// references them -- a legal downward Runtime -> X25 dependency. Namespace preserved.
namespace RetroIO.Protocols.X25
{
    /// <summary>
    /// XOT (X.25 over TCP) interface configuration
    /// Equivalent to Cisco XOT destination configuration
    /// </summary>
    public class XotInterfaceConfig
    {
        /// <summary>
        /// Interface name (e.g., "XOT0", "XOT1")
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Enable this XOT interface
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// XOT mode (Client, Server)
        /// </summary>
        public XotMode Mode { get; set; } = XotMode.Client;

        /// <summary>
        /// Remote IP address (for client mode)
        /// </summary>
        public string? RemoteIPAddress { get; set; }

        /// <summary>
        /// Remote TCP port (for client mode, default 1998)
        /// </summary>
        public int RemotePort { get; set; } = 1998;

        /// <summary>
        /// First logical channel this side may use for calls over the connection. Default 1.
        /// </summary>
        public ushort LciBase { get; set; } = 1;

        /// <summary>
        /// Number of logical channels this side may use. Default 16: as a DTE the engine picks
        /// channels from the TOP of the range (X.25 Annex A note 6), so the range must lie inside
        /// what the far switch port offers - the C demo switches offer 1..2048 on trunks and
        /// 1..32 on one port, and 1..16 fits every one of them.
        /// </summary>
        public ushort LciCount { get; set; } = 16;

        /// <summary>
        /// Local listening port (for server mode, default 1998)
        /// </summary>
        public int LocalPort { get; set; } = 1998;

        /// <summary>
        /// Remote X.25 machine ID reachable through this XOT connection
        /// </summary>
        public string RemoteMachineId { get; set; } = string.Empty;

        /// <summary>
        /// Packet-layer window to REQUEST in the call request's flow control facility.
        /// </summary>
        /// <remarks>
        /// This is a proposal, not the window we then use. What we actually use is whatever comes
        /// back in the call accepted packet, and X.25 6.12 says a called DTE may lower it. Only when
        /// the peer sends no window size facility at all do we fall back - and then to 2, the
        /// standard default (X.25 6.10), never to this number.
        /// <para>
        /// 7 is the widest a modulo 8 circuit can carry (X.25 7.3.1.2) and is what the C reference
        /// switches are configured for, so proposing it costs nothing against a peer that wants
        /// less: it will say so.
        /// </para>
        /// </remarks>
        public byte RequestedWindowSize { get; set; } = 7;

        /// <summary>
        /// Maximum user data field length to REQUEST, in octets. Negotiated the same way as
        /// <see cref="RequestedWindowSize"/>.
        /// </summary>
        public int RequestedPacketSize { get; set; } = 128;

        /// <summary>
        /// TCP keepalive settings (equivalent to Cisco tcp-keepalives)
        /// </summary>
        public bool TcpKeepalive { get; set; } = true;

        /// <summary>
        /// Keep-alive interval in seconds (0 = disabled)
        /// </summary>
        public int KeepAliveIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// Connection timeout in milliseconds (for client mode)
        /// </summary>
        public int ConnectTimeoutMs { get; set; } = 5000;

        /// <summary>
        /// Reconnect delay in milliseconds (0 = no auto-reconnect)
        /// </summary>
        public int ReconnectDelayMs { get; set; } = 5000;

        /// <summary>
        /// Protocol mode for this link (XOT typically only carries X.25)
        /// </summary>
        public ProtocolDetectionMode ProtocolMode { get; set; } = ProtocolDetectionMode.X25Only;

        /// <summary>
        /// Description for this XOT interface
        /// </summary>
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// XOT operating mode
    /// </summary>
    public enum XotMode
    {
        /// <summary>
        /// XOT client - connects to remote XOT server
        /// </summary>
        Client,

        /// <summary>
        /// XOT server - accepts incoming XOT connections
        /// </summary>
        Server
    }

    /// <summary>
    /// High-performance bridge configuration for pass-through mode
    /// Supports XOT-to-Serial, XOT-to-XOT, and Serial-to-Serial bridging
    /// Bypasses routing layer for maximum throughput
    /// </summary>
    public class XotSerialBridgeConfig
    {
        /// <summary>
        /// Enable the bridge
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Name of first interface to bridge (Serial0, XOT0, etc.)
        /// </summary>
        public string Interface1Name { get; set; } = string.Empty;

        /// <summary>
        /// Name of second interface to bridge (Serial1, XOT1, etc.)
        /// </summary>
        public string Interface2Name { get; set; } = string.Empty;

        /// <summary>
        /// Buffer size for data relay (default 4096 bytes)
        /// Larger buffers reduce allocations but increase memory usage
        /// </summary>
        public int BufferSize { get; set; } = 4096;

        /// <summary>
        /// Enable detailed statistics (slight performance cost)
        /// When disabled, only basic counters are maintained
        /// </summary>
        public bool DetailedStatistics { get; set; } = true;

        /// <summary>
        /// Description for this bridge configuration
        /// </summary>
        public string Description { get; set; } = string.Empty;

        // Legacy property names for backward compatibility
        /// <summary>
        /// Gets or sets the first interface name. Obsolete alias of Interface1Name.
        /// </summary>
        [Obsolete("Use Interface1Name instead")]
        public string XotInterfaceName
        {
            get => Interface1Name;
            set => Interface1Name = value;
        }

        /// <summary>
        /// Gets or sets the second interface name. Obsolete alias of Interface2Name.
        /// </summary>
        [Obsolete("Use Interface2Name instead")]
        public string SerialInterfaceName
        {
            get => Interface2Name;
            set => Interface2Name = value;
        }
    }
}
