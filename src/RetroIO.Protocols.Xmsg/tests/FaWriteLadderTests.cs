//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Fa;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// The write ladder, checked against every request a real client sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// From <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-write.txt</c>, node 100 writing to
    /// node 102. Node 100 is the asker: its conversation word is <c>0x0044</c>, and node 102 sent
    /// the <c>07D2</c> confirmation.
    /// </para>
    /// <para>
    /// The expected list is EVERY request in the capture, not a sample of it. The first version of
    /// this test asserted only the opening four operations, because only the start of the capture
    /// had been read - and it passed, which is exactly why a partial expectation is dangerous.
    /// </para>
    /// </remarks>
    public sealed class FaWriteLadderTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Creates the fixture.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public FaWriteLadderTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// Every operation code the asker sent, in wire order.
        /// </summary>
        private static readonly ushort[] CapturedOperations = new ushort[]
        {
            0x0002,                                                   // ReserveFileEntry
            0x0005,                                                   // OpenFile
            0x0007,                                                   // SetBlockSize
            0x0009, 0x0009, 0x0009, 0x0009, 0x0009,                   // WriteFile, blocks 0-4
            0x0009, 0x0009, 0x0009, 0x0009,                           // WriteFile, blocks 5-8
            0x000C,                                                   // SiiiSpecial
            0x0006,                                                   // CloseFile
            0x0003,                                                   // ReleaseFileEntry
        };

        /// <summary>
        /// The sequence word each request carried, including the unexplained high bit.
        /// </summary>
        private static readonly ushort[] CapturedSequences = new ushort[]
        {
            0x0001, 0x0002, 0x0003,
            0x0004, 0x8005, 0x0006, 0x8007, 0x0008,
            0x8009, 0x000A, 0x800B, 0x000C,
            0x000D, 0x000E, 0x000F,
        };

        /// <summary>
        /// The ladder for nine blocks is exactly what the capture contains.
        /// </summary>
        [Fact]
        public void TheLadderMatchesEveryCapturedRequest()
        {
            FaOperation[] ladder = FaWriteLadder.ForBlockCount(FaWriteLadder.CapturedBlockCount);

            Assert.Equal(CapturedOperations.Length, ladder.Length);

            for (int i = 0; i < ladder.Length; i++)
            {
                _output.WriteLine("step " + i + ": " + ladder[i]);
                Assert.Equal(CapturedOperations[i], (ushort)ladder[i]);
            }
        }

        /// <summary>
        /// The setup is reserve, then open, then set the block size.
        /// </summary>
        /// <remarks>
        /// A plausible client opens and writes. A real one reserves the entry first.
        /// </remarks>
        [Fact]
        public void ThePrologueIsReserveThenOpenThenBlockSize()
        {
            FaOperation[] prologue = FaWriteLadder.Prologue();

            Assert.Equal(FaOperation.ReserveFileEntry, prologue[0]);
            Assert.Equal(FaOperation.OpenFile, prologue[1]);
            Assert.Equal(FaOperation.SetBlockSize, prologue[2]);
        }

        /// <summary>
        /// The close is three operations, not one.
        /// </summary>
        /// <remarks>
        /// This is what the original four-step version of this file missed entirely. A client that
        /// stops after its last block never closes the file and never releases the entry - the
        /// server is left holding a session, and the file may well be unusable.
        /// </remarks>
        [Fact]
        public void TheEpilogueIsSpecialThenCloseThenRelease()
        {
            FaOperation[] epilogue = FaWriteLadder.Epilogue();

            Assert.Equal(3, epilogue.Length);
            Assert.Equal(FaOperation.SiiiSpecial, epilogue[0]);
            Assert.Equal(FaOperation.CloseFile, epilogue[1]);
            Assert.Equal(FaOperation.ReleaseFileEntry, epilogue[2]);
        }

        /// <summary>
        /// One write request goes out per block.
        /// </summary>
        [Fact]
        public void ThereIsOneWriteRequestPerBlock()
        {
            Assert.Equal(FaOperation.WriteFile, FaWriteLadder.BlockOperation);

            FaOperation[] three = FaWriteLadder.ForBlockCount(3);
            int writes = 0;
            for (int i = 0; i < three.Length; i++)
            {
                if (three[i] == FaOperation.WriteFile) { writes++; }
            }

            Assert.Equal(3, writes);
            Assert.Equal(3 + 3 + 3, three.Length);
        }

        /// <summary>
        /// A file with no content still opens and closes properly.
        /// </summary>
        [Fact]
        public void AnEmptyFileIsStillOpenedAndClosed()
        {
            FaOperation[] none = FaWriteLadder.ForBlockCount(0);

            Assert.Equal(6, none.Length);
            Assert.Equal(FaOperation.ReserveFileEntry, none[0]);
            Assert.Equal(FaOperation.ReleaseFileEntry, none[5]);
        }

        /// <summary>
        /// A negative block count is refused.
        /// </summary>
        [Fact]
        public void ANegativeBlockCountIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FaWriteLadder.ForBlockCount(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => FaWriteLadder.SequenceForStep(-1));
        }

        /// <summary>
        /// The base sequence is the exchange count, and the captured high bit is NOT invented.
        /// </summary>
        /// <remarks>
        /// The capture alternates <c>0x8000</c> across the write repetitions - 0004, 8005, 0006,
        /// 8007 - and then stops doing so for the closing operations, including at 000D where the
        /// alternation would have continued. Nothing establishes the rule, so
        /// <see cref="FaWriteLadder.SequenceForStep"/> returns the plain count and this test pins
        /// that it does NOT guess at the flag.
        /// </remarks>
        [Fact]
        public void TheSequenceIsThePlainCountAndTheHighBitIsNotGuessed()
        {
            Assert.Equal(1, FaWriteLadder.SequenceForStep(0));
            Assert.Equal(4, FaWriteLadder.SequenceForStep(3));

            // The capture's fifth request carried 0x8005; we return 0x0005 and leave the flag to
            // whoever is reproducing that traffic.
            Assert.Equal(0x8005, CapturedSequences[4]);
            Assert.Equal(0x0005, FaWriteLadder.SequenceForStep(4));
        }

        /// <summary>
        /// The measured content framing, and the arithmetic that does NOT close.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Nine write requests produced eighteen content messages - two per block - each a fragment
        /// pair with bodies of 594 and 438 bytes, so 1032 per message.
        /// </para>
        /// <para>
        /// REWRITTEN 2026-08-09. This test used to assert a GAP: that two messages carried
        /// 2036 bytes against a declared block size of 2048, that nothing reconciled them, and
        /// that a content splitter therefore could not be written. All of that came from one wrong
        /// number - the second fragment is 438 bytes, not 424 - and the "open question" then kept
        /// the error from being re-checked. Re-measured by reassembling all eighteen fragment
        /// pairs out of the capture, everything closes to the byte, so the assertions now pin the
        /// arithmetic that CLOSES rather than a mismatch that never existed.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheContentFramingExplainsTheDeclaredBlockSizeExactly()
        {
            Assert.Equal(2, FaWriteLadder.MessagesPerBlock);
            Assert.Equal(594, FaWriteLadder.FirstFragmentBodyLength);
            Assert.Equal(438, FaWriteLadder.SecondFragmentBodyLength);
            Assert.Equal(17905, FaWriteLadder.CapturedFileLength);

            // 594 is the transport's fixed first-fragment size, so there is one of it.
            Assert.Equal(SintranMessageFragment.FirstFragmentBodyLength, FaWriteLadder.FirstFragmentBodyLength);

            // The message is the PAIR, and it is exactly the read path's data message: 8 + 1024.
            int perMessage = FaWriteLadder.FirstFragmentBodyLength
                + FaWriteLadder.SecondFragmentBodyLength;
            Assert.Equal(1032, perMessage);
            Assert.Equal(FaWriteLadder.CapturedContentMessageLength, perMessage);
            Assert.Equal(FaFileDataCodec.DataMessageLength, FaWriteLadder.CapturedContentMessageLength);

            // Strip the FA envelope and one message carries a whole 1024-byte block of content.
            Assert.Equal(FaFileDataCodec.BlockLength, FaWriteLadder.ContentBytesPerMessage);

            // Two of those per WriteFile request is the 2048 the same session declared through
            // SetBlockSize. This is the equality the old test asserted could not hold.
            Assert.Equal(2048, FaWriteLadder.ContentBytesPerBlock);

            // And the block count follows from the file length, so the ladder length is derivable
            // rather than copied out of the capture.
            int blocks = (FaWriteLadder.CapturedFileLength + FaWriteLadder.ContentBytesPerBlock - 1)
                / FaWriteLadder.ContentBytesPerBlock;
            Assert.Equal(FaWriteLadder.CapturedBlockCount, blocks);
        }

        /// <summary>
        /// The returned arrays are copies, so a caller cannot rewrite the record.
        /// </summary>
        [Fact]
        public void TheRecordCannotBeAlteredByACaller()
        {
            FaOperation[] first = FaWriteLadder.Prologue();
            first[0] = FaOperation.DeleteFile;

            Assert.Equal(FaOperation.ReserveFileEntry, FaWriteLadder.Prologue()[0]);
        }
    }
}
