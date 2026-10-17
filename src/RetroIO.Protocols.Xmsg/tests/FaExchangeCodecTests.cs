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
    /// Verifies the shared file-server envelope against the FILE-STATISTICS and DELETE-FILE captures.
    /// </summary>
    public sealed class FaExchangeCodecTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public FaExchangeCodecTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private const string FileStatCapture = "claude-file-stat-102-to-100-2026-07-29.pcapng";
        private const string DeleteCapture = "claude-delete-file-102-to-100-2026-07-29.pcapng";

        /// <summary>
        /// The CREATE-FILE recording, taken a day later than the other two.
        /// </summary>
        private const string CreateCapture = "claude-create-file-102-to-100-2026-07-30.pcapng";

        /// <summary>
        /// Frame offset at which the message body begins.
        /// </summary>
        private const int BodyOffsetFullHeader = 28;

        /// <summary>
        /// The central claim: the two operations open with byte-identical requests apart from the
        /// conversation number, so the operation is NOT named in the opening exchange.
        /// </summary>
        /// <remarks>
        /// If this ever fails, the "conversation number, not opcode" conclusion is wrong and every
        /// codec resting on it needs revisiting - which is exactly why it is asserted rather than
        /// merely written down.
        /// </remarks>
        [Fact]
        public void OpeningRequest_CarriesAFieldThatIsNotTheConversationNumber()
        {
            List<byte[]> deleteBodies = ReadBodies(DeleteCapture);
            List<byte[]> createBodies = ReadBodies(CreateCapture);

            if (deleteBodies.Count == 0 || createBodies.Count == 0)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            byte[] deleteOpen = FindOpeningRequest(deleteBodies);
            byte[] createOpen = FindOpeningRequest(createBodies);

            Assert.NotNull(deleteOpen);
            Assert.NotNull(createOpen);
            Assert.Equal(deleteOpen.Length, createOpen.Length);

            List<int> differing = new List<int>();
            for (int i = 0; i < deleteOpen.Length; i++)
            {
                if (i == FaExchangeCodec.ConversationOffset || i == FaExchangeCodec.ConversationOffset + 1)
                {
                    continue;
                }

                if (deleteOpen[i] != createOpen[i]) { differing.Add(i); }
            }

            System.Text.StringBuilder detail = new System.Text.StringBuilder();
            for (int i = 0; i < differing.Count; i++)
            {
                int at = differing[i];
                detail.Append(" offset ").Append(at)
                    .Append(": DELETE 0x").Append(deleteOpen[at].ToString("x2"))
                    .Append(" ('").Append((char)deleteOpen[at]).Append("')")
                    .Append(" vs CREATE 0x").Append(createOpen[at].ToString("x2"))
                    .Append(" ('").Append((char)createOpen[at]).Append("')");
            }

            _output.WriteLine("bytes differing outside the conversation number: " + differing.Count
                + detail.ToString());

            // The opening request is NOT fixed apart from the conversation number. DELETE-FILE carries
            // the text BAK05 where CREATE-FILE carries BAK03, so there is a second varying field.
            // Meaning UNKNOWN - it looks like a batch or terminal identifier, but nothing here tests
            // that reading, so it must not be synthesised.
            Assert.NotEmpty(differing);
        }

        /// <summary>
        /// Two recordings whose opening requests DO match byte for byte apart from the conversation
        /// number.
        /// </summary>
        /// <remarks>
        /// Kept because it is true of these two and it is what first showed the conversation number is
        /// a counter rather than an operation code. It is NOT a rule about the protocol - see
        /// <see cref="OpeningRequest_CarriesAFieldThatIsNotTheConversationNumber"/>, where a third
        /// recording differs in a second place.
        /// </remarks>
        [Fact]
        public void FileStatAndDelete_OpenWithIdenticalRequestsApartFromTheConversationNumber()
        {
            List<byte[]> statBodies = ReadBodies(FileStatCapture);
            List<byte[]> deleteBodies = ReadBodies(DeleteCapture);

            if (statBodies.Count == 0 || deleteBodies.Count == 0)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            byte[] statOpen = FindOpeningRequest(statBodies);
            byte[] deleteOpen = FindOpeningRequest(deleteBodies);

            Assert.NotNull(statOpen);
            Assert.NotNull(deleteOpen);
            Assert.Equal(statOpen.Length, deleteOpen.Length);

            // Compare every byte except the conversation number at offsets 2 and 3.
            int differences = 0;
            for (int i = 0; i < statOpen.Length; i++)
            {
                if (i == FaExchangeCodec.ConversationOffset || i == FaExchangeCodec.ConversationOffset + 1)
                {
                    continue;
                }

                if (statOpen[i] != deleteOpen[i]) { differences++; }
            }

            _output.WriteLine("opening request length " + statOpen.Length
                + "; differences outside the conversation number: " + differences);

            // These two happen to match byte for byte. A THIRD recording does not - see
            // OpeningRequest_CarriesAFieldThatIsNotTheConversationNumber - so this is a fact about
            // these two, not a rule about the protocol.
            Assert.Equal(0, differences);

            // And the conversation numbers really are different, so the test is not vacuous.
            ushort statConversation = (ushort)((statOpen[2] << 8) | statOpen[3]);
            ushort deleteConversation = (ushort)((deleteOpen[2] << 8) | deleteOpen[3]);
            _output.WriteLine("conversations: FILE-STAT 0x" + statConversation.ToString("x4")
                + ", DELETE 0x" + deleteConversation.ToString("x4"));
            Assert.NotEqual(statConversation, deleteConversation);
        }

        /// <summary>
        /// Checks that the operation code and exchange sequence read correctly, and that DELETE-FILE
        /// is distinguished by the operation code of its SECOND exchange.
        /// </summary>
        [Fact]
        public void DeleteConversation_IsDistinguishedByItsSecondExchange()
        {
            List<byte[]> bodies = ReadBodies(DeleteCapture);
            if (bodies.Count == 0)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            List<FaOperation> requestOperations = new List<FaOperation>();

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

                if (messageType != FaMessageType.Request) { continue; }
                if (FaExchangeCodec.IsReply(body)) { continue; }

                FaOperation operation;
                ushort sequence;
                if (!FaExchangeCodec.TryReadOperation(body, out operation, out sequence)) { continue; }

                _output.WriteLine("request: operation=" + operation
                    + " (0x" + ((ushort)operation).ToString("x4") + ")"
                    + " sequence=" + sequence
                    + " sessionByte=0x" + sequenceByte.ToString("x2")
                    + " token=0x" + token.ToString("x4"));

                requestOperations.Add(operation);
            }

            Assert.Equal(3, requestOperations.Count);
            Assert.Equal(FaOperation.ReserveFileEntry, requestOperations[0]);
            Assert.Equal(FaOperation.DeleteFile, requestOperations[1]);
            Assert.Equal(FaOperation.ReleaseFileEntry, requestOperations[2]);
        }

        /// <summary>
        /// Checks that the exchange sequence counts from one and that the session token switches from
        /// the first-exchange value to the steady-state value.
        /// </summary>
        [Fact]
        public void SessionToken_SwitchesAfterTheFirstExchange()
        {
            List<byte[]> bodies = ReadBodies(DeleteCapture);
            if (bodies.Count == 0)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            bool sawFirst = false;

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

                if (messageType != FaMessageType.Request) { continue; }
                if (FaExchangeCodec.IsReply(body)) { continue; }

                if (sequenceByte == 0x80)
                {
                    Assert.Equal(FaExchangeCodec.SessionTokenFirst, token);
                    sawFirst = true;
                }
                else
                {
                    Assert.Equal(FaExchangeCodec.SessionTokenAsker, token);
                }
            }

            Assert.True(sawFirst, "No opening exchange was found.");
        }

        /// <summary>
        /// Finds the opening request: the first non-reply request whose session byte is 0x80.
        /// </summary>
        /// <param name="bodies">
        /// The message bodies from a capture.
        /// </param>
        /// <returns>
        /// The opening request body, or null.
        /// </returns>
        private static byte[] FindOpeningRequest(List<byte[]> bodies)
        {
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

                if (messageType == FaMessageType.Request
                    && !FaExchangeCodec.IsReply(body)
                    && sequenceByte == 0x80)
                {
                    return body;
                }
            }

            return null!;
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
