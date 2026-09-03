using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Fa
{
    /// <summary>
    /// The order of operations a real client uses to write a file, taken off the wire.
    /// </summary>
    /// <remarks>
    /// <para><b>Measured, not designed</b></para>
    /// <para>
    /// From <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-write.txt</c>. Node 100 is the asker
    /// (its conversation word is <c>0x0044</c>); node 102 answers with the <c>07D2</c>
    /// confirmation, so node 102 is the server. Every request the asker sent, in order:
    /// </para>
    /// <code>
    /// op 0002  seq 0001  112 bytes  ReserveFileEntry   carries the user and file name
    /// op 0005  seq 0002   42 bytes  OpenFile           "WRTEST1:OUT" and the access letter W
    /// op 0007  seq 0003   24 bytes  SetBlockSize       A2 0800 - 2048
    /// op 0009  seq 0004   26 bytes  WriteFile          A4 00000000 - block 0
    /// op 0009  seq 8005   26 bytes  WriteFile          block 1
    /// op 0009  seq 0006   26 bytes  WriteFile          block 2
    /// op 0009  seq 8007   26 bytes  WriteFile          block 3
    /// op 0009  seq 0008   26 bytes  WriteFile          block 4
    /// op 0009  seq 8009   26 bytes  WriteFile          block 5
    /// op 0009  seq 000A   26 bytes  WriteFile          block 6
    /// op 0009  seq 800B   26 bytes  WriteFile          block 7
    /// op 0009  seq 000C   26 bytes  WriteFile          block 8
    /// op 000C  seq 000D   34 bytes  SiiiSpecial        sub-function 003B, SetEndOfFile
    /// op 0006  seq 000E   18 bytes  CloseFile
    /// op 0003  seq 000F   18 bytes  ReleaseFileEntry
    /// </code>
    /// <para>
    /// <b>CORRECTED 2026-08-09.</b> The first version of this type listed only the opening four
    /// operations and called that the ladder, because only the first part of the capture had been
    /// read. A client built from that would have written its blocks and then simply stopped -
    /// never closing the file and never releasing the entry, leaving the server holding a session
    /// and the file quite possibly unusable. Read the whole capture, not its beginning.
    /// </para>
    /// <para><b>The shape</b></para>
    /// <para>
    /// Three operations to set up, then <see cref="FaOperation.WriteFile"/> ONCE PER BLOCK, then
    /// three to finish. The block count is a property of the file, not of the protocol - this
    /// capture wrote nine.
    /// </para>
    /// <para><b>The sequence numbers, and what is not known about them</b></para>
    /// <para>
    /// The sequence is the exchange count from one, but across the WriteFile repetitions every
    /// other one carries <c>0x8000</c> as well: <c>0004, 8005, 0006, 8007, 0008, 8009, 000A,
    /// 800B, 000C</c>. The three closing operations do NOT carry it, though <c>000D</c> falls
    /// where the alternation would have set it. <b>Its meaning is UNKNOWN.</b> It is recorded here
    /// rather than explained, and <see cref="SequenceForStep"/> deliberately does not invent it -
    /// a caller reproducing this traffic must take the flag from the capture, not from a rule
    /// nobody has established.
    /// </para>
    /// <para><b>What this does NOT say</b></para>
    /// <para>
    /// Only the ORDER is established here. The CONTENT of each request past the operation and
    /// sequence is not modelled - the body lengths are recorded so a builder can be checked
    /// against them, not so they can be assumed; the reserve's 112 bytes carried one particular
    /// name. Whether a file must already exist before <c>ReserveFileEntry</c>, how a contiguous
    /// file's size is fixed, and what <c>SiiiSpecial</c> does are all NOT settled by this capture.
    /// </para>
    /// </remarks>
    public static class FaWriteLadder
    {
        /// <summary>
        /// The operations that open a write, before any block goes out.
        /// </summary>
        /// <returns>
        /// Reserve, open, set block size - a fresh array each call.
        /// </returns>
        public static FaOperation[] Prologue()
        {
            return new FaOperation[]
            {
                FaOperation.ReserveFileEntry,
                FaOperation.OpenFile,
                FaOperation.SetBlockSize,
            };
        }

        /// <summary>
        /// The operation sent once per block of content.
        /// </summary>
        /// <remarks>
        /// Each of these is followed by the block itself as a data message. The capture wrote nine
        /// blocks; the count depends on the file.
        /// </remarks>
        public const FaOperation BlockOperation = FaOperation.WriteFile;

        /// <summary>
        /// The operations that finish a write, after the last block.
        /// </summary>
        /// <returns>
        /// The special, the close and the release - a fresh array each call.
        /// </returns>
        /// <remarks>
        /// <b>Do not skip these.</b> A client that stops after its last block leaves the file
        /// unclosed and the entry reserved. <see cref="FaOperation.SiiiSpecial"/> here carries
        /// sub-function <see cref="FaSpecialFunction.SetEndOfFile"/> and declares how long the
        /// file really is - without it the file keeps the padding of its last block.
        /// </remarks>
        public static FaOperation[] Epilogue()
        {
            return new FaOperation[]
            {
                FaOperation.SiiiSpecial,
                FaOperation.CloseFile,
                FaOperation.ReleaseFileEntry,
            };
        }

        /// <summary>
        /// The whole ladder for a write of a given number of blocks.
        /// </summary>
        /// <param name="blockCount">
        /// How many blocks of content the file needs. Must not be negative.
        /// </param>
        /// <returns>
        /// Every operation in order, prologue then one per block then epilogue.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="blockCount"/> is negative.
        /// </exception>
        public static FaOperation[] ForBlockCount(int blockCount)
        {
            if (blockCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(blockCount), "A file cannot have a negative number of blocks.");
            }

            FaOperation[] prologue = Prologue();
            FaOperation[] epilogue = Epilogue();

            FaOperation[] all = new FaOperation[prologue.Length + blockCount + epilogue.Length];
            int at = 0;
            for (int i = 0; i < prologue.Length; i++) { all[at++] = prologue[i]; }
            for (int i = 0; i < blockCount; i++) { all[at++] = BlockOperation; }
            for (int i = 0; i < epilogue.Length; i++) { all[at++] = epilogue[i]; }
            return all;
        }

        /// <summary>
        /// How many blocks the captured session wrote.
        /// </summary>
        public const int CapturedBlockCount = 9;

        /// <summary>
        /// How many messages carry ONE block of content.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two, measured. Nine write requests produced eighteen messages, and the session-header
        /// counter moves by three per write - the request plus these two.
        /// </para>
        /// </remarks>
        public const int MessagesPerBlock = 2;

        /// <summary>
        /// The body size of the first fragment of a content message.
        /// </summary>
        /// <remarks>
        /// Every one of the eighteen <c>0x0A</c> fragments carried exactly this - but that is the
        /// TRANSPORT's fixed split, not a file-access fact. Aliased rather than repeated so there
        /// is one 594 in the codebase; <see cref="FaTransferCodec.FirstFragmentBodyLength"/> does
        /// the same.
        /// </remarks>
        public const int FirstFragmentBodyLength = SintranMessageFragment.FirstFragmentBodyLength;

        /// <summary>
        /// The body size of the second fragment of a content message.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every one of the eighteen <c>0x0C</c> fragments carried exactly this: the ND length
        /// field says 452, less the 14-byte SINTRAN header, so 438 bytes of message.
        /// </para>
        /// <para>
        /// <b>CORRECTED 2026-08-09. This said 424, and it was simply miscounted.</b> The wrong
        /// figure was what created the "block size does not reconcile" puzzle recorded below for
        /// days - see <see cref="CapturedContentMessageLength"/>. Re-measured by reassembling all
        /// eighteen pairs out of the capture rather than reading lengths off a summary.
        /// </para>
        /// </remarks>
        public const int SecondFragmentBodyLength = 438;

        /// <summary>
        /// The file length the captured write declared through SetEndOfFile.
        /// </summary>
        /// <remarks>
        /// <para><b>Measured, from the captured write - and it all reconciles</b></para>
        /// <code>
        /// 9 write requests
        /// 18 content messages          - two per block
        /// each message a fragment pair - 0x0A body 594, 0x0C body 438, so 1032 per message
        /// each message                 - 8-byte FA envelope + 1024 bytes of file content
        /// two messages per block       - 2 x 1024 = 2048 content bytes
        /// SetBlockSize asked for       - 2048                        MATCHES
        /// SetEndOfFile declared        - 17905 bytes of file
        /// ceil(17905 / 2048)           - 9 blocks                    MATCHES the 9 requests
        /// </code>
        /// <para><b>CORRECTED 2026-08-09: there was never a gap, only a miscount</b></para>
        /// <para>
        /// This used to state at length that the framing could NOT be reconciled with the declared
        /// block size - 2 x 1018 = 2036 against 2048 - and warned that a content splitter must not
        /// be written until a new capture settled it. That was wrong, and the whole puzzle came
        /// from ONE bad number: the second fragment is 438 bytes, not the 424 recorded here. With
        /// the right figure a content message is 1032 bytes, which is exactly
        /// <c>FaFileDataCodec.DataMessageLength</c> - the same 8 + 1024 message the READ path
        /// uses - and everything closes to the byte.
        /// </para>
        /// <para>
        /// The lesson is not about the protocol. The numbers had been read off a summary of the
        /// capture instead of by reassembling the fragment pairs, and a stated "open question"
        /// then protected the error from being re-checked for days. Reassemble, then conclude.
        /// </para>
        /// <para>
        /// So a content splitter IS writable: cut the file into <see cref="ContentBytesPerBlock"/>
        /// blocks, pad the last, send each block as two messages of
        /// <see cref="ContentBytesPerMessage"/> bytes, and declare the true length with
        /// SetEndOfFile - which carries the last byte INDEX, not the length.
        /// </para>
        /// </remarks>
        public const int CapturedFileLength = 17905;

        /// <summary>
        /// Bytes of file content carried by ONE content message.
        /// </summary>
        /// <remarks>
        /// The message is <see cref="CapturedContentMessageLength"/> bytes, of which the first
        /// <c>FaExchangeCodec.QformOffset</c> are the FA envelope; the rest is raw file content
        /// with no page or displacement header of its own. That is what makes it the same shape as
        /// the read path's data message rather than the TRANSFER-FILE one, which does carry a
        /// six-byte header.
        /// </remarks>
        public const int ContentBytesPerMessage =
            CapturedContentMessageLength - FaExchangeCodec.QformOffset;

        /// <summary>
        /// Bytes of file content in one block, and therefore the block size to declare.
        /// </summary>
        /// <remarks>
        /// 2048, which is what the captured session passed to SetBlockSize. The last block of a
        /// file is padded out to this, which is why SetEndOfFile has to declare the real length.
        /// </remarks>
        public const int ContentBytesPerBlock = ContentBytesPerMessage * MessagesPerBlock;

        /// <summary>
        /// The measured body size of ONE content message, before the transport splits it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>CORRECTED 2026-08-09.</b> This constant used to say 594 and call that the message
        /// length, adding "which is <c>FaFileDataCodec.BlockLength</c> - the same block the read
        /// path uses". Both halves were wrong. 594 is the FIRST FRAGMENT, not the message: the
        /// message is 594 + 424 = 1018, and the fragmenting is what splits it. And
        /// <c>FaFileDataCodec.BlockLength</c> is 1024, never 594, so the two were never the same
        /// number at all. A splitter built on the old constant would have cut every block at the
        /// transport's fragment boundary and called it a file block.
        /// </para>
        /// <para>
        /// <b>Now measured at 1032</b>, which IS the read path's
        /// <c>FaFileDataCodec.DataMessageLength</c> (8 + 1024). The write path carries the same
        /// message the read path does. This paragraph used to say the two were different and that
        /// the relationship was not established; that followed from the 424 miscount and is gone.
        /// </para>
        /// </remarks>
        public const int CapturedContentMessageLength =
            FirstFragmentBodyLength + SecondFragmentBodyLength;

        /// <summary>
        /// The base sequence number for a step, counting from one.
        /// </summary>
        /// <param name="stepIndex">
        /// The zero-based position in the ladder.
        /// </param>
        /// <returns>
        /// The sequence number, WITHOUT the <c>0x8000</c> bit some captured writes carry - see the
        /// remarks on this type. That bit is not modelled because nothing establishes when it is
        /// set.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="stepIndex"/> is negative.
        /// </exception>
        public static ushort SequenceForStep(int stepIndex)
        {
            if (stepIndex < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stepIndex), "A ladder step cannot be negative.");
            }

            return (ushort)(stepIndex + 1);
        }
    }
}
