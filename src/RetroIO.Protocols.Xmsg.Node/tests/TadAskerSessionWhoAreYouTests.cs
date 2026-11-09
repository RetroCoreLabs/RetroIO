//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;

using RetroIO.Protocols.Xmsg.Node.Tad;
using RetroIO.Protocols.Xmsg.SubProtocol;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// The host's 0xFC "who are you" (SINTRAN 7WHO) during a running terminal session must be
    /// answered with 7IAM. The host's TADA command waits for that answer and hangs forever without it.
    /// </summary>
    /// <remarks>
    /// Real bytes from <c>src/pcap-decode-sample.json</c> (list index = record number). Record 3653 is
    /// the question: host 0x67 *TADADM port 04DA to asker 0x66 port 05DA, flags 0x82, role 0x14,
    /// control word 0x0048, body <c>00 00 00 02 FC 00</c>. Record 3618 is the real asker's answer:
    /// flags 0x86, role 0x84, back to 04DA from 05DA, control word 0x0048, body
    /// <c>00 00 00 3F | 28 00 | 0A 21 + 33 bytes "D102'"+zeros | 00 | 0B 02 00 01 | 10 11 + 17 bytes
    /// "SYSTEM'"+zeros | FF 00</c>.
    /// The question is moved onto the session the notice test uses (asker 100/02E3, host 102,
    /// *TADADM port 0156): only the systems, ports, Flags1 and header checksum change. The checksum
    /// rule (both nodes + Flags1 + control word + checksum = 0xDEDE) was read off the captured frames.
    /// The 33-byte name field is the ASKER'S OWN system name (in 3618 the asker is system 0x66 = 102,
    /// hence "D102"). Measured live 2026-11-09 on RetroCore D100: a C asker on node 19999 answered
    /// 7WHO with "D19999" and TADA printed the row "777/ 0 BAK07 Enab 14 - 1 1 REMOTE D19999" -
    /// the name in the SYSTEM column, the 0x0B field's 1 in TERMNO, the user name in USER. So here,
    /// with the asker on node 100, the field is "D100'" + zeros (33 bytes). The terminal word 0001
    /// and the user name "SYSTEM'" are kept as in 3618: this session logs in as SYSTEM and is the
    /// asker's first terminal, which is what the C# would have to report.
    /// </remarks>
    public sealed class TadAskerSessionWhoAreYouTests
    {
        /// <summary>
        /// Record 3653 moved to 100/02E3 from 102/0156: header Flags1 0x000F, checksum 0xDDBD.
        /// </summary>
        private const string WhoAreYou = "2113000E00640066000F0048DDBD21008214006402E300660156004800000002FC00";

        /// <summary>
        /// Record 3618 from the frame flags on, moved to dst 0066/0156, src 0064/02E3.
        /// </summary>
        private const string ExpectedIamFromFrameFlags =
            "8684006601560064" + "02E30048" + "0000003F" + "2800" +
            "0A21" + "443130302700000000000000000000000000000000000000000000000000000000" + "00" +
            "0B020001" +
            "1011" + "53595354454D2700000000000000000000" + "FF00";

        /// <summary>
        /// Drives the asker to the "@" prompt, feeds the 7WHO and checks that one of the frames it
        /// sends back is the captured 7IAM.
        /// </summary>
        [Fact]
        public void HostAsksWhoAreYou_AskerAnswersWithIam()
        {
            TadAskerSession asker = new TadAskerSession(100, 102, 0x02E3, seed: 0x14, "D102");
            StringBuilder text = new StringBuilder();
            asker.TerminalText += t => text.Append(t);
            asker.Start();

            // The same accept-to-prompt frames as TadAskerSessionNoticeTests (records 1307-1326).
            string[] beforeFirstNotice =
            {
                "2113000E0064006600040400DA1021008640006402E300660156040000410008010200000202000A",
                "2113000E0064006600050400DA0F21008640006402E30066015604000000001800070500006603131F034C0000000B02030015020108FF00",
                "2113000E0064006600060108DD0621009200006402E3006603130108000000021800",
                "2113000E0064006600070008DE0521008600006402E3006603130008000000022000",
                "2113000E0064006600080108DD0421009600006402E3006603130108000000021600",
                "2113000E0064006600090108DD0321009200006402E3006603130108000000021600",
                "2113000E00640066000A0108DD0221009600006402E30066031301080000007C00040301000000030101015F0D0A2031362E33312E333820202020202035204A554C59202020313939380D0A2053494E5452414E20494949202D205653582F353030204C0D0A2D2D2D20524554524F434F524520454D554C41544544204C2049443A313032202D2D2D0D0A001302000201080D0A454E544552200200",
                "2113000E00640066000B0108DD0121009600006402E30066031301080000000B01020D0A130200030E0100",
                "2113000E00640066000C0108DD0021009200006402E300660313010800000012010A50415353574F52443A20000301FF0200",
                "2113000E00640066000D0108DCFF21009600006402E30066031301080000001301020D0A0003010101024F4B130200060E0101",
                "2113000E00640066000E0108DCFE21009600006402E30066031301080000000E01020D0A1302000A010252400200",
            };
            for (int i = 0; i < beforeFirstNotice.Length; i++)
            {
                asker.OnReceive(XmsgFrame.Parse(Convert.FromHexString(beforeFirstNotice[i])));
            }

            Assert.Contains("@", text.ToString());

            IReadOnlyList<XmsgFrame> reply = asker.OnReceive(XmsgFrame.Parse(Convert.FromHexString(WhoAreYou)));

            // Write down every frame sent back, whole, so the failure message shows what the C# did.
            StringBuilder sent = new StringBuilder();
            string? iam = null;
            for (int i = 0; i < reply.Count; i++)
            {
                byte[] bytes = reply[i].ToArray();
                sent.Append("frame ").Append(i).Append(" (").Append(reply[i].Header!.Subtype).Append("): ")
                    .Append(Convert.ToHexString(bytes)).Append('\n');
                if (bytes.Length > 16 && Convert.ToHexString(bytes, 16, bytes.Length - 16) == ExpectedIamFromFrameFlags)
                {
                    iam = Convert.ToHexString(bytes);
                }
            }

            Assert.True(iam != null, "No 7IAM in the reply to 7WHO. The asker sent " + reply.Count + " frame(s):\n" + sent);
        }

        /// <summary>
        /// TADADM opens a fresh port on every TADA run, so a second 7WHO from a different port must be
        /// answered to THAT port, not to the first one. The second question is the first one again with
        /// the *TADADM port 0157 instead of 0156 and the next Flags1 (0x0010, checksum 0xDDBC by the
        /// same 0xDEDE rule; the ports are not in the header checksum). Each answer is the ACK and the
        /// 7IAM and nothing else: no DUMM for a 7WHO.
        /// </summary>
        [Fact]
        public void SecondWhoFromAnotherPort_IsAnsweredToThatPort()
        {
            TadAskerSession asker = new TadAskerSession(100, 102, 0x02E3, seed: 0x14, "D102");
            asker.Start();

            IReadOnlyList<XmsgFrame> first = asker.OnReceive(XmsgFrame.Parse(Convert.FromHexString(WhoAreYou)));
            Assert.Equal(1, asker.WhoAnswerCount);
            Assert.Equal(2, first.Count);
            Assert.Equal(SintranPacketSubtype.Ack, first[0].Header!.Subtype);
            byte[] firstBytes = first[1].ToArray();
            Assert.Equal(ExpectedIamFromFrameFlags, Convert.ToHexString(firstBytes, 16, firstBytes.Length - 16));

            const string SecondWho = "2113000E006400660010" + "0048DDBC" + "21008214006402E300660157004800000002FC00";
            IReadOnlyList<XmsgFrame> second = asker.OnReceive(XmsgFrame.Parse(Convert.FromHexString(SecondWho)));

            Assert.Equal(2, second.Count);
            Assert.Equal(2, asker.WhoAnswerCount);
            Assert.Equal(SintranPacketSubtype.Ack, second[0].Header!.Subtype);
            XmsgSubHeader sub = second[1].SubHeader!;
            Assert.Equal((ushort)0x0157, sub.DestinationPort);
            Assert.Equal((ushort)0x02E3, sub.SourcePort);
            string expected = "8684006601570064" + ExpectedIamFromFrameFlags.Substring(16);
            byte[] bytes = second[1].ToArray();
            Assert.Equal(expected, Convert.ToHexString(bytes, 16, bytes.Length - 16));
        }
    }
}
