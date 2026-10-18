//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Qform;

namespace RetroIO.Protocols.Xmsg.Fa
{
    /// <summary>
    /// Builds and reads the bodies of a <c>ReadFile</c> or <c>WriteFile</c> exchange, including the
    /// 1032-byte data messages that carry the file content in either direction.
    /// </summary>
    /// <remarks>
    /// <para><b>ONE codec, both directions</b></para>
    /// From <c>DOC\FA-READ-WRITE-WIRE-PROTOCOL-2026-08-04.md</c> section 5: "The request is the
    /// READ request with the operation changed from <c>0x0008</c> to <c>0x0009</c>. Nothing else
    /// differs." Same selector 1, same 32-bit position under <c>A4</c>, same reply shape, same
    /// 1032-byte data message with the same counter and token rule. So
    /// <see cref="TryReadRequest"/> never looks at the operation, and
    /// <see cref="BuildReply(FaOperation, ushort, ushort)"/> takes it as a parameter.
    /// <para><b>What DOES differ: when the data moves</b></para>
    /// Measured in wire order across both captures:
    ///  - READ: request, then the reply, THEN the data messages from the server.
    ///  - WRITE: request, then the data messages from the client, THEN the reply.
    /// So the reply is the COMPLETION of the operation in both cases. That is a property of the
    /// server's sequencing rather than of these bodies, but it is the thing to get right when
    /// wiring either one up.
    /// <para>
    /// Named for the data rather than for reading: it was <c>FaReadFileCodec</c> until the write
    /// direction showed the shapes were identical, and a codec that builds write replies should not
    /// be called a read codec.
    /// </para>
    /// <para><b>The reply carries no data at all</b></para>
    /// Measured in <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-read.txt</c>:
    /// <code>
    /// request  92 0008  92 seq  F2 0001  A4 00000000  F2 00FF
    /// reply    92 0008  92 seq                        F2 00FF
    /// </code>
    /// The content then arrives as SEPARATE messages after the reply, two of them per read, each
    /// carrying 1024 raw bytes. Two blocks of 1024 make the 2048-byte unit a read moves.
    /// <para><b>A data message is not QFORM</b></para>
    /// Its body is the ordinary eight-byte FA prefix - message type, conversation, session header -
    /// followed by 1024 raw file bytes and nothing else. There are no tags inside. A reader that
    /// walks it as QFORM will decode file content as tags: the first captured block opens
    /// <c>8D 0A C0 28 4E 44 2D 50 41 D4</c>, which is the text ".. (ND-PAT" in ND 7-bit, not a
    /// <c>0x8D</c> tag.
    /// <para><b>Do not reuse FaTransferCodec</b></para>
    /// That codec serves XFTRA and its data message is 1030 bytes - a six-byte function/page/
    /// displacement header plus the block. This one is 1032 with an eight-byte header and no page
    /// or displacement. The two are different protocols that happen to move 1024 bytes at a time.
    /// <para><b>There is no end marker</b></para>
    /// The last read of a file returns a FULL 2048 bytes even when fewer remain - the capture's
    /// final read returns 2048 with only 1521 bytes left. The caller stops on the size it learned
    /// when it opened the file, so a server must pad rather than short-count.
    /// </remarks>
    public static class FaFileDataCodec
    {
        /// <summary>
        /// The number of file bytes one data message carries.
        /// </summary>
        public const int BlockLength = 1024;

        /// <summary>
        /// The number of data messages one read produces.
        /// </summary>
        /// <remarks>
        /// Verified by count: 18 data messages for 9 reads in <c>capture-read.txt</c>, and the same
        /// pairing in <c>capture-write.txt</c>. Two blocks of 1024 make the 2048 bytes a read moves.
        /// </remarks>
        public const int BlocksPerRead = 2;

        /// <summary>
        /// The number of file bytes one read moves.
        /// </summary>
        public const int ReadLength = BlockLength * BlocksPerRead;

