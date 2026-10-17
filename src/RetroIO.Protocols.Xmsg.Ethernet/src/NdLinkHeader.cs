//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Ethernet
{
    /// <summary>
    /// The NPDU (network protocol data unit) type carried in the HIGH NIBBLE of the kind byte at
    /// header offset +2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// VERIFIED 2026-08-03 by carving the ENCOS monitor <c>encos-mon-ii-b01.prog</c>: its trace
    /// decoder dispatches at <c>ram:2556</c> on a value bounded by 7 (<c>ram:26ad</c>) through an
    /// eight-entry jump table at <c>ram:26ae</c>. That value is the internal type index 0-7, and
    /// the names below are the decoder's own ISO-transport NPDU names.
    /// </para>
    /// <para>
    /// Indices 0 (CR) and 1 (CC) were read directly off the code the table branches into. Indices
    /// 2-7 follow the order of the format-string blob at <c>ram:2642</c>-<c>ram:26a0</c> and are
    /// STRONGLY INDICATED but NOT individually read - treat them as such.
    /// </para>
    /// <para>
    /// Only four of the eight have a CONFIRMED wire byte, seen on real captures. For the other
    /// four the wire byte is UNKNOWN: the high nibble presumably equals the index, but the low
    /// nibble is unexplained (see <see cref="NdLinkHeader.KindLowNibble"/>), so no constant is
    /// offered for them in <see cref="NdLinkFrameKind"/>.
    /// </para>
    /// <para>
    /// This is NOT a bit field - the values are a dense index 0-7, so no <c>[Flags]</c>.
    /// </para>
    /// <para>
    /// Doc: <c>SINTRAN/XMSG/DOC/COSMOS-ETHERNET-LINK-CONTROL-FRAMES-2026-08-03.md</c> sections 2c
    /// and 2d.
    /// </para>
    /// </remarks>
    public enum NdNpduType : byte
    {
        /// <summary>
        /// Index 0 - CR, connection request. Wire byte CONFIRMED as <c>0x0F</c>.
        /// </summary>
        ConnectionRequest = 0,

        /// <summary>
        /// Index 1 - CC, connection confirm. Wire byte UNKNOWN, never captured.
        /// </summary>
        ConnectionConfirm = 1,

        /// <summary>
        /// Index 2 - DT, data. Wire byte CONFIRMED as <c>0x20</c>.
        /// </summary>
        Data = 2,

        /// <summary>
        /// Index 3 - AK, acknowledge. Wire byte CONFIRMED as <c>0x3F</c>.
        /// </summary>
        Acknowledge = 3,

        /// <summary>
        /// Index 4 - WO, window. Wire byte UNKNOWN, never captured.
        /// </summary>
        Window = 4,

        /// <summary>
        /// Index 5 - DR, disconnect request raised by the user. Wire byte UNKNOWN, never captured.
        /// </summary>
        DisconnectRequestByUser = 5,

        /// <summary>
        /// Index 6 - DR, disconnect request raised by the network service. Wire byte CONFIRMED as
        /// <c>0x6F</c>.
        /// </summary>
        DisconnectRequestByNetworkService = 6,

        /// <summary>
        /// Index 7 - DC, disconnect confirm. Wire byte UNKNOWN, never captured.
        /// </summary>
        DisconnectConfirm = 7
    }

    /// <summary>
    /// The kind of ND link frame, i.e. the raw byte 2 of <see cref="NdLinkHeader"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are the four values with a CONFIRMED wire byte. Their high nibble is the
    /// <see cref="NdNpduType"/>; the low nibble is NOT explained - three of the four end in
    /// <c>0xF</c> and the data frame ends in <c>0x0</c>. Do not read meaning into it.
    /// </para>
    /// <para>
    /// The 2026-08-01 captures only had <c>0x20</c> and <c>0x3F</c>; <c>0x0F</c> and <c>0x6F</c>
    /// were added from the both-ends capture of 2026-08-03. Four more NPDU types exist (see
    /// <see cref="NdNpduType"/>) whose wire bytes have never been seen, so they are deliberately
    /// absent here rather than guessed. A parser must therefore still ACCEPT an unrecognised value
    /// and surface it rather than throw - see <see cref="NdLinkHeader.Kind"/>.
    /// </para>
    /// </remarks>
    public enum NdLinkFrameKind
    {
        /// <summary>
        /// <c>0x0F</c> - CR, a connection request. Carries NO payload at all (802.3 length
        /// <c>0x000E</c> = 3 + 11 + 0); everything after the header is Ethernet padding. The
        /// sender link id is <c>0x0000</c> because no link exists yet.
        /// </summary>
        ConnectionRequest = 0x0F,

        /// <summary>
        /// <c>0x20</c> - DT, carries a payload (the SINTRAN datagram).
        /// </summary>
        Data = 0x20,

        /// <summary>
        /// <c>0x3F</c> - AK, an acknowledgement. Always carries
        /// <see cref="NdLinkHeader.PayloadLength"/> = 0.
        /// </summary>
        Acknowledge = 0x3F,

        /// <summary>
        /// <c>0x6F</c> - DR, a disconnect request raised by the network service. Sent on an
        /// ESTABLISHED link (both link ids are real) and carries NO payload; the trailing field
        /// was <c>0x0101</c> on every sample and its meaning is UNKNOWN.
        /// </summary>
        DisconnectRequestByNetworkService = 0x6F,

        /// <summary>
        /// <c>0x60</c> - a disconnect request, by its high nibble.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The low nibble is UNVERIFIED. High nibble 6 is the disconnect-request family -
        /// that is what <c>0x6F</c> is - so a <c>0x60</c> is treated as a disconnect request too.
        /// What the <c>0</c> distinguishes from the <c>F</c> of <c>0x6F</c> is NOT established:
        /// the low nibble is unexplained across the whole family (three known kinds end in
        /// <c>0xF</c> and the data frame <c>0x20</c> ends in <c>0x0</c>), and no capture shows the
        /// two side by side under conditions that would separate them. Do not read
        /// "by user" versus "by network service" into it without evidence.
        /// </para>
        /// <para>
        /// It is handled because D100 sends it repeatedly once it gives up on a conversation, and
        /// leaving it unrecognised produced an endless run of "unknown ND frame kind 0x60" in the
        /// runner log while the link stayed nominally up.
        /// </para>
        /// </remarks>
        DisconnectRequest60 = 0x60,

        /// <summary>
        /// <c>0x70</c> - DC, the confirmation of a disconnect request. INFERRED from where it sits
        /// in the exchange, not from any document.
        /// </summary>
        /// <remarks>
        /// <para><b>The evidence</b></para>
        /// <para>
        /// Three independent occurrences across two captures on 2026-08-24, every one the same
        /// shape: a connection request is answered by <see cref="DisconnectRequest60"/>, and the
        /// refused side replies with this. The link ids are those of the disconnect request with
        /// the two halves swapped, which is what a reply looks like, and the trailing field is
        /// zero where the disconnect request carried <c>0x0001</c>. It carries no payload.
        /// </para>
        /// <para>
        /// The numbering agrees: <c>0x60</c> and <c>0x6F</c> are the disconnect-request family and
        /// <c>0x70</c> is the next one up, which is also how the rest of these kinds line up
        /// against the X.25 family they resemble.
        /// </para>
        /// <para><b>WHAT WOULD DISPROVE IT</b></para>
        /// <para>
        /// A <c>0x70</c> that does NOT follow a disconnect request. Every sample so far follows
        /// one immediately, so a single counter-example retires this reading. It is INFERRED, not
        /// MEASURED - nobody has read it in a manual or carved it out of the kernel.
        /// </para>
        /// <para>
        /// One oddity worth keeping: the FIRST of each pair is addressed to MAC
        /// <c>08:00:26:00:00:00</c> - system number zero - and only the retry is addressed
        /// properly. The sender has not learned the peer's identity yet at that point.
        /// </para>
        /// </remarks>
        DisconnectConfirm = 0x70
    }

    /// <summary>
    /// The 11-byte ND link header that sits between the LLC header and the SINTRAN header on
    /// COSMOS Ethernet frames.
    /// </summary>
    /// <remarks>
    /// <para><b>There IS a link layer on Ethernet</b></para>
    /// <para>
    /// COSMOS does not rely on LLC1's connectionless service. It carries its own sequenced,
    /// acknowledged link protocol inside the LLC payload - the Ethernet counterpart of LAPB on
    /// HDLC. An earlier revision of the transport document claimed Ethernet had no link layer;
    /// that was drawn from a 2026-07-24 capture of a link that never came up, which contained only
    /// retransmissions and therefore no acknowledgements to notice.
    /// </para>
    /// <para>Layout (VERIFIED, 96 frames, both directions)</para>
    /// Byte offsets within the header:
    ///  - <c>+0</c>: <c>0x0B</c> = 11 - the LENGTH of this header, not a magic constant. VERIFIED
    ///    2026-08-03: the 802.3 length is always 3 (LLC) + 11 (this header) + payload, and the
    ///    sender writes a computed value. Every earlier hunt for a fixed <c>0B02</c> constant in a
    ///    binary failed because there is no such constant to find. It is still checked as a
    ///    signature byte here because it is 11 on every frame observed.
    ///  - <c>+1</c>: <c>0x02</c> - constant on every frame observed, meaning UNKNOWN.
    ///  - <c>+2</c>: the frame kind, see <see cref="NdLinkFrameKind"/>. Its HIGH NIBBLE is the
    ///    <see cref="NdNpduType"/>; its low nibble is unexplained.
    ///  - <c>+3</c>: <c>0x00</c> - constant on every frame observed.
    ///  - <c>+4</c>: the send sequence number.
    ///  - <c>+5</c>..<c>+6</c>: the SENDER's link id, big-endian. <c>0x0000</c> on a connection
    ///    request, because no link exists yet (VERIFIED 2026-08-03).
    ///  - <c>+7</c>..<c>+8</c>: the RECEIVER's link id, big-endian.
    ///  - <c>+9</c>..<c>+10</c>: big-endian, and its MEANING DEPENDS ON THE KIND - see
    ///    <see cref="TrailingField"/> and <see cref="PayloadLength"/>.
    /// <para><b>The length relation - data frames only</b></para>
    /// <para>
    /// On data and acknowledge frames, in both directions:
    /// <c>802.3 length = 3 (LLC) + 11 (this header) + PayloadLength</c>. The frame is padded out to
    /// the 60-byte Ethernet minimum and the padding is NOT part of the message.
    /// </para>
    /// <para><b>CORRECTION 2026-08-03: the trailing field is not always a length</b></para>
    /// <para>
    /// A both-ends capture pinned three different meanings for offsets <c>+9</c>..<c>+10</c>:
    /// on <c>0x20</c> data it IS the payload length; on a <c>0x0F</c> connection request it
    /// carried <c>0x0066</c> = 102 = the SENDER'S OWN SYSTEM NUMBER while the real payload length
    /// was zero (802.3 length <c>0x000E</c>); on a <c>0x6F</c> disconnect request it carried
    /// <c>0x0101</c>, meaning UNKNOWN, payload again zero. The 802.3 length field is the authority
    /// on how many payload bytes are present. Read <see cref="PayloadLength"/> only when
    /// <see cref="IsData"/>; otherwise read <see cref="TrailingField"/>.
    /// </para>
    /// <para><b>Control frames carry no payload</b></para>
    /// <para>
    /// VERIFIED 2026-08-03: <c>0x0F</c> and <c>0x6F</c> frames have 802.3 length <c>0x000E</c> =
    /// 3 + 11 + 0. Every byte after the eleventh is Ethernet padding to the 60-byte minimum.
    /// Earlier readings that assigned meaning to that tail were reading padding.
    /// </para>
    /// <para><b>Sequencing</b></para>
    /// <para>
    /// Each direction runs its own send sequence (observed stepping <c>0x43, 0x44, 0x45, ...</c>).
    /// On receiving a <see cref="NdLinkFrameKind.Data"/> frame the peer replies with an
    /// <see cref="NdLinkFrameKind.Acknowledge"/> frame whose sequence is the received sequence
    /// plus one - the next expected value - and whose payload length is zero.
    /// </para>
    /// <para><b>Link ids</b></para>
    /// <para>
    /// UNKNOWN where they come from. In the 2026-08-01 capture node 102 used <c>0x5062</c> and node
    /// 100 <c>0x59C1</c>. In the both-ends capture of 2026-08-03 the pair was D100 <c>0x2F48</c>
    /// and D102 <c>0x28A4</c>. Both pairs are real observations from different sessions, which is
    /// itself the point: the ids are per-session. They are neither the node number nor the system
    /// number embedded in the MAC. They are stable for the life of the link and swap with
    /// direction. A node must LEARN the peer's id from the first frame received and take its own
    /// from configuration - synthesising one from the node number would be a fabricated constant.
    /// </para>
    /// <para>
    /// Docs: <c>SINTRAN/XMSG/DOC/COSMOS-ETHERNET-TRANSPORT-FRAMING-2026-08-01.md</c> section 2a,
    /// and <c>SINTRAN/XMSG/DOC/COSMOS-ETHERNET-LINK-CONTROL-FRAMES-2026-08-03.md</c> sections 2b,
    /// 2c and 2d.
    /// </para>
    /// </remarks>
    public readonly struct NdLinkHeader
    {
        /// <summary>
        /// Length of the ND link header in bytes.
        /// </summary>
        public const int Length = 11;

        /// <summary>
        /// Byte 0, <c>0x0B</c> = 11 on every observed frame - this is the header LENGTH, the same
        /// value as <see cref="Length"/>, not a magic number. VERIFIED 2026-08-03.
        /// </summary>
        /// <remarks>
        /// Kept as a named signature constant because <see cref="TryParse"/> uses it to reject
        /// buffers that are not an ND link header at all. Do not describe it as a magic constant:
        /// searches for a literal <c>0B02</c> in ND binaries find nothing because the sender
        /// computes the value.
        /// </remarks>
        public const byte Signature0 = 0x0B;

        /// <summary>
        /// Byte 1, constant <c>0x02</c> on every observed frame.
        /// </summary>
        public const byte Signature1 = 0x02;

        /// <summary>
        /// Initialises a header.
        /// </summary>
        /// <param name="kind">
        /// The raw frame-kind byte (offset 2).
        /// </param>
        /// <param name="sequence">
        /// The send sequence number (offset 4).
        /// </param>
        /// <param name="senderLinkId">
        /// The sender's link id (offsets 5-6).
        /// </param>
        /// <param name="receiverLinkId">
        /// The receiver's link id (offsets 7-8).
        /// </param>
        /// <param name="payloadLength">
        /// The number of payload bytes following the header (offsets 9-10).
        /// </param>
        public NdLinkHeader(byte kind, byte sequence, ushort senderLinkId, ushort receiverLinkId, ushort payloadLength)
        {
            Kind = kind;
            Sequence = sequence;
            SenderLinkId = senderLinkId;
            ReceiverLinkId = receiverLinkId;
            PayloadLength = payloadLength;
        }

        /// <summary>
        /// Gets the raw frame-kind byte at offset 2.
        /// </summary>
        /// <remarks>
        /// Deliberately a <see cref="byte"/> and not <see cref="NdLinkFrameKind"/>: only
        /// <c>0x0F</c>, <c>0x20</c>, <c>0x3F</c> and <c>0x6F</c> have been observed, four more NPDU
        /// types exist whose wire byte is unknown, and an unexpected value must reach a log rather
        /// than take the link down. Use <see cref="IsData"/> / <see cref="IsAcknowledge"/> /
        /// <see cref="IsConnectionRequest"/> / <see cref="IsDisconnectRequest"/> for the known
        /// cases, or <see cref="NpduType"/> for the high-nibble type.
        /// </remarks>
        public byte Kind { get; }

        /// <summary>
        /// Gets the send sequence number at offset 4.
        /// </summary>
        public byte Sequence { get; }

        /// <summary>
        /// Gets the sender's link id (offsets 5-6, big-endian).
        /// </summary>
        public ushort SenderLinkId { get; }

        /// <summary>
        /// Gets the receiver's link id (offsets 7-8, big-endian).
        /// </summary>
        public ushort ReceiverLinkId { get; }

        /// <summary>
        /// Gets the payload length in bytes (offsets 9-10, big-endian) - VALID ONLY WHEN
        /// <see cref="IsData"/> is true.
        /// </summary>
        /// <remarks>
        /// <para>
        /// CORRECTION 2026-08-03. This field was documented as the payload length unconditionally.
        /// It is not. On a <c>0x0F</c> connection request the same two bytes carried the sender's
        /// own system number (<c>0x0066</c> = 102) while the frame had NO payload at all, and on a
        /// <c>0x6F</c> disconnect request they carried <c>0x0101</c>, also with no payload. A
        /// caller that trusted this property on a connection request would try to read 102 bytes of
        /// Ethernet padding as a message.
        /// </para>
        /// <para>
        /// The property is kept, with its name and behaviour unchanged, because callers and tests
        /// depend on it. Guard every use with <see cref="IsData"/>. To read the raw two bytes on
        /// any kind without pretending they are a length, use <see cref="TrailingField"/>. The
        /// 802.3 length field remains the authority on how many payload bytes are really present.
        /// </para>
        /// </remarks>
        public ushort PayloadLength { get; }

        /// <summary>
        /// Gets the raw 16-bit field at offsets 9-10, big-endian, WITHOUT interpreting it.
        /// </summary>
        /// <remarks>
        /// The same storage as <see cref="PayloadLength"/>, exposed under a name that makes no
        /// claim about the meaning. Observed values: the payload length on <c>0x20</c> data, zero
        /// on <c>0x3F</c> acknowledge, the sender's own system number on <c>0x0F</c> connection
        /// request, and <c>0x0101</c> (meaning UNKNOWN) on <c>0x6F</c>.
        /// </remarks>
        public ushort TrailingField => PayloadLength;

        /// <summary>
        /// Gets the NPDU type, taken from the HIGH NIBBLE of <see cref="Kind"/>.
        /// </summary>
        /// <remarks>
        /// VERIFIED for the four kinds with a captured wire byte. For an unrecognised kind byte
        /// this still returns a value in 0-7, which may be meaningless - check
        /// <see cref="NdLinkFrameKind"/> membership if that matters.
        /// </remarks>
        public NdNpduType NpduType => (NdNpduType)(Kind >> 4);

        /// <summary>
        /// Gets the low nibble of <see cref="Kind"/>, whose meaning is NOT EXPLAINED.
        /// </summary>
        /// <remarks>
        /// <c>0xF</c> on the connection request, acknowledge and disconnect request; <c>0x0</c> on
        /// data. Exposed so a caller can log it. Do not build behaviour on it.
        /// </remarks>
        public byte KindLowNibble => (byte)(Kind & 0x0F);

        /// <summary>
        /// Gets a value indicating whether this frame carries a payload.
        /// </summary>
        public bool IsData => Kind == (byte)NdLinkFrameKind.Data;

        /// <summary>
        /// Gets a value indicating whether this frame is an acknowledgement.
        /// </summary>
        public bool IsAcknowledge => Kind == (byte)NdLinkFrameKind.Acknowledge;

        /// <summary>
        /// Gets a value indicating whether this frame is a connection request (<c>0x0F</c>, CR).
        /// </summary>
        /// <remarks>
        /// Such a frame carries no payload and a sender link id of <c>0x0000</c>.
        /// </remarks>
        public bool IsConnectionRequest => Kind == (byte)NdLinkFrameKind.ConnectionRequest;

        /// <summary>
        /// Gets a value indicating whether this frame is a disconnect request - anything whose
        /// HIGH NIBBLE is 6.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>0x6F</c> is the network-service disconnect request, VERIFIED from the both-ends
        /// capture of 2026-08-03. <c>0x60</c> is classified here by its high nibble only: D100
        /// sends it repeatedly once it gives up, and what its low nibble means is UNVERIFIED - see
        /// <see cref="NdLinkFrameKind.DisconnectRequest60"/>. The nibble test is used rather than
        /// a list so that a third value in the family is handled instead of spamming the log.
        /// </para>
        /// <para>
        /// The user disconnect request (<see cref="NdNpduType.DisconnectRequestByUser"/>) has
        /// never been captured, so no wire byte is claimed for it.
        /// </para>
        /// </remarks>
        public bool IsDisconnectRequest => (Kind & 0xF0) == 0x60;

        /// <summary>
        /// Builds a data header.
        /// </summary>
        /// <param name="sequence">
        /// The send sequence number.
        /// </param>
        /// <param name="senderLinkId">
        /// This node's link id.
        /// </param>
        /// <param name="receiverLinkId">
        /// The peer's link id.
        /// </param>
        /// <param name="payloadLength">
        /// The number of payload bytes that will follow.
        /// </param>
        /// <returns>
        /// A header with <see cref="Kind"/> set to <see cref="NdLinkFrameKind.Data"/>.
        /// </returns>
        public static NdLinkHeader Data(byte sequence, ushort senderLinkId, ushort receiverLinkId, ushort payloadLength)
        {
            return new NdLinkHeader((byte)NdLinkFrameKind.Data, sequence, senderLinkId, receiverLinkId, payloadLength);
        }

        /// <summary>
        /// Builds the acknowledgement for a received data frame.
        /// </summary>
        /// <param name="receivedSequence">
        /// The sequence number of the data frame being acknowledged.
        /// </param>
        /// <param name="senderLinkId">
        /// This node's link id.
        /// </param>
        /// <param name="receiverLinkId">
        /// The peer's link id.
        /// </param>
        /// <returns>
        /// A header with <see cref="Kind"/> set to <see cref="NdLinkFrameKind.Acknowledge"/>,
        /// sequence <paramref name="receivedSequence"/> + 1, and zero payload length.
        /// </returns>
        /// <remarks>
        /// The plus one is the observed convention - the acknowledgement carries the NEXT EXPECTED
        /// sequence, not the one being acknowledged. Verified on every data frame in the capture.
        /// It wraps inside <see cref="SequenceModulus"/>, so <c>0x7F</c> is acknowledged with
        /// <c>0x00</c>.
        /// </remarks>
        public static NdLinkHeader AcknowledgeFor(byte receivedSequence, ushort senderLinkId, ushort receiverLinkId)
        {
            return new NdLinkHeader(
                (byte)NdLinkFrameKind.Acknowledge, NextSequence(receivedSequence), senderLinkId, receiverLinkId, 0);
        }

        /// <summary>
        /// The number of values the frame sequence counts through before wrapping back to zero.
        /// </summary>
        /// <remarks>
        /// <para><b>SEVEN BITS, not eight - MEASURED 2026-08-11</b></para>
        /// <para>
        /// Counted over every ND frame in the three real machine-to-machine captures in
        /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\</c>: <c>capture-list-files.txt</c> (887
        /// frames), <c>capture-read.txt</c> and <c>capture-write.txt</c>. The highest sequence any
        /// of them carries is <c>0x7F</c>, and not one frame in any of them has bit 7 set. The wrap
        /// is visible in <c>capture-list-files.txt</c> at 02:20:10.52, where D102 sends sequence
        /// <c>0x7F</c> and its next data frame is <c>0x00</c>.
        /// </para>
        /// <para>
        /// This wrapped at 256 until 2026-08-11, so after 128 frames it would have emitted
        /// sequences no real machine ever sends. The live run on 2026-08-10 reached 124 and stopped
        /// four frames short of finding out what D100 does with <c>0x80</c>.
        /// </para>
        /// </remarks>
        public const int SequenceModulus = 128;

        /// <summary>
        /// Steps a frame sequence on by one, wrapping inside the seven-bit space the wire uses.
        /// </summary>
        /// <param name="sequence">
        /// The sequence to step.
        /// </param>
        /// <returns>
        /// The next sequence value.
        /// </returns>
        public static byte NextSequence(byte sequence)
        {
            return (byte)((sequence + 1) % SequenceModulus);
        }

        /// <summary>
        /// Parses a header from the start of a buffer.
        /// </summary>
        /// <param name="source">
        /// The bytes to parse, starting at the header.
        /// </param>
        /// <param name="header">
        /// Receives the parsed header, or the default value when parsing fails.
        /// </param>
        /// <returns>
        /// True when the buffer is long enough and carries the expected signature bytes.
        /// </returns>
        public static bool TryParse(ReadOnlySpan<byte> source, out NdLinkHeader header)
        {
            if (source.Length < Length || source[0] != Signature0 || source[1] != Signature1)
            {
                header = default;
                return false;
            }

            header = new NdLinkHeader(
                source[2],
                source[4],
                (ushort)((source[5] << 8) | source[6]),
                (ushort)((source[7] << 8) | source[8]),
                (ushort)((source[9] << 8) | source[10]));
            return true;
        }

        /// <summary>
        /// Writes the header in wire order.
        /// </summary>
        /// <param name="destination">
        /// The buffer to write into; must be at least <see cref="Length"/> bytes.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="destination"/> is shorter than <see cref="Length"/>.
        /// </exception>
        public void Write(Span<byte> destination)
        {
            if (destination.Length < Length)
            {
                throw new ArgumentException($"Need {Length} bytes to write an ND link header.", nameof(destination));
            }

            destination[0] = Signature0;
            destination[1] = Signature1;
            destination[2] = Kind;
            destination[3] = 0x00;
            destination[4] = Sequence;
            destination[5] = (byte)(SenderLinkId >> 8);
            destination[6] = (byte)(SenderLinkId & 0xFF);
            destination[7] = (byte)(ReceiverLinkId >> 8);
            destination[8] = (byte)(ReceiverLinkId & 0xFF);
            destination[9] = (byte)(PayloadLength >> 8);
            destination[10] = (byte)(PayloadLength & 0xFF);
        }
    }
}
