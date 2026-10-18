//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Fa
{
    /// <summary>
    /// Builds and reads the message bodies of a COSMOS file transfer: the data blocks that carry
    /// file content, the end-of-transfer marker, and the replies to both.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Captured from node 102 to node 100 in
    /// <c>claude-transfer-file-COMPLETE-102-to-100-2026-07-29.pcapng</c>, the first transfer ever
    /// captured carrying real file content:
    /// </para>
    /// <code>
    /// TRANSFER-FILE d100(system)."xfertest:data",DUMMY:DATA
    ///   -> Completed. Transfer rate: 3 Kbytes/sec
    /// </code>
    /// <para>
    /// This class handles message BODIES only. A transfer data message is 1030 bytes and does
    /// not fit in one LAPB frame, so on the wire it is segmented across two frames using packet
    /// subtypes <c>0x0A</c> and <c>0x0C</c>. That segmentation belongs to the frame layer;
    /// <see cref="SplitForTransmission"/> is provided to compute the split, but assembling the frames
    /// themselves is not this class's job.
    /// </para>
    /// <para>
    /// The transfer is stop-and-wait. Measured over the capture in wire order, the peak number
    /// of unacknowledged messages is exactly 1 - the sender never begins a message before the
    /// previous one is answered. A server may therefore process one message at a time and never needs
    /// to buffer a window of outstanding blocks.
    /// </para>
    /// </remarks>
    public static class FaTransferCodec
    {
        /// <summary>
        /// Size in bytes of the file-content block carried by one data message.
        /// </summary>
        public const int BlockLength = 1024;

        /// <summary>
        /// Size in bytes of the message header preceding the block: function, page, displacement.
        /// </summary>
        public const int DataHeaderLength = 6;

        /// <summary>
        /// Total length of a data message body.
        /// </summary>
        /// <remarks>
        /// 6 + 1024 = 1030, which is the length the frame's Flags2 announces.
        /// </remarks>
        public const int DataMessageLength = DataHeaderLength + BlockLength;

        /// <summary>
        /// Length of the short control bodies: end-of-transfer and every reply.
        /// </summary>
        public const int ControlMessageLength = 6;

        /// <summary>
        /// Number of body bytes carried by the FIRST of the two frames of a data message.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The remaining <c>1030 - 594 = 436</c> bytes travel in the continuation frame.
        /// </para>
        /// <para>
        /// No longer a single-case observation, 2026-07-30. This used to warn that only one
        /// transfer had been recorded, so 594 might be a property of that one file. A second transfer
        /// was then recorded, of <c>LOAD-MODE:BATC</c> - a file of only 167 bytes, a sixth of one
        /// block. It splits at exactly the same point.
        /// </para>
        /// <para>
        /// The reason is that a transfer always ships WHOLE PAGES. The 167-byte file produced two
        /// full 1030-byte messages, page 0 displacement 0 and page 0 displacement 512 words, padded.
        /// The message length never tracks how much of the file is real, so within a transfer this
        /// split cannot vary.
        /// </para>
        /// <para>
        /// Nor is it specific to a transfer, 2026-08-05. The file-access <c>ReadFile</c>
        /// delivery is a 1032-byte message of an entirely different protocol, and
        /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-read.txt</c> splits it at 594 as well -
        /// <c>2113 000A ... 0408</c> then <c>2113 000C ... 0252</c>. So the split belongs to the
        /// FRAME layer rather than to this codec, and it now lives in
        /// <see cref="SintranMessageFragment"/>; this constant is kept as the name the transfer code
        /// reads it by. Locked in by
        /// <c>TransferFragmentationTests.TransferMessage_IsAlwaysAWholeBlockNoMatterHowSmallTheFileIs</c>.
        /// </para>
        /// </remarks>
        public const int FirstFragmentBodyLength = SintranMessageFragment.FirstFragmentBodyLength;

        /// <summary>
        /// The displacement, in words, at which the second block of a page begins.
        /// </summary>
        /// <remarks>
        /// The displacement counts WORDS, not bytes: it advances 0 -> 512 within a page and resets as
        /// the page number increments, so one page holds two 1024-byte blocks.
        /// </remarks>
        public const ushort SecondBlockDisplacementWords = 512;

        /// <summary>
        /// Builds the body of a data message carrying one block of file content.
        /// </summary>
        /// <param name="page">
        /// The page number within the file.
        /// </param>
        /// <param name="displacementWords">
        /// The displacement within the page, in WORDS - 0 for the first block, 512 for the second.
        /// </param>
        /// <param name="block">
        /// Exactly <see cref="BlockLength"/> bytes of file content.
        /// </param>
        /// <returns>
        /// The <see cref="DataMessageLength"/>-byte message body.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// The block is the wrong length.
        /// </exception>
        public static byte[] BuildDataMessage(ushort page, ushort displacementWords, ReadOnlySpan<byte> block)
        {
            if (block.Length != BlockLength)
            {
                throw new ArgumentException(
                    "A transfer block is " + BlockLength + " bytes, got " + block.Length.ToString() + ".",
                    nameof(block));
            }

            byte[] body = new byte[DataMessageLength];
            NdEndian.PutBe16(body, 0, (ushort)FaTransferFunction.Data);
            NdEndian.PutBe16(body, 2, page);
            NdEndian.PutBe16(body, 4, displacementWords);
            block.CopyTo(new Span<byte>(body, DataHeaderLength, BlockLength));
            return body;
        }

        /// <summary>
        /// Builds the end-of-transfer message body.
        /// </summary>
        /// <returns>
        /// The six-byte body <c>0043 FFFF FFFF</c>.
        /// </returns>
        public static byte[] BuildEndOfTransfer()
        {
            byte[] body = new byte[ControlMessageLength];
            NdEndian.PutBe16(body, 0, (ushort)FaTransferFunction.EndOfTransfer);
            NdEndian.PutBe16(body, 2, 0xFFFF);
            NdEndian.PutBe16(body, 4, 0xFFFF);
            return body;
        }

        /// <summary>
        /// Builds the reply to a data message.
        /// </summary>
        /// <returns>
        /// Six zero bytes.
        /// </returns>
        /// <remarks>
        /// The reply carries no reference to the message it answers - it does not need one, because
        /// the exchange is stop-and-wait and only one message is ever outstanding. The frame that
        /// carries it does repeat the data message's XMCSM, so its Flags2 reads 1030.
        /// </remarks>
        public static byte[] BuildDataReply()
        {
            return new byte[ControlMessageLength];
        }

        /// <summary>
        /// Builds the reply to the end-of-transfer message.
        /// </summary>
        /// <returns>
        /// The six-byte body <c>0000 0000 0001</c>.
        /// </returns>
        /// <remarks>
        /// This differs from <see cref="BuildDataReply"/> only in the final word, which is 1 rather
        /// than 0. The meaning of that word is UNKNOWN; it is reproduced because it is what the
        /// capture contains. It may be a status or a count, and one capture cannot tell them apart.
        /// </remarks>
        public static byte[] BuildEndOfTransferReply()
        {
            byte[] body = new byte[ControlMessageLength];
            NdEndian.PutBe16(body, 4, 0x0001);
            return body;
        }

        /// <summary>
        /// Reads the header of a data message body.
        /// </summary>
        /// <param name="body">
        /// The message body.
        /// </param>
        /// <param name="page">
        /// The page number within the file.
        /// </param>
        /// <param name="displacementWords">
        /// The displacement within the page, in words.
        /// </param>
        /// <param name="block">
        /// The file-content block, as a slice of <paramref name="body"/>.
        /// </param>
        /// <returns>
        /// <c>true</c> if the body was a well-formed data message.
        /// </returns>
        public static bool TryReadDataMessage(
            ReadOnlySpan<byte> body,
            out ushort page,
            out ushort displacementWords,
            out ReadOnlySpan<byte> block)
        {
            page = 0;
            displacementWords = 0;
            block = default;

            if (body.Length != DataMessageLength) { return false; }
            if ((FaTransferFunction)NdEndian.GetBe16(body, 0) != FaTransferFunction.Data) { return false; }

            page = NdEndian.GetBe16(body, 2);
            displacementWords = NdEndian.GetBe16(body, 4);
            block = body.Slice(DataHeaderLength, BlockLength);
            return true;
        }

        /// <summary>
        /// Determines whether a body is the end-of-transfer message.
        /// </summary>
        /// <param name="body">
        /// The message body.
        /// </param>
        /// <returns>
        /// <c>true</c> if this is the end-of-transfer marker.
        /// </returns>
        public static bool IsEndOfTransfer(ReadOnlySpan<byte> body)
        {
            return body.Length == ControlMessageLength && (FaTransferFunction)NdEndian.GetBe16(body, 0) == FaTransferFunction.EndOfTransfer;
        }

        /// <summary>
        /// Computes the byte offset at which a data message body must be split across two frames.
        /// </summary>
        /// <param name="body">
        /// The message body to send.
        /// </param>
        /// <param name="first">
        /// The portion carried by the first fragment (subtype <c>0x0A</c>).
        /// </param>
        /// <param name="continuation">
        /// The portion carried by the continuation (subtype <c>0x0C</c>), or empty when the body fits
        /// in a single frame and needs no segmentation.
        /// </param>
        /// <remarks>
        /// The continuation frame's Flags2 is the offset at which it resumes, which is exactly
        /// <c>first.Length</c>. The split itself is a property of the frame layer and lives in
        /// <see cref="SintranMessageFragment"/>; this method is the transfer code's name for it.
        /// </remarks>
        public static void SplitForTransmission(
            ReadOnlySpan<byte> body,
            out ReadOnlySpan<byte> first,
            out ReadOnlySpan<byte> continuation)
        {
            SintranMessageFragment.Split(body, out first, out continuation);
        }

    }
}
