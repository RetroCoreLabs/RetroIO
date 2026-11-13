//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Switch
{
    /// <summary>
    /// Configuration for one switch port. Defaults follow the switch configuration spec (window size 2,
    /// packet size 128, LCI base 1 / count 16, TCP port 1998).
    /// </summary>
    public sealed class PortConfig
    {
        /// <summary>
        /// Port number (its index among configured ports).
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Access or trunk role.
        /// </summary>
        public PortType Type { get; set; } = PortType.Access;

        /// <summary>
        /// Link transport (XOT / HDLC-TCP / HDLC-serial / HDLC-PPP).
        /// </summary>
        public PortTransport Transport { get; set; } = PortTransport.Xot;

        /// <summary>
        /// DTE or DCE role. Access ports default to DCE.
        /// </summary>
        public PortMode Mode { get; set; } = PortMode.Dce;

        /// <summary>
        /// Remote host (DTE mode) or bind address (DCE mode).
        /// </summary>
        public string Host { get; set; } = string.Empty;

        /// <summary>
        /// TCP port for XOT / HDLC-TCP transports.
        /// </summary>
        public ushort Port { get; set; } = 1998;

        /// <summary>
        /// Default X.25 packet size negotiated on this port.
        /// </summary>
        public ushort DefaultPacketSize { get; set; } = 128;

        /// <summary>
        /// Default X.25 window size. The default is 2.
        /// </summary>
        public byte DefaultWindowSize { get; set; } = 2;

        /// <summary>
        /// Maximum packet size this port will allow.
        /// </summary>
        public ushort MaxPacketSize { get; set; } = 1024;

        /// <summary>
        /// Maximum window size this port will allow.
        /// </summary>
        public byte MaxWindowSize { get; set; } = 7;

        /// <summary>
        /// Base logical channel for two-way SVCs.
        /// </summary>
        public ushort LciBase { get; set; } = 1;

        /// <summary>
        /// Number of two-way SVC logical channels.
        /// </summary>
        public ushort LciCount { get; set; } = 16;

        /// <summary>
        /// Serial device path (HDLC-serial transport only).
        /// </summary>
        public string SerialDevice { get; set; } = string.Empty;

        /// <summary>
        /// Serial baud rate (HDLC-serial transport only).
        /// </summary>
        public uint SerialBaud { get; set; } = 9600;

        /// <summary>
        /// Serial data bits, 7 or 8 (HDLC-serial transport only). The default is 8.
        /// </summary>
        public int SerialDataBits { get; set; } = 8;

        /// <summary>
        /// Serial parity: <c>none</c>, <c>odd</c> or <c>even</c> (HDLC-serial transport only). The default is <c>none</c>.
        /// </summary>
        public string SerialParity { get; set; } = "none";

        /// <summary>
        /// Serial stop bits, 1 or 2 (HDLC-serial transport only). The default is 1.
        /// </summary>
        public int SerialStopBits { get; set; } = 1;

        /// <summary>
        /// Serial flow control: <c>rtscts</c> (hardware RTS/CTS) or <c>none</c> (HDLC-serial transport only).
        /// The default is <c>rtscts</c>, the same default the C emulator has.
        /// </summary>
        public string SerialFlow { get; set; } = "rtscts";

        /// <summary>
        /// LAPB T1, the retransmission and poll timer, in milliseconds (HDLC transports). Zero means the
        /// default, 3000.
        /// </summary>
        public int LapbT1Ms { get; set; }

        /// <summary>
        /// LAPB T2, the longest an acknowledgement of a received frame may be held back, in milliseconds
        /// (HDLC transports). Zero means no delay: every frame is acknowledged at once. When set it must be
        /// less than T1.
        /// </summary>
        public int LapbT2Ms { get; set; }

        /// <summary>
        /// LAPB T3, the idle keepalive timer, in milliseconds (HDLC transports). Zero means the default, 30000.
        /// </summary>
        public int LapbT3Ms { get; set; }

        /// <summary>
        /// LAPB N2, the most times a frame is sent again before the link is declared failed (HDLC
        /// transports). Zero means the default, 10.
        /// </summary>
        public int LapbN2 { get; set; }

        /// <summary>
        /// LAPB window k, the most unacknowledged frames in flight (HDLC transports), 1 to 7. Zero means the
        /// default, 7.
        /// </summary>
        public int LapbK { get; set; }

        /// <summary>
        /// Start of the local X.121 address range reachable on this port.
        /// </summary>
        public string AddrRangeStart { get; set; } = string.Empty;

        /// <summary>
        /// End of the local X.121 address range reachable on this port.
        /// </summary>
        public string AddrRangeEnd { get; set; } = string.Empty;

        /// <summary>
        /// The name of the neighbouring node reached over this port when it is a trunk, or empty when the
        /// port has no declared peer. Used only on the management/monitor plane to build the network map; the
        /// X.25 protocol is unaffected.
        /// </summary>
        public string PeerName { get; set; } = string.Empty;

        /// <summary>
        /// The neighbouring node's monitor host, so a viewer can discover and connect to it. Defaults to
        /// <see cref="Host"/> when empty and a peer is declared.
        /// </summary>
        public string PeerMonitorHost { get; set; } = string.Empty;

        /// <summary>
        /// The neighbouring node's monitor TCP port, or 0 when unknown/not declared.
        /// </summary>
        public int PeerMonitorPort { get; set; }

        /// <summary>
        /// The neighbouring node's in-band management X.121 address, reported by <c>GET_TOPOLOGY</c> so
        /// a network monitor can place the next management call and walk the network (see
        /// docs/NMC_API_V1.md). Empty when the neighbour has no reachable management address.
        /// </summary>
        public string PeerMgmtAddr { get; set; } = string.Empty;
    }
}
