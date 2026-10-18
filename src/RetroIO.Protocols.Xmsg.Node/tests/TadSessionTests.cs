//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Node.Tad;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Byte-identical builder proofs for the TAD session state machine. Each expected value is
    /// a frame lifted verbatim from <c>2026-04-09-1242-tad-connect-to-d102-from-d100.pcapng</c> (OBSERVED, single
    /// capture), so these tests do not depend on the capture being present at run time.
    /// </summary>
    /// <remarks>
    /// Frame references (by ordinal in the decoded connect-to) are noted per test. The client
    /// direction is 100->102; the server direction is 102->100.
    /// </remarks>
    public sealed class TadSessionTests
    {
        /// <summary>
        /// The client XROUT XSLET setup letter to "D102" (capture frame #1) is reproduced
        /// byte-for-byte by the client builder.
        /// </summary>
        [Fact]
        public void Client_BuildsXroutSetupLetter_ByteIdentical()
        {
            // Capture frame #1: 100 -> 102, XMCSM 0x04000041 (XSLET), names "D102".
            byte[] expected = LiveTestHex.Parse(
                "21 13 00 0E 00 66 00 64 00 46 04 00 D9 CE 21 00 86 E4 00 66 00 00 00 64 02 88 " +
                "04 00 00 41 00 10 FF 07 2A 54 41 44 41 44 4D 00 FE 04 44 31 30 32");

            TadFrameContext ctx = new TadFrameContext
            {
                DestinationNode = 102,
                SourceNode = 100,
                DatagramSequence = 0x0046,
                FrameClass = 0x0400,
                ProtocolId = (SintranProtocolId)0xD9,
                Counter = 0xCE,
                FrameFlags = 0x86,
                Role = 0xE4,
                DestinationSystem = 102,
                DestinationPort = 0x0000,
                SourceSystem = 100,
                SourcePort = 0x0288,
                ControlService = TadSession.XroutSetupControlService,
            };

            TadSession client = new TadSession(TadSessionRole.Client);
            XmsgFrame frame = client.BuildXroutSetupFrame(ctx, "D102");

            Assert.Equal(expected, frame.ToArray());
        }

        /// <summary>
        /// The server reset request RESE (capture frame #67) is reproduced byte-for-byte by
        /// the control-frame builder.
        /// </summary>
        [Fact]
        public void Server_BuildsRese_ByteIdentical()
        {
            // Capture frame #67: 102 -> 100, TAD trailer 16 00 (RESE, count 0).
            byte[] expected = LiveTestHex.Parse(
                "21 13 00 0E 00 64 00 66 00 4A 01 08 DC C2 21 00 96 00 00 64 02 88 00 66 03 41 " +
                "01 08 00 00 00 02 16 00");

            TadFrameContext ctx = new TadFrameContext
            {
                DestinationNode = 100,
                SourceNode = 102,
                DatagramSequence = 0x004A,
                FrameClass = 0x0108,
                ProtocolId = SintranProtocolId.Dc,
                Counter = 0xC2,
                FrameFlags = 0x96,
                Role = 0x00,
                DestinationSystem = 100,
                DestinationPort = 0x0288,
                SourceSystem = 102,
                SourcePort = 0x0341,
                ControlService = 0x01080000u,
            };

            TadSession server = new TadSession(TadSessionRole.Server);
            XmsgFrame frame = server.BuildControlFrame(ctx, TadOp.Rese, ReadOnlySpan<byte>.Empty);

            Assert.Equal(expected, frame.ToArray());
        }

        /// <summary>
        /// The client reset confirm RECO (capture frame #10) is reproduced byte-for-byte,
        /// completing the RESE->RECO exchange proof.
        /// </summary>
        [Fact]
        public void Client_BuildsReco_ByteIdentical()
        {
            // Capture frame #10: 100 -> 102, TAD trailer 17 00 (RECO, count 0).
            byte[] expected = LiveTestHex.Parse(
                "21 13 00 0E 00 66 00 64 00 4A 01 08 DC C2 21 00 96 94 00 66 03 41 00 64 02 88 " +
                "01 08 00 00 00 02 17 00");

            TadFrameContext ctx = new TadFrameContext
            {
                DestinationNode = 102,
                SourceNode = 100,
                DatagramSequence = 0x004A,
                FrameClass = 0x0108,
                ProtocolId = SintranProtocolId.Dc,
                Counter = 0xC2,
                FrameFlags = 0x96,
                Role = 0x94,
                DestinationSystem = 102,
                DestinationPort = 0x0341,
                SourceSystem = 100,
                SourcePort = 0x0288,
                ControlService = 0x01080000u,
            };

            TadSession client = new TadSession(TadSessionRole.Client);
            XmsgFrame frame = client.BuildControlFrame(ctx, TadOp.Reco, ReadOnlySpan<byte>.Empty);

            Assert.Equal(expected, frame.ToArray());
        }

        /// <summary>
        /// The replay server reproduces the captured secure delivery ACK (capture frame #65)
        /// byte-for-byte for the client frame it acknowledges (frame #7).
        /// </summary>
        [Fact]
        public void Server_ProducesReachableAck_ByteIdentical()
        {
            // Client frame #7: 100 -> 102, data (ESCA), datagram sequence 0x0049, proto 0xDD.
            byte[] clientInfo = LiveTestHex.Parse(
                "21 13 00 0E 00 66 00 64 00 49 00 08 DD C3 21 00 82 94 00 66 03 41 00 64 02 88 " +
                "00 08 00 00 00 02 08 00");

            // Captured ACK #65: 102 -> 100, echoes seq 0x0049, counter 0xD5.
            byte[] expectedAck = LiveTestHex.Parse("21 13 00 03 00 64 00 66 00 49 00 01 DD D5");

            XmsgFrame client = XmsgFrame.Parse(clientInfo);
            TadReplayServer server = new TadReplayServer(ackCounter: 0xD5);

            TadReplayResult result = server.Handle(client);

            Assert.NotNull(result.Ack);
            Assert.Equal(expectedAck, result.Ack!.ToArray());
        }

        /// <summary>
        /// The replay server replays a recorded response for an exact recorded input, and
        /// refuses to fabricate one for an input it never saw.
        /// </summary>
        [Fact]
        public void Server_RepliesToRecordedInput_AndRefusesUnknown()
        {
            byte[] setupLetter = LiveTestHex.Parse(
                "21 13 00 0E 00 66 00 64 00 46 04 00 D9 CE 21 00 86 E4 00 66 00 00 00 64 02 88 " +
                "04 00 00 41 00 10 FF 07 2A 54 41 44 41 44 4D 00 FE 04 44 31 30 32");

            // Server frame #60: 102 -> 100, XSLET response letter.
            byte[] setupReply = LiveTestHex.Parse(
                "21 13 00 0E 00 64 00 66 00 46 04 00 D9 CE 21 00 86 40 00 64 02 88 00 66 01 56 " +
                "04 00 00 41 00 08 01 02 00 00 02 02 00 0A");

            byte[] unknownInput = LiveTestHex.Parse(
                "21 13 00 0E 00 66 00 64 00 49 00 08 DD C3 21 00 82 94 00 66 03 41 00 64 02 88 " +
                "00 08 00 00 00 02 08 00");

            TadReplayServer server = new TadReplayServer(ackCounter: 0xD8);
            server.Record(setupLetter, setupReply);

            TadReplayResult known = server.Handle(XmsgFrame.Parse(setupLetter));
            Assert.True(known.HasRecordedResponse);
            Assert.Equal(setupReply, known.RecordedResponse!.ToArray());

            TadReplayResult unknown = server.Handle(XmsgFrame.Parse(unknownInput));
            Assert.False(unknown.HasRecordedResponse);
            Assert.Contains("INFERRED", unknown.Note);
        }

        /// <summary>
        /// Observing the negotiation frames captures the terminal parameters: terminal type
        /// <c>0x0000</c>, escape character <c>0x1B</c>, and OS/proto version <c>4C 01 04</c>.
        /// </summary>
        [Fact]
        public void Observe_CapturesNegotiatedParameters()
        {
            // Client frame #6: 100 -> 102, TAD chain TMOD(08) TTYP(00 00) DESC(1B) OPSV(4C 01 04).
            byte[] negotiation = LiveTestHex.Parse(
                "21 13 00 0E 00 66 00 64 00 48 01 08 DC C4 21 00 86 84 00 66 03 41 00 64 02 88 " +
                "01 08 00 00 00 0F 0C 01 08 0D 02 00 00 0F 01 1B 1F 03 4C 01 04");

            TadSession client = new TadSession(TadSessionRole.Client);
            client.Observe(XmsgFrame.Parse(negotiation));

            Assert.Equal(TadSessionState.Negotiating, client.State);
            Assert.True(client.Parameters.TerminalType.HasValue);
            Assert.Equal((ushort)0x0000, client.Parameters.TerminalType!.Value);
            Assert.True(client.Parameters.EscapeCharacter.HasValue);
            Assert.Equal((byte)0x1B, client.Parameters.EscapeCharacter!.Value);
            Assert.Equal(new byte[] { 0x4C, 0x01, 0x04 }, client.Parameters.GetOsVersionCopy());
            Assert.True(client.Parameters.TerminalMode.HasValue);
            Assert.Equal((byte)0x08, client.Parameters.TerminalMode!.Value);
        }
    }
}
