//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// SINTRAN header packet-subtype code (offset 3). Identifies the message kind;
    /// it is not a length.
    /// </summary>
    /// <remarks>
    /// Values from XMSG-PROTOCOL.md section 4.1. These subtypes occur across the 1947-frame
    /// verified corpus, plus the network-error subtype seen on live rejects.
    /// </remarks>
    public enum SintranPacketSubtype : byte
    {
        /// <summary>
        /// Network error / reject frame (<c>0x07</c>).
        /// </summary>
        /// <remarks>
        /// Carries a negative XE* error code in Flags2 (for example XEIMA -19 invalid magic, XENSE -34
        /// sequence error). Observed live when a node rejects a datagram; not part of the four core
        /// corpus subtypes but a real on-wire value.
        /// </remarks>
        NetworkError = 0x07,

        /// <summary>
        /// Delivery acknowledgment / flow-control frame.
        /// </summary>
        /// <remarks>
        /// 14-byte frame whose Flags1 echoes the acknowledged data frame's datagram sequence.
        /// </remarks>
        Ack = 0x03,

        /// <summary>
        /// First fragment of a message too large for one LAPB frame (<c>0x0A</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Carries the same 28-byte header as <see cref="Data"/> - SINTRAN header plus the full
        /// addressing words - and then as much of the message body as fits. Flags2 is the TOTAL
        /// message length, not this fragment's length. The rest arrives as
        /// <see cref="MessageContinuation"/> with the same Flags1 datagram sequence.
        /// </para>
        /// <para>
        /// Observed on every COSMOS file-transfer data message, which is 1030 bytes and splits
        /// 594 + 436. Capture: <c>2026-07-29-1946-xftra-send-file-complete-d102-to-d100.pcapng</c>.
        /// </para>
        /// </remarks>
        MessageFirstFragment = 0x0A,

        /// <summary>
        /// Continuation of a message begun by <see cref="MessageFirstFragment"/> (<c>0x0C</c>).
        /// </summary>
        /// <remarks>
        /// Does NOT repeat the addressing words: the body resumes at offset 14, after the 13-byte
        /// SINTRAN header and the single counter byte. Flags2 is the byte OFFSET into the message at
        /// which this fragment resumes, so <c>offset + fragment length == total length</c>.
        /// </remarks>
        MessageContinuation = 0x0C,

        /// <summary>
        /// Data message carrying the XMSG sub-header and user payload.
        /// </summary>
        Data = 0x0E,

        /// <summary>
        /// Reachability reply (answer to a reachability request).
        /// </summary>
        ReachabilityReply = 0x13,

        /// <summary>
        /// Initialisation negative acknowledgement - SINTRAN's own <c>INNAK</c> (<c>0x17</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Wire shape</b></para>
        /// <para>
        /// Header only, 14 bytes, with no body, where the last five words are the destination node,
        /// the source node, Flags1, Flags2 and the checksum:
        /// <c>21FE 0017 dst src FFFF FFFD checksum</c>. Two fields stand out:
        /// </para>
        /// Distinctive fields:
        ///  - Marker 2 is <c>0xFE</c>, which appears nowhere else in the verified corpus.
        ///  - Flags2 is <c>0xFFFD</c>, that is -3.
        /// <para><b>Where the name comes from</b></para>
        /// <para>
        /// Not a guess and not a carve. Neither the XMSG kernel (23552 words) nor XROUT
        /// (39943 words) contains the constant - the frame is assembled arithmetically, so eight
        /// separate byte searches found nothing. The live machine supplied it instead: the
        /// <c>Nettype</c> column of <c>LIST-FRAMES</c> labels this frame <c>INNAK</c> and labels our
        /// own <see cref="ReachabilityRequest"/> announce <c>INIT</c>.
        /// </para>
        /// <para><b>It is not fatal, despite the name</b></para>
        /// <para>
        /// On 2026-08-08 both D100 and D103 sent an INNAK in reply to every announce while
        /// simultaneously completing naming, reachability and routing - each reported
        /// <c>A: *->19999</c> and D103 went on to route real Data through our node. So an INNAK
        /// must NOT be treated as a rejection of the sender's identity. What it does mean is still
        /// UNPROVEN. See <c>DOC/SUBTYPE-17-INIT-REJECT-2026-08-07.md</c> steps 7-9.
        /// </para>
        /// <para><b>We do not answer it yet</b></para>
        /// <para>
        /// The live runner logs <c>*** NO REPLY BUILT *** (this hangs the caller)</c> for every one.
        /// What the correct reply is has never been captured, and guessing at XMSG replies has
        /// crashed a live machine before.
        /// </para>
        /// </remarks>
        InitializationNak = 0x17,

        /// <summary>
        /// Reachability request (probe testing whether a remote node is accessible).
        /// </summary>
        /// <remarks>
        /// SINTRAN calls this an <c>INIT</c> in the <c>Nettype</c> column of <c>LIST-FRAMES</c>; it is
        /// what a node sends to announce itself when a link reaches Active. The negative answer is
        /// <see cref="InitializationNak"/>.
        /// </remarks>
        ReachabilityRequest = 0x19,
    }
}
