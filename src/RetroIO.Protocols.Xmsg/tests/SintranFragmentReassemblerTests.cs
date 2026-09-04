using System;
using System.Collections.Generic;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Covers rejoining a message that arrived as a fragment pair - the half of fragmentation that
    /// has to work before a file can be written TO us.
    /// </summary>
    /// <remarks>
    /// Sending a split message was straightforward; receiving one is where the state lives. These
    /// tests cover the pairing rule, the two ways a pair can be malformed, and the round trip
    /// against our own splitter.
    /// </remarks>
    public sealed class SintranFragmentReassemblerTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public SintranFragmentReassemblerTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// A node number used as the sender in these tests.
        /// </summary>
        private const ushort SenderNode = 100;

        /// <summary>
        /// A frame that is not a fragment passes straight through untouched.
        /// </summary>
        /// <remarks>
        /// The reassembler sits in front of every received frame, so the ordinary path costing
        /// nothing and changing nothing matters more than the fragment path working.
        /// </remarks>
        [Fact]
        public void AnOrdinaryDataFrameIsReturnedUnchanged()
        {
            SintranFragmentReassembler reassembler = new SintranFragmentReassembler();

            XmsgFrame plain = BuildFrame(SintranPacketSubtype.Data, 0x0100, 8, new byte[] { 1, 2, 3, 4 }, true);

            XmsgFrame? result = reassembler.Accept(plain);

            Assert.Same(plain, result);
            Assert.Equal(0, reassembler.PendingCount);
        }

        /// <summary>
        /// A pair rejoins into one data frame carrying both payloads.
        /// </summary>
        [Fact]
        public void APairRejoinsIntoOneDataFrame()
        {
            SintranFragmentReassembler reassembler = new SintranFragmentReassembler();

            byte[] head = Bytes(0, SintranMessageFragment.FirstFragmentBodyLength);
            byte[] tail = Bytes(SintranMessageFragment.FirstFragmentBodyLength, 438);
            int total = head.Length + tail.Length;

            XmsgFrame first = BuildFrame(
                SintranPacketSubtype.MessageFirstFragment, 0x0204, (ushort)total, head, true);
            XmsgFrame continuation = BuildFrame(
                SintranPacketSubtype.MessageContinuation, 0x0204, (ushort)head.Length, tail, false);

            // The first fragment is consumed and nothing is answered yet.
            Assert.Null(reassembler.Accept(first));
            Assert.Equal(1, reassembler.PendingCount);

            XmsgFrame? joined = reassembler.Accept(continuation);
            Assert.NotNull(joined);
            Assert.Equal(0, reassembler.PendingCount);

            // It presents as an ordinary data frame, so every dispatch above this layer sees what it
            // would have seen had the message arrived whole.
            Assert.Equal(SintranPacketSubtype.Data, joined!.Header.Subtype);
            Assert.NotNull(joined.SubHeader);
            Assert.Equal(0x0204, joined.Header.Flags1);

            byte[] body = joined.GetBodyBytes();
            Assert.Equal(total, body.Length);

            for (int i = 0; i < total; i++)
            {
                Assert.Equal((byte)(i & 0xFF), body[i]);
            }
        }

        /// <summary>
        /// A whole 1032-byte message survives our own splitter and this reassembler unchanged.
        /// </summary>
        /// <remarks>
        /// The round trip is the test that matters: it pins the two halves against each other, so a
        /// change to the split point cannot pass unless the join agrees with it.
        /// </remarks>
        [Fact]
        public void OurOwnSplitMessageRoundTrips()
        {
            const int MessageLength = 1032;
            byte[] message = Bytes(0, MessageLength);

            ReadOnlySpan<byte> head;
            ReadOnlySpan<byte> tail;
            SintranMessageFragment.Split(message, out head, out tail);

            _output.WriteLine("split " + MessageLength + " into " + head.Length + " + " + tail.Length);

            SintranFragmentReassembler reassembler = new SintranFragmentReassembler();
            reassembler.Accept(BuildFrame(
                SintranPacketSubtype.MessageFirstFragment, 0x0300, MessageLength, head.ToArray(), true));

            XmsgFrame? joined = reassembler.Accept(BuildFrame(
                SintranPacketSubtype.MessageContinuation, 0x0300, (ushort)head.Length, tail.ToArray(), false));

            Assert.NotNull(joined);
            Assert.Equal(message, joined!.GetBodyBytes());
        }

        /// <summary>
        /// A continuation with no first fragment is dropped rather than passed on.
        /// </summary>
        [Fact]
        public void AContinuationWithNoFirstFragmentIsDropped()
        {
            SintranFragmentReassembler reassembler = new SintranFragmentReassembler();

            string reported = string.Empty;
            reassembler.Log += line => { reported = line; _output.WriteLine(line); };

            XmsgFrame orphan = BuildFrame(
                SintranPacketSubtype.MessageContinuation, 0x0400, 594, new byte[] { 9, 9 }, false);

            Assert.Null(reassembler.Accept(orphan));
            Assert.Contains("no first fragment", reported);
        }

        /// <summary>
        /// A continuation whose resume offset disagrees with what the first fragment carried is
        /// dropped, however well their Flags 1 agreed.
        /// </summary>
        /// <remarks>
        /// Joining them anyway would hand the layer above a message with a hole or an overlap in it,
        /// which is worse than losing it: the bytes would look plausible and be wrong.
        /// </remarks>
        [Fact]
        public void AContinuationThatResumesAtTheWrongOffsetIsDropped()
        {
            SintranFragmentReassembler reassembler = new SintranFragmentReassembler();

            string reported = string.Empty;
            reassembler.Log += line => { reported = line; _output.WriteLine(line); };

            reassembler.Accept(BuildFrame(
                SintranPacketSubtype.MessageFirstFragment, 0x0500, 1032, Bytes(0, 594), true));

            // Says it resumes at 500, but 594 bytes came before it.
            XmsgFrame wrong = BuildFrame(
                SintranPacketSubtype.MessageContinuation, 0x0500, 500, Bytes(594, 438), false);

            Assert.Null(reassembler.Accept(wrong));
            Assert.Contains("resumes at", reported);
        }

        /// <summary>
        /// Two peers mid-message on the same Flags 1 do not get spliced together.
        /// </summary>
        /// <remarks>
        /// Flags 1 is per LINK, so the same number really can be in flight from two nodes at once.
        /// Pairing on it alone would join one peer's continuation to the other's first fragment and
        /// produce a message that never existed.
        /// </remarks>
        [Fact]
        public void TwoPeersOnTheSameFlags1AreKeptApart()
        {
            SintranFragmentReassembler reassembler = new SintranFragmentReassembler();

            XmsgFrame firstFrom100 = BuildFrame(
                SintranPacketSubtype.MessageFirstFragment, 0x0600, 8, new byte[] { 0xAA, 0xAA, 0xAA, 0xAA }, true);
            firstFrom100.Header.SourceNode = 100;

            XmsgFrame firstFrom103 = BuildFrame(
                SintranPacketSubtype.MessageFirstFragment, 0x0600, 8, new byte[] { 0xBB, 0xBB, 0xBB, 0xBB }, true);
            firstFrom103.Header.SourceNode = 103;

            reassembler.Accept(firstFrom100);
            reassembler.Accept(firstFrom103);
            Assert.Equal(2, reassembler.PendingCount);

            XmsgFrame contFrom103 = BuildFrame(
                SintranPacketSubtype.MessageContinuation, 0x0600, 4, new byte[] { 0xBB, 0xBB, 0xBB, 0xBB }, false);
            contFrom103.Header.SourceNode = 103;

            XmsgFrame? joined = reassembler.Accept(contFrom103);

            Assert.NotNull(joined);
            Assert.Equal(103, joined!.Header.SourceNode);

            // Every byte came from 103, none from 100.
            byte[] body = joined.GetBodyBytes();
            for (int i = 0; i < body.Length; i++)
            {
                Assert.Equal(0xBB, body[i]);
            }

            // And 100's half is still waiting, not consumed by 103's continuation.
            Assert.Equal(1, reassembler.PendingCount);
        }

        /// <summary>
        /// Incomplete messages do not accumulate without bound.
        /// </summary>
        [Fact]
        public void IncompleteMessagesAreCappedRatherThanHeldForever()
        {
            SintranFragmentReassembler reassembler = new SintranFragmentReassembler();
            reassembler.Log += line => _output.WriteLine(line);

            for (int i = 0; i < SintranFragmentReassembler.MaxPendingMessages + 4; i++)
            {
                reassembler.Accept(BuildFrame(
                    SintranPacketSubtype.MessageFirstFragment, (ushort)(0x0700 + i), 8, new byte[] { 1, 2, 3, 4 }, true));
            }

            Assert.Equal(SintranFragmentReassembler.MaxPendingMessages, reassembler.PendingCount);
        }

        /// <summary>
        /// Builds bytes whose value tracks their position, so a splice error shows up as content.
        /// </summary>
        /// <param name="start">
        /// The position the first byte stands at.
        /// </param>
        /// <param name="length">
        /// How many bytes to make.
        /// </param>
        /// <returns>
        /// The bytes.
        /// </returns>
        private static byte[] Bytes(int start, int length)
        {
            byte[] data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (byte)((start + i) & 0xFF);
            }

            return data;
        }

        /// <summary>
        /// Builds a received frame for the reassembler to chew on.
        /// </summary>
        /// <param name="subtype">
        /// The packet subtype.
        /// </param>
        /// <param name="flags1">
        /// The datagram sequence, which pairs the two fragments.
        /// </param>
        /// <param name="flags2">
        /// The total length on a first fragment, the resume offset on a continuation.
        /// </param>
        /// <param name="body">
        /// The payload this frame carries.
        /// </param>
        /// <param name="withSubHeader">
        /// Whether the frame carries the addressing head. A continuation does not.
        /// </param>
        /// <returns>
        /// The frame.
        /// </returns>
        private static XmsgFrame BuildFrame(
            SintranPacketSubtype subtype, ushort flags1, ushort flags2, byte[] body, bool withSubHeader)
        {
            XmsgFrame frame = new XmsgFrame();
            frame.Header.Marker1 = SintranHeader.Marker1Value;
            frame.Header.Marker2 = SintranHeader.Marker2Normal;
            frame.Header.Subtype = subtype;
            frame.Header.DestinationNode = 19999;
            frame.Header.SourceNode = SenderNode;
            frame.Header.Flags1 = flags1;
            frame.Header.Flags2 = flags2;

            if (withSubHeader)
            {
                XmsgSubHeader sub = new XmsgSubHeader();
                sub.DestinationSystem = 19999;
                sub.DestinationPort = 0x0211;
                sub.SourceSystem = SenderNode;
                sub.SourcePort = 0x02F7;
                sub.Xmcsm = flags2;
                frame.SubHeader = sub;
            }

            frame.TrailingBytes = body;
            frame.ClearRawBytes();
            return frame;
        }
    }
}
