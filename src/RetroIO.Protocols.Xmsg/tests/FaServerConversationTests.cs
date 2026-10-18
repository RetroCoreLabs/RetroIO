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
    /// Replays node 100's side of the captured conversations through
    /// <see cref="FaServerConversation"/> and requires every message to match byte for byte.
    /// </summary>
    /// <remarks>
    /// Together with <c>FaClientConversationTests</c> this covers both halves of the exchange: the
    /// client driver reproduces every request the real client sent, and this reproduces every
    /// message the real server answered with.
    /// </remarks>
    public sealed class FaServerConversationTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public FaServerConversationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private const string DeleteCapture = "2026-07-29-2047-fa-delete-file-d102-to-d100.pcapng";

        /// <summary>
        /// The conversation number the client used in the DELETE-FILE capture.
        /// </summary>
        private const ushort DeleteConversation = 0x0048;

        /// <summary>
        /// Frame offset at which the message body begins.
        /// </summary>
        private const int BodyOffsetFullHeader = 28;

        /// <summary>
        /// Rebuilds every reply the server sent and compares it with the captured bytes.
        /// </summary>
        [Fact]
        public void RebuiltReplies_AreByteIdenticalToTheCapturedReplies()
        {
            List<byte[]> serverBodies = ReadServerBodies();
            if (serverBodies.Count == 0)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            FaServerConversation server = new FaServerConversation(DeleteConversation);
            int rebuilt = 0;

            for (int i = 0; i < serverBodies.Count; i++)
            {
                byte[] captured = serverBodies[i];

                FaMessageType messageType = (FaMessageType)((captured[0] << 8) | captured[1]);
                if (messageType != FaMessageType.Request) { continue; }

                FaOperation operation;
                ushort sequence;
                Assert.True(FaExchangeCodec.TryReadOperation(captured, out operation, out sequence));

                int payloadOffset = FaExchangeCodec.QformOffset + 6;
                ReadOnlySpan<byte> payload = new ReadOnlySpan<byte>(
                    captured, payloadOffset, captured.Length - payloadOffset);

                // The session-header byte comes from the conversation's own counter - nothing is fed
                // back from the capture. Byte equality against the captured reply therefore also
                // checks the counter: it has to reach the captured value on its own, in order.
                byte[] reply = server.BuildReply(operation, sequence, payload);

                _output.WriteLine("reply " + (rebuilt + 1)
                    + ": operation=" + operation + " (0x" + ((ushort)operation).ToString("x4") + ")"
                    + " sequence=" + sequence
                    + " length=" + captured.Length);

                Assert.Equal(captured, reply);
                rebuilt++;
            }

            _output.WriteLine("rebuilt " + rebuilt + " replies, all byte-identical");
            Assert.Equal(3, rebuilt);
        }

        /// <summary>
        /// Rebuilds the server's short acknowledgements, connection confirmation and close.
        /// </summary>
        [Fact]
        public void RebuiltControlMessages_AreByteIdenticalToTheCapturedOnes()
        {
            List<byte[]> serverBodies = ReadServerBodies();
            if (serverBodies.Count == 0)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            FaServerConversation server = new FaServerConversation(DeleteConversation);

            List<byte[]> shortAcks = new List<byte[]>();
            byte[]? confirm = null;
            byte[]? close = null;

            for (int i = 0; i < serverBodies.Count; i++)
            {
                byte[] body = serverBodies[i];
                FaMessageType messageType = (FaMessageType)((body[0] << 8) | body[1]);

                if (messageType == FaMessageType.ShortAck) { shortAcks.Add(body); }
                else if (messageType == FaMessageType.ConnectionConfirm) { confirm = body; }
                else if (messageType == FaMessageType.Close) { close = body; }
            }

            // The connection confirmation echoes the letter's request word and carries the
            // server's own connection number, then the trailing constant. CORRECTED 2026-08-04: the
            // second word used to be asserted as the constant ResponderConversation and the third
            // as the CLIENT's conversation number. The captures disprove both - see
            // FaServerConversation.BuildConnectionConfirm. The values are read back out of the
            // captured body here rather than restated, so this checks the LAYOUT, which is what
            // the builder is responsible for.
            Assert.NotNull(confirm);
            _output.WriteLine("confirm: " + Hex(confirm!));
            ushort echoed = (ushort)((confirm![2] << 8) | confirm[3]);
            ushort connectionNumber = (ushort)((confirm[4] << 8) | confirm[5]);

            // CORRECTED AGAIN 2026-08-09: the last word was passed here as the number 100, matching
            // a byte that was modelled as "the system number". It is a CONSTANT - a real server
            // emits 0x6400 even when neither it nor its client is node 100 - so assert it against
            // the constant, taken from THIS captured body rather than from our own builder.
            Assert.Equal(
                FaExchangeCodec.ConfirmTrailingWord,
                (ushort)((confirm[6] << 8) | confirm[7]));

            Assert.Equal(
                confirm,
                server.BuildConnectionConfirm(
                    echoed, connectionNumber, FaExchangeCodec.ConfirmTrailingWord));

            Assert.NotNull(close);
            _output.WriteLine("close:   " + Hex(close!));
            Assert.Equal(close, server.BuildClose());

            Assert.Equal(3, shortAcks.Count);
            for (int i = 0; i < shortAcks.Count; i++)
            {
                byte[] expected = server.BuildShortAck((byte)(i + 1), true);
                _output.WriteLine("ack " + (i + 1) + ":   " + Hex(shortAcks[i]));
                Assert.Equal(shortAcks[i], expected);
            }
        }

        /// <summary>
        /// The shared message counter walks the sequence the capture shows.
        /// </summary>
        /// <remarks>
        /// <para><b>The measurement</b></para>
        /// The first 26 messages the server sends in
        /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-read.txt</c> run:
        /// <code>
        /// 80 81 82 83 84 05 86 | 87 08 89 | 8A 0B 8C | 8D 0E 8F | 90 11 92 ...
        /// </code>
        /// One counter, +1 per message, bit 7 set on every message EXCEPT the first data message of
        /// each pair. Five replies open the conversation, then each read contributes a reply and two
        /// data messages.
        /// <para><b>Why this test is worth its length</b></para>
        /// The listing goldens cannot catch this: a listing sends replies only, so a reply-only
        /// counter and a shared one produce identical bytes. It took a live file transfer to tell
        /// them apart.
        /// </remarks>
        [Fact]
        public void TheMessageCounterReproducesTheCapture()
        {
            FaServerConversation server = new FaServerConversation(DeleteConversation);

            // The five replies that open the conversation.
            byte[] opening = new byte[5];
            for (int i = 0; i < opening.Length; i++)
            {
                opening[i] = server.NextMessageCounter(true);
            }

            Assert.Equal(new byte[] { 0x80, 0x81, 0x82, 0x83, 0x84 }, opening);

            // Then three reads, each a first data message, a last data message, and a reply.
            byte[] expected =
            {
                0x05, 0x86, 0x87,
                0x08, 0x89, 0x8A,
                0x0B, 0x8C, 0x8D,
            };

            byte[] actual = new byte[expected.Length];
            for (int read = 0; read < 3; read++)
            {
                actual[read * 3] = server.NextMessageCounter(false);
                actual[read * 3 + 1] = server.NextMessageCounter(true);
                actual[read * 3 + 2] = server.NextMessageCounter(true);
            }

            Assert.Equal(expected, actual);
        }

        /// <summary>
        /// Guards the client/server asymmetry: the first reply must NOT use the client's
        /// first-exchange token.
        /// </summary>
        /// <remarks>
        /// If the server were built symmetric with the client it would send <c>0x0001</c> here. The
        /// capture says <c>0x9081</c> from the very first reply, so this pins the difference.
        /// </remarks>
        [Fact]
        public void FirstReply_UsesTheResponderTokenNotTheFirstExchangeToken()
        {
            FaServerConversation server = new FaServerConversation(DeleteConversation);
            byte[] reply = server.BuildReply(FaOperation.ReserveFileEntry, 1, new byte[] { 0xF2, 0x00, 0xFF });

            ushort token = (ushort)((reply[FaExchangeCodec.SessionHeaderOffset + 2] << 8)
                | reply[FaExchangeCodec.SessionHeaderOffset + 3]);

            Assert.Equal(FaExchangeCodec.SessionTokenResponder, token);
            Assert.NotEqual(FaExchangeCodec.SessionTokenFirst, token);

            // And it answers with its own conversation number, not the client's.
            ushort conversation = (ushort)((reply[FaExchangeCodec.ConversationOffset] << 8)
                | reply[FaExchangeCodec.ConversationOffset + 1]);
            Assert.Equal(FaExchangeCodec.ResponderConversation, conversation);
            Assert.NotEqual(DeleteConversation, conversation);
        }

        /// <summary>
        /// Formats bytes as spaced hex.
        /// </summary>
        /// <param name="data">
        /// The bytes to format.
        /// </param>
        private static string Hex(byte[] data)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder(data.Length * 3);
            for (int i = 0; i < data.Length; i++)
            {
                text.Append(data[i].ToString("x2"));
                if ((i % 2) == 1) { text.Append(' '); }
            }

            return text.ToString();
        }

        /// <summary>
        /// Reads the bodies node 100 sent, in wire order.
        /// </summary>
        private static List<byte[]> ReadServerBodies()
        {
            List<byte[]> bodies = new List<byte[]>();

            string? path = PcapFiles.File(DeleteCapture);
            if (path == null) { return bodies; }

            IReadOnlyList<CapturedFrame> frames = HdlcPcap.ReadCapturedFramesInCaptureOrder(path);
            for (int i = 0; i < frames.Count; i++)
            {
                LapbFrame frame = frames[i].Frame;
                if (frame.Kind != LapbFrameKind.Information || frame.Info.Length <= BodyOffsetFullHeader)
                {
                    continue;
                }

                ReadOnlySpan<byte> info = frame.Info.Span;
                if (info[3] != (byte)SintranPacketSubtype.Data) { continue; }

                // Node 100 is the listener, so its frames are the ones leaving port 10362.
                if (frames[i].Key.SourcePort != 10362) { continue; }

                bodies.Add(info.Slice(BodyOffsetFullHeader).ToArray());
            }

            return bodies;
        }

    }
}
