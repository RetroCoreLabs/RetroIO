using System;

using RetroIO.Protocols.Xmsg.Fa;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Covers the <c>ReadFile</c> bodies against the captured exchange.
    /// </summary>
    /// <remarks>
    /// The bytes come from <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-read.txt</c>, which
    /// records a real ND machine reading a 17905-byte file in nine steps.
    /// </remarks>
    public sealed class FaFileDataCodecTests
    {
        /// <summary>
        /// The captured read request is parsed, position and all.
        /// </summary>
        [Fact]
        public void TheCapturedRequestIsParsed()
        {
            byte[] body = Hex.ToBytes("920008920005F20001A400000000F200FF");

            ushort serial;
            uint position;
            ushort wanted;
            Assert.True(FaFileDataCodec.TryReadRequest(body, out serial, out position, out wanted));

            Assert.Equal(5, serial);
            Assert.Equal(0u, position);
            Assert.Equal(0, wanted);
        }

        /// <summary>
        /// A request that names a wanted byte count is parsed with it.
        /// </summary>
        /// <remarks>
        /// The second conversation in the capture never sent <c>SetBlockSize</c>, so it asks for
        /// <c>0x0800</c> = 2048 bytes explicitly.
        /// </remarks>
        [Fact]
        public void ARequestThatNamesAByteCountIsParsedWithIt()
        {
            byte[] body = Hex.ToBytes("920008920005F20001A400000001F20003A20800F200FF");

            ushort serial;
            uint position;
            ushort wanted;
            Assert.True(FaFileDataCodec.TryReadRequest(body, out serial, out position, out wanted));

            Assert.Equal(1u, position);
            Assert.Equal(0x0800, wanted);
        }

        /// <summary>
        /// The reply reproduces the captured bytes and carries no data.
        /// </summary>
        [Fact]
        public void TheReplyReproducesTheCapturedBytes()
        {
            byte[] expected = Hex.ToBytes("920008920005F200FF");

            byte[] actual = FaFileDataCodec.BuildReply(serial: 5, deliveredBytes: 0);

            Assert.Equal(expected, actual);
        }

        /// <summary>
        /// A reply to a request that named a count reports the count back.
        /// </summary>
        [Fact]
        public void AReplyReportsTheDeliveredCountWhenOneWasAsked()
        {
            byte[] expected = Hex.ToBytes("920008920005F20002A20800F200FF");

            byte[] actual = FaFileDataCodec.BuildReply(serial: 5, deliveredBytes: 0x0800);

            Assert.Equal(expected, actual);
        }

        /// <summary>
        /// A short tail is padded to a whole block rather than shortened.
        /// </summary>
        /// <remarks>
        /// The capture's last read returns a full 2048 bytes with only 1521 left in the file. There
        /// is no short block and no end marker, so padding is the protocol's own behaviour and
        /// shortening would invent a signal it does not have.
        /// </remarks>
        [Fact]
        public void AShortTailIsPaddedToAWholeBlock()
        {
            byte[] tail = new byte[3] { 0x41, 0x42, 0x43 };

            byte[] block = FaFileDataCodec.BuildDataBlock(tail);

            Assert.Equal(FaFileDataCodec.BlockLength, block.Length);
            Assert.Equal(0x41, block[0]);
            Assert.Equal(0x43, block[2]);

            for (int i = 3; i < block.Length; i++)
            {
                Assert.Equal(0, block[i]);
            }
        }

        /// <summary>
        /// The captured counter pairs follow the rule: increment by one, then set bit 7.
        /// </summary>
        /// <remarks>
        /// <para><b>What this pins</b></para>
        /// The counter/token pattern on data messages was recorded as UNKNOWN until 2026-08-05.
        /// These are the real counters, read off both captures, and the rule reproduces every one.
        /// <para><b>Why it looked mysterious</b></para>
        /// Read as raw bytes the first pair is <c>05</c> then <c>86</c>, which looks like a jump. It
        /// is <c>05</c>, then <c>06</c> with bit 7 set to mark the end of the delivery.
        /// </remarks>
        [Theory]
        [InlineData(0x05, 0x86)]
        [InlineData(0x08, 0x89)]
        [InlineData(0x0B, 0x8C)]
        [InlineData(0x1D, 0x9E)]
        [InlineData(0x03, 0x84)]
        [InlineData(0x04, 0x85)]
        [InlineData(0x13, 0x94)]
        public void TheCapturedCounterPairsFollowTheRule(int first, int second)
        {
            byte expected = (byte)((first + 1) | FaFileDataCodec.LastDataMessageFlag);

            Assert.Equal(second, expected);

            // And the first of a pair never carries the flag.
            Assert.Equal(0, first & FaFileDataCodec.LastDataMessageFlag);
        }

        /// <summary>
        /// A data message reproduces the captured prefix and carries raw content after it.
        /// </summary>
        /// <remarks>
        /// The first data message of the captured read is <c>07F0 0002 05 00 90BB</c> followed by
        /// 1024 raw bytes that open <c>8D 0A C0 28 4E 44 2D 50 41 D4</c> - the text ".. (ND-PAT" in
        /// ND 7-bit, which is file content and not tags.
        /// </remarks>
        [Fact]
        public void ADataMessageReproducesTheCapturedPrefix()
        {
            byte[] content = Hex.ToBytes("8D0AC0284E442D5041D4");

            byte[] message = FaFileDataCodec.BuildDataMessage(
                conversation: 0x0002, counter: 0x05, token: 0x90BB, content: content);

            Assert.Equal(1032, message.Length);
            Assert.Equal(FaFileDataCodec.DataMessageLength, message.Length);

            byte[] prefix = new byte[8];
            Array.Copy(message, prefix, 8);
            Assert.Equal(Hex.ToBytes("07F00002050090BB"), prefix);

            // The content starts immediately after the prefix, untagged.
            Assert.Equal(0x8D, message[8]);
            Assert.Equal(0xD4, message[17]);
            Assert.Equal(0, message[18]);
        }

        // The delivery-counter test that stood here is GONE (2026-08-06) along with the helper it
        // exercised. It asserted a data-message-only counter and had to call the helper an extra
        // time to "consume" the reply's number - a fudge for the fact that the counter is shared.
        // Its replacement is FaServerConversationTests.TheMessageCounterReproducesTheCapture, which
        // walks the real sequence instead.

        /// <summary>
        /// One read moves two blocks, which is the 2048-byte unit the positions count in.
        /// </summary>
        [Fact]
        public void OneReadMovesTwoBlocks()
        {
            Assert.Equal(1024, FaFileDataCodec.BlockLength);
            Assert.Equal(2, FaFileDataCodec.BlocksPerRead);
            Assert.Equal(2048, FaFileDataCodec.ReadLength);

            // The capture reads a 17905-byte file in nine steps, positions 0 to 8.
            const int CapturedFileLength = 17905;
            int steps = (CapturedFileLength + FaFileDataCodec.ReadLength - 1) / FaFileDataCodec.ReadLength;
            Assert.Equal(9, steps);
        }
    }
}
