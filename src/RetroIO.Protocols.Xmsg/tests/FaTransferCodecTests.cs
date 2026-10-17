//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using RetroIO.Protocols.Xmsg.Fa;
using RetroIO.Protocols.Xmsg.Hdlc;
using RetroIO.Protocols.Xmsg.Tests;
using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Holds <see cref="FaTransferCodec"/> to the bytes actually captured: every message it builds
    /// must be byte-identical to the corresponding message in the transfer capture, and every
    /// message in the capture must read back correctly.
    /// </summary>
    /// <remarks>
    /// A codec written from a document can drift from the wire without anything failing. These tests
    /// replay against the capture itself, so if the codec and the machine ever disagree the build
    /// says so.
    /// </remarks>
    public sealed class FaTransferCodecTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public FaTransferCodecTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private const string CaptureName = "claude-transfer-file-COMPLETE-102-to-100-2026-07-29.pcapng";

        /// <summary>
        /// Frame offset at which the body begins on a fully addressed frame.
        /// </summary>
        private const int BodyOffsetFullHeader = 28;

        /// <summary>
        /// Frame offset at which the body resumes on a continuation frame.
        /// </summary>
        private const int BodyOffsetContinuation = 14;

        /// <summary>
        /// Rebuilds every captured data message from its own page, displacement and block, and
        /// requires the result to be byte-identical to what was captured.
        /// </summary>
        [Fact]
        public void BuiltDataMessages_AreByteIdenticalToTheCapturedMessages()
        {
            List<byte[]> messages = ReassembleDataMessages();
            if (messages.Count == 0)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            for (int i = 0; i < messages.Count; i++)
            {
                byte[] captured = messages[i];

                ushort page;
                ushort displacement;
                ReadOnlySpan<byte> block;
                bool read = FaTransferCodec.TryReadDataMessage(captured, out page, out displacement, out block);
                Assert.True(read, "Message " + i + " did not read as a data message.");

                byte[] rebuilt = FaTransferCodec.BuildDataMessage(page, displacement, block);

                _output.WriteLine("message " + i + ": page=" + page + " displacementWords=" + displacement);
                Assert.Equal(captured, rebuilt);
            }

            Assert.Equal(4, messages.Count);
        }

        /// <summary>
        /// Checks the page and displacement progression across the transfer.
        /// </summary>
        /// <remarks>
        /// Two blocks per page, displacement alternating 0 and 512 words. This is what establishes
        /// that the displacement counts words rather than bytes: 512 words is 1024 bytes, exactly one
        /// block, so a byte reading would leave the second block overlapping the first.
        /// </remarks>
        [Fact]
        public void DataMessages_AdvanceTwoBlocksPerPage()
        {
            List<byte[]> messages = ReassembleDataMessages();
            if (messages.Count == 0)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            for (int i = 0; i < messages.Count; i++)
            {
                ushort page;
                ushort displacement;
                ReadOnlySpan<byte> block;
                Assert.True(FaTransferCodec.TryReadDataMessage(messages[i], out page, out displacement, out block));

                Assert.Equal(i / 2, page);
                Assert.Equal(
                    (i % 2) == 0 ? 0 : FaTransferCodec.SecondBlockDisplacementWords,
                    displacement);
            }
        }

        /// <summary>
        /// Requires the split point to match the segmentation the machine actually used.
        /// </summary>
        [Fact]
        public void SplitForTransmission_MatchesTheCapturedFragmentBoundary()
        {
            List<byte[]> messages = ReassembleDataMessages();
            if (messages.Count == 0)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            ReadOnlySpan<byte> first;
            ReadOnlySpan<byte> continuation;
            FaTransferCodec.SplitForTransmission(messages[0], out first, out continuation);

            _output.WriteLine("first fragment " + first.Length + " bytes, continuation " + continuation.Length);

            Assert.Equal(594, first.Length);
            Assert.Equal(436, continuation.Length);
            Assert.Equal(FaTransferCodec.DataMessageLength, first.Length + continuation.Length);
        }

        /// <summary>
        /// Requires the end-of-transfer message and both reply forms to match the capture exactly.
        /// </summary>
        /// <remarks>
        /// The capture contains six short control bodies in this order: four replies to the four data
        /// messages, then the end-of-transfer marker, then its reply. The end-of-transfer reply
        /// differs from the data replies in its final word.
        /// </remarks>
        [Fact]
        public void ControlMessages_AreByteIdenticalToTheCapturedBodies()
        {
            string? path = PcapFiles.File(CaptureName);
            if (path == null)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            IReadOnlyList<LapbFrame> frames = HdlcPcap.ReadFramesInCaptureOrder(path);

            List<byte[]> controlBodies = new List<byte[]>();
            for (int i = 0; i < frames.Count; i++)
            {
                LapbFrame frame = frames[i];
                if (frame.Kind != LapbFrameKind.Information || frame.Info.Length < BodyOffsetFullHeader)
                {
                    continue;
                }

                ReadOnlySpan<byte> info = frame.Info.Span;
                if (info[3] != (byte)SintranPacketSubtype.Data) { continue; }
                if (info.Length - BodyOffsetFullHeader != FaTransferCodec.ControlMessageLength) { continue; }

                controlBodies.Add(info.Slice(BodyOffsetFullHeader).ToArray());
            }

            Assert.Equal(6, controlBodies.Count);

            // The four data replies.
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(FaTransferCodec.BuildDataReply(), controlBodies[i]);
            }

            // The end-of-transfer marker, then its distinct reply.
            Assert.True(FaTransferCodec.IsEndOfTransfer(controlBodies[4]));
            Assert.Equal(FaTransferCodec.BuildEndOfTransfer(), controlBodies[4]);
            Assert.Equal(FaTransferCodec.BuildEndOfTransferReply(), controlBodies[5]);

            // The two replies really do differ - guarding against both builders collapsing to zeros.
            Assert.NotEqual(FaTransferCodec.BuildDataReply(), FaTransferCodec.BuildEndOfTransferReply());
        }

        /// <summary>
        /// Rejects a block that is not exactly one block long.
        /// </summary>
        [Fact]
        public void BuildDataMessage_RejectsAWronglySizedBlock()
        {
            Assert.Throws<ArgumentException>(
                () => FaTransferCodec.BuildDataMessage(0, 0, new byte[FaTransferCodec.BlockLength - 1]));
        }

        /// <summary>
        /// Reassembles the segmented data messages from the capture.
        /// </summary>
        /// <returns>
        /// The message bodies, or an empty list when the capture is not present.
        /// </returns>
        private static List<byte[]> ReassembleDataMessages()
        {
            List<byte[]> messages = new List<byte[]>();

            string? path = PcapFiles.File(CaptureName);
            if (path == null) { return messages; }

            IReadOnlyList<LapbFrame> frames = HdlcPcap.ReadFramesInCaptureOrder(path);
            Dictionary<int, byte[]> firstBySequence = new Dictionary<int, byte[]>();

            for (int i = 0; i < frames.Count; i++)
            {
                LapbFrame frame = frames[i];
                if (frame.Kind != LapbFrameKind.Information || frame.Info.Length < BodyOffsetContinuation)
                {
                    continue;
                }

                ReadOnlySpan<byte> info = frame.Info.Span;
                int sequence = (info[8] << 8) | info[9];

                if (info[3] == (byte)SintranPacketSubtype.MessageFirstFragment)
                {
                    firstBySequence[sequence] = info.Slice(BodyOffsetFullHeader).ToArray();
                }
                else if (info[3] == (byte)SintranPacketSubtype.MessageContinuation)
                {
                    byte[]? head;
                    if (!firstBySequence.TryGetValue(sequence, out head)) { continue; }

                    ReadOnlySpan<byte> tail = info.Slice(BodyOffsetContinuation);
                    byte[] joined = new byte[head!.Length + tail.Length];
                    Array.Copy(head, 0, joined, 0, head.Length);
                    tail.CopyTo(new Span<byte>(joined, head.Length, tail.Length));
                    messages.Add(joined);
                    firstBySequence.Remove(sequence);
                }
            }

            return messages;
        }

    }
}
