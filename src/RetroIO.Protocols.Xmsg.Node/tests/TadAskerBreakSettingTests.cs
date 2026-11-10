//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Node.Tad;
using RetroIO.Protocols.Xmsg.SubProtocol;
using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Checks the break setting rule of <see cref="TadAskerSession.SendLine"/> against the RetroServer
    /// hub capture of 2026-11-10: D102 runs CONNECT-TO D100, starts S3-CONFIG and presses the TDV key
    /// SLUTT (ESC [ 4 8 _). D100 sends BMMX strategy 0 and the real D102 asker answers each RFI with
    /// one byte.
    /// </summary>
    public sealed class TadAskerBreakSettingTests
    {
        /// <summary>
        /// Hub frame 1001 (11:55:32.403), D100 to D102 session 0985 to 07EA: pad, BMMX strategy 0
        /// BRKMAX 0, pad, ECKM FF, RFI.
        /// </summary>
        private const string GrantStrategy0 = "2113000E0066006400760108DC9621009600006607EA0064098501080000000C000403000000000301FF0200";

        /// <summary>
        /// Hub frame 1009 (11:55:32.812): the same grant again, one Flags1 on.
        /// </summary>
        private const string GrantStrategy0Again = "2113000E0066006400770108DC9521009600006607EA0064098501080000000C000403000000000301FF0200";

        /// <summary>
        /// In S3-CONFIG the key goes one byte per grant, and nothing goes without a grant.
        /// </summary>
        [Fact]
        public void Strategy0_SendsOneBytePerGrant()
        {
            TadAskerSession asker = new TadAskerSession(102, 100, 0x07EA, seed: 0x14, "D100");
            asker.Start();
            Assert.Equal(1, asker.BreakStrategy);

            // No grant yet: the key waits.
            Assert.Empty(asker.SendLine("\u001B[48_"));
            Assert.Equal(5, asker.PendingInputLength);

            IReadOnlyList<XmsgFrame> first = asker.OnReceive(Parse(GrantStrategy0));
            Assert.Equal(0, asker.BreakStrategy);
            Assert.Equal(new byte[] { 0x1B }, BdatOf(first));
            Assert.Equal(4, asker.PendingInputLength);

            IReadOnlyList<XmsgFrame> second = asker.OnReceive(Parse(GrantStrategy0Again));
            Assert.Equal(new byte[] { 0xDB }, BdatOf(second)); // '[' with even parity, as D102 sent it
            Assert.Equal(3, asker.PendingInputLength);
        }

        /// <summary>
        /// At SINTRAN's default (strategy 1) a line goes whole up to its CR, and the CR ends the letter.
        /// </summary>
        [Fact]
        public void Strategy1_SendsUpToTheControlCharacter()
        {
            TadAskerSession asker = new TadAskerSession(102, 100, 0x07EA, seed: 0x14, "D100");
            asker.Start();
            const string Rfi = "2113000E0066006400760108DC9621009600006607EA0064098501080000000C000403010000000301010200";
            IReadOnlyList<XmsgFrame> grant = asker.OnReceive(Parse(Rfi));
            Assert.Equal(1, asker.BreakStrategy);
            Assert.Null(BdatOf(grant));

            IReadOnlyList<XmsgFrame> line = asker.SendLine("SYSTEM\rmore");
            // SYSTEM + CR with even parity, byte for byte as D102 sent it (hub frame 903, 11:55:05).
            Assert.Equal(new byte[] { 0x53, 0x59, 0x53, 0xD4, 0xC5, 0x4D, 0x8D }, BdatOf(line));
            Assert.Equal(4, asker.PendingInputLength);
        }

        /// <summary>
        /// With parity off the bytes go as given.
        /// </summary>
        [Fact]
        public void ParityOff_SendsBytesAsGiven()
        {
            TadAskerSession asker = new TadAskerSession(102, 100, 0x07EA, seed: 0x14, "D100");
            asker.KeystrokeParity = false;
            asker.Start();
            asker.OnReceive(Parse(GrantStrategy0));
            Assert.Equal(new byte[] { 0x43 }, BdatOf(asker.SendLine("C"))); // 0xC3 with parity
        }

        /// <summary>
        /// S3-CONFIG's screen letter sets strategy 7 with BRKMAX 1 and a table of all ones (hub frame
        /// 989, XMSGGW line 13:16:00.271), so the first byte of a key goes alone.
        /// </summary>
        [Fact]
        public void Strategy7AllOnes_SendsOneByte()
        {
            TadAskerSession asker = new TadAskerSession(102, 100, 0x07EA, seed: 0x14, "D100");
            asker.Start();
            const string Chain = "0413070001FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00031107FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF0200";
            string frame = "2113000E0066006400760108DC9621009600006607EA00640985010800000"
                + (Chain.Length / 2).ToString("X3") + Chain;
            IReadOnlyList<XmsgFrame> grant = asker.OnReceive(Parse(frame));
            Assert.Equal(7, asker.BreakStrategy);
            Assert.Null(BdatOf(grant));

            IReadOnlyList<XmsgFrame> first = asker.SendLine("\u001B[48_");
            Assert.Equal(new byte[] { 0x1B }, BdatOf(first));
            Assert.Equal(4, asker.PendingInputLength);
        }

        /// <summary>
        /// Parses one captured frame from hex.
        /// </summary>
        /// <param name="hex">
        /// The raw frame bytes as hex.
        /// </param>
        /// <returns>
        /// The decoded frame.
        /// </returns>
        private static XmsgFrame Parse(string hex)
        {
            return XmsgFrame.Parse(System.Convert.FromHexString(hex));
        }

        /// <summary>
        /// Returns the BDAT data of the one outgoing frame that carries one, or null when none does.
        /// </summary>
        /// <param name="frames">
        /// The asker's response frames.
        /// </param>
        /// <returns>
        /// The BDAT bytes, or null.
        /// </returns>
        private static byte[]? BdatOf(IReadOnlyList<XmsgFrame> frames)
        {
            byte[]? found = null;
            for (int i = 0; i < frames.Count; i++)
            {
                // A frame we built carries its TAD chain only as body bytes; decode its wire bytes.
                XmsgFrame onWire = XmsgFrame.Parse(frames[i].ToArray());
                if (onWire.Tad == null)
                {
                    continue;
                }

                IReadOnlyList<TadMessage> messages = onWire.Tad.Messages;
                for (int j = 0; j < messages.Count; j++)
                {
                    if ((TadOp)messages[j].Opcode == TadOp.Bdat)
                    {
                        Assert.Null(found);
                        found = messages[j].Data;
                    }
                }
            }

            return found;
        }
    }
}
