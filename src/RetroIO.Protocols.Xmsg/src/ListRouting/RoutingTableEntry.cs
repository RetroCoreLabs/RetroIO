//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Xmsg;

namespace RetroIO.Protocols.Xmsg.ListRouting
{
    /// <summary>
    /// One decoded routing-table entry as returned by the XROUT <c>XSGSY</c>
    /// ("get routing info for system N") reply.
    /// </summary>
    /// <remarks>
    /// The four fields map directly onto the four XSGSY reply parameter blocks
    /// (XMSG-PROTOCOL.md section 9.1):
    ///  - <see cref="System"/> is parameter 1 (the first routing entry whose system is greater than or equal to the queried one; <c>0</c> means none).
    ///  - <see cref="ConnectionType"/> is parameter 2 (a 0-4 enum, not a bitfield).
    ///  - <see cref="ExtraInfo"/> is parameter 3 (link index / relay system / subaddress, depending on <see cref="ConnectionType"/>).
    ///  - <see cref="NetworkInfo"/> is parameter 4 (low byte = hop count, high byte = number of WANs).
    /// This is a <c>readonly struct</c> so it can be passed by <c>in</c> without a defensive copy.
    /// </remarks>
    public readonly struct RoutingTableEntry
    {
        /// <summary>
        /// Initialises a new routing-table entry.
        /// </summary>
        /// <param name="system">
        /// The system number of the entry (XSGSY parameter 1); <c>0</c> means no entry.
        /// </param>
        /// <param name="connectionType">
        /// The connection type to the system (XSGSY parameter 2).
        /// </param>
        /// <param name="extraInfo">
        /// The extra info (link index / relay system / subaddress) whose meaning
        /// depends on <paramref name="connectionType"/> (XSGSY parameter 3).
        /// </param>
        /// <param name="hops">
        /// The hop count to the system (the low byte of XSGSY parameter 4).
        /// </param>
        /// <param name="wans">
        /// The number of WANs crossed to reach the system (the high byte of XSGSY parameter 4).
        /// </param>
        public RoutingTableEntry(ushort system, XroutConnectionType connectionType, ushort extraInfo, byte hops, byte wans)
        {
            System = system;
            ConnectionType = connectionType;
            ExtraInfo = extraInfo;
            Hops = hops;
            Wans = wans;
        }

        /// <summary>
        /// Gets the system number of this entry (XSGSY parameter 1); <c>0</c> means no entry.
        /// </summary>
        public ushort System { get; }

        /// <summary>
        /// Gets the connection type to the system (XSGSY parameter 2).
        /// </summary>
        public XroutConnectionType ConnectionType { get; }

        /// <summary>
        /// Gets the extra info (link index / relay system / subaddress) whose meaning
        /// depends on <see cref="ConnectionType"/> (XSGSY parameter 3).
        /// </summary>
        public ushort ExtraInfo { get; }

        /// <summary>
        /// Gets the hop count to the system (the low byte of XSGSY parameter 4).
        /// </summary>
        public byte Hops { get; }

        /// <summary>
        /// Gets the number of WANs crossed to reach the system (the high byte of XSGSY parameter 4).
        /// </summary>
        public byte Wans { get; }

        /// <summary>
        /// Gets the packed XSGSY parameter-4 network-info word: hop count in the low
        /// byte, number of WANs in the high byte.
        /// </summary>
        public ushort NetworkInfo
        {
            // Param 4 encoding (XMSG-PROTOCOL.md section 9.1): low byte = #hops, high byte = #WANs.
            get { return (ushort)(Hops | (Wans << 8)); }
        }
    }
}
