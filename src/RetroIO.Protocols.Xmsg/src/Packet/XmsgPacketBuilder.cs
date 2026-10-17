//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Packet
{
    /// <summary>
    /// Builds outgoing <see cref="XmsgPacket"/>s from the envelope model. The single point where
    /// the seam turns intent (a data frame, an ACK, a reachability reply, a network-error reject)
    /// into wire bytes, reusing the verified <see cref="XmsgFrame"/> serialisation underneath.
    /// </summary>
    /// <remarks>
    /// Every <c>Create*</c> here reproduces bytes that are VERIFIED against captures / live tests:
    /// <see cref="CreateData"/> reproduces the conn-to-d102 connect-accept and port-assign frames
    /// byte-for-byte; <see cref="CreateAck"/> reproduces the subtype-<c>0x03</c> secure ACK that
    /// machine 100 accepts (channel = connect-channel + 4, decrementing trailing counter);
    /// <see cref="CreateReachabilityReply"/> the subtype-<c>0x13</c> reply; and
    /// <see cref="CreateNetworkError"/> the subtype-<c>0x07</c> reject whose Flags2 is a negative
    /// XE* code. No stateful sequencing lives here - the layer owns that and passes final values in.
    /// </remarks>
    public static class XmsgPacketBuilder
    {
        /// <summary>
        /// Flags 2 value carried by a secure ACK (subtype 0x03). VERIFIED constant.
        /// </summary>
        public const ushort AckFlags2 = 0x0001;

        /// <summary>
        /// Flags 1 broadcast marker carried by reachability frames. VERIFIED constant.
        /// </summary>
        public const ushort ReachabilityFlags1 = 0xFFFF;

        /// <summary>
        /// Flags 2 value carried by reachability frames. VERIFIED constant.
        /// </summary>
        public const ushort ReachabilityFlags2 = 0x0001;

        /// <summary>
        /// Builds a data (subtype <c>0x0E</c>) packet from a full envelope field set.
        /// </summary>
        /// <param name="fields">
        /// The header + sub-header + payload fields.
        /// </param>
        /// <returns>
        /// The built data packet.
        /// </returns>
        /// <remarks>
        /// This is the exact assembly the captured responder frames use: fixed markers, packet type
        /// <c>0x00</c>, subtype Data; the sub-header's constant <c>0x21 0x00</c> marker is written by
        /// <see cref="XmsgSubHeader.Serialize"/>. The body goes out verbatim from absolute offset
        /// 28. The frame is built from scratch (no retained RawBytes) so it serialises from the
        /// model.
        /// </remarks>
        public static XmsgPacket CreateData(XmsgDataFields fields)
        {
            // The BODY is everything from absolute offset 28: XMCSM's old "low half" (really the
            // first body word), what the old model called Pad and UserDataLength (really body
            // bytes 2 and 3), and the payload. XmsgDataFields keeps them in one array.
            byte[] body = fields.Body != null ? fields.Body : Array.Empty<byte>();

            XmsgFrame frame = new XmsgFrame();
            frame.Header.Marker1 = SintranHeader.Marker1Value;
            frame.Header.Marker2 = SintranHeader.Marker2Normal;
            frame.Header.PacketType = 0x00;
            frame.Header.Subtype = SintranPacketSubtype.Data;
            frame.Header.DestinationNode = fields.DestinationNode;
            frame.Header.SourceNode = fields.SourceNode;
            frame.Header.Flags1 = fields.Flags1;
            frame.Header.Flags2 = fields.Flags2;
            frame.Header.Checksum = fields.Checksum;

            XmsgSubHeader sub = new XmsgSubHeader();
            sub.FrameFlags = fields.FrameFlags;
            sub.Role = fields.Role;
            sub.DestinationSystem = fields.DestinationSystem;
            sub.DestinationPort = fields.DestinationPort;
            sub.SourceSystem = fields.SourceSystem;
            sub.SourcePort = fields.SourcePort;
            sub.Xmcsm = fields.Xmcsm;

            frame.SubHeader = sub;
            frame.TrailingBytes = body;
            // Ensure serialisation is driven by the model, not any stray retained bytes.
            frame.ClearRawBytes();
            return new XmsgPacket(frame);
        }

        /// <summary>
        /// Builds a session-data (subtype <c>0x0E</c>) packet whose channel is DERIVED from the
        /// envelope model rather than supplied - the correct way to emit responder session frames
        /// (MOTD, prompts, terminal control) so their Protocol ID is consistent with their
        /// Flags1 / Counter / XMCSM. Replaying a canned channel from another session instead has the
        /// wrong Base and crashes the peer's XMSG (XXPER); this computes it.
        /// </summary>
        /// <param name="fields">
        /// The envelope fields. <see cref="XmsgDataFields.ChecksumHigh"/> is ignored and overwritten
        /// with <see cref="XmsgEnvelope.DeriveChannel(ushort, byte, uint)"/> of the Flags1 / Counter / XMCSM.
        /// </param>
        /// <returns>
        /// The built session-data packet on the derived channel.
        /// </returns>
        public static XmsgPacket CreateSessionData(XmsgDataFields fields)
        {
            // Word 6 of the SINTRAN header is a ones-complement checksum over the other six words,
            // carved from the XMSG kernel at 137314 and verified on 3595/3595 captured frames. Its
            // HIGH byte is what we have historically called the Protocol ID / channel and its LOW
            // byte is what we have called the Counter, so BOTH are outputs here - neither is a
            // free choice. See XmsgEnvelope.ComputeHeaderChecksum.
            ushort checksum = XmsgEnvelope.ComputeHeaderChecksum(
                (ushort)((SintranHeader.Marker1Value << 8) | SintranHeader.Marker2Normal),
                (ushort)SintranPacketSubtype.Data,          // packet type 0x00 in the high byte
                fields.DestinationNode,
                fields.SourceNode,
                fields.Flags1,
                fields.Flags2);

            fields.Checksum = checksum;
            return CreateData(fields);
        }

        /// <summary>
        /// Builds a secure-delivery ACK (subtype <c>0x03</c>) for a received data frame.
        /// </summary>
        /// <param name="destinationNode">
        /// The node the ACK is sent to (the data frame's source).
        /// </param>
        /// <param name="sourceNode">
        /// The replying node (the data frame's destination).
        /// </param>
        /// <param name="datagramSequence">
        /// The Flags 1 (datagram sequence) to echo from the acknowledged data frame.
        /// </param>
        /// <param name="ackChannel">
        /// The Protocol-ID the ACK rides. For a TAD session this is the per-session constant
        /// connect-channel + 4 (VERIFIED D9->DD, DA->DE); echoing the data channel is
        /// the malformed +0 ACK that crashed machine 100 (XXPER).
        /// </param>
        /// <param name="trailingCounter">
        /// The single trailing byte: the receiver's decrementing per-direction counter (seeded at
        /// connect-counter + 0x0A). Sending <c>0x00</c> is a malformed ACK that crashed 100.
        /// </param>
        /// <returns>
        /// The 14-byte ACK packet (header only - an ACK has no body).
        /// </returns>
        public static XmsgPacket CreateAck(
            ushort destinationNode,
            ushort sourceNode,
            ushort datagramSequence,
            SintranProtocolId ackChannel,
            byte trailingCounter)
        {
            XmsgFrame ack = new XmsgFrame();
            ack.Header.Marker1 = SintranHeader.Marker1Value;
            ack.Header.Marker2 = SintranHeader.Marker2Normal;
            ack.Header.PacketType = 0x00;
            ack.Header.Subtype = SintranPacketSubtype.Ack;             // 0x03
            ack.Header.DestinationNode = destinationNode;
            ack.Header.SourceNode = sourceNode;
            ack.Header.Flags1 = datagramSequence;                     // echo the datagram sequence
            ack.Header.Flags2 = AckFlags2;                            // 0x0001
            // CORRECTED 2026-08-04: an ACK is FOURTEEN bytes and carries no trailing byte at all.
            // What was modelled as "channel + a trailing counter" is header word 6, the checksum.
            // Worked example from DOC/captures/FA-READ-WRITE-2026-08-04/capture-read.txt:
            //   21 13 00 03 00 64 00 66 01 F9 00 01 DC 25
            // ones-complement sum of the first six words = 0x23DA and ~0x23DA = 0xDC25.
            // DERIVED 2026-08-07. This used to assemble word 6 from the two parameters, with the
            // note that they were "kept so callers compile". They still are - the signature is
            // unchanged so no caller breaks - but they no longer decide the bytes on the wire.
            //
            // Proving it was safe was the whole question: every caller of this class is a test
            // passing CAPTURED values, so computing instead yields identical bytes if and only if
            // the captures are self-consistent. They are - the suite passes unchanged.
            XmsgEnvelope.StampChecksum(ack.Header);
            return new XmsgPacket(ack);
        }

        /// <summary>
        /// Builds a reachability reply (subtype <c>0x13</c>) answering a reachability request.
        /// </summary>
        /// <param name="destinationNode">
        /// The requester the reply is sent to.
        /// </param>
        /// <param name="sourceNode">
        /// The replying node.
        /// </param>
        /// <param name="protocolId">
        /// The channel to reply on (echo the request's Protocol ID).
        /// </param>
        /// <param name="trailingCommand">
        /// The trailing reply command byte (the request counter + 6; VERIFIED across both captured
        /// request/reply pairs: 102 request 0x08 -> reply 0x0E, 103 request 0x07 -> reply 0x0D).
        /// </param>
        /// <returns>
        /// The reachability-reply packet.
        /// </returns>
        public static XmsgPacket CreateReachabilityReply(
            ushort destinationNode,
            ushort sourceNode,
            SintranProtocolId protocolId,
            byte trailingCommand)
        {
            XmsgFrame reply = new XmsgFrame();
            reply.Header.Marker1 = SintranHeader.Marker1Value;
            reply.Header.Marker2 = SintranHeader.Marker2Normal;
            reply.Header.PacketType = 0x00;
            reply.Header.Subtype = SintranPacketSubtype.ReachabilityReply;   // 0x13
            reply.Header.DestinationNode = destinationNode;                  // swap direction
            reply.Header.SourceNode = sourceNode;
            reply.Header.Flags1 = ReachabilityFlags1;                        // 0xFFFF broadcast marker
            reply.Header.Flags2 = ReachabilityFlags2;                        // 0x0001
            // Same correction as CreateAck: this is header word 6, not a channel plus a trailing
            // byte. The reachability request 21 13 00 19 00 66 00 64 FF FF 00 01 DE 08 sums to
            // 0x21F7 and ~0x21F7 = 0xDE08 - exactly the two bytes at offsets 12-13.
            // DERIVED 2026-08-07, same as CreateAck. The parameters remain on the signature so no
            // caller breaks, but the wire bytes are computed.
            XmsgEnvelope.StampChecksum(reply.Header);
            return new XmsgPacket(reply);
        }

        /// <summary>
        /// Builds a network-error / reject packet (subtype <c>0x07</c>).
        /// </summary>
        /// <param name="destinationNode">
        /// The node the reject is sent to.
        /// </param>
        /// <param name="sourceNode">
        /// The rejecting node.
        /// </param>
        /// <param name="datagramSequence">
        /// The Flags 1 (datagram sequence) to echo from the offending frame.
        /// </param>
        /// <param name="errorCode">
        /// The Flags 2 word: a NEGATIVE XE* code (e.g. XEIMA -19 = <c>0xFFED</c> invalid magic,
        /// XENSE -34 = <c>0xFFDE</c> sequence error). Pass the raw 16-bit two's-complement value.
        /// </param>
        /// <param name="protocolId">
        /// The channel to send the reject on.
        /// </param>
        /// <param name="trailingByte">
        /// The single trailing reject byte.
        /// </param>
        /// <returns>
        /// The network-error packet.
        /// </returns>
        public static XmsgPacket CreateNetworkError(
            ushort destinationNode,
            ushort sourceNode,
            ushort datagramSequence,
            ushort errorCode,
            SintranProtocolId protocolId,
            byte trailingByte)
        {
            XmsgFrame err = new XmsgFrame();
            err.Header.Marker1 = SintranHeader.Marker1Value;
            err.Header.Marker2 = SintranHeader.Marker2Normal;
            err.Header.PacketType = 0x00;
            // The network-error subtype (0x07) is now a named member of SintranPacketSubtype.
            err.Header.Subtype = SintranPacketSubtype.NetworkError;
            err.Header.DestinationNode = destinationNode;
            err.Header.SourceNode = sourceNode;
            err.Header.Flags1 = datagramSequence;
            err.Header.Flags2 = errorCode;                          // negative XE* code
            // DERIVED 2026-08-07, same as CreateAck.
            XmsgEnvelope.StampChecksum(err.Header);
            return new XmsgPacket(err);
        }
    }
}