        /// <summary>
        /// Set in the session-header counter of the LAST data message of a delivery.
        /// </summary>
        /// <remarks>
        /// <para><b>SOLVED 2026-08-05 - this was recorded as UNKNOWN</b></para>
        /// The counter is the ordinary session-header exchange counter and simply increments by one
        /// per message. Bit 7 is then set on the second message of each pair, which is what marks
        /// the end of the delivery. Reading the counters of the first pair as raw bytes -
        /// <c>05</c> then <c>86</c> - made it look like a jump; it is <c>05</c>, then <c>06</c> with
        /// bit 7 set.
        /// <para><b>Verified across three conversations and 27 pairs</b></para>
        /// In <c>capture-read.txt</c> the pairs run
        /// <c>05/86, 08/89, 0B/8C, 0E/8F, 11/92, 14/95, 17/98, 1A/9B, 1D/9E</c> and again
        /// <c>03/84, 06/87, ...</c> on a second conversation. In <c>capture-write.txt</c>, where the
        /// data flows the other way, D100 sends <c>04/85, 07/88, 0A/8B, 0D/8E, 10/91, 13/94, ...</c>.
        /// Every second counter is the first plus one, with bit 7 set. The step of three between
        /// pairs is just the reply that sits between them.
        /// </remarks>
        public const byte LastDataMessageFlag = 0x80;

        /// <summary>
        /// The session token the LAST data message of a delivery carries.
        /// </summary>
        /// <remarks>
        /// The first message of a pair carries the SENDER'S OWN session token - <c>0x90BB</c> for
        /// D102 reading, <c>0xD761</c> for D100 writing - and the second carries <c>0x0001</c>
        /// instead, in both directions and in all three captured conversations. Why the token is
        /// replaced rather than repeated is UNKNOWN; that it IS replaced is measured.
        /// </remarks>
        public const ushort LastDataMessageToken = 0x0001;

        /// <summary>
        /// Selector carrying the file position in a request.
        /// </summary>
        public const ushort PositionSelector = 0x0001;

        /// <summary>
        /// Selector carrying the byte count delivered, in a reply that reports one.
        /// </summary>
        public const ushort DeliveredSelector = 0x0002;

        /// <summary>
        /// Selector carrying the byte count wanted, in a request that asks for one.
        /// </summary>
        /// <remarks>
        /// Present only when the conversation never sent <c>SetBlockSize</c>. In
        /// <c>capture-read.txt</c> the second conversation asks <c>F2 0003 A2 0800</c> for 2048
        /// bytes and is answered <c>F2 0002 A2 0800</c>.
        /// </remarks>
        public const ushort WantedSelector = 0x0003;

        /// <summary>
        /// Reads a <c>ReadFile</c> request.
        /// </summary>
        /// <param name="body">
        /// The QFORM body, from the operation word onwards.
        /// </param>
        /// <param name="serial">
        /// The exchange sequence, echoed by the reply.
        /// </param>
        /// <param name="position">
        /// The file position asked for, in units of the current block size.
        /// </param>
        /// <param name="wantedBytes">
        /// The byte count the request asked for, or zero when it named none.
        /// </param>
        /// <returns>
        /// <c>true</c> when the body parsed as a read request.
        /// </returns>
        /// <remarks>
        /// <para><b>The position is not a byte offset</b></para>
        /// It counts blocks of the size set by <c>SetBlockSize</c>. The capture's nine reads run
        /// <c>00000000</c> to <c>00000008</c> over a 17905-byte file, which is nine units of 2048.
        /// Multiply by the block size to get a byte offset.
        /// </remarks>
        public static bool TryReadRequest(
            ReadOnlySpan<byte> body,
            out ushort serial,
            out uint position,
            out ushort wantedBytes)
        {
            serial = 0;
            position = 0;
            wantedBytes = 0;

            IReadOnlyList<QformField> fields;
            if (!QformReader.TryRead(body, out fields) || fields.Count < 2)
            {
                return false;
            }

            serial = QformValue.ReadUInt16(body, fields[1]);

            QformField positionField;
            if (!QformValue.TryFindValue(
                    body, fields, PositionSelector, QformClass.TypedInteger, 4, out positionField))
            {
                return false;
            }

            position = QformValue.ReadUInt32(body, positionField);

            QformField wantedField;
            if (QformValue.TryFindValue(
                    body, fields, WantedSelector, QformClass.TypedInteger, 2, out wantedField))
            {
                wantedBytes = QformValue.ReadUInt16(body, wantedField);
            }

            return true;
        }

