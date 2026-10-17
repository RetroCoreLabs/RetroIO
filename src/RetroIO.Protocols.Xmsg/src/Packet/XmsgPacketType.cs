//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Packet
{
    /// <summary>
    /// Transport-level XMSG packet type - the SINTRAN header packet-subtype (offset 3),
    /// surfaced as the seam's L2/L3 packet classifier (the XMSG counterpart of an
    /// <c>X25PacketType</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately the transport granularity: the wire subtype byte, not
    /// the higher-level XMCSM service class (connect / terminal-data / control / routing).
    /// Service-class dispatch happens INSIDE <c>XmsgLayer</c> once a packet is parsed, exactly
    /// as the plan's decision table specifies - the enum stays at the shape the wire gives us.
    /// </para>
    /// <para>
    /// Values are the verified SINTRAN subtypes from XMSG-PROTOCOL.md section 4.1 plus the
    /// network-error/reject subtype <c>0x07</c> (see the reconciliation notes: subtype 0x07 =
    /// network error, Flags2 = a negative XE* code such as XEIMA -19). Any other byte maps to
    /// <see cref="Unknown"/> so an unexpected frame is classified, never silently dropped.
    /// </para>
    /// </remarks>
    public enum XmsgPacketType : byte
    {
        /// <summary>
        /// Any subtype not otherwise modelled. The packet still parses (its raw bytes are
        /// retained) but no service is dispatched for it.
        /// </summary>
        Unknown = 0x00,

        /// <summary>
        /// Delivery acknowledgment / flow-control frame (subtype <c>0x03</c>): 14 bytes whose
        /// Flags1 echoes the acknowledged data frame's datagram sequence.
        /// </summary>
        Ack = 0x03,

        /// <summary>
        /// Network error / reject (subtype <c>0x07</c>): Flags2 carries a negative XE* error code
        /// (e.g. XEIMA -19 = <c>0xFFED</c> invalid magic, XENSE -34 = <c>0xFFDE</c> sequence error).
        /// </summary>
        NetworkError = 0x07,

        /// <summary>
        /// Data message (subtype <c>0x0E</c>) carrying the XMSG sub-header and user payload.
        /// </summary>
        Data = 0x0E,

        /// <summary>
        /// Reachability reply (subtype <c>0x13</c>): the answer to a reachability request.
        /// </summary>
        ReachabilityReply = 0x13,

        /// <summary>
        /// Reachability request (subtype <c>0x19</c>): a probe testing whether a remote node is
        /// accessible.
        /// </summary>
        ReachabilityRequest = 0x19,
    }
}
