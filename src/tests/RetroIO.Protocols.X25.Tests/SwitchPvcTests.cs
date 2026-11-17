//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RetroIO.Protocols.X25.Common;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Permanent virtual circuits through the switch (RetroX25 PLAN C2): the configuration the loader accepts and
    /// refuses, the join, data both ways with no call, and X.25 4.6 when one end's interface goes out of order and comes
    /// back. Expected causes are X.25 Table 5-8, not another switch's output.
    /// </summary>
    [TestFixture]
    public class SwitchPvcTests
    {
        private const string TwoPorts =
            "[port.0]\ntype = access\ntransport = hdlc-tcp\nlci_base = 16\npvc_base = 1\npvc_count = 8\n" +
            "[port.1]\ntype = access\ntransport = hdlc-tcp\nlci_base = 16\npvc_base = 1\npvc_count = 8\n";

        private static SwitchConfig Load(string text)
        {
            return SwitchConfigLoader.Load(IniConfig.Parse(text.Split('\n')));
        }

        [Test]
        public void APvc_IsLoadedWithItsAccount()
        {
            SwitchConfig sw = Load(TwoPorts + "[pvc.0]\nsrc_port = 0\nsrc_lci = 1\ndst_port = 1\ndst_lci = 2\naccount = leased\n");

            Assert.That(sw.Pvcs.Count, Is.EqualTo(1));
            Assert.That(sw.Pvcs[0].SourcePort, Is.EqualTo(0));
            Assert.That(sw.Pvcs[0].SourceLci, Is.EqualTo((ushort)1));
            Assert.That(sw.Pvcs[0].DestPort, Is.EqualTo(1));
            Assert.That(sw.Pvcs[0].DestLci, Is.EqualTo((ushort)2));
            Assert.That(sw.Pvcs[0].Account, Is.EqualTo("leased"));
        }

        [TestCase("[pvc.0]\nsrc_port = 0\nsrc_lci = 1\ndst_port = 5\ndst_lci = 1\n", "not a configured port")]
        [TestCase("[pvc.0]\nsrc_port = 0\nsrc_lci = 9\ndst_port = 1\ndst_lci = 1\n", "PVC channels")]
        [TestCase("[pvc.0]\nsrc_port = 0\nsrc_lci = 1\ndst_port = 0\ndst_lci = 1\n", "itself")]
        [TestCase("[pvc.0]\nsrc_port = 0\nsrc_lci = 1\ndst_port = 1\ndst_lci = 1\n[pvc.1]\nsrc_port = 0\nsrc_lci = 1\ndst_port = 1\ndst_lci = 2\n",
            "already joins")]
        public void ABadPvc_IsRefused(string pvc, string because)
        {
            InvalidDataException e = Assert.Throws<InvalidDataException>(() => Load(TwoPorts + pvc))!;
            Assert.That(e.Message, Does.Contain(because));
        }

        [Test]
        public void PvcChannelsInsideTheCallRange_AreRefused()
        {
            // X.25 Annex A keeps the PVC channels apart from the virtual call channels; lci_base 1 with 16 channels
            // covers 1..16, which pvc 1..8 overlaps.
            InvalidDataException e = Assert.Throws<InvalidDataException>(
                () => Load("[port.0]\ntype = access\ntransport = hdlc-tcp\npvc_count = 8\n"))!;
            Assert.That(e.Message, Does.Contain("overlap"));
        }

        private const string XotInitiatorAndHdlc =
            "[port.0]\ntype = access\ntransport = xot\nlci_base = 16\npvc_count = 8\nxot_pvc_role = initiator\n" +
            "[port.1]\ntype = access\ntransport = hdlc-tcp\nlci_base = 16\npvc_count = 8\n";

        [Test]
        public void APvcOnAnXotInitiatorPort_WithoutTheFarEnd_IsRefused()
        {
            InvalidDataException e = Assert.Throws<InvalidDataException>(() => Load(XotInitiatorAndHdlc
                + "[pvc.0]\nsrc_port = 0\nsrc_lci = 1\ndst_port = 1\ndst_lci = 1\n"))!;
            Assert.That(e.Message, Does.Contain("remote_host, remote_iface and remote_lci"));
        }

        [Test]
        public void APvcOnAnXotInitiatorPort_WithTheFarEnd_IsLoaded()
        {
            SwitchConfig sw = Load(XotInitiatorAndHdlc
                + "[pvc.0]\nsrc_port = 0\nsrc_lci = 1\ndst_port = 1\ndst_lci = 1\nremote_host = 10.0.0.2\nremote_iface = Serial1\nremote_lci = 5\n");
            Assert.That(sw.Pvcs.Count, Is.EqualTo(1));
        }

        [Test]
        public void APvcOnAnXotResponderPort_NeedsNoFarEnd()
        {
            SwitchConfig sw = Load(
                "[port.0]\ntype = access\ntransport = xot\nlci_base = 16\npvc_count = 8\nxot_pvc_role = responder\n" +
                "[port.1]\ntype = access\ntransport = hdlc-tcp\nlci_base = 16\npvc_count = 8\n" +
                "[pvc.0]\nsrc_port = 0\nsrc_lci = 1\ndst_port = 1\ndst_lci = 1\n");
            Assert.That(sw.Pvcs.Count, Is.EqualTo(1));
        }

        [TestCase("initiator", true)]
        [TestCase("Responder", false)]
        [TestCase("", false)]
        public void TheXotPvcRole_IsReadWithTheInterfaceName(string role, bool initiator)
        {
            SwitchConfig sw = Load("[port.0]\ntype = access\ntransport = hdlc-tcp\nlci_base = 16\niface_name = Serial0\nxot_pvc_role = " + role + "\n");
            Assert.That(sw.Ports[0].XotPvcInitiator, Is.EqualTo(initiator));
            Assert.That(sw.Ports[0].InterfaceName, Is.EqualTo("Serial0"));
        }

        [TestCase("client")]
        [TestCase("server")]
        public void AnyOtherRoleWord_IsRefused(string role)
        {
            // X.25 has no client or server; RFC 1613 6.5 has initiator and responder, and nothing else is guessed.
            InvalidDataException e = Assert.Throws<InvalidDataException>(
                () => Load("[port.0]\ntype = access\ntransport = hdlc-tcp\nlci_base = 16\nxot_pvc_role = " + role + "\n"))!;
            Assert.That(e.Message, Does.Contain("initiator or responder"));
        }

        [Test]
        public void ThePvcsRemoteInterfaceAndChannel_AreRead()
        {
            SwitchConfig sw = Load(TwoPorts
                + "[pvc.0]\nsrc_port = 0\nsrc_lci = 1\ndst_port = 1\ndst_lci = 2\nremote_iface = S1\nremote_lci = 5\nremote_host = 10.0.0.2\n");
            Assert.That(sw.Pvcs[0].RemoteHost, Is.EqualTo("10.0.0.2"));
            Assert.That(sw.Pvcs[0].RemoteInterface, Is.EqualTo("S1"));
            Assert.That(sw.Pvcs[0].RemoteLci, Is.EqualTo((ushort)5));
        }

        private sealed class Rig
        {
            public readonly SwitchNode Node;
            public readonly SwitchCore Core;
            public readonly LoopbackSwitchPort A;
            public readonly LoopbackSwitchPort B;

            public Rig(string account, int recordMinutes = 15)
            {
                SwitchConfig cfg = new SwitchConfig { Dnic = "2340", PvcRecordMinutes = recordMinutes };
                cfg.Pvcs.Add(new PvcConfig { SourcePort = 0, SourceLci = 1, DestPort = 1, DestLci = 2, Account = account });
                if (account.Length > 0)
                {
                    cfg.Accounts.Add(new AccountConfig { Name = account });
                }

                Node = new SwitchNode(cfg);
                A = new LoopbackSwitchPort(new PortConfig { Id = 0, LciBase = 16 });
                B = new LoopbackSwitchPort(new PortConfig { Id = 1, LciBase = 16 });
                Node.AddPort(A);
                Node.AddPort(B);
                A.Start();
                B.Start();
                Core = new SwitchCore(Node);
            }
        }

        [Test]
        public void ThePvcIsJoinedAtStart_AndCarriesDataBothWaysWithNoCall()
        {
            Rig r = new Rig("leased");

            Assert.That(r.A.Pvcs, Is.EqualTo(new[] { (ushort)1 }));
            Assert.That(r.B.Pvcs, Is.EqualTo(new[] { (ushort)2 }));

            r.A.InjectData(1, new byte[] { 1, 2, 3 });
            r.B.InjectData(2, new byte[] { 9 });

            Assert.That(r.B.SentData.Count, Is.EqualTo(1));
            Assert.That(r.B.SentData[0].Lci, Is.EqualTo((ushort)2));
            Assert.That(r.B.SentData[0].Data, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(r.A.SentData.Count, Is.EqualTo(1));
            Assert.That(r.A.SentData[0].Lci, Is.EqualTo((ushort)1));
            Assert.That(r.A.PlacedCalls.Count + r.B.PlacedCalls.Count, Is.EqualTo(0), "no call is placed (X.25 4.2)");

            // Billed to the configured account as the traffic passes: 3 octets and 1 octet, one segment each (D.12).
            SwitchAccount acct = r.Node.Accounts[0];
            Assert.That(acct.PacketsSent, Is.EqualTo(1ul));
            Assert.That(acct.BytesSent, Is.EqualTo(3ul));
            Assert.That(acct.SegmentsSent, Is.EqualTo(1ul));
            Assert.That(acct.SegmentsReceived, Is.EqualTo(1ul));
        }

        [Test]
        public void APortGoingDown_ResetsTheFarEndOutOfOrder_AndNeverClears()
        {
            Rig r = new Rig(string.Empty);

            r.A.Stop();

            // X.25 4.6.3: "a reset with the cause 'Out of order' for each permanent virtual circuit" - Table 5-8, 0x01.
            Assert.That(r.B.ResetCircuits.Count, Is.EqualTo(1));
            Assert.That(r.B.ResetCircuits[0].Lci, Is.EqualTo((ushort)2));
            Assert.That(r.B.ResetCircuits[0].Cause, Is.EqualTo((byte)0x01));
            Assert.That(r.A.ClearedCalls.Count + r.B.ClearedCalls.Count, Is.EqualTo(0));
            Assert.That(r.Node.SnapshotStitches().Count, Is.EqualTo(1), "the join outlives the outage");
        }

        [Test]
        public void DataFromTheFarEndDuringTheOutage_IsAnsweredWithOutOfOrder()
        {
            Rig r = new Rig(string.Empty);
            r.A.Stop();
            r.B.ResetCircuits.Clear();

            r.B.InjectData(2, new byte[] { 7 });

            // 4.6.4 (2): reset "Out of order", and the packet goes nowhere.
            Assert.That(r.B.ResetCircuits.Count, Is.EqualTo(1));
            Assert.That(r.B.ResetCircuits[0].Cause, Is.EqualTo((byte)0x01));
            Assert.That(r.A.SentData.Count, Is.EqualTo(0));
        }

        [Test]
        public void ThePortComingBack_ResetsTheFarEndRemoteDteOperational_AndDataFlowsAgain()
        {
            Rig r = new Rig(string.Empty);
            r.A.Stop();
            r.B.ResetCircuits.Clear();

            r.A.Start();

            // 4.6.5 (2): "a reset with the cause 'Remote DTE operational'" - Table 5-8, 0x09.
            Assert.That(r.B.ResetCircuits.Count, Is.EqualTo(1));
            Assert.That(r.B.ResetCircuits[0].Cause, Is.EqualTo((byte)0x09));

            r.B.InjectData(2, new byte[] { 5 });
            Assert.That(r.A.SentData.Count, Is.EqualTo(1));
        }

        [Test]
        public void TheFirstStart_IsNotARecovery()
        {
            // Nothing was out of order before the port first came up, so the far end is not reset.
            Rig r = new Rig(string.Empty);
            Assert.That(r.A.ResetCircuits.Count + r.B.ResetCircuits.Count, Is.EqualTo(0));
        }

        [Test]
        public void AResetFromOneEnd_GoesToTheOther()
        {
            Rig r = new Rig(string.Empty);

            r.A.InjectReset(1, 0x00, 0x21);

            Assert.That(r.B.ResetCircuits.Count, Is.EqualTo(1));
            Assert.That(r.B.ResetCircuits[0].Lci, Is.EqualTo((ushort)2));
            Assert.That(r.B.ResetCircuits[0].Diag, Is.EqualTo((byte)0x21));
        }

        [Test]
        public void AnInterruptOnAPvc_ReachesTheOtherEnd()
        {
            // X.25 4.3.7: the interrupt procedure applies in d1 within p4 "on the virtual call or permanent virtual
            // circuit"; a PVC is in p4 from the start (4.2).
            Rig r = new Rig(string.Empty);

            r.A.InjectInterrupt(1, new byte[] { 0x42 });
            r.B.InjectInterrupt(2, new byte[] { 0x43 });

            Assert.That(r.B.SentInterrupts.Count, Is.EqualTo(1));
            Assert.That(r.B.SentInterrupts[0].Lci, Is.EqualTo((ushort)2));
            Assert.That(r.B.SentInterrupts[0].UserData, Is.EqualTo(new byte[] { 0x42 }));
            Assert.That(r.A.SentInterrupts.Count, Is.EqualTo(1));
            Assert.That(r.A.SentInterrupts[0].Lci, Is.EqualTo((ushort)1));
        }

        [Test]
        public void AnInterruptFromTheFarEndDuringAnOutage_IsAnsweredWithOutOfOrder()
        {
            // 4.6.4 (2): "data or interrupt" from the remote DTE while the interface is out of order is reset, cause
            // "Out of order" (Table 5-8, 0x01), and goes nowhere.
            Rig r = new Rig(string.Empty);
            r.A.Stop();
            r.B.ResetCircuits.Clear();

            r.B.InjectInterrupt(2, new byte[] { 0x44 });

            Assert.That(r.B.ResetCircuits.Count, Is.EqualTo(1));
            Assert.That(r.B.ResetCircuits[0].Cause, Is.EqualTo((byte)0x01));
            Assert.That(r.A.SentInterrupts.Count, Is.EqualTo(0));
        }

        [Test]
        public void ResetsFromBothEnds_EachReachTheOtherEnd()
        {
            Rig r = new Rig(string.Empty);

            r.A.InjectReset(1, 0x00, 0x10);
            r.B.InjectReset(2, 0x00, 0x11);

            Assert.That(r.B.ResetCircuits.Count, Is.EqualTo(1));
            Assert.That(r.B.ResetCircuits[0].Diag, Is.EqualTo((byte)0x10));
            Assert.That(r.A.ResetCircuits.Count, Is.EqualTo(1));
            Assert.That(r.A.ResetCircuits[0].Diag, Is.EqualTo((byte)0x11));
            Assert.That(r.Node.SnapshotStitches().Count, Is.EqualTo(1), "a reset never ends a PVC");
        }

        [Test]
        public void APvcIsBilledInPeriods_EachLineCountingOnlyItsOwnPeriod()
        {
            // Worked by hand (D.12: 64-octet segments, a message rounded up once at its category B packet):
            //  period 1: 3 octets forward = 1 segment; 100 octets back = ceil(100/64) = 2 segments.
            //  period 2: a full 128-octet packet with M=1 (category A) opens a message; nothing completes - 0 segments.
            //  period 3: 1 more octet completes it: 129 octets = ceil(129/64) = 3 segments, charged here.
            Rig r = new Rig("leased", recordMinutes: 1);
            List<Cdr> lines = new List<Cdr>();
            r.Core.CdrRecorded += c => { lock (lines) { lines.Add(c); } };
            long start = r.Node.SnapshotStitches()[0].StartTimeMs;
            long now = start;
            r.Core.ClockMs = () => now;

            r.A.InjectData(1, new byte[3]);
            r.B.InjectData(2, new byte[100]);
            now = start + 60000;
            r.Core.WritePvcRecordsDue();

            r.A.InjectData(1, new byte[128], mBit: true);
            now = start + 120000;
            r.Core.WritePvcRecordsDue();

            r.A.InjectData(1, new byte[1]);
            now = start + 150000;
            r.Core.WritePvcRecordsDue();   // only 30 s into period 3: nothing due

            Assert.That(lines.Count, Is.EqualTo(2));
            Assert.That(lines[0].StartTimeMs, Is.EqualTo(start));
            Assert.That(lines[0].EndTimeMs, Is.EqualTo(start + 60000));
            Assert.That(lines[0].BytesForward, Is.EqualTo(3u));
            Assert.That(lines[0].BytesReverse, Is.EqualTo(100u));
            Assert.That(lines[0].SegmentsForward, Is.EqualTo(1ul));
            Assert.That(lines[0].SegmentsReverse, Is.EqualTo(2ul));
            Assert.That(lines[0].Account, Is.EqualTo("leased"));
            Assert.That(lines[0].ClearCause, Is.EqualTo((byte)0));
            Assert.That(lines[1].StartTimeMs, Is.EqualTo(start + 60000), "period 2 starts where period 1 ended");
            Assert.That(lines[1].BytesForward, Is.EqualTo(128u));
            Assert.That(lines[1].SegmentsForward, Is.EqualTo(0ul), "the open message is not charged yet");

            // A reset ends period 3 with its own line: cause and diagnostic of the reset, the 3 segments of the message.
            r.A.InjectReset(1, 0x00, 0x21);
            Assert.That(lines.Count, Is.EqualTo(3));
            Assert.That(lines[2].SegmentsForward, Is.EqualTo(3ul));
            Assert.That(lines[2].ClearDiagnostic, Is.EqualTo((byte)0x21));
            Assert.That(r.Node.Accounts[0].TotalDurationMs, Is.EqualTo(150000ul), "the periods add up to the whole time");

            // And the account's totals are the sum of its lines: the read-out never shows more or less than the record
            // file holds. By hand: segments forward 1 + 0 + 3 = 4, back 2 + 0 + 0 = 2; bytes forward 3 + 128 + 1 = 132,
            // back 100.
            SwitchAccount leased = r.Node.Accounts[0];
            ulong segsF = 0, segsR = 0, bytesF = 0, bytesR = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                segsF += lines[i].SegmentsForward;
                segsR += lines[i].SegmentsReverse;
                bytesF += lines[i].BytesForward;
                bytesR += lines[i].BytesReverse;
            }

            Assert.That(segsF, Is.EqualTo(4ul));
            Assert.That(segsR, Is.EqualTo(2ul));
            Assert.That(bytesF, Is.EqualTo(132ul));
            Assert.That(bytesR, Is.EqualTo(100ul));
            Assert.That(leased.SegmentsSent, Is.EqualTo(segsF));
            Assert.That(leased.SegmentsReceived, Is.EqualTo(segsR));
            Assert.That(leased.BytesSent, Is.EqualTo(bytesF));
            Assert.That(leased.BytesReceived, Is.EqualTo(bytesR));
        }

        [Test]
        public void AResetDropsAMessageStillOpen()
        {
            // X.25 4.4: a reset discards what is in flight, so a message it cuts off is never completed and never charged.
            Rig r = new Rig("leased", recordMinutes: 0);
            List<Cdr> lines = new List<Cdr>();
            r.Core.CdrRecorded += c => { lock (lines) { lines.Add(c); } };

            r.A.InjectData(1, new byte[128], mBit: true);
            r.A.InjectReset(1, 0x00, 0);
            r.A.InjectData(1, new byte[1]);
            r.A.InjectReset(1, 0x00, 0);

            Assert.That(lines.Count, Is.EqualTo(2));
            Assert.That(lines[0].SegmentsForward, Is.EqualTo(0ul));
            Assert.That(lines[1].SegmentsForward, Is.EqualTo(1ul), "only the 1 octet after the reset: ceil(1/64) = 1");
        }

        [Test]
        public void APortGoingDown_EndsThePeriodWithOutOfOrder()
        {
            Rig r = new Rig("leased", recordMinutes: 0);
            List<Cdr> lines = new List<Cdr>();
            r.Core.CdrRecorded += c => { lock (lines) { lines.Add(c); } };

            r.A.Stop();

            Assert.That(lines.Count, Is.EqualTo(1));
            Assert.That(lines[0].ClearCause, Is.EqualTo((byte)0x01), "reset cause Out of order, Table 5-8");
        }

        [Test]
        public void PvcRecordMinutes_IsReadAndANegativeValueRefused()
        {
            Assert.That(Load("[switch]\npvc_record_minutes = 5\n").PvcRecordMinutes, Is.EqualTo(5));
            Assert.That(Load("[switch]\n").PvcRecordMinutes, Is.EqualTo(15), "x25emu's default");
            Assert.Throws<InvalidDataException>(() => Load("[switch]\npvc_record_minutes = -1\n"));
        }

        [Test]
        public void APvcWhoseAccountIsNotConfigured_StopsTheSwitch()
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340" };
            cfg.Pvcs.Add(new PvcConfig { SourcePort = 0, SourceLci = 1, DestPort = 1, DestLci = 2, Account = "nobody" });
            SwitchNode node = new SwitchNode(cfg);
            node.AddPort(new LoopbackSwitchPort(new PortConfig { Id = 0, LciBase = 16 }));
            node.AddPort(new LoopbackSwitchPort(new PortConfig { Id = 1, LciBase = 16 }));

            Assert.Throws<InvalidOperationException>(() => new SwitchCore(node));
        }
    }
}
