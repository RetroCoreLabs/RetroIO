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
    /// The host's 0xFD session notice must not make the asker hang up; only the owner's
    /// <see cref="TadAskerSession.Hangup"/> sends DCON.
    /// </summary>
    /// <remarks>
    /// The host frames are the real bytes from <c>src/pcap-decode-sample.json</c> (list index = record
    /// number): every host data frame to asker node 100 port 02E3 in records 1307-1341, in Flags1
    /// order. Host 102, session port 0313, *TADADM port 0156. Record 1327 is the first FD (right after
    /// the "@" prompt), 1341 the second (after "--EXIT--"). The real asker's last frame is the DCON
    /// in record 1260.
    /// </remarks>
    public sealed class TadAskerSessionNoticeTests
    {
        /// <summary>
        /// Host data frames to 100/02E3, records 1307-1326 (accept up to the "@" prompt).
        /// </summary>
        private static readonly string[] BeforeFirstNotice =
        {
            "2113000E0064006600040400DA1021008640006402E300660156040000410008010200000202000A", // 1307
            "2113000E0064006600050400DA0F21008640006402E30066015604000000001800070500006603131F034C0000000B02030015020108FF00", // 1309
            "2113000E0064006600060108DD0621009200006402E3006603130108000000021800", // 1310
            "2113000E0064006600070008DE0521008600006402E3006603130008000000022000", // 1313
            "2113000E0064006600080108DD0421009600006402E3006603130108000000021600", // 1314
            "2113000E0064006600090108DD0321009200006402E3006603130108000000021600", // 1316
            "2113000E00640066000A0108DD0221009600006402E30066031301080000007C00040301000000030101015F0D0A2031362E33312E333820202020202035204A554C59202020313939380D0A2053494E5452414E20494949202D205653582F353030204C0D0A2D2D2D20524554524F434F524520454D554C41544544204C2049443A313032202D2D2D0D0A001302000201080D0A454E544552200200", // 1318
            "2113000E00640066000B0108DD0121009600006402E30066031301080000000B01020D0A130200030E0100", // 1320
            "2113000E00640066000C0108DD0021009200006402E300660313010800000012010A50415353574F52443A20000301FF0200", // 1322
            "2113000E00640066000D0108DCFF21009600006402E30066031301080000001301020D0A0003010101024F4B130200060E0101", // 1324 "OK"
            "2113000E00640066000E0108DCFE21009600006402E30066031301080000000E01020D0A1302000A010252400200", // 1326 "@"
        };

        /// <summary>
        /// Record 1327: the first FD, from *TADADM port 0156.
        /// </summary>
        private const string FirstNotice = "2113000E00640066000F0006DDFF21008254006402E300660156000600000002FD00";

        /// <summary>
        /// Host data frames to 100/02E3 between the two FDs, records 1329-1338 (more text, "--EXIT--").
        /// </summary>
        private static readonly string[] BetweenNotices =
        {
            "2113000E0064006600100108DCFC21009600006402E30066031301080000003A010F0D0A22434F4E454E454354494F4E22001302000C01170D0A4E4F20535543482046494C45204E414D450D0A0D0A001302000A010252400200", // 1329
            "2113000E0064006600110108DCFB21009600006402E30066031301080000003C01110D0A22444953434F4E4E454354494E4722001302000C01170D0A4E4F20535543482046494C45204E414D450D0A0D0A001302000A010252400200", // 1331
            "2113000E0064006600120108DCFA21009600006402E300660313010800000023011E0D0A2031362E33312E353820202020202035204A554C59202020313939380E0100", // 1333
            "2113000E0064006600130108DCF921009600006402E30066031301080000000D000403000000000301000E0100", // 1335
            "2113000E0064006600140108DCF821009600006402E300660313010800000012010C0D0A2D2D455849542D2D0D0A1302000B", // 1337 "--EXIT--"
            "2113000E0064006600150108DCF721009600006402E3006603130108000000030E0101", // 1338
        };

        /// <summary>
        /// Record 1341: the second FD, byte-identical to 1327 apart from Flags1.
        /// </summary>
        private const string SecondNotice = "2113000E0064006600160006DDF821008254006402E300660156000600000002FD00";

        /// <summary>
        /// Record 1260: the real asker's DCON to session port 0313, from the frame flags on (sub-header
        /// offset 2): flags 0x82, role 0x94, dst 0066/0313, src 0064/02E3, XMCSM 0x0008, body
        /// <c>00 00 00 02 09 00</c>.
        /// </summary>
        private const string CapturedDconFromFrameFlags = "829400660313006402E30008000000020900";

        /// <summary>
        /// Drives the asker through the captured session 100/02E3 and checks that neither FD hangs up,
        /// that the session keeps working between them, and that <see cref="TadAskerSession.Hangup"/>
        /// gives exactly the captured DCON.
        /// </summary>
        [Fact]
        public void HostSessionNotice_DoesNotHangUp_HangupSendsCapturedDcon()
        {
            TadAskerSession asker = new TadAskerSession(100, 102, 0x02E3, seed: 0x14, "D102");
            StringBuilder text = new StringBuilder();
            asker.TerminalText += t => text.Append(t);
            int notices = 0;
            asker.SessionNoticeReceived += () => notices++;
            asker.Start();

            for (int i = 0; i < BeforeFirstNotice.Length; i++)
            {
                AssertNoDcon(asker.OnReceive(Parse(BeforeFirstNotice[i])), "before the first FD, frame " + i);
            }

            Assert.Contains("@", text.ToString());

            // First FD (record 1327): acked, reported, no DCON.
            IReadOnlyList<XmsgFrame> afterFirst = asker.OnReceive(Parse(FirstNotice));
            AssertNoDcon(afterFirst, "after the first FD");
            Assert.Single(afterFirst);
            Assert.Equal(SintranPacketSubtype.Ack, afterFirst[0].Header!.Subtype);
            Assert.Equal(1, notices);
            Assert.Equal(1, asker.SessionNoticeCount);

            // The user can still type (the real user typed "log", records 1246-1250).
            IReadOnlyList<XmsgFrame> line = asker.SendLine("log");
            Assert.Single(line);
            Assert.Equal(0x0313, line[0].SubHeader!.DestinationPort);

            // The session still takes the host's text.
            text.Clear();
            for (int i = 0; i < BetweenNotices.Length; i++)
            {
                AssertNoDcon(asker.OnReceive(Parse(BetweenNotices[i])), "between the FDs, frame " + i);
            }

            Assert.Contains("--EXIT--", text.ToString());

            // Second FD (record 1341): no automatic DCON either.
            AssertNoDcon(asker.OnReceive(Parse(SecondNotice)), "after the second FD");
            Assert.Equal(2, notices);
            Assert.Equal(2, asker.SessionNoticeCount);

            // The owner hangs up: exactly one DCON, shaped like record 1260.
            IReadOnlyList<XmsgFrame> hangup = asker.Hangup();
            Assert.Single(hangup);
            Assert.True(IsDcon(hangup[0]), "Hangup must produce a DCON");
            byte[] bytes = hangup[0].ToArray();
            string fromFrameFlags = Convert.ToHexString(bytes, 16, bytes.Length - 16);
            Assert.Equal(CapturedDconFromFrameFlags, fromFrameFlags);
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
            return XmsgFrame.Parse(Convert.FromHexString(hex));
        }

        /// <summary>
        /// Fails when any of the frames is a DCON.
        /// </summary>
        /// <param name="frames">
        /// The asker's response frames.
        /// </param>
        /// <param name="where">
        /// Where in the session this was, for the failure message.
        /// </param>
        private static void AssertNoDcon(IReadOnlyList<XmsgFrame> frames, string where)
        {
            for (int i = 0; i < frames.Count; i++)
            {
                Assert.False(IsDcon(frames[i]), "The asker sent DCON " + where);
            }
        }

        /// <summary>
        /// Returns true when the frame's TAD chain holds a DCON.
        /// </summary>
        /// <param name="frame">
        /// The frame to inspect.
        /// </param>
        /// <returns>
        /// True when the frame carries DCON.
        /// </returns>
        private static bool IsDcon(XmsgFrame frame)
        {
            // A frame we built carries its TAD chain only as body bytes, so decode its wire bytes the
            // same way a receiver would before looking at the opcodes.
            XmsgFrame onWire = XmsgFrame.Parse(frame.ToArray());
            if (onWire.Tad == null)
            {
                return false;
            }

            IReadOnlyList<TadMessage> messages = onWire.Tad.Messages;
            for (int i = 0; i < messages.Count; i++)
            {
                if ((TadOp)messages[i].Opcode == TadOp.Dcon)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
