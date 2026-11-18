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
        /// XOT mode (Client, Server, Both)
        /// </summary>
        public XotMode Mode { get; set; } = XotMode.Client;

        /// <summary>
        /// Remote IP address (for client mode). In Both mode this is the peer: the address we dial
        /// when a call is routed to this entry, and the source address an incoming connection is
        /// matched against.
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

        private ushort? _lciCount;

        /// <summary>
        /// Number of logical channels this side may use. When it is never set, every channel from
        /// <see cref="LciBase"/> up to 4095 (X.25 3.1; <see cref="RetroIO.Protocols.X25.PacketLayer.X25Channels.DefaultCount"/>).
        /// Until 2026-11-18 the default was 16. As a DTE the engine picks channels from the TOP of the
        /// range (X.25 Annex A note 6), so with the default a DTE's first call goes out on channel 4095:
        /// the far switch port must offer that channel too, which it does when it also uses the default.
        /// A far port configured with fewer channels needs this count set to match.
        /// <para>
        /// It is also the line's connection limit (Server and Both modes): RFC 1613 section 5 gives
        /// every circuit its own TCP connection, so the entry holds at most this many connections at
        /// once, an idle one (a caller's spare for arriving calls) included. When all are taken the
        /// next connection is not accepted; it waits in the TCP backlog until a connection closes.
        /// Copied from x25emu, whose XOT port has one connection per channel (tools/x25switch.c,
        /// conn_max = vc_count). A caller that holds N calls and keeps a spare needs N + 1.
        /// </para>
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public ushort LciCount
        {
            get => _lciCount ?? RetroIO.Protocols.X25.PacketLayer.X25Channels.DefaultCount(LciBase);
            set => _lciCount = value;
        }

        /// <summary>
        /// True when <see cref="LciCount"/> was set; false when it is the default, every channel up to 4095. Not
        /// written to the configuration file.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool LciCountIsSet => _lciCount.HasValue;

        /// <summary>
        /// The channel count as the configuration file holds it: null when the file does not set it (the default
        /// then applies, and nothing is written), so a saved file keeps "not set" instead of turning the default
        /// into a fixed 4095. This is the property the file's <c>LciCount</c> key maps to.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("LciCount")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public ushort? LciCountSetting
        {
            get => _lciCount;
            set => _lciCount = value;
        }

        /// <summary>
        /// Local listening port (for server mode, default 1998). In Both mode this is the port we
        /// accept the peer on. Several Server and Both entries may use the same port: they share one
        /// listening socket (see <see cref="Transport.XotListenerRegistry"/>).
        /// </summary>
        public int LocalPort { get; set; } = 1998;

        /// <summary>
        /// Both mode only: when true, a connection that comes from an IP address that no entry
        /// on the same local port lists as its peer is refused, with a warning in the log. When false
        /// (the default) such a connection is accepted as an unlisted peer and handled with the
        /// default settings.
        /// </summary>
        /// <remarks>
        /// RFC 1613 has no authentication, so the source IP address is all there is to go on, and
        /// everybody behind one NAT router shares one address. If any entry on a port sets this, the
        /// whole port refuses unlisted addresses: a port has one listening socket, and the answer
        /// for a connection that belongs to no entry cannot depend on which entry is asked.
        /// </remarks>
        public bool AllowOnlyListedPeers { get; set; } = false;

        /// <summary>
        /// Remote X.25 machine ID reachable through this XOT connection
        /// </summary>
        public string RemoteMachineId { get; set; } = string.Empty;

        /// <summary>
        /// Window size to REQUEST in the Call Request's flow control facility for calls over this entry, 1 to 7; null uses the
        /// X.25 section of the configuration. This is the peer's own setting (a Cisco "profile").
        /// </summary>
        /// <remarks>
        /// This is a proposal, not the window we then use. What we actually use is whatever comes
        /// back in the call accepted packet, and X.25 6.12 says a called DTE may lower it. Only when
        /// the peer sends no window size facility at all do we fall back - and then to 2, the
        /// standard default (X.25 6.10), never to this number. For a connection the peer opened, a call
        /// that carries no size facility uses this number; one that does is accepted as asked.
        /// <para>
        /// 7 is the widest a modulo 8 circuit can carry (X.25 7.3.1.2). A value that cannot work (outside 1 to 7)
        /// refuses the entry when the runtime starts; it is never changed to something else.
        /// </para>
        /// </remarks>
        public byte? RequestedWindowSize { get; set; }

        /// <summary>
        /// Maximum user data field length to REQUEST, in octets, for calls over this entry: a power of two from 16 to 4096;
        /// null uses the X.25 section of the configuration. Negotiated the same way as
        /// <see cref="RequestedWindowSize"/>.
        /// </summary>
        public int? RequestedPacketSize { get; set; }

        /// <summary>
        /// Server mode: regular expressions for the called addresses this entry accepts calls for.
        /// A call to any other address is cleared with cause 0x0B (access barred). Empty (the
        /// default) accepts every address. Passed on to <see cref="Transport.XotServerConfig.AcceptedX121Ranges"/>.
        /// </summary>
        public System.Collections.Generic.List<string> AcceptedX121Ranges { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>
        /// Server mode: how an incoming call on one of this entry's connections is answered. True
        /// (the default): the runtime answers at once - Call Accepted when a local service takes the
        /// call, a Clear Request when none does. False: the call is offered to the program through
        /// the server transport's <c>IncomingCall</c> event and waits for <c>AcceptCall</c> or
        /// <c>RejectCall</c>; a call not answered within T11 (X.25 4.1.4, Table D.1: 180 s) is
        /// cleared by the connection's X.25 engine. Passed on to
        /// <see cref="Transport.XotServerConfig.AnswerIncomingCallsAutomatically"/>.
        /// </summary>
        public bool AnswerIncomingCallsAutomatically { get; set; } = true;

        /// <summary>
        /// Client mode: keep one connection to the remote address open with no call on it, so the far end can put an
        /// arriving call on it - how a subscriber line of a switch port works (the port listens, the subscriber dials,
        /// and the port puts a call for the subscriber on a connection the subscriber opened that has never carried a
        /// call). When a call arrives on it, a new one is opened; every connection still carries one call only
        /// (RFC 1613 section 5) and is closed when its call ends. Calls that arrive are answered by the local services
        /// (the TAD, the chat) like calls on any other line. Default false: a Client entry only dials when it has a
        /// call to place. Passed on to <see cref="Transport.XotPeerConfig.KeepSpareConnection"/>.
        /// </summary>
        public bool KeepConnectionForIncomingCalls { get; set; } = false;

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
        Server,

        /// <summary>
        /// XOT peer: ONE entry that dials out when a call is routed to it and also accepts incoming
        /// connections from the same peer. RFC 1613 has no client and server roles: whichever end
        /// has a call to make opens a new TCP connection (one per virtual circuit, section 5) to the
        /// other end's port 1998, and sends the Call Request on it (section 6.1). This is how Cisco
        /// routers behave, so one entry per peer is the natural fit. Added last, so the numbers of
        /// Client (0) and Server (1) in existing configuration files do not change.
        /// </summary>
        Both
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
