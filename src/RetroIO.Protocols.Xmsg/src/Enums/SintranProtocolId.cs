//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// The byte at SINTRAN header offset 12. It is not a sub-protocol selector - it is the
    /// HIGH BYTE OF THE HEADER CHECKSUM.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CORRECTED 2026-07-31. This type used to be documented as a "sub-protocol selector...
    /// a stable selector, not a counter", with members named for the traffic each value was seen
    /// carrying. Both the description and the names are wrong.
    /// </para>
    /// <para>
    /// The SINTRAN header is SEVEN WORDS, and word 6 is a ones-complement checksum over the other
    /// six - <c>w6 == ~ones_complement_sum(w0..w5, 0)</c> - carved from the XMSG kernel routine at
    /// <c>137314</c> and verified on 3595 of 3595 frames in the capture corpus, every subtype, with
    /// no special cases. Offset 12 is that checksum's high byte and offset 13 (the "Counter") is
    /// its low byte. See <see cref="Packet.XmsgEnvelope.ComputeHeaderChecksum(SintranHeader)"/> and
    /// <c>SINTRAN/XMSG/DOC/XMSG-HEADER-WORD6-IS-A-CHECKSUM-2026-07-31.md</c>.
    /// </para>
    /// <para>
    /// So the value is derived, never chosen. Setting it independently of the other header
    /// words produces a corrupt header, which is what the documented XMSG 24B / <c>PERF_CONNCT</c>
    /// crash actually is - a peer rejecting a bad checksum, not a peer objecting to a "wrong
    /// channel".
    /// </para>
    /// <para>
    /// The member names are MISNOMERS, kept only for source compatibility. They record which
    /// traffic happened to produce each checksum high byte in the captures we had, which is a
    /// coincidence of the header fields that traffic carried - a TAD session tends to produce
    /// <c>0xDD</c> because of its node numbers, sequence range and XMCSM, not because <c>0xDD</c>
    /// means TAD. Do not branch on these names to identify traffic; dispatch on the subtype and on
    /// XMCSM instead. The narrow <c>0xD8</c>..<c>0xDE</c> range is simply where the checksum lands
    /// for the header values these two nodes exchange.
    /// </para>
    /// </remarks>
    public enum SintranProtocolId : byte
    {
        /// <summary>
        /// Checksum high byte <c>0xDE</c>. MISNOMER: named "Routing" from the traffic it was first
        /// seen on; it does not select routing.
        /// </summary>
        Routing = 0xDE,

        /// <summary>
        /// Checksum high byte <c>0xDD</c>. MISNOMER: named "Tad" from the traffic it was first seen
        /// on; it does not select TAD.
        /// </summary>
        Tad = 0xDD,

        /// <summary>
        /// Checksum high byte <c>0xDC</c>. MISNOMER: "Dc" was never a protocol, only this value.
        /// </summary>
        Dc = 0xDC,

        /// <summary>
        /// Checksum high byte <c>0xDB</c>.
        /// </summary>
        Db = 0xDB,

        /// <summary>
        /// Checksum high byte <c>0xDA</c>. MISNOMER: named "Pad" for X.25 PAD data; it does not
        /// select PAD.
        /// </summary>
        Pad = 0xDA,

        /// <summary>
        /// Checksum high byte <c>0xD9</c>.
        /// </summary>
        D9 = 0xD9,

        /// <summary>
        /// Checksum high byte <c>0xD8</c>.
        /// </summary>
        D8 = 0xD8,
    }
}
