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
        /// The port's name (<c>name</c> in <c>[port.N]</c>), which routes may use instead of the number; empty
        /// when it has none.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// How the called number is written in a call sent out of this port (<c>called_address</c>:
        /// <c>as_received</c>, <c>international</c> or <c>national</c>). X.121 7.8.1 and 7.12.6-7.12.7 want the
        /// full international number without prefix toward another network, so a trunk port defaults to
        /// <c>international</c>; what a local terminal wants is not in the spec, so an access port defaults to
        /// leaving the number as it came in.
        /// </summary>
        public RetroIO.Protocols.X25.Routing.X121AddressForm CalledAddressForm { get; set; } = RetroIO.Protocols.X25.Routing.X121AddressForm.AsReceived;

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
        /// Which end opens the TCP connection of an <c>hdlc-tcp</c> port (key <c>tcp = listen | dial</c>).
        /// The default follows <see cref="Mode"/>: DCE listens, DTE dials. See <see cref="PortTcpRole"/>.
        /// </summary>
        public PortTcpRole TcpRole { get; set; } = PortTcpRole.FromMode;

        /// <summary>
        /// True when this port listens for its TCP connection, false when it dials: <see cref="TcpRole"/> when
        /// it is set, otherwise the X.25 role decides (DCE listens).
        /// </summary>
        public bool TcpListens => TcpRole == PortTcpRole.FromMode ? Mode == PortMode.Dce : TcpRole == PortTcpRole.Listen;

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
        /// First logical channel kept for permanent virtual circuits (<c>pvc_base</c>). X.25 Annex A puts the PVC channels
        /// below the virtual call range; they are agreed at subscription, not chosen per call.
        /// </summary>
        public ushort PvcBase { get; set; } = 1;

        /// <summary>
        /// How many channels from <see cref="PvcBase"/> are kept for PVCs (<c>pvc_count</c>); 0, the default, keeps none.
        /// </summary>
        public ushort PvcCount { get; set; }

        /// <summary>
        /// This port's interface name (<c>iface_name</c>), sent in the RFC 1613 6.5 PVC setup packet and checked against an
        /// incoming one. The RFC says the names "SHOULD be case-insensitive".
        /// </summary>
        public string InterfaceName { get; set; } = string.Empty;

        /// <summary>
        /// For an XOT port with PVCs (<c>xot_pvc_role</c>): true when this end is the RFC 1613 6.5 initiator - it opens the
        /// TCP connection and sends the setup packet - and false when it is the responder, which waits, checks the packet
        /// and answers. X.25 itself has no such roles; they exist only in XOT.
        /// </summary>
        public bool XotPvcInitiator { get; set; }

        /// <summary>
        /// True when every call arriving on this port must name a network user identifier (X.25 6.21.3: "Some networks
        /// may require that the NUI selection facility be requested by the DTE in every call request packet"). A call
        /// without one, and without a <see cref="DefaultNui"/>, is cleared with "Local procedure error", diagnostic 84
        /// (Annex C). Config key <c>nui_required</c>; off by default.
        /// </summary>
        public bool NuiRequired { get; set; }

        /// <summary>
        /// The DTE on this port subscribes to the charging information facility (X.25 6.22: "agreed for a period of
        /// time"): when it is the DTE to be charged it gets the charging answer at the end of every call, without asking
        /// for it in the call. Config key <c>charging_info</c>; off by default. Only an access port has a DTE of ours.
        /// </summary>
        public bool ChargingInfo { get; set; }

        /// <summary>
        /// The network user identifier a call arriving on this port is billed to when it names none: the port's own
        /// subscription (X.25 6.21, agreed for a period of time). It must be the NUI of a configured account. Config key
        /// <c>default_nui</c>; empty for none. Made for easy test set-ups: one line and every call on the port has a
        /// paying account.
        /// </summary>
        public string DefaultNui { get; set; } = string.Empty;

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
