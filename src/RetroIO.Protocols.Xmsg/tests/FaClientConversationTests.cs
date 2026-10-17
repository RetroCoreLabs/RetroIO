//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using RetroIO.Protocols.Xmsg.Hdlc;
using RetroIO.Protocols.Xmsg.Fa;
using RetroIO.Protocols.Xmsg.Tests;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Replays the captured DELETE-FILE and FILE-STATISTICS conversations through
    /// <see cref="FaClientConversation"/> and requires it to reproduce every request byte for byte.
    /// </summary>
    /// <remarks>
    /// This is the test that makes the conversation driver worth trusting. The payloads are taken
    /// from the capture - they are opaque to the driver - but the ENVELOPE around them is entirely
    /// the driver's work: the message type, the conversation number, the session header with its
    /// 0x80+n counter and its first-exchange token, and the operation/sequence pair. If any of those
    /// is wrong, the rebuilt request will not match.
    /// </remarks>
    public sealed class FaClientConversationTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public FaClientConversationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// Frame offset at which the message body begins.
        /// </summary>
        private const int BodyOffsetFullHeader = 28;

        /// <summary>
        /// Rebuilds every request of a captured conversation and compares it with the original.
        /// </summary>
        /// <param name="captureName">
        /// The capture to replay.
        /// </param>
        /// <param name="conversation">
        /// The conversation number the client used.
        /// </param>
        /// <param name="expectedExchanges">
        /// How many requests the conversation contains. DELETE-FILE takes three - open, delete,
        /// close - but FILE-STATISTICS takes six, because it repeats the directory-enquiry exchange
        /// four times between the open and the close. Both counts come from the captures.
        /// </param>
        [Theory]
        [InlineData("claude-delete-file-102-to-100-2026-07-29.pcapng", (ushort)0x0048, 3)]
        [InlineData("claude-file-stat-102-to-100-2026-07-29.pcapng", (ushort)0x0046, 6)]
        public void ReplayedConversation_RebuildsEveryRequestByteForByte(
            string captureName,
            ushort conversation,
            int expectedExchanges)
        {
            List<byte[]> requests = ReadClientRequests(captureName);
            if (requests.Count == 0)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            FaClientConversation client = new FaClientConversation(conversation);

            for (int i = 0; i < requests.Count; i++)
            {
                byte[] captured = requests[i];

                // Read the operation out of the captured request, then hand the driver everything
                // after the operation/sequence pair as an opaque payload.
                FaOperation operation;
                ushort sequence;
                Assert.True(FaExchangeCodec.TryReadOperation(captured, out operation, out sequence));

                int payloadOffset = FaExchangeCodec.QformOffset + 6;
                ReadOnlySpan<byte> payload = new ReadOnlySpan<byte>(captured, payloadOffset, captured.Length - payloadOffset);

                byte[] rebuilt = client.BuildRequest(operation, payload);

                _output.WriteLine("exchange " + (i + 1)
                    + ": operation=" + operation + " (0x" + ((ushort)operation).ToString("x4") + ")"
                    + " capturedSequence=" + sequence
                    + " length=" + captured.Length);

                // The driver must have produced the same sequence number the machine did.
                Assert.Equal(i + 1, sequence);
                Assert.Equal(captured, rebuilt);
            }

            _output.WriteLine("rebuilt " + requests.Count + " requests, all byte-identical");
            Assert.Equal(expectedExchanges, requests.Count);
        }

        /// <summary>
        /// Checks that replies are matched to their requests by the echoed operation and sequence.
        /// </summary>
        [Fact]
        public void Replies_AreMatchedByOperationAndSequence()
        {
            List<byte[]> bodies = ReadBodies("claude-delete-file-102-to-100-2026-07-29.pcapng");
            if (bodies.Count == 0)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            int matched = 0;

            for (int i = 0; i < bodies.Count; i++)
            {
                byte[] body = bodies[i];
                if (!FaExchangeCodec.IsReply(body)) { continue; }

                FaMessageType messageType;
                ushort conversation;
                byte sequenceByte;
                ushort token;
                if (!FaExchangeCodec.TryReadEnvelope(body, out messageType, out conversation, out sequenceByte, out token))
                {
                    continue;
                }

                if (messageType != FaMessageType.Request) { continue; }

                FaOperation operation;
                ushort sequence;
                Assert.True(FaExchangeCodec.TryReadOperation(body, out operation, out sequence));

                Assert.True(FaClientConversation.IsReplyTo(body, operation, sequence));

                // A wrong sequence must NOT match, or the check is worthless.
                Assert.False(FaClientConversation.IsReplyTo(body, operation, (ushort)(sequence + 1)));

                matched++;
            }

            _output.WriteLine("matched " + matched + " replies");
            Assert.Equal(3, matched);
        }

        /// <summary>
        /// Reads the client's request bodies from a capture, in wire order.
        /// </summary>
        /// <param name="captureName">
        /// The capture file name.
        /// </param>
        private static List<byte[]> ReadClientRequests(string captureName)
        {
            List<byte[]> requests = new List<byte[]>();
            List<byte[]> bodies = ReadBodies(captureName);

            for (int i = 0; i < bodies.Count; i++)
            {
                byte[] body = bodies[i];

                FaMessageType messageType;
                ushort conversation;
                byte sequenceByte;
                ushort token;
                if (!FaExchangeCodec.TryReadEnvelope(body, out messageType, out conversation, out sequenceByte, out token))
                {
                    continue;
                }

                if (messageType == FaMessageType.Request && !FaExchangeCodec.IsReply(body))
                {
                    requests.Add(body);
                }
            }

            return requests;
        }

        /// <summary>
        /// Reads every data-frame body from a capture, in wire order.
        /// </summary>
        /// <param name="captureName">
        /// The capture file name.
        /// </param>
        private static List<byte[]> ReadBodies(string captureName)
        {
            List<byte[]> bodies = new List<byte[]>();

            string? path = PcapFiles.File(captureName);
            if (path == null) { return bodies; }

            IReadOnlyList<LapbFrame> frames = HdlcPcap.ReadFramesInCaptureOrder(path);
            for (int i = 0; i < frames.Count; i++)
            {
                LapbFrame frame = frames[i];
                if (frame.Kind != LapbFrameKind.Information || frame.Info.Length <= BodyOffsetFullHeader)
                {
                    continue;
                }

                ReadOnlySpan<byte> info = frame.Info.Span;
                if (info[3] != (byte)SintranPacketSubtype.Data) { continue; }

                bodies.Add(info.Slice(BodyOffsetFullHeader).ToArray());
            }

            return bodies;
        }

    }
}
