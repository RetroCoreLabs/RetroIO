using System;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Packet;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Verifies the universal envelope channel derivation (<see cref="XmsgEnvelope.DeriveChannel"/>)
    /// against the captured <c>conn-to-d102-from-100</c> responder frames and our own live-accepted
    /// accept. The channel is DERIVED from Flags1 + Counter + XMCSM, not allocated - this is the
    /// primitive that lets the responder build session-data frames on the correct channel.
    /// </summary>
    public sealed class EnvelopeChannelTests
    {
        // Each row: Flags1, Counter, XMCSM, expected Protocol ID - taken directly from the capture
        // (102 -> 100 responder frames) and from our live accept.
        [Theory]
        // Capture conn-to-d102-from-100, 102's responder side:
        [InlineData(0x012F, 0xE5, 0x04000041u, (byte)SintranProtocolId.D8)]    // frame 51 connect-accept
        [InlineData(0x0130, 0xE4, 0x04000000u, (byte)SintranProtocolId.D8)]    // frame 53 port-assign
        [InlineData(0x0131, 0xDB, 0x01080000u, (byte)SintranProtocolId.Db)]    // frame 54 DUMM
        [InlineData(0x0132, 0xDA, 0x00080000u, (byte)SintranProtocolId.Dc)]    // frame 57 control 0x20
        [InlineData(0x0135, 0xD7, 0x01080000u, (byte)SintranProtocolId.Db)]    // frame 62 MOTD
        [InlineData(0x0137, 0xD5, 0x01080000u, (byte)SintranProtocolId.Db)]    // frame 66 PASSWORD prompt
        // Our own live session accept (Base 0x0014) - 100 accepted this exact channel:
        [InlineData(0x0004, 0x10, 0x04000041u, (byte)SintranProtocolId.Pad)]   // live accept -> DA
        public void DeriveChannel_MatchesCapturedResponderFrames(int flags1, int counter, uint xmcsm, byte expected)
        {
            SintranProtocolId channel = XmsgEnvelope.DeriveChannel((ushort)flags1, (byte)counter, xmcsm);
            Assert.Equal((SintranProtocolId)expected, channel);
        }

        /// <summary>
        /// The seed is learned from any received frame: seed = (Counter + Flags1 + (Flags2 AND 0xFF)).
        /// </summary>
        [Fact]
        public void LearnSeed_RecoversTheLinkSeed()
        {
            // Live 100 connect (100-102): Flags1 0x0000, Counter 0x14, Flags2 0x0400 -> seed 0x14.
            Assert.Equal(0x14, XmsgEnvelope.LearnSeed(0x0000, 0x14, 0x0400));
            // Captured conn-to-d102 DUMM (frame 54): Flags1 0x0131, Counter 0xDB, Flags2 0x0108 -> 0x14.
            Assert.Equal(0x14, XmsgEnvelope.LearnSeed(0x0131, 0xDB, 0x0108));
            // 100-103 connect: Counter 0x13, Flags1 0x0000 -> seed 0x13.
            Assert.Equal(0x13, XmsgEnvelope.LearnSeed(0x0000, 0x13, 0x0400));
        }

        /// <summary>
        /// The full seed model (Counter + channel) for the predicted live node-102 bring-up
        /// (100 to/from 102 seed 0x14), and for captured frames spanning epochs 0/1/2. Each row:
        /// Flags1, Flags2, XMCSM, expected Counter, expected channel.
        /// </summary>
        [Theory]
        // Predicted live bring-up (seed 0x14) - the analysis result table:
        [InlineData(0x14, 0x0000, 0x0400, 0x04000041u, 0x14, (byte)SintranProtocolId.Pad)]  // accept  -> DA
        [InlineData(0x14, 0x0001, 0x0400, 0x04000000u, 0x13, (byte)SintranProtocolId.Pad)]  // port-assign -> DA
        [InlineData(0x14, 0x0002, 0x0108, 0x01080000u, 0x0A, (byte)SintranProtocolId.Tad)]  // DUMM    -> DD
        [InlineData(0x14, 0x0003, 0x0008, 0x00080000u, 0x09, (byte)SintranProtocolId.Routing)] // ctrl 0x20 -> DE
        [InlineData(0x14, 0x0004, 0x0108, 0x01080000u, 0x08, (byte)SintranProtocolId.Tad)]  // RESE    -> DD
        [InlineData(0x14, 0x0005, 0x0108, 0x01080000u, 0x07, (byte)SintranProtocolId.Tad)]  // MOTD    -> DD
        // Captured frames (seed 0x14) at higher epochs:
        [InlineData(0x14, 0x0131, 0x0108, 0x01080000u, 0xDB, (byte)SintranProtocolId.Db)]   // frame 54 DUMM  epoch 2 -> DB
        [InlineData(0x14, 0x00FA, 0x0108, 0x01080000u, 0x12, (byte)SintranProtocolId.Dc)]   // frame 6  (100) epoch 1 -> DC
        [InlineData(0x14, 0x012F, 0x0400, 0x04000041u, 0xE5, (byte)SintranProtocolId.D8)]   // frame 51 accept epoch 2 -> D8
        public void SeedModel_ComputesCounterAndChannel(int seed, int flags1, int flags2, uint xmcsm, int expectedCounter, byte expectedChannel)
        {
            Assert.Equal((byte)expectedCounter, XmsgEnvelope.ComputeCounter((byte)seed, (ushort)flags1, (ushort)flags2));
            Assert.Equal((SintranProtocolId)expectedChannel, XmsgEnvelope.DeriveChannel((byte)seed, (ushort)flags1, (ushort)flags2, xmcsm));
        }

        /// <summary>
        /// Base is Flags1 + Counter, and within one stream Flags1up / Counterdown keep it constant.
        /// </summary>
        [Fact]
        public void ComputeBase_IsConstantAcrossAStream()
        {
            // Capture frames 54 and 57 belong to the same session-data stream; both have Base 0x020C.
            Assert.Equal(0x020C, XmsgEnvelope.ComputeBase(0x0131, 0xDB));
            Assert.Equal(0x020C, XmsgEnvelope.ComputeBase(0x0132, 0xDA));
            Assert.Equal(0x020C, XmsgEnvelope.ComputeBase(0x0135, 0xD7));
        }

        /// <summary>
        /// The session-data builder derives the channel and reproduces the captured DUMM (frame 54)
        /// and MOTD (frame 62) responder frames byte-for-byte - proof the builder can emit the real
        /// bring-up frames once the session's Flags1/Counter stream is chosen.
        /// </summary>
        [Fact]
        public void CreateSessionData_ReproducesCapturedDummAndMotd()
        {
            // Frame 54: DUMM on the derived DB channel.
            XmsgDataFields dumm = new XmsgDataFields
            {
                DestinationNode = 0x0064,
                SourceNode = 0x0066,
                Flags1 = 0x0131,
                Flags2 = 0x0108,
                ChecksumLow = 0xDB,
                FrameFlags = 0x92,
                Role = 0x00,
                DestinationSystem = 0x0064,
                DestinationPort = 0x02AB,
                SourceSystem = 0x0066,
                SourcePort = 0x04C2,
                XmcsmWithBodyWord = 0x01080000u,
                XroutPayload = Convert.FromHexString("1800"),   // TAD DUMM (0x18) count=0
            };
            Assert.Equal(
                Convert.FromHexString("2113000E0064006601310108DBDB21009200006402AB006604C20108000000021800"),
                XmsgPacketBuilder.CreateSessionData(dumm).ToBytes());

            // Frame 62: the combined terminal-setup + MOTD chain on the derived DB channel.
            XmsgDataFields motd = new XmsgDataFields
            {
                DestinationNode = 0x0064,
                SourceNode = 0x0066,
                Flags1 = 0x0135,
                Flags2 = 0x0108,
                ChecksumLow = 0xD7,
                FrameFlags = 0x96,
                Role = 0x00,
                DestinationSystem = 0x0064,
                DestinationPort = 0x02AB,
                SourceSystem = 0x0066,
                SourcePort = 0x04C2,
                XmcsmWithBodyWord = 0x01080000u,
                XroutPayload = Convert.FromHexString(
                    "0004030100000003010101600D0A2032322E32372E32322020202020203820415052494C202020"
                    + "313939380D0A2053494E5452414E20494949202D205653582F353030204C0D0A2D2D2D2052455452"
                    + "4F434F524520454D554C41544544204C2049443A313032202D2D2D0D0A1302000201080D0A454E54"
                    + "4552200200"),
            };
            Assert.Equal(
                Convert.FromHexString(
                    "2113000E0064006601350108DBD721009600006402AB006604C201080000007C0004030100000003"
                    + "010101600D0A2032322E32372E32322020202020203820415052494C202020313939380D0A205349"
                    + "4E5452414E20494949202D205653582F353030204C0D0A2D2D2D20524554524F434F524520454D55"
                    + "4C41544544204C2049443A313032202D2D2D0D0A1302000201080D0A454E544552200200"),
                XmsgPacketBuilder.CreateSessionData(motd).ToBytes());
        }
    }
}