        /// <summary>
        /// Reads the block size out of a <c>SetBlockSize</c> request.
        /// </summary>
        /// <param name="body">
        /// The QFORM body, from the operation word onwards.
        /// </param>
        /// <param name="blockSize">
        /// The block size in bytes the client is setting.
        /// </param>
        /// <returns>
        /// <c>true</c> when the body carried a block size.
        /// </returns>
        /// <remarks>
        /// <para><b>The whole request</b></para>
        /// It carries one value under selector 1 and nothing else:
        /// <code>
        /// 92 0007  92 seq  F2 0001  A2 0800  F2 00FF  pad
        /// </code>
        /// <c>0x0800</c> = 2048.
        /// <para><b>VERIFIED against two independent sources</b></para>
        /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-read.txt</c> line 49 (D100 to D102), and
        /// a live D100 driving <c>COPY-FILE</c> at us on 2026-08-06. The two differ in the
        /// conversation number and in NOTHING else - the same operation, sequence, selector, value
        /// and trailing pad. That is as pinned as a request gets.
        /// <para><b>Why it matters</b></para>
        /// The <c>ReadFile</c> position counts BLOCKS of this size, not bytes. See
        /// <see cref="TryReadRequest"/>.
        /// </remarks>
        public static bool TryReadBlockSize(ReadOnlySpan<byte> body, out ushort blockSize)
        {
            blockSize = 0;

            IReadOnlyList<QformField> fields;
            if (!QformReader.TryRead(body, out fields) || fields.Count < 2)
            {
                return false;
            }

            QformField sizeField;
            if (!QformValue.TryFindValue(
                    body, fields, PositionSelector, QformClass.TypedInteger, 2, out sizeField))
            {
                return false;
            }

            blockSize = QformValue.ReadUInt16(body, sizeField);
            return blockSize != 0;
        }

        /// <summary>
        /// Builds the reply to a <c>ReadFile</c>, which announces nothing but the echo.
        /// </summary>
        /// <param name="serial">
        /// The exchange sequence to echo.
        /// </param>
        /// <param name="deliveredBytes">
        /// The byte count to report under selector 2, or zero to omit it. Only a request that named
        /// a wanted count is answered with one.
        /// </param>
        /// <returns>
        /// The QFORM body, from the operation word onwards.
        /// </returns>
        public static byte[] BuildReply(ushort serial, ushort deliveredBytes)
        {
            return BuildReply(FaOperation.ReadFile, serial, deliveredBytes);
        }

        /// <summary>
        /// Builds the reply to a read or a write, which announces nothing but the echo.
        /// </summary>
        /// <param name="operation">
        /// The operation being answered - <see cref="FaOperation.ReadFile"/> or
        /// <see cref="FaOperation.WriteFile"/>. It is ECHOED, as every file-access reply echoes the
        /// operation it answers.
        /// </param>
        /// <param name="serial">
        /// The exchange sequence to echo.
        /// </param>
        /// <param name="deliveredBytes">
        /// The byte count to report under selector 2, or zero to omit it. Only a request that named
        /// a wanted count is answered with one.
        /// </param>
        /// <returns>
        /// The QFORM body, from the operation word onwards.
        /// </returns>
        /// <remarks>
        /// <para><b>The two directions share this shape exactly</b></para>
        /// From <c>DOC\FA-READ-WRITE-WIRE-PROTOCOL-2026-08-04.md</c> section 5, quoting both:
        /// <code>
        /// read  request  92 0008 92 seq F2 0001 A4 00000000 F2 00FF
        /// write request  92 0009 92 seq F2 0001 A4 00000000 F2 00FF
        /// </code>
        /// "The request is the READ request with the operation changed from <c>0x0008</c> to
        /// <c>0x0009</c>. Nothing else differs." Same selector, same 32-bit position under
        /// <c>A4</c>, same reply shape - which is why one codec serves both and
        /// <see cref="TryReadRequest"/> never looks at the operation at all.
        /// </remarks>
        public static byte[] BuildReply(FaOperation operation, ushort serial, ushort deliveredBytes)
        {
            bool withCount = deliveredBytes > 0;

            // 92 000x | 92 serial | [F2 0002 A2 count] | F2 00FF
            byte[] body = new byte[3 + 3 + (withCount ? 3 + 3 : 0) + 3];
            QformWriter writer = new QformWriter(body);

            writer.WriteInteger((ushort)operation);
            writer.WriteInteger(serial);

            if (withCount)
            {
                writer.WriteSelector(DeliveredSelector);
                writer.WriteTypedInteger(deliveredBytes);
            }

            writer.WriteEndOfList();
            writer.EnsureComplete("Read reply");

            return body;
        }

