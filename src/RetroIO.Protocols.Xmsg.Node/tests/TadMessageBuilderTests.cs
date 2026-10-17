//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Node.Tad;
using RetroIO.Protocols.Xmsg.SubProtocol;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Proves the typed TAD message API encodes each message correctly and, critically, keeps the
    /// chain word-aligned (spec TAD-Message-Formats.md section 1) - the missing alignment pad after an
    /// odd-length BDAT is what hung odd-length terminal replies (the menu and the Echo reply).
    /// </summary>
    public sealed class TadMessageBuilderTests
    {
        /// <summary>
        /// An odd-length BDAT is followed by a 0x00 pad so the RFI starts on an even (word) offset.
        /// </summary>
        [Fact]
        public void OddLengthBdat_InsertsPadBeforeRfi()
        {
            // "\r\nIPSUM LORUM\r\n# " is 17 bytes (odd) - the Echo reply that hung.
            byte[] chain = new TadMessageBuilder()
                .BdatText("\r\nIPSUM LORUM\r\n# ")
                .Rfi()
                .Build();

            // BDAT = opcode(0x01) + count(0x11=17) + 17 data = 19 bytes (offset 0..18), so the chain
            // length is 19 (odd). A 0x00 pad is inserted, then RFI (0x02 0x00).
            Assert.Equal(0x01, chain[0]);
            Assert.Equal(0x11, chain[1]);        // count = 17
            Assert.Equal(0x00, chain[19]);       // alignment pad
            Assert.Equal(0x02, chain[20]);       // RFI opcode on an even offset
            Assert.Equal(0x00, chain[21]);       // RFI count = 0
            Assert.Equal(22, chain.Length);
        }

        /// <summary>
        /// An even-length BDAT needs no pad: the RFI already starts on an even offset.
        /// </summary>
        [Fact]
        public void EvenLengthBdat_NoPadBeforeRfi()
        {
            // "\r\n22:27:22\r\n# " is 14 bytes (even) - a reply that worked.
            byte[] chain = new TadMessageBuilder()
                .BdatText("\r\n22:27:22\r\n# ")
                .Rfi()
                .Build();

            Assert.Equal(0x01, chain[0]);
            Assert.Equal(0x0E, chain[1]);        // count = 14
            Assert.Equal(0x02, chain[16]);       // RFI directly at offset 16 - NO pad
            Assert.Equal(0x00, chain[17]);
            Assert.Equal(18, chain.Length);
        }

        /// <summary>
        /// SYCN encodes its login state as a big-endian 16-bit value.
        /// </summary>
        [Fact]
        public void Sycn_EncodesBigEndianState()
        {
            byte[] chain = new TadMessageBuilder().Sycn(SycnState.LoggedIn).Build();
            Assert.Equal(new byte[] { 0x13, 0x02, 0x00, 0x0A }, chain);   // opcode, count=2, 0x000A
        }

        /// <summary>
        /// The verified section 21 logged-in steady-state tail (SYCN 000A + BDAT "R@" + RFI) is built
        /// byte-for-byte by the API - a parity check against the documented capture.
        /// </summary>
        [Fact]
        public void LoggedInPromptTail_MatchesCapturedBytes()
        {
            byte[] chain = new TadMessageBuilder()
                .Sycn(SycnState.LoggedIn)
                .Bdat(new byte[] { 0x52, 0x40 })   // the verbatim prompt bytes "R@"
                .Rfi()
                .Build();

            // 13 02 000A | 01 02 5240 | 02 00 - all even-aligned, no pads (spec section 21).
            Assert.Equal(new byte[] { 0x13, 0x02, 0x00, 0x0A, 0x01, 0x02, 0x52, 0x40, 0x02, 0x00 }, chain);
        }

        /// <summary>
        /// The single-byte and multi-byte control messages encode their fields correctly.
        /// </summary>
        [Fact]
        public void ControlMessages_EncodeFields()
        {
            Assert.Equal(new byte[] { 0x0E, 0x01, 0x00 }, new TadMessageBuilder().Cesc(CescState.EscapeDisabled).Build());
            // ECKM and BMMX carry the intrinsic 0x00 prefix on the wire (spec 2.1 / 22.3).
            Assert.Equal(new byte[] { 0x00, 0x03, 0x01, 0xFF }, new TadMessageBuilder().Eckm(EchoStrategy.NoEcho).Build());
            Assert.Equal(new byte[] { 0x00, 0x04, 0x03, 0x01, 0x00, 0x00 }, new TadMessageBuilder().Bmmx(0x01, 0x0000).Build());
            Assert.Equal(new byte[] { 0x02, 0x00 }, new TadMessageBuilder().Rfi().Build());
            Assert.Equal(new byte[] { 0x18, 0x00 }, new TadMessageBuilder().Dumm().Build());
        }

        /// <summary>
        /// A built chain round-trips through the decoder to the same messages, with pads skipped.
        /// </summary>
        [Fact]
        public void BuiltChain_RoundTripsThroughParser()
        {
            byte[] chain = new TadMessageBuilder()
                .BdatText("\r\nIPSUM LORUM\r\n# ")   // odd -> forces a pad before the RFI
                .Rfi()
                .Build();

            TadChain parsed = TadChain.Parse(chain);

            Assert.Equal(2, parsed.Messages.Count);           // pad is skipped, not a message
            Assert.Equal(0x01, parsed.Messages[0].Opcode);    // BDAT
            Assert.Equal(17, parsed.Messages[0].Data.Length);
            Assert.Equal(0x02, parsed.Messages[1].Opcode);    // RFI
            Assert.Empty(parsed.Messages[1].Data);
        }

        /// <summary>
        /// Data longer than 255 bytes cannot fit the single-byte count and is rejected.
        /// </summary>
        [Fact]
        public void DataOver255_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new TadMessageBuilder().Bdat(new byte[256]));
        }

        /// <summary>
        /// The typed API rebuilds the real captured MOTD banner byte-for-byte - proving the intrinsic
        /// 0x00-prefix (ECKM/BMMX) plus the even-offset alignment model reproduces the wire exactly.
        /// </summary>
        [Fact]
        public void MotdBanner_RebuiltByApi_MatchesCapturedBytes()
        {
            byte[] motd = Convert.FromHexString(
                "0004030100000003010101600D0A2032322E32372E32322020202020203820415052494C202020313939380D0A"
                + "2053494E5452414E20494949202D205653582F353030204C0D0A2D2D2D20524554524F434F524520454D554C4154"
                + "4544204C2049443A313032202D2D2D0D0A1302000201080D0A454E544552200200");

            // The 96-byte banner text sits between the ECKM message and the SYCN (offsets 12..107).
            byte[] banner = new byte[96];
            Array.Copy(motd, 12, banner, 0, 96);

            byte[] rebuilt = new TadMessageBuilder()
                .Bmmx(0x01, 0x0000)                 // 00 04 03 01 0000
                .Eckm(0x01)                         // 00 03 01 01
                .Bdat(banner)                       // 01 60 <96 banner bytes>
                .Sycn(SycnState.WaitingForUsername) // 13 02 0002
                .BdatText("\r\nENTER ")             // 01 08 0D0A "ENTER "
                .Rfi()                              // 02 00
                .Build();

            Assert.Equal(motd, rebuilt);
        }

        /// <summary>
        /// The real captured MOTD greeting decodes to the expected message sequence, ending at SYCN
        /// 0002 (waiting for username) - validating our decode against a genuine capture payload.
        /// </summary>
        [Fact]
        public void CapturedMotd_DecodesToExpectedMessages()
        {
            // The verbatim MOTD payload from conn-to-d102 frame 62 (a real node-102 greeting).
            byte[] motd = Convert.FromHexString(
                "0004030100000003010101600D0A2032322E32372E32322020202020203820415052494C202020313939380D0A"
                + "2053494E5452414E20494949202D205653582F353030204C0D0A2D2D2D20524554524F434F524520454D554C4154"
                + "4544204C2049443A313032202D2D2D0D0A1302000201080D0A454E544552200200");

            TadChain parsed = TadChain.Parse(motd);

            // Collect the opcodes in order (pads are skipped by the parser).
            List<byte> opcodes = new List<byte>();
            for (int i = 0; i < parsed.Messages.Count; i++)
            {
                opcodes.Add(parsed.Messages[i].Opcode);
            }

            // BMMX, ECKM, BDAT(banner), SYCN, BDAT(ENTER prompt), RFI.
            Assert.Equal(new byte[] { 0x04, 0x03, 0x01, 0x13, 0x01, 0x02 }, opcodes.ToArray());

            // The SYCN state is 0x0002 = waiting for username (the greeting puts 100 at the login prompt).
            TadMessage sycn = parsed.Messages[3];
            Assert.Equal(0x13, sycn.Opcode);
            Assert.Equal(0x00, sycn.Data[0]);
            Assert.Equal(0x02, sycn.Data[1]);
        }
    }
}
