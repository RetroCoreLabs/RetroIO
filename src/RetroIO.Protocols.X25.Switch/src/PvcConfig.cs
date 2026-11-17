//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Switch
{
    /// <summary>
    /// One permanent virtual circuit through the switch (<c>[pvc.N]</c>): two channels, on two ports or one, that the
    /// switch joins for as long as it runs.
    /// </summary>
    /// <remarks>
    /// X.25 4.2: "For permanent virtual circuits there is no call set-up or clearing." Neither end is a caller or a called
    /// party; both DTEs are in data transfer from the start. The "src" and "dst" names are only which end is written
    /// first in the configuration.
    /// </remarks>
    public sealed class PvcConfig
    {
        /// <summary>
        /// One end's port.
        /// </summary>
        public int SourcePort { get; set; }

        /// <summary>
        /// One end's logical channel, inside that port's PVC channels.
        /// </summary>
        public ushort SourceLci { get; set; }

        /// <summary>
        /// The other end's port.
        /// </summary>
        public int DestPort { get; set; }

        /// <summary>
        /// The other end's logical channel.
        /// </summary>
        public ushort DestLci { get; set; }

        /// <summary>
        /// The account this PVC is billed to (an <c>[account.N]</c> name), or empty for none. A PVC carries no NUI (it has
        /// no Call Request), so its configuration is its billing identity.
        /// </summary>
        public string Account { get; set; } = string.Empty;

        /// <summary>
        /// For an end on an XOT port: the far XOT's interface name, sent as the responder interface in the RFC 1613 6.5
        /// setup packet (<c>remote_iface</c>). Empty for an end on any other port.
        /// </summary>
        public string RemoteInterface { get; set; } = string.Empty;

        /// <summary>
        /// For an end on an XOT port: the PVC's channel on the far XOT's interface (<c>remote_lci</c>), sent as the
        /// responder LCN. RFC 1613 6.5 carries both ends' channels, so the two need not be equal.
        /// </summary>
        public ushort RemoteLci { get; set; }

        /// <summary>
        /// For an end on an XOT initiator port: the far XOT's address, <c>host:port</c> or <c>host</c>
        /// (<c>remote_host</c>). RFC 1613 6.5: "Each XOT PVC is configured with the identity of the other XOT (i.e., IP
        /// address)". When no port is given, 1998 is used: RFC 1613 says "All
        /// connections MUST be made to TCP port number 1998". Another port is for a test bench on one machine only.
        /// </summary>
        public string RemoteHost { get; set; } = string.Empty;
    }
}
