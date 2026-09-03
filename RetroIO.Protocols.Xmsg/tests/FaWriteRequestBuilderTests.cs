using System;

using RetroIO.Protocols.Xmsg.Fa;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// The write request builders, compared against the bytes a real client sent.
    /// </summary>
    /// <remarks>
    /// Expected values are the QFORM field runs from
    /// <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-write.txt</c> - a RECORDING, so a builder
    /// that is confidently wrong fails here rather than agreeing with itself.
    /// </remarks>
    public sealed class FaWriteRequestBuilderTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Creates the fixture.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink, used to print built and captured bytes side by side.
        /// </param>
        public FaWriteRequestBuilderTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private void Check(string what, string captured, byte[] built)
        {
            string actual = Convert.ToHexString(built);
            _output.WriteLine(what + " built    : " + actual);
            _output.WriteLine(what + " captured : " + captured);
            Assert.Equal(captured, actual);
        }

        /// <summary>
        /// SetBlockSize reproduces the captured fields.
        /// </summary>
        /// <remarks>
        /// Captured request body:
        /// <c>07F0 0044 8200 D761 | 92 0007 | 92 0003 | F2 0001 A2 0800 F2 00FF | 0A</c>.
        /// The fields are what follows the operation and sequence pair.
        /// </remarks>
        [Fact]
        public void SetBlockSizeMatchesTheCapture()
        {
            Check("SetBlockSize", "F20001A20800F200FF",
                FaWriteRequests.SetBlockSize(FaWriteRequests.CapturedBlockSize));
        }

        /// <summary>
        /// The captured block size is 2048.
        /// </summary>
        [Fact]
        public void TheCapturedBlockSizeIs2048()
        {
            Assert.Equal(2048, FaWriteRequests.CapturedBlockSize);
        }

        /// <summary>
        /// WriteFile reproduces the captured fields for block zero.
        /// </summary>
        /// <remarks>
        /// Captured: <c>92 0009 | 92 0004 | F2 0001 A4 00000000 F2 00FF | BF</c>.
        /// </remarks>
        [Fact]
        public void WriteFileMatchesTheCaptureForTheFirstBlock()
        {
            Check("WriteFile[0]", "F20001A400000000F200FF", FaWriteRequests.WriteFile(0));
        }

        /// <summary>
        /// The block number is a 32-bit field and rises by one per request.
        /// </summary>
        /// <remarks>
        /// The captured session wrote blocks 0 to 8. Block 8 is checked as well as block 0, because
        /// a builder that ignored its argument would pass on block 0 alone.
        /// </remarks>
        [Fact]
        public void TheBlockNumberIsCarriedAsA32BitValue()
        {
            Check("WriteFile[8]", "F20001A400000008F200FF", FaWriteRequests.WriteFile(8));

            // Wide enough for a value no 16-bit field could hold.
            Check("WriteFile[0x00012345]", "F20001A400012345F200FF",
                FaWriteRequests.WriteFile(0x00012345));
        }

        /// <summary>
        /// The reserve reproduces the captured fields for each of the three background programs.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Five captures, three distinct bodies. Everything is identical except the asker string,
        /// which is what proved the reserve names the CLIENT and not the file:
        /// </para>
        /// <code>
        /// BAK03  create-file, open-close
        /// BAK04  write
        /// BAK05  file-stat, delete-file
        /// </code>
        /// </remarks>
        [Theory]
        [InlineData("BAK03", "F20001A207D0F200028C06920001920001F20003BD42414B3033202053595354"
            + "454DF200048C38B01053595354454D27000000000000000000E180B010000000000000000000000000"
            + "00000000B01000000000000000000000000000000000F200FF")]
        [InlineData("BAK04", "F20001A207D0F200028C06920001920001F20003BD42414B3034202053595354"
            + "454DF200048C38B01053595354454D27000000000000000000E180B010000000000000000000000000"
            + "00000000B01000000000000000000000000000000000F200FF")]
        [InlineData("BAK05", "F20001A207D0F200028C06920001920001F20003BD42414B3035202053595354"
            + "454DF200048C38B01053595354454D27000000000000000000E180B010000000000000000000000000"
            + "00000000B01000000000000000000000000000000000F200FF")]
        public void ReserveFileEntryMatchesTheCapture(string backgroundProgram, string captured)
        {
            Check("Reserve/" + backgroundProgram, captured,
                FaWriteRequests.ReserveFileEntry(backgroundProgram, "SYSTEM"));
        }

        /// <summary>
        /// The reserve carries no file name at all.
        /// </summary>
        /// <remarks>
        /// The structural point. Four captures of four DIFFERENT operations - stat, create, delete,
        /// open - produced bodies that differ only in the background program. Whatever the reserve
        /// is reserving, it is not identified by a file name here.
        /// </remarks>
        [Fact]
        public void TheReserveDoesNotNameAFile()
        {
            byte[] a = FaWriteRequests.ReserveFileEntry("BAK04", "SYSTEM");
            byte[] b = FaWriteRequests.ReserveFileEntry("BAK04", "SYSTEM");

            // Same asker, same bytes - nothing about a file enters into it.
            Assert.Equal(a, b);

            string text = System.Text.Encoding.ASCII.GetString(a);
            Assert.DoesNotContain("WRTEST", text);
        }

        /// <summary>
        /// A user too long for the captured field is refused.
        /// </summary>
        [Fact]
        public void AnOverLongUserIsRefused()
        {
            Assert.Throws<ArgumentException>(
                () => FaWriteRequests.ReserveFileEntry("BAK04", "A-VERY-LONG-USER-NAME"));
        }

        /// <summary>
        /// OpenFile reproduces the captured fields, quotes and access letter included.
        /// </summary>
        /// <remarks>
        /// Captured: <c>92 0005 | 92 0002 | F2 0002 BF "WRTEST1:OUT"'W F2 0003 92 0001 F2 00FF</c>.
        /// The <c>BF</c> tag carries the length in its nibble - fifteen bytes - which is the
        /// compact form a real client uses.
        /// </remarks>
        [Fact]
        public void OpenFileMatchesTheCapture()
        {
            Check("OpenFile", "F20002BF225752544553543"
                + "13A4F5554222757F20003920001F200FF",
                FaWriteRequests.OpenFile("\"WRTEST1:OUT\"", 'W'));
        }

        /// <summary>
        /// A specification too long for the compact form uses the LONG form instead.
        /// </summary>
        /// <remarks>
        /// <para><b>This test used to assert the opposite, and it was wrong</b></para>
        /// <para>
        /// It said "what a real client sends for a longer name is UNKNOWN - the capture only shows
        /// fifteen bytes", and refused anything longer rather than guess. The read capture had the
        /// answer all along: <c>FaOpenFileCodec</c> documents an open request as
        /// <c>B0 10 "PATCH-FILE:OUT" 27 54</c> - the LONG form, a sixteen-byte field holding a
        /// FOURTEEN-character name. So a client picks the form by length, and both are captured.
        /// </para>
        /// <para>
        /// The old rule capped a specification at 13 characters including its quotes, which forced
        /// every file deployed to a machine into a shortened name plus a rename afterwards, and
        /// was written up in two skills as if it were the protocol's own limit.
        /// </para>
        /// </remarks>
        [Fact]
        public void AnOverLongFileSpecUsesTheLongStringForm()
        {
            byte[] fields = FaWriteRequests.OpenFile("\"XMSG-STARTEX-L03:MODE\"", 'W');

            string hex = Convert.ToHexString(fields).ToUpperInvariant();

            // F2 0002 then B0 <length> - the long form, not BF.
            Assert.StartsWith("F20002B0", hex);

            // The declared length must be the field's real length: the quoted spec, an
            // apostrophe and the access letter.
            int declared = fields[4];
            Assert.Equal("\"XMSG-STARTEX-L03:MODE\"".Length + 2, declared);

            // And the name must survive intact - a truncation here would still produce a valid
            // request, for the wrong file.
            Assert.Contains("XMSG-STARTEX-L03:MODE", System.Text.Encoding.ASCII.GetString(fields));
        }

        /// <summary>
        /// A specification that still fits keeps using the compact form.
        /// </summary>
        /// <remarks>
        /// The captured write request is <c>BF</c>, so switching everything to the long form would
        /// stop matching a real client for the common case.
        /// </remarks>
        [Fact]
        public void AShortFileSpecStillUsesTheCompactForm()
        {
            byte[] fields = FaWriteRequests.OpenFile("\"WRTEST1:OUT\"", 'W');

            string hex = Convert.ToHexString(fields).ToUpperInvariant();

            Assert.StartsWith("F20002BF", hex);
        }

        /// <summary>
        /// SetEndOfFile reproduces the captured fields, sending the last byte's INDEX.
        /// </summary>
        /// <remarks>
        /// Captured: <c>92 000C | 92 000D | F2 0001 92 003B F2 0002 8C 80 05 A4 000045F0 F2 00FF</c>.
        /// The captured file is 17905 bytes and the wire carries 17904.
        /// </remarks>
        [Fact]
        public void SetEndOfFileMatchesTheCapture()
        {
            Check("SetEndOfFile", "F2000192003BF200028C8005A4000045F0F200FF",
                FaWriteRequests.SetEndOfFile(17905));
        }

        /// <summary>
        /// The wire value is one less than the length, and getting it wrong loses a byte.
        /// </summary>
        /// <remarks>
        /// The failure is nearly invisible: the stored file is byte-identical to the source except
        /// that its last character is gone. It happened live on D100 with a 12690-byte file.
        /// </remarks>
        [Fact]
        public void TheWireCarriesTheLastByteIndexNotTheLength()
        {
            // A one-byte file's last index is zero.
            Check("SetEndOfFile(1)", "F2000192003BF200028C8005A400000000F200FF",
                FaWriteRequests.SetEndOfFile(1));

            // The live D100 case: 12690 bytes went out as 12689 = 0x3191.
            Check("SetEndOfFile(12690)", "F2000192003BF200028C8005A400003191F200FF",
                FaWriteRequests.SetEndOfFile(12690));
        }

        /// <summary>
        /// A length below one is refused.
        /// </summary>
        [Fact]
        public void AZeroLengthIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FaWriteRequests.SetEndOfFile(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => FaWriteRequests.SetEndOfFile(-1));
        }

        /// <summary>
        /// CloseFile carries no parameters.
        /// </summary>
        /// <remarks>
        /// Captured: <c>92 0006 | 92 000E | F2 00FF | 0A</c>.
        /// </remarks>
        [Fact]
        public void CloseFileMatchesTheCapture()
        {
            Check("CloseFile", "F200FF", FaWriteRequests.CloseFile());
        }

        /// <summary>
        /// ReleaseFileEntry carries no parameters either.
        /// </summary>
        /// <remarks>
        /// Captured: <c>92 0003 | 92 000F | F2 00FF | 0E</c>.
        /// </remarks>
        [Fact]
        public void ReleaseFileEntryMatchesTheCapture()
        {
            Check("ReleaseFileEntry", "F200FF", FaWriteRequests.ReleaseFileEntry());
        }

        /// <summary>
        /// Built through the conversation, a request reproduces the captured body apart from its
        /// trailing pad.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The end-to-end check: the envelope from <c>FaClientConversation</c>, the operation and
        /// sequence it writes, and the fields from <c>FaWriteRequests</c>, against what node 100
        /// actually put on the wire for its close.
        /// </para>
        /// <para>
        /// The captured body ends with one extra byte to reach an even length. Its value is NOT a
        /// constant across the capture - the four simple requests carry <c>0A</c>, <c>BF</c>,
        /// <c>0A</c> and <c>0E</c> - so it is leftover buffer content rather than a field, and the
        /// comparison stops before it.
        /// </para>
        /// </remarks>
        [Fact]
        public void EveryRequestIsWordAlignedLikeTheOnesARealClientSends()
        {
            // MEASURED 2026-08-10 from a real client writing to a real server
            // (DOC/captures/ND-TO-ND-WRITE-2026-08-10/): EVERY message D102 sent was an even
            // number of bytes - 112, 44, 24, 26, 594, 594, 34, 18, 18 - because the ND-100 is a
            // 16-bit word machine.
            //
            // Ours was not. SetBlockSize came to 23 bytes against the real 24, and D100 simply
            // STOPPED ANSWERING at that step - no reply, no XENSE, no disconnect. An odd body is
            // dropped in silence, which is why this cost so much longer to find than a rejection
            // would have. The two steps before it survived only by being even already.
            FaClientConversation conversation = new FaClientConversation(0x0044);
            conversation.BuildRequest(FaOperation.ReserveFileEntry, FaWriteRequests.CloseFile());
            conversation.BuildRequest(FaOperation.OpenFile, FaWriteRequests.CloseFile());
            byte[] setBlockSize = conversation.BuildRequest(
                FaOperation.SetBlockSize, FaWriteRequests.SetBlockSize(FaWriteRequests.CapturedBlockSize));

            // The captured SetBlockSize, whole, for reference:
            //
            //     07F0 0044 8200 D79B 920007 920003 F20001 A20800 F200FF 0A
            //
            // Its last byte is the alignment pad. Neither the pad VALUE nor the session token is
            // asserted: a real machine leaves stale buffer content in the pad (0x0A here, 0xB0 and
            // 0x06 on other steps), and the token is per-session (D79B in this capture, D761 in
            // the 2026-08-04 one this fixture replays). What must hold is the LENGTH.
            const int CapturedSetBlockSizeLength = 24;

            Assert.Equal(CapturedSetBlockSizeLength, setBlockSize.Length);
            Assert.Equal(0, setBlockSize.Length % 2);

            // And the request still says what it means - the block size survives the padding.
            // ... A2 <blocksize:2> F2 00FF <pad> - so the A2 tag is seven bytes from the end.
            int blockSizeAt = setBlockSize.Length - 7;
            Assert.Equal(0xA2, setBlockSize[blockSizeAt]);
            Assert.Equal(
                FaWriteRequests.CapturedBlockSize,
                (ushort)((setBlockSize[blockSizeAt + 1] << 8) | setBlockSize[blockSizeAt + 2]));
        }

        [Fact]
        public void AWholeCloseRequestMatchesTheCapturedBodyBeforeThePad()
        {
            // Replay the captured session up to the close: three setup requests, nine writes each
            // followed by a block, then the special.
            //
            // A block is TWO messages of 1024 content bytes each - NOT, as this comment used to
            // say, one message that leaves as a fragment pair. Both are true of the wire but only
            // the first is what the counter counts: each of those two messages is ITSELF split
            // into an 0x0A and an 0x0C fragment, so the block costs 2 messages and 4 frames.
            FaClientConversation conversation = new FaClientConversation(0x0044);
            conversation.BuildRequest(FaOperation.ReserveFileEntry, FaWriteRequests.CloseFile());
            conversation.BuildRequest(FaOperation.OpenFile, FaWriteRequests.CloseFile());
            conversation.BuildRequest(
                FaOperation.SetBlockSize, FaWriteRequests.SetBlockSize(FaWriteRequests.CapturedBlockSize));

            byte[] blockContent = new byte[FaWriteLadder.ContentBytesPerBlock];

            for (uint block = 0; block < 9; block++)
            {
                conversation.BuildRequest(FaOperation.WriteFile, FaWriteRequests.WriteFile(block));

                // The conversation BUILDS the two content messages now, so it counts them itself.
                byte[][] content = conversation.BuildContentMessages(blockContent);
                Assert.Equal(FaWriteLadder.MessagesPerBlock, content.Length);
            }

            conversation.BuildRequest(FaOperation.SiiiSpecial, FaWriteRequests.CloseFile());

            byte[] body = conversation.BuildRequest(FaOperation.CloseFile, FaWriteRequests.CloseFile());

            // UPDATED 2026-08-10. This used to compare against the captured body with the pad
            // CHOPPED OFF, because we did not emit one. That made a defect look like a passing
            // test: the captured close is 17 bytes and the pad takes it to 18, and an ODD body is
            // silently dropped by a real ND-100 (see EveryRequestIsWordAlignedLikeTheOnesARealClient
            // Sends). We now emit the pad, so the comparison covers the whole message.
            //
            // The pad VALUE is still not compared - a real machine leaves stale buffer content
            // there and the four simple requests in the capture carry 0A, BF, 0A and 0E.
            const string CapturedContent = "07F000449F00D76192000692000EF200FF";
            string actual = Convert.ToHexString(body);

            _output.WriteLine("built    : " + actual);
            _output.WriteLine("captured : " + CapturedContent + "  (+ 0A pad)");

            Assert.Equal(CapturedContent.Length / 2 + 1, body.Length);
            Assert.Equal(0, body.Length % 2);
            Assert.Equal(CapturedContent, actual.Substring(0, CapturedContent.Length));
        }

        /// <summary>
        /// The content messages carry the counters and tokens node 100 put on the wire.
        /// </summary>
        /// <remarks>
        /// <para>
        /// From <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-write.txt</c>, reassembling the
        /// fragment pairs. The asker's stream after the three setup requests runs:
        /// </para>
        /// <code>
        /// 8300 D761   WriteFile block 0
        /// 0400 D761   content, first  - bit 7 CLEAR, our own token
        /// 8500 0001   content, second - bit 7 SET,   the last-message token
        /// 8600 D761   WriteFile block 1
        /// 0700 D761   content, first
        /// 8800 0001   content, second
        /// </code>
        /// <para>
        /// Bit 7 is a flag rather than part of the count: strip it and the sequence is a plain
        /// 03 04 05 06 07 08. Reading the bytes raw is what once produced a "counter that jumps by
        /// three", and two wrong models with it.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheContentMessagesCarryTheCapturedCountersAndTokens()
        {
            FaClientConversation conversation = new FaClientConversation(0x0044);
            conversation.BuildRequest(FaOperation.ReserveFileEntry, FaWriteRequests.CloseFile());
            conversation.BuildRequest(FaOperation.OpenFile, FaWriteRequests.CloseFile());
            conversation.BuildRequest(
                FaOperation.SetBlockSize, FaWriteRequests.SetBlockSize(FaWriteRequests.CapturedBlockSize));

            byte[] blockContent = new byte[FaWriteLadder.ContentBytesPerBlock];

            // Block 0: request 83, content 04 then 85.
            byte[] request0 = conversation.BuildRequest(
                FaOperation.WriteFile, FaWriteRequests.WriteFile(0));
            byte[][] content0 = conversation.BuildContentMessages(blockContent);

            // Block 1: request 86, content 07 then 88.
            byte[] request1 = conversation.BuildRequest(
                FaOperation.WriteFile, FaWriteRequests.WriteFile(1));
            byte[][] content1 = conversation.BuildContentMessages(blockContent);

            _output.WriteLine("counters: " + Convert.ToHexString(new byte[]
            {
                request0[4], content0[0][4], content0[1][4],
                request1[4], content1[0][4], content1[1][4],
            }));

            Assert.Equal(0x83, request0[4]);
            Assert.Equal(0x04, content0[0][4]);
            Assert.Equal(0x85, content0[1][4]);
            Assert.Equal(0x86, request1[4]);
            Assert.Equal(0x07, content1[0][4]);
            Assert.Equal(0x88, content1[1][4]);

            // The token is swapped on the last message of each pair, not repeated.
            Assert.Equal(
                FaExchangeCodec.SessionTokenAsker, NdEndian.GetBe16(content0[0], 6));
            Assert.Equal(
                FaFileDataCodec.LastDataMessageToken, NdEndian.GetBe16(content0[1], 6));

            // Every content message is the captured 8 + 1024.
            Assert.Equal(FaWriteLadder.CapturedContentMessageLength, content0[0].Length);
            Assert.Equal(FaWriteLadder.CapturedContentMessageLength, content0[1].Length);
        }

        /// <summary>
        /// A short last block is padded out rather than shortened.
        /// </summary>
        /// <remarks>
        /// There is no short block and no end marker in this protocol - the real length is declared
        /// by SetEndOfFile, which carries the last byte INDEX. A client that shortened its last
        /// block would be inventing a signal.
        /// </remarks>
        [Fact]
        public void AShortLastBlockIsPaddedNotShortened()
        {
            FaClientConversation conversation = new FaClientConversation(0x0044);

            // One byte of real content, in a block that must still go out whole.
            byte[] tail = new byte[] { 0x41 };
            byte[][] messages = conversation.BuildContentMessages(tail);

            Assert.Equal(FaWriteLadder.MessagesPerBlock, messages.Length);
            Assert.Equal(FaWriteLadder.CapturedContentMessageLength, messages[0].Length);
            Assert.Equal(FaWriteLadder.CapturedContentMessageLength, messages[1].Length);

            // The byte is where it belongs, and everything after it is pad.
            Assert.Equal(0x41, messages[0][FaExchangeCodec.QformOffset]);
            Assert.Equal(0x00, messages[0][FaExchangeCodec.QformOffset + 1]);

            // The second message is entirely pad, and still goes.
            for (int i = FaExchangeCodec.QformOffset; i < messages[1].Length; i++)
            {
                Assert.Equal(0x00, messages[1][i]);
            }
        }
    }
}