        /// <summary>
        /// The length in bytes of a whole data message: the FA prefix plus one block.
        /// </summary>
        /// <remarks>
        /// 8 + 1024 = 1032, which is the length the frame's Flags 2 announces on every captured data
        /// message.
        /// </remarks>
        public const int DataMessageLength = FaExchangeCodec.QformOffset + BlockLength;

        /// <summary>
        /// Builds one complete data message: the FA prefix, then a block of file content.
        /// </summary>
        /// <param name="conversation">
        /// The conversation the delivery belongs to.
        /// </param>
        /// <param name="counter">
        /// The session-header exchange counter for this message, taken from the conversation's
        /// shared counter - <c>FaServerConversation.NextMessageCounter</c>. It carries
        /// <see cref="LastDataMessageFlag"/> on the last message of a delivery and not on the first.
        /// </param>
        /// <param name="token">
        /// The session token. The sender's own on the first message of a pair, and
        /// <see cref="LastDataMessageToken"/> on the last.
        /// </param>
        /// <param name="content">
        /// The file bytes for this block. A short tail is padded - see
        /// <see cref="BuildDataBlock"/>.
        /// </param>
        /// <returns>
        /// The <see cref="DataMessageLength"/>-byte body.
        /// </returns>
        /// <remarks>
        /// <para><b>There is no QFORM in here</b></para>
        /// After the eight-byte prefix the body is raw file content to the end. A reader that walks
        /// it as tagged fields will decode file text as tags, which is exactly what the first
        /// captured block looks like if you try.
        /// <para><b>This does not send anything</b></para>
        /// The message still has to leave as a fragment pair - subtype <c>0x0A</c> carrying the
        /// first 594 bytes and <c>0x0C</c> the rest - and that lives in the transport, not here.
        /// </remarks>
        public static byte[] BuildDataMessage(
            ushort conversation,
            byte counter,
            ushort token,
            ReadOnlySpan<byte> content)
        {
            byte[] body = new byte[DataMessageLength];

            // The same envelope every other file-server message carries; only what follows it
            // differs - raw file content here, a QFORM body elsewhere.
            FaExchangeCodec.WriteEnvelope(
                body, FaMessageType.Request, conversation, counter, token);

            int count = content.Length;
            if (count > BlockLength)
            {
                count = BlockLength;
            }

            for (int i = 0; i < count; i++)
            {
                body[FaExchangeCodec.QformOffset + i] = content[i];
            }

            return body;
        }

        // NextDataMessageCounter is GONE (2026-08-06). It advanced a counter that belonged only to
        // the data messages, which is why a delivery stepped by two where the wire steps by three.
        // The session-header byte for BOTH replies and data messages now comes from
        // FaServerConversation.NextMessageCounter, so there is one counter and one rule.

        /// <summary>
        /// Copies one block of file content into a data-message body, padding a short tail.
        /// </summary>
        /// <param name="content">
        /// The file bytes to send. Fewer than <see cref="BlockLength"/> is allowed and the rest is
        /// zero filled.
        /// </param>
        /// <returns>
        /// Exactly <see cref="BlockLength"/> bytes, ready to follow the eight-byte FA prefix.
        /// </returns>
        /// <remarks>
        /// <para><b>Why it pads instead of shortening</b></para>
        /// The capture's last read returns a full 2048 bytes with only 1521 left in the file. There
        /// is no short block and no end marker anywhere in the transfer; the caller stops on the
        /// size it learned at open. A server that shortened the last block would be inventing a
        /// signal the protocol does not have.
        /// </remarks>
        public static byte[] BuildDataBlock(ReadOnlySpan<byte> content)
        {
            byte[] block = new byte[BlockLength];

            int count = content.Length;
            if (count > BlockLength)
            {
                count = BlockLength;
            }

            for (int i = 0; i < count; i++)
            {
                block[i] = content[i];
            }

            return block;
        }

    }
}
