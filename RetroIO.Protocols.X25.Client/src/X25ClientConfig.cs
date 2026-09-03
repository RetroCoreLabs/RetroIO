using System;

namespace RetroIO.Protocols.X25.Client
{
    /// <summary>
    /// Transport/framing mode for an <see cref="X25Client"/> link.
    /// </summary>
    /// <remarks>
    /// Selects the transport/framing. XOT is the only mode implemented today (the default for
    /// switch-attached tools); SLIP and HDLC over TCP are reserved for a later pass and currently throw
    /// from <see cref="X25Client.Open"/>.
    /// </remarks>
    public enum X25ClientFraming
    {
        /// <summary>
        /// X.25-over-TCP (RFC 1613), no LAPB. The default for switch-attached tools.
        /// </summary>
        Xot,

        /// <summary>
        /// SLIP framing (RFC 1055) over TCP, with LAPB. Not yet wired in the facade.
        /// </summary>
        Slip,

        /// <summary>
        /// HDLC/PPP framing (RFC 1662) over TCP, with LAPB. Not yet wired in the facade.
        /// </summary>
        Hdlc
    }

    /// <summary>
    /// Configuration for opening an <see cref="X25Client"/>.
    /// </summary>
    /// <remarks>
    /// A few fields (<see cref="RemoteMachineId"/>, <see cref="OutboundRoutePattern"/>) support the
    /// runtime's routing: outbound calls need an explicit interface + route entry to be sent at the single
    /// XOT link toward the switch.
    /// </remarks>
    public sealed class X25ClientConfig
    {
        /// <summary>
        /// Remote host to connect to (the switch). Ignored when <see cref="Listen"/> is set.
        /// </summary>
        public string Host { get; set; } = "127.0.0.1";

        /// <summary>
        /// Remote TCP port (XOT is 1998 on a real switch; the emulator's switch ports vary).
        /// </summary>
        public ushort Port { get; set; } = 1998;

        /// <summary>
        /// Reserved: 1 = listen for an inbound transport connection, 0 = connect out. Only connect
        /// (XOT client) is implemented today.
        /// </summary>
        public bool Listen { get; set; } = false;

        /// <summary>
        /// Transport/framing mode. See <see cref="X25ClientFraming"/>.
        /// </summary>
        public X25ClientFraming Framing { get; set; } = X25ClientFraming.Xot;

        /// <summary>
        /// Whether to run a LAPB layer under X.25. Required for SLIP/HDLC, not used for XOT.
        /// </summary>
        public bool UseLapb { get; set; } = false;

        /// <summary>
        /// True to act as X.25 DTE, false for DCE.
        /// </summary>
        public bool IsDte { get; set; } = true;

        /// <summary>
        /// Default X.25 packet size in bytes.
        /// </summary>
        public ushort PacketSize { get; set; } = 128;

        /// <summary>
        /// Default X.25 window size.
        /// </summary>
        public byte WindowSize { get; set; } = 7;

        /// <summary>
        /// Number of SVCs to support on the link.
        /// </summary>
        public ushort NumVcs { get; set; } = 8;

        /// <summary>
        /// Our local X.121 address (the switch routes inbound calls for this address to us).
        /// </summary>
        public string LocalAddress { get; set; } = string.Empty;

        /// <summary>
        /// Routing hint for the single XOT interface toward the switch. No C analog.
        /// </summary>
        public string RemoteMachineId { get; set; } = "SWITCH";

        /// <summary>
        /// X.121 address regex whose matching outbound calls are sent at the switch link. Defaults to
        /// ".*" so every outbound call is handed to the switch, which routes onward. No C analog.
        /// </summary>
        public string OutboundRoutePattern { get; set; } = ".*";
    }
}
