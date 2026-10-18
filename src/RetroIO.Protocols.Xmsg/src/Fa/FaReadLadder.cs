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
    /// The order of operations a real client uses to READ a file, taken off the wire.
    /// </summary>
    /// <remarks>
    /// <para><b>Measured, not designed</b></para>
    /// <para>
    /// From <c>DOC/captures/ND-TO-ND-WRITE-2026-08-10/readback-10-blocks.pcapng</c>, decoded with
    /// the <c>fa_view.py</c> in that folder. D102 reads <c>BIGPSH3:TXT</c> from D100; the client's
    /// conversation word is <c>0x0052</c> and the server answers on <c>0x0006</c>. Both machines
    /// are real NDs - there is no C# anywhere in that capture, which is what makes it a reference
    /// rather than a recording of our own behaviour.
    /// </para>
    /// <para>
    /// Every request the reader sent, in order, with the server's reply beneath it:
    /// </para>
    /// <code>
    /// op 0002  seq 0001  ReserveFileEntry   112 bytes, the asker and user - same as a write
    /// op 0005  seq 0002  OpenFile           F2 0002 BD "BIGPSH3:TXT'."
    ///     reply                             F2 0002 A2 0040        the file number
    ///                                       F2 0003 A4 00004FB0    THE FILE'S BYTE LENGTH, 20400
    /// op 0007  seq 0003  SetBlockSize       A2 0800 = 2048
    /// op 000C  seq 0004  SiiiSpecial        F2 0001 92 0021 - FileInformation
    ///     reply                             the 64-byte directory entry
    /// op 0008  seq 0005  ReadFile           A4 00000000   block 0
    /// op 0008  seq 8006  ReadFile           A4 00000001   block 1
    /// op 0008  seq 0007  ReadFile           A4 00000002   block 2
    ///     ... ten in all, positions 0 to 9 ...
    /// op 0008  seq 800E  ReadFile           A4 00000009   block 9
    /// op 0006  seq 000F  CloseFile
    /// op 0003  seq 0010  ReleaseFileEntry
    /// </code>
    /// <para><b>Where it differs from the write ladder</b></para>
    ///  - A read asks <see cref="FaSpecialFunction.FileInformation"/> before the first block; a
    ///    write does not.
    ///  - A write ends with <see cref="FaSpecialFunction.SetEndOfFile"/> before closing; a read
    ///    does NOT, and must not - a reader has no business declaring the file's length.
    ///  - The open carries no access selector. See <see cref="ReadAccessSuffix"/>.
    ///  - The content moves the other way, and at a different point in the exchange. See
    ///    <see cref="FaFileDataCodec"/>: on a READ the reply COMPLETES the request and the content
    ///    follows it; on a WRITE the content follows the request and the reply completes it.
    /// <para><b>The block count is not chosen - it is read out of the open reply</b></para>
    /// <para>
    /// <c>0x00004FB0</c> is 20400, which is exactly the file that had been pushed. Ten blocks of
    /// 2048 covers it, and the capture sends exactly ten <c>ReadFile</c> requests. That is the
    /// whole stopping rule: the last block is PADDED, never shortened, and there is no end marker
    /// anywhere in the transfer, so a reader that does not take the length from the open reply has
    /// no way to know where the file ends. See <see cref="BlockCountForLength"/>.
    /// </para>
    /// </remarks>
    public static class FaReadLadder
    {
        /// <summary>
        /// The operations that open a read, before any block arrives.
        /// </summary>
        /// <returns>
        /// Reserve, open, set block size, ask for the file information - a fresh array each call.
        /// </returns>
        /// <remarks>
        /// FOUR steps, where a write has three. The extra one is the
        /// <see cref="FaSpecialFunction.FileInformation"/> request, and it is not decoration: our
        /// own server learned on 2026-08-06 that refusing it made <c>COPY-FILE</c> close the file
        /// and give up without ever issuing a <c>ReadFile</c>. A real reader sends it, so we do.
        /// </remarks>
        public static FaOperation[] Prologue()
        {
            return new FaOperation[]
            {
                FaOperation.ReserveFileEntry,
                FaOperation.OpenFile,
                FaOperation.SetBlockSize,
                FaOperation.SiiiSpecial,
            };
        }

        /// <summary>
        /// A DIAGNOSTIC ladder that reserves a file entry and then sets the block size, with no
        /// <see cref="FaOperation.OpenFile"/> in between.
        /// </summary>
        /// <returns>
        /// Reserve then set block size - a fresh array each call.
        /// </returns>
        /// <remarks>
        /// <para><b>This is not a transfer and must never be used as one.</b></para>
        /// It exists to answer one question about the follow-on refusal <c>A2 4104</c>, which every
        /// capture so far has produced only on operations issued after a REFUSED
        /// <see cref="FaOperation.OpenFile"/>. Two readings fit that evidence equally well:
        ///  - the value means "no file is open on this entry" - a state.
        ///  - the value means "an earlier operation in this session failed" - a history.
        /// Nothing separates them, because a refused open creates both conditions at once. Sending
        /// this ladder against a file that EXISTS creates the first without the second: <c>4104</c>
        /// again means the state reading, anything else means the history reading.
        /// <para>
        /// Deliberately NOT a shortened <see cref="Prologue"/>: <see cref="PrologueLength"/> feeds
        /// the block-index arithmetic in <c>FaClientReadSession</c>, so changing the real prologue
        /// to serve a diagnostic would break counting on the transfer path. This ladder stops before
        /// any block operation, so none of that arithmetic is ever reached.
        /// </para>
        /// See <c>DOC/CARVE-FA-READ-REFUSAL-2026-08-18.md</c>.
        /// </remarks>
        public static FaOperation[] ProbeWithoutOpen()
        {
            return new FaOperation[]
            {
                FaOperation.ReserveFileEntry,
                FaOperation.SetBlockSize,
            };
        }

        /// <summary>
        /// How many steps come before the first block request.
        /// </summary>
        /// <remarks>
        /// A constant so the hot path does not allocate a <see cref="Prologue"/> array just to ask
        /// its length. <c>FaReadLadderTests</c> checks the two agree, so this cannot drift if a
        /// step is ever added.
        /// </remarks>
        public const int PrologueLength = 4;

        /// <summary>
        /// How many steps come after the last block request.
        /// </summary>
        /// <remarks>
        /// Same reason as <see cref="PrologueLength"/>, and checked by the same test.
        /// </remarks>
        public const int EpilogueLength = 2;

        /// <summary>
        /// The operation sent once per block of content.
        /// </summary>
        /// <remarks>
        /// Each one is answered by a reply and then by two data messages carrying
        /// <see cref="FaFileDataCodec.BlockLength"/> bytes each.
        /// </remarks>
        public const FaOperation BlockOperation = FaOperation.ReadFile;

        /// <summary>
        /// The operations that finish a read, after the last block.
        /// </summary>
        /// <returns>
        /// The close and the release - a fresh array each call.
        /// </returns>
        /// <remarks>
        /// TWO steps, where a write has three. A write declares the file's true length with
        /// <see cref="FaSpecialFunction.SetEndOfFile"/> first; a read has nothing to declare and
        /// the capture sends no such request. Adding one "for symmetry" would tell the server to
        /// truncate a file we were only reading.
        /// </remarks>
        public static FaOperation[] Epilogue()
        {
            return new FaOperation[]
            {
                FaOperation.CloseFile,
                FaOperation.ReleaseFileEntry,
            };
        }

        /// <summary>
        /// The whole ladder for a read of a given number of blocks.
        /// </summary>
        /// <param name="blockCount">
        /// How many blocks the file needs. Must not be negative.
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
        /// How many blocks a file of a given byte length takes.
        /// </summary>
        /// <param name="fileLength">
        /// The file's true byte length, as the open reply reports it. Must not be negative.
        /// </param>
        /// <returns>
        /// The number of <see cref="FaWriteLadder.ContentBytesPerBlock"/> blocks needed, at least
        /// one.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="fileLength"/> is negative.
        /// </exception>
        /// <remarks>
        /// <para><b>Checked against the capture</b></para>
        /// 20400 bytes gives ten blocks, and the capture sends exactly ten <c>ReadFile</c>
        /// requests. The block size is the one <see cref="FaOperation.SetBlockSize"/> asked for, so
        /// this must be called AFTER that value is settled, not before.
        /// <para>
        /// An empty file still costs one block, for the same reason a write of one does: the ladder
        /// has no way to say "no content". Whether a real server answers a read of block 0 on an
        /// empty file is UNTESTED - no capture holds one.
        /// </para>
        /// </remarks>
        public static int BlockCountForLength(long fileLength)
        {
            if (fileLength < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fileLength), "A file cannot have a negative length.");
            }

            long blocks = (fileLength + FaWriteLadder.ContentBytesPerBlock - 1)
                / FaWriteLadder.ContentBytesPerBlock;

            if (blocks < 1)
            {
                blocks = 1;
            }

            return (int)blocks;
        }

        /// <summary>
        /// What a read's open specification carries after the apostrophe.
        /// </summary>
        /// <remarks>
        /// <para><b>Measured, and its meaning is NOT established</b></para>
        /// <para>
        /// The captured read opens <c>BD "BIGPSH3:TXT'."</c> - the specification, an apostrophe,
        /// then a full stop. Captured WRITES put a letter there instead and vary it:
        /// <c>"NDWR812:LIST"'D</c> and <c>"NDWR811:TXT"'N</c>. So the character after the
        /// apostrophe is not a constant and it is not obviously an access mode either, since a
        /// write used two different letters. It is reproduced because a real reader sent it, and
        /// deliberately not explained.
        /// </para>
        /// <para><b>Two things about the read open ARE settled</b></para>
        ///  - It carries NO selector 3. A write open ends <c>F2 0003 92 0001</c>; the read has
        ///    nothing between the string and the end-of-list. Our own
        ///    <see cref="FaOperation.OpenFile"/> remark predicted exactly this before the capture
        ///    was taken - "the access mode rides under field selector 3 and is omitted entirely for
        ///    read" - so the capture confirms it rather than correcting it.
        ///  - The name is NOT quoted. Writes that CREATE a file quote it; this read does not. That
        ///    matches SINTRAN's own rule, where quotes go around a name being created.
        /// </remarks>
        public const string ReadAccessSuffix = ".";

        /// <summary>
        /// The <c>0x8000</c> bit some block requests carry in their sequence number.
        /// </summary>
        /// <remarks>
        /// <para><b>EXPLAINED 2026-08-10. It was recorded as UNKNOWN for weeks.</b></para>
        /// <para>
        /// <see cref="FaWriteLadder"/> recorded the alternation <c>0004, 8005, 0006, 8007, ...</c>
        /// and said in as many words that its meaning was unknown and must not be invented. With a
        /// READ capture beside it the rule falls straight out, because the two ladders start their
        /// blocks at different parities:
        /// </para>
        /// <code>
        /// write blocks   0004 8005 0006 8007 0008 8009 000A 800B 000C     bit on the 2nd, 4th, 6th, 8th
        /// read blocks    0005 8006 0007 8008 0009 800A 000B 800C 000D 800E   bit on the 2nd, 4th, 6th, 8th, 10th
        /// </code>
        /// <para>
        /// It is not the parity of the sequence number - <c>0005</c> is odd and clear in the read
        /// while <c>8005</c> is odd and set in the write. It ALTERNATES across block requests,
        /// starting CLEAR on the first block. The epilogue steps never carry it in either capture,
        /// including the write's <c>000D</c>, which falls exactly where the alternation would have
        /// set it. Two independent captures, nineteen block requests, no exception.
        /// </para>
        /// <para><b>Known to be optional, so it is not emitted</b></para>
        /// <para>
        /// What the bit MEANS is still not established - only when it is set. And our own push
        /// omits it entirely across ten blocks and completes, verified by a second real ND reading
        /// the file back byte for byte, so a server plainly does not require it. Emitting a bit we
        /// cannot explain, to no measured benefit, is how a wrong constant gets frozen into a
        /// codebase. It stays documented and unsent.
        /// </para>
        /// <para>
        /// If a read ever stalls on a block boundary, this is the first thing to try.
        /// </para>
        /// </remarks>
        public const ushort AlternatingBlockSequenceBit = 0x8000;

        /// <summary>
        /// Whether the captured ladders would have set
        /// <see cref="AlternatingBlockSequenceBit"/> on a given block.
        /// </summary>
        /// <param name="blockIndex">
        /// Which block, counting from zero.
        /// </param>
        /// <returns>
        /// <c>true</c> for every second block, starting with the SECOND.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="blockIndex"/> is negative.
        /// </exception>
        /// <remarks>
        /// Nothing in this library sends the bit - see
        /// <see cref="AlternatingBlockSequenceBit"/> for why. This exists so the rule can be
        /// checked against the captures by a test rather than only asserted in a comment, and so a
        /// future experiment can turn it on in one place.
        /// </remarks>
        public static bool CapturedSequenceBitSetForBlock(int blockIndex)
        {
            if (blockIndex < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(blockIndex), "A block index cannot be negative.");
            }

            return (blockIndex & 1) == 1;
        }
    }
}
