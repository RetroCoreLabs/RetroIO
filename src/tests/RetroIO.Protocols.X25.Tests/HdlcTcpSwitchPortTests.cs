//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using RetroIO.Protocols.X25.Client;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The HDLC trunk port on real sockets on loopback: LAPB over 0x7E framing on TCP, both roles, with and without
    /// faults on the line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every test builds its own pair of ports on operating-system chosen ports, so they do not collide with each
    /// other or with anything on the machine. The LAPB retransmission timer T1 is 500 ms here, not 3000, so a
    /// recovery that has to wait for T1 does not make the suite slow; nothing else is changed from the defaults.
    /// </para>
    /// <para>
    /// The faults come from <see cref="HdlcFaultProxy"/>, a relay between the two ends that drops, repeats or damages
    /// chosen frames. It sees the real bytes of the real link, so what these tests prove is LAPB's own recovery and
    /// the FCS check, not a model of them.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class HdlcTcpSwitchPortTests
    {
        private static byte[] Numbered(int n, int length = 20)
        {
            byte[] b = new byte[length];
            b[0] = (byte)(n >> 24);
            b[1] = (byte)(n >> 16);
            b[2] = (byte)(n >> 8);
            b[3] = (byte)n;
            for (int i = 4; i < length; i++)
            {
                b[i] = (byte)(n + i);
            }

            return b;
        }

        private static int NumberOf(byte[] b)
        {
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }

        private static string Dump(List<string> lines)
        {
            lock (lines)
            {
                return string.Join("\n", lines);
            }
        }

        private static int CountOf(List<ReceivedData> list)
        {
            lock (list)
            {
                return list.Count;
            }
        }

        // ------------------------------------------------------------------ link up and the restart

        /// <summary>
        /// The link comes up with a SABM from the dialling end and a UA from the listening end, and both then restart X.25.
        /// </summary>
        [Test]
        public void TheLinkComesUp_WithSabmAndUa_AndBothEndsRestartX25()
        {
            using TrunkPair pair = TrunkPair.Create(rule: f => FrameFate.Pass);
            pair.WaitBothUp();

            Assert.That(SpinWait.SpinUntil(() => TrunkPair.Count(pair.DteOut, X25PacketType.RestartRequest) == 1 && TrunkPair.Count(pair.DceOut, X25PacketType.RestartRequest) == 1, 5000),
                Is.True, "each end sends one Restart Request when LAPB is up");

            // Both Restart Requests crossed, so each end took it as a restart collision (X.25 4.1.2) and completed;
            // no Restart Confirmation is expected from either.
            HdlcLinkSnapshot dce = pair.Dce.GetLinkSnapshot();
            HdlcLinkSnapshot dte = pair.Dte.GetLinkSnapshot();
            Assert.That(dce.LapbState, Is.EqualTo("DataTransfer"));
            Assert.That(dte.LapbState, Is.EqualTo("DataTransfer"));
            Assert.That(dce.LinkUps, Is.EqualTo(1));
            Assert.That(dte.LinkUps, Is.EqualTo(1));
            Assert.That(dce.LinkResets, Is.EqualTo(0), "a first set-up is not a reset");
            Assert.That(dte.LinkResets, Is.EqualTo(0));
            Assert.That(pair.Dte.LastConnectedMs, Is.Not.EqualTo(0));
            Assert.That(pair.Dte.ConnectAttempts, Is.EqualTo(1));
            Assert.That(pair.Dce.ConnectAttempts, Is.EqualTo(1), "the listening end counts the connection it accepted");

            // The first frame each way, read off the line: the DTE's SABM, then the DCE's UA. Both use address 0x01
            // (X.25 2.4.2: a command from the DTE and a response from the DCE carry B = 0x01).
            List<ProxiedFrame> seen = pair.Proxy!.Seen();
            ProxiedFrame firstUp = seen.Find(f => f.Direction == TrunkDirection.ToDce)!;
            ProxiedFrame firstDown = seen.Find(f => f.Direction == TrunkDirection.ToDte)!;
            Assert.That(firstUp.Control, Is.EqualTo(0x3F), "SABM with the poll bit");
            Assert.That(firstUp.Address, Is.EqualTo(0x01));
            Assert.That(firstDown.Control, Is.EqualTo(0x73), "UA with the final bit");
            Assert.That(firstDown.Address, Is.EqualTo(0x01));
        }

        /// <summary>
        /// A port reports itself not linked until LAPB is up, and linked after.
        /// </summary>
        [Test]
        public void ADtePortWhosePeerIsNotThere_IsNotLinked_AndCountsTheFailedDials()
        {
            TrunkListLogger log = new TrunkListLogger();
            int free;
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            free = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            using HdlcTcpSwitchPort dte = new HdlcTcpSwitchPort(TrunkPair.Config(1, PortMode.Dte, "127.0.0.1", (ushort)free), log);
            Assert.That(dte.Start(), Is.True, "a dialling port starts even when its peer is down: it keeps trying");
            Assert.That(SpinWait.SpinUntil(() => dte.ConnectFailures >= 1, 5000), Is.True);

            Assert.That(dte.PeerConnected, Is.False);
            Assert.That(dte.Active, Is.True);
            Assert.That(dte.LastError, Is.Not.Empty);
            Assert.That(dte.LastFailureMs, Is.Not.EqualTo(0));
            Assert.That(log.Contains("Warning: Port 1"), Is.True);
            Assert.That(log.Contains("failed"), Is.True);
            Assert.That(log.Contains("the next try is in 2 seconds"), Is.True, log.All());
            Assert.That(dte.PlaceCall("23420001", "23400001", null), Is.EqualTo(0), "no call can be placed on a trunk that is not linked");
        }

        // ------------------------------------------------------------------ data

        /// <summary>
        /// A call crosses the trunk and 128-byte packets and the Q, M and D bits arrive unchanged both ways.
        /// </summary>
        [Test]
        public void ACallCrossesTheTrunk_WithFullSizePacketsAndTheQMAndDBits_BothWays()
        {
            using TrunkPair pair = TrunkPair.Create();
            pair.WaitBothUp();

            ushort lci = pair.Call(pair.Dte);

            byte[] full = new byte[128];
            for (int i = 0; i < full.Length; i++)
            {
                full[i] = (byte)(i * 7);
            }

            // A full packet with the M bit says "more follows"; the next packet ends that sequence. The Q bit must not
            // change inside a sequence (X.25 4.3.3), so the Q packet comes after the sequence has ended.
            TrunkPair.SendAll(pair.Dte, lci, full, q: false, m: true, d: false);
            TrunkPair.SendAll(pair.Dte, lci, Numbered(1, 10), q: false, m: false, d: false);
            TrunkPair.SendAll(pair.Dte, lci, Numbered(2, 10), q: true, m: false, d: false);
            TrunkPair.SendAll(pair.Dte, lci, Numbered(3, 10), q: false, m: false, d: true);

            // The DCE end sees the call under the same channel number: nothing is renumbered on a trunk.
            Assert.That(SpinWait.SpinUntil(() => CountOf(pair.AtDce) == 4, 5000), Is.True,
                "arrived: " + CountOf(pair.AtDce) + "\nDCE log:\n" + pair.DceLog.All() + "\nDTE log:\n" + pair.DteLog.All());
            lock (pair.AtDce)
            {
                Assert.That(pair.AtDce[0].Lci, Is.EqualTo(lci));
                Assert.That(pair.AtDce[0].Data, Is.EqualTo(full));
                Assert.That(pair.AtDce[0].M, Is.True);
                Assert.That(pair.AtDce[0].Q, Is.False);
                Assert.That(pair.AtDce[1].M, Is.False);
                Assert.That(pair.AtDce[2].Q, Is.True);
                Assert.That(pair.AtDce[2].D, Is.False);
                Assert.That(pair.AtDce[3].D, Is.True);
                Assert.That(pair.AtDce[3].Q, Is.False);
            }

            TrunkPair.SendAll(pair.Dce, lci, full, q: false, m: true, d: false);
            TrunkPair.SendAll(pair.Dce, lci, Numbered(4, 10), q: false, m: false, d: false);
            TrunkPair.SendAll(pair.Dce, lci, Numbered(5, 10), q: true, m: false, d: false);
            Assert.That(SpinWait.SpinUntil(() => CountOf(pair.AtDte) == 3, 5000), Is.True);
            lock (pair.AtDte)
            {
                Assert.That(pair.AtDte[0].Data, Is.EqualTo(full));
                Assert.That(pair.AtDte[0].M, Is.True);
                Assert.That(pair.AtDte[2].Q, Is.True);
            }
        }

        /// <summary>
        /// A call placed from the DCE end and one placed from the DTE end take their channels from opposite ends of
        /// the range (X.25 Annex A), and the frames from each end carry the address of that end.
        /// </summary>
        [Test]
        public void BothRolesPlaceCalls_TheDceTakesTheLowChannel_TheDteTheHighChannel_AndEachUsesItsOwnAddress()
        {
            using TrunkPair pair = TrunkPair.Create(rule: f => FrameFate.Pass);
            pair.WaitBothUp();

            ushort fromDte = pair.Call(pair.Dte);
            ushort fromDce = pair.Call(pair.Dce);
            Assert.That(fromDte, Is.EqualTo(16), "a DTE searches from the highest channel of its range");
            Assert.That(fromDce, Is.EqualTo(1), "a DCE searches from the lowest");

            TrunkPair.SendAll(pair.Dte, fromDte, Numbered(1));
            TrunkPair.SendAll(pair.Dce, fromDce, Numbered(2));
            Assert.That(SpinWait.SpinUntil(() => CountOf(pair.AtDce) == 1 && CountOf(pair.AtDte) == 1, 5000), Is.True);

            // On the line: a DTE's information frames carry address 0x01 (a command from the DTE) and a DCE's carry
            // 0x03 (a command from the DCE). That pair is what decides which end is which for LAPB.
            List<ProxiedFrame> seen = pair.Proxy!.Seen();
            int fromDteChecked = 0;
            int fromDceChecked = 0;
            for (int i = 0; i < seen.Count; i++)
            {
                if (!seen[i].IsInformation)
                {
                    continue;
                }

                if (seen[i].Direction == TrunkDirection.ToDce)
                {
                    Assert.That(seen[i].Address, Is.EqualTo(0x01));
                    fromDteChecked++;
                }
                else
                {
                    Assert.That(seen[i].Address, Is.EqualTo(0x03));
                    fromDceChecked++;
                }
            }

            Assert.That(fromDteChecked, Is.GreaterThan(0));
            Assert.That(fromDceChecked, Is.GreaterThan(0));
        }

        /// <summary>
        /// More packets than the LAPB window and the 8 sequence numbers hold: 200 packets in one call arrive in
        /// order and exactly once, and the counters on the two ends agree with what was sent.
        /// </summary>
        [Test]
        public void TwoHundredPackets_WrapTheSequenceNumbers_ArriveInOrder_AndTheCountersMatch()
        {
            using TrunkPair pair = TrunkPair.Create();
            pair.WaitBothUp();
            ushort lci = pair.Call(pair.Dte);

            const int Total = 200;
            for (int i = 0; i < Total; i++)
            {
                TrunkPair.SendAll(pair.Dte, lci, Numbered(i));
            }

            Assert.That(SpinWait.SpinUntil(() => CountOf(pair.AtDce) == Total, 15000), Is.True, "only " + CountOf(pair.AtDce) + " arrived");
            lock (pair.AtDce)
            {
                for (int i = 0; i < Total; i++)
                {
                    Assert.That(NumberOf(pair.AtDce[i].Data), Is.EqualTo(i), "packet " + i + " is out of order or missing");
                }
            }

            // Let the last acknowledgements settle before the counters are compared, in BOTH directions and at both
            // layers. A packet is counted as sent when the port hands it to LAPB (HdlcSwitchPortBase.WritePacket), and
            // LAPB holds it until its window has room, so for a moment the packet count is one ahead of the frame count.
            // Waiting only for the DTE-to-DCE frames left the DCE's own acknowledgements in that state: 202 packets and
            // 201 frames, seen twice in full-suite runs on 2026-11-16.
            Assert.That(SpinWait.SpinUntil(() =>
            {
                HdlcLinkSnapshot dteNow = pair.Dte.GetLinkSnapshot();
                HdlcLinkSnapshot dceNow = pair.Dce.GetLinkSnapshot();
                return dteNow.InformationFramesOut == dceNow.InformationFramesIn
                    && dceNow.InformationFramesOut == dteNow.InformationFramesIn
                    && dteNow.InformationFramesOut == pair.Dte.Counters.AllPacketsOut
                    && dceNow.InformationFramesOut == pair.Dce.Counters.AllPacketsOut;
            }, 5000), Is.True, "the link goes quiet: every packet handed to LAPB has gone out and arrived");

            HdlcLinkSnapshot dte = pair.Dte.GetLinkSnapshot();
            HdlcLinkSnapshot dce = pair.Dce.GetLinkSnapshot();

            // Clean line, so nothing was sent twice and nothing was damaged.
            Assert.That(dte.Retransmissions, Is.EqualTo(0));
            Assert.That(dte.FcsErrors, Is.EqualTo(0));
            Assert.That(dce.FcsErrors, Is.EqualTo(0));

            // One X.25 packet in each LAPB information frame, both counted: what the DTE's packet layer wrote is what
            // went out as information frames, and what the DCE received is what its LAPB took in.
            // AllPacketsOut counts every X.25 packet of every type the port wrote. The data-only counters (PacketsIn and
            // PacketsOut) belong to the switch core, which is not in this test, so the delivered data is counted from the tap.
            Assert.That(dte.InformationFramesOut, Is.EqualTo(pair.Dte.Counters.AllPacketsOut));
            Assert.That(dce.InformationFramesIn, Is.EqualTo(pair.Dte.Counters.AllPacketsOut));
            Assert.That(dce.InformationFramesOut, Is.EqualTo(pair.Dce.Counters.AllPacketsOut));
            Assert.That(dte.InformationFramesIn, Is.EqualTo(pair.Dce.Counters.AllPacketsOut));
            Assert.That(pair.Dce.Counters.AllPacketsIn, Is.EqualTo(pair.Dte.Counters.AllPacketsOut));
            lock (pair.AtDce)
            {
                Assert.That(pair.AtDce.Count, Is.EqualTo(Total), "the DCE's packet layer delivered 200 data packets");
            }
            Assert.That(pair.Dte.Counters.BytesOut, Is.EqualTo(pair.Dce.Counters.BytesIn), "bytes are counted at the X.25 packet layer on both ends");
            Assert.That(pair.Dce.Counters.BytesOut, Is.EqualTo(pair.Dte.Counters.BytesIn));
            Assert.That(dte.InformationFramesOut, Is.GreaterThan(Total), "200 data packets plus the call set-up and the acknowledgements");
            Assert.That(dte.WireBytesOut, Is.GreaterThan(Total * 20), "wire bytes include framing");
            Assert.That(pair.Dte.Counters.CallsOut, Is.EqualTo(0u), "the port counts calls it placed through the switch, which is not in this test");
        }

        // ------------------------------------------------------------------ faults on the line

        /// <summary>
        /// Frames lost, repeated and damaged on the line: LAPB sends again, the FCS catches the damage, and the data
        /// arrives exactly once and in order.
        /// </summary>
        [Test]
        public void FramesLostRepeatedAndDamaged_AreRecoveredByLapb_AndTheDataArrivesOnceInOrder()
        {
            // Three faults on the first lap of the sequence numbers, in each direction: a lost information frame, a
            // repeated one and a damaged one. The first transmission of a given N(S) is the one that is hit; the
            // retransmission passes.
            Func<ProxiedFrame, FrameFate> rule = f =>
            {
                if (!f.IsInformation || f.TimesSeen != 1)
                {
                    return FrameFate.Pass;
                }

                if (f.Ns == 2)
                {
                    return FrameFate.Drop;
                }

                if (f.Ns == 4)
                {
                    return FrameFate.Duplicate;
                }

                if (f.Ns == 5)
                {
                    return FrameFate.Corrupt;
                }

                return FrameFate.Pass;
            };

            using TrunkPair pair = TrunkPair.Create(rule);
            pair.WaitBothUp();
            ushort lci = pair.Call(pair.Dte);

            const int Total = 60;
            for (int i = 0; i < Total; i++)
            {
                TrunkPair.SendAll(pair.Dte, lci, Numbered(i));
                TrunkPair.SendAll(pair.Dce, lci, Numbered(1000 + i));
            }

            Assert.That(SpinWait.SpinUntil(() => CountOf(pair.AtDce) == Total && CountOf(pair.AtDte) == Total, 20000), Is.True,
                "arrived at the DCE: " + CountOf(pair.AtDce) + ", at the DTE: " + CountOf(pair.AtDte)
                + "\nDCE log:\n" + pair.DceLog.All() + "\nDTE log:\n" + pair.DteLog.All());

            // Give anything duplicated a moment to show up as an extra packet.
            Thread.Sleep(1200);
            Assert.That(CountOf(pair.AtDce), Is.EqualTo(Total), "a repeated frame must not deliver a packet twice");
            Assert.That(CountOf(pair.AtDte), Is.EqualTo(Total));
            lock (pair.AtDce)
            {
                for (int i = 0; i < Total; i++)
                {
                    Assert.That(NumberOf(pair.AtDce[i].Data), Is.EqualTo(i), "DTE to DCE, packet " + i);
                }
            }

            lock (pair.AtDte)
            {
                for (int i = 0; i < Total; i++)
                {
                    Assert.That(NumberOf(pair.AtDte[i].Data), Is.EqualTo(1000 + i), "DCE to DTE, packet " + i);
                }
            }

            HdlcLinkSnapshot dte = pair.Dte.GetLinkSnapshot();
            HdlcLinkSnapshot dce = pair.Dce.GetLinkSnapshot();

            // The damaged frames were seen and thrown away by the FCS check: one in each direction.
            Assert.That(dce.FcsErrors, Is.GreaterThanOrEqualTo(1), "the damaged frame towards the DCE must have been caught by the FCS");
            Assert.That(dte.FcsErrors, Is.GreaterThanOrEqualTo(1), "and the one towards the DTE");

            // Lost and damaged frames were sent again, by REJ or by T1.
            Assert.That(dte.Retransmissions, Is.GreaterThanOrEqualTo(1));
            Assert.That(dce.Retransmissions, Is.GreaterThanOrEqualTo(1));
            Assert.That(dte.RejectsReceived + dte.T1Timeouts, Is.GreaterThanOrEqualTo(1));
            Assert.That(dce.RejectsSent + dce.RejectsReceived + dce.T1Timeouts, Is.GreaterThanOrEqualTo(1));

            // The link never fell: a lost frame is LAPB's business, not the packet layer's.
            Assert.That(dte.LinkUps, Is.EqualTo(1));
            Assert.That(dce.LinkUps, Is.EqualTo(1));
            Assert.That(dte.LinkResets + dce.LinkResets, Is.EqualTo(0));
            Assert.That(pair.Proxy!.Connections, Is.EqualTo(1));
        }

        // ------------------------------------------------------------------ losing and recovering the link

        /// <summary>
        /// The TCP connection is cut: calls on it are cleared at both ends with cause out of order, the dialling end
        /// dials again, LAPB comes up again, and a new call works.
        /// </summary>
        [Test]
        public void WhenTheConnectionIsCut_CallsAreCleared_TheDteRedials_LapbComesUpAgain_AndANewCallWorks()
        {
            using TrunkPair pair = TrunkPair.Create(rule: f => FrameFate.Pass);
            pair.WaitBothUp();
            ushort lci = pair.Call(pair.Dte);
            TrunkPair.SendAll(pair.Dte, lci, Numbered(1));
            Assert.That(SpinWait.SpinUntil(() => CountOf(pair.AtDce) == 1, 5000), Is.True);

            pair.Proxy!.DropConnections();

            Assert.That(SpinWait.SpinUntil(() => !pair.Dce.PeerConnected && !pair.Dte.PeerConnected, 5000), Is.True, "both ends see the link go down");
            Assert.That(SpinWait.SpinUntil(() => pair.ClearedAtDce.Count == 1 && pair.ClearedAtDte.Count == 1, 5000), Is.True,
                "the call on the lost link is cleared at both ends");
            lock (pair.ClearedAtDce)
            {
                Assert.That(pair.ClearedAtDce[0].Cause, Is.EqualTo(0x09), "out of order");
            }

            Assert.That(pair.Dte.Disconnects, Is.GreaterThanOrEqualTo(1));
            Assert.That(pair.Dte.LastDisconnectedMs, Is.Not.EqualTo(0));
            Assert.That(pair.DteLog.Contains("Warning: Port 1 (Dte Trunk, HDLC over TCP): line down"), Is.True, pair.DteLog.All());
            Assert.That(pair.DteLog.Contains("dialling again in 2 seconds"), Is.True);

            // The dialling end tries again by itself (every two seconds).
            pair.WaitBothUp(15000);
            Assert.That(pair.Proxy.Connections, Is.EqualTo(2));
            Assert.That(pair.Dte.GetLinkSnapshot().LinkUps, Is.EqualTo(2));
            Assert.That(pair.Dce.GetLinkSnapshot().LinkUps, Is.EqualTo(2));

            ushort again = pair.Call(pair.Dte);
            TrunkPair.SendAll(pair.Dte, again, Numbered(2));
            Assert.That(SpinWait.SpinUntil(() => CountOf(pair.AtDce) == 2, 5000), Is.True);
            lock (pair.AtDce)
            {
                Assert.That(NumberOf(pair.AtDce[1].Data), Is.EqualTo(2));
            }

            Assert.That(pair.DteLog.Contains("Information: Port 1 (Dte Trunk, HDLC over TCP): LAPB link up"), Is.True);
        }

        /// <summary>
        /// Both ends reset the link at the same moment (each sees a SABM on a link that is up): the link stays up,
        /// the resets are counted, each packet layer restarts, and a call that was up is cleared.
        /// </summary>
        [Test]
        public void ASabmOnAnUpLink_ResetsTheLink_RestartsX25_AndIsCounted()
        {
            using TrunkPair pair = TrunkPair.Create(rule: f => FrameFate.Pass);
            pair.WaitBothUp();
            ushort lci = pair.Call(pair.Dte);
            int dceRestarts = TrunkPair.Count(pair.DceOut, X25PacketType.RestartRequest);
            int dteRestarts = TrunkPair.Count(pair.DteOut, X25PacketType.RestartRequest);

            // A SABM towards each end, as if each had been reset by the other: address 0x01 is a command from the
            // DTE (to the DCE), 0x03 a command from the DCE (to the DTE); control 0x3F is SABM with the poll bit.
            pair.Proxy!.Inject(TrunkDirection.ToDce, new byte[] { 0x01, 0x3F });
            pair.Proxy.Inject(TrunkDirection.ToDte, new byte[] { 0x03, 0x3F });

            Assert.That(SpinWait.SpinUntil(() => pair.Dce.GetLinkSnapshot().LinkResets == 1 && pair.Dte.GetLinkSnapshot().LinkResets == 1, 5000), Is.True,
                "each end counts the reset" + Environment.NewLine + pair.DceLog.All());
            Assert.That(SpinWait.SpinUntil(() => TrunkPair.Count(pair.DceOut, X25PacketType.RestartRequest) == dceRestarts + 1
                && TrunkPair.Count(pair.DteOut, X25PacketType.RestartRequest) == dteRestarts + 1, 5000), Is.True,
                "a reset throws away what was in flight, so each packet layer restarts");
            Assert.That(SpinWait.SpinUntil(() => pair.ClearedAtDce.Count == 1 && pair.ClearedAtDte.Count == 1, 5000), Is.True, "the call is cleared by the restart");
            Assert.That(pair.Dce.PeerConnected && pair.Dte.PeerConnected, Is.True, "the link stays up through a reset");
            Assert.That(pair.Dce.GetLinkSnapshot().LinkUps, Is.EqualTo(1), "it did not go down and up again" + Environment.NewLine + pair.DceLog.All());
            Assert.That(pair.Dte.GetLinkSnapshot().LinkUps, Is.EqualTo(1), pair.DteLog.All());
            Assert.That(pair.DceLog.Contains("LAPB link reset"), Is.True);

            // And the link is good for a new call.
            ushort again = pair.Call(pair.Dte);
            TrunkPair.SendAll(pair.Dte, again, Numbered(9));
            Assert.That(SpinWait.SpinUntil(() => CountOf(pair.AtDce) == 1, 5000), Is.True);
            Assert.That(lci, Is.Not.EqualTo(0));
        }

        /// <summary>
        /// Only one end is reset: the other end finds the sequence numbers do not fit (LAPB answers with FRMR), the link is
        /// set up again with a new SABM, the X.25 layers restart, and the link is good again.
        /// </summary>
        [Test]
        public void AOneSidedReset_IsRepairedByLapb_AndTheLinkIsGoodAgain()
        {
            using TrunkPair pair = TrunkPair.Create(rule: f => FrameFate.Pass);
            pair.WaitBothUp();
            pair.Call(pair.Dte);

            pair.Proxy!.Inject(TrunkDirection.ToDce, new byte[] { 0x01, 0x3F });

            Assert.That(SpinWait.SpinUntil(() => pair.Dce.GetLinkSnapshot().LinkResets >= 1, 5000), Is.True);
            Assert.That(SpinWait.SpinUntil(() => pair.Dce.PeerConnected && pair.Dte.PeerConnected && pair.ClearedAtDte.Count == 1, 15000), Is.True,
                "the link must be up again and the old call gone" + Environment.NewLine + pair.DceLog.All() + Environment.NewLine + pair.DteLog.All());

            ushort again = pair.Call(pair.Dte);
            TrunkPair.SendAll(pair.Dte, again, Numbered(5));
            Assert.That(SpinWait.SpinUntil(() => CountOf(pair.AtDce) == 1, 5000), Is.True);
        }

        /// <summary>
        /// The listening end is stopped and a new one started on the same port; the dialling end finds it and the link
        /// comes up again.
        /// </summary>
        [Test]
        public void WhenTheListeningEndRestarts_TheDialingEndFindsItAgain()
        {
            // A fixed free port, so that the new listener can use the number the dialling end keeps trying.
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            ushort port = (ushort)((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            HdlcTcpSwitchPort dce = new HdlcTcpSwitchPort(TrunkPair.Config(0, PortMode.Dce, "127.0.0.1", port));
            using HdlcTcpSwitchPort dte = new HdlcTcpSwitchPort(TrunkPair.Config(1, PortMode.Dte, "127.0.0.1", port));
            try
            {
                Assert.That(dce.Start(), Is.True);
                Assert.That(dte.Start(), Is.True);
                Assert.That(SpinWait.SpinUntil(() => dce.PeerConnected && dte.PeerConnected, 10000), Is.True);

                dce.Dispose();
                Assert.That(SpinWait.SpinUntil(() => !dte.PeerConnected, 5000), Is.True, "the dialling end sees the link go");

                dce = new HdlcTcpSwitchPort(TrunkPair.Config(0, PortMode.Dce, "127.0.0.1", port));
                Assert.That(dce.Start(), Is.True);
                Assert.That(SpinWait.SpinUntil(() => dce.PeerConnected && dte.PeerConnected, 15000), Is.True, "the link comes up again by itself");
                Assert.That(dte.GetLinkSnapshot().LinkUps, Is.EqualTo(2));
            }
            finally
            {
                dce.Dispose();
            }
        }

        /// <summary>
        /// Both ends are stopped and started again: the link comes up and a call works.
        /// </summary>
        [Test]
        public void WhenBothEndsAreStoppedAndStartedAgain_TheLinkComesUpAndACallWorks()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            ushort port = (ushort)((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            using HdlcTcpSwitchPort dce = new HdlcTcpSwitchPort(TrunkPair.Config(0, PortMode.Dce, "127.0.0.1", port));
            using HdlcTcpSwitchPort dte = new HdlcTcpSwitchPort(TrunkPair.Config(1, PortMode.Dte, "127.0.0.1", port));
            List<ReceivedData> atDce = new List<ReceivedData>();
            dce.DataReceived += (p, lci, bytes, length, q, m, d) =>
            {
                lock (atDce)
                {
                    atDce.Add(new ReceivedData { Data = bytes.AsSpan(0, length).ToArray() });
                }
            };
            dce.IncomingCall += (p, lci, c, g, cud) => dce.AcceptCall(lci);

            Assert.That(dce.Start() && dte.Start(), Is.True);
            Assert.That(SpinWait.SpinUntil(() => dce.PeerConnected && dte.PeerConnected, 10000), Is.True);

            dte.Stop();
            dce.Stop();
            Assert.That(dce.PeerConnected, Is.False);
            Assert.That(dte.PeerConnected, Is.False);
            Assert.That(dce.Active, Is.False);

            Assert.That(dce.Start(), Is.True);
            Assert.That(dte.Start(), Is.True);
            Assert.That(SpinWait.SpinUntil(() => dce.PeerConnected && dte.PeerConnected, 10000), Is.True, "the link comes up after both restarted");

            ManualResetEventSlim connected = new ManualResetEventSlim(false);
            dte.CallConnected += (p, lci) => connected.Set();
            ushort call = dte.PlaceCall("23420001", "23400001", null);
            Assert.That(call, Is.Not.EqualTo(0));
            Assert.That(connected.Wait(10000), Is.True);
            TrunkPair.SendAll(dte, call, Numbered(7));
            Assert.That(SpinWait.SpinUntil(() => CountOf(atDce) == 1, 5000), Is.True);
        }

        /// <summary>
        /// A new connection from the peer replaces an old one that never closed (a half dead link).
        /// </summary>
        [Test]
        public void ANewConnectionFromThePeer_ReplacesAnOldOneThatNeverClosed()
        {
            using HdlcTcpSwitchPort dce = new HdlcTcpSwitchPort(TrunkPair.Config(0, PortMode.Dce, "127.0.0.1", 0));
            Assert.That(dce.Start(), Is.True);

            // Two raw sockets stand in for the peer. The first connects and is left open and silent.
            using TcpClient first = new TcpClient();
            first.Connect(IPAddress.Loopback, dce.BoundPort);
            Assert.That(SpinWait.SpinUntil(() => dce.ConnectAttempts == 1, 5000), Is.True);

            using HdlcTcpSwitchPort dte = new HdlcTcpSwitchPort(TrunkPair.Config(1, PortMode.Dte, "127.0.0.1", dce.BoundPort));
            Assert.That(dte.Start(), Is.True);
            Assert.That(SpinWait.SpinUntil(() => dce.PeerConnected && dte.PeerConnected, 10000), Is.True,
                "the silent first connection must not keep the real peer out");
            Assert.That(dce.ConnectAttempts, Is.EqualTo(2));
        }

        // ------------------------------------------------------------------ refusals

        /// <summary>
        /// A dialling port with no host cannot work; it says so and does not start.
        /// </summary>
        [Test]
        public void ADtePortWithNoHost_DoesNotStart_AndSaysWhy()
        {
            TrunkListLogger log = new TrunkListLogger();
            using HdlcTcpSwitchPort dte = new HdlcTcpSwitchPort(TrunkPair.Config(3, PortMode.Dte, "", 1998), log);

            Assert.That(dte.Start(), Is.False);
            Assert.That(dte.Active, Is.False);
            Assert.That(log.Contains("Error: Port 3"), Is.True, log.All());
            Assert.That(log.Contains("host"), Is.True);
            Assert.That(dte.LastError, Does.Contain("host"));
        }

        /// <summary>
        /// A LAPB value that cannot work is refused at once, naming the port and the value.
        /// </summary>
        [TestCase(9, 0, "lapb_k")]
        [TestCase(0, 50, "lapb_t1")]
        public void ALapbValueThatCannotWork_IsRefused_NamingThePortAndTheKey(int k, int t1, string key)
        {
            PortConfig config = TrunkPair.Config(4, PortMode.Dce, "127.0.0.1", 0);
            config.LapbK = k;
            config.LapbT1Ms = t1;

            Exception ex = Assert.Throws<System.IO.InvalidDataException>(() => new HdlcTcpSwitchPort(config))!;
            Assert.That(ex.Message, Does.Contain("Port 4"));
            Assert.That(ex.Message, Does.Contain(key));
        }

        /// <summary>
        /// T2 not less than T1 is refused (X.25 2.4.9.2).
        /// </summary>
        [Test]
        public void T2NotLessThanT1_IsRefused()
        {
            PortConfig config = TrunkPair.Config(4, PortMode.Dce, "127.0.0.1", 0, t1: 500);
            config.LapbT2Ms = 500;
            Exception ex = Assert.Throws<System.IO.InvalidDataException>(() => new HdlcTcpSwitchPort(config))!;
            Assert.That(ex.Message, Does.Contain("lapb_t2"));
        }

        // ------------------------------------------------------------------ the switch over a trunk

        /// <summary>
        /// A call crosses a chain of two switches whose trunk is hdlc-tcp, with XOT clients at both ends: data of 128
        /// bytes and a Q bit packet arrive both ways, the call clears, and the trunk's counters equal what was sent.
        /// </summary>
        [Test]
        public void ACallCrossesTwoSwitchesJoinedByAnHdlcTrunk_AndDataReturns()
        {
            // Switch 1: port 0 access (XOT, DCE), port 1 trunk (HDLC, DTE, dials switch 2). Calls to 2342 go out port 1.
            SwitchConfig cfg1 = new SwitchConfig { Dnic = "2340" };
            cfg1.Routing.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = "2342", PrefixLen = 4, DestPort = 1 });
            // Switch 2: port 0 trunk (HDLC, DCE), port 1 access (XOT, DCE) where the callee is. Calls to 2342 go out port 1.
            SwitchConfig cfg2 = new SwitchConfig { Dnic = "2342" };
            cfg2.Routing.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = "2342", PrefixLen = 4, DestPort = 1 });

            SwitchNode node1 = new SwitchNode(cfg1);
            SwitchNode node2 = new SwitchNode(cfg2);

            PortConfig trunk2Config = TrunkPair.Config(0, PortMode.Dce, "127.0.0.1", 0);
            ISwitchPort trunk2 = SwitchPortFactory.Create(trunk2Config);
            XotSwitchPort access2 = new XotSwitchPort(new PortConfig { Id = 1, Mode = PortMode.Dce, Host = "127.0.0.1", Port = 0, LciBase = 1, LciCount = 16 });
            node2.AddPort(trunk2);
            node2.AddPort(access2);
            SwitchCore core2 = new SwitchCore(node2);
            Assert.That(trunk2.Start() && access2.Start(), Is.True);

            XotSwitchPort access1 = new XotSwitchPort(new PortConfig { Id = 0, Mode = PortMode.Dce, Host = "127.0.0.1", Port = 0, LciBase = 1, LciCount = 16 });
            ISwitchPort trunk1 = SwitchPortFactory.Create(TrunkPair.Config(1, PortMode.Dte, "127.0.0.1", ((HdlcTcpSwitchPort)trunk2).BoundPort));
            node1.AddPort(access1);
            node1.AddPort(trunk1);
            SwitchCore core1 = new SwitchCore(node1);
            Assert.That(access1.Start() && trunk1.Start(), Is.True);

            try
            {
                Assert.That(SpinWait.SpinUntil(() => trunk1.PeerConnected && trunk2.PeerConnected, 10000), Is.True, "the trunk comes up");
                List<string> tap1 = new List<string>();
                List<string> tap2 = new List<string>();
                ((HdlcSwitchPortBase)trunk1).PacketCaptured += (id, inb, pk) => { lock (tap1) { tap1.Add((inb ? "IN  " : "OUT ") + X25PacketParser.ParsePacket(pk).PacketType + " " + BitConverter.ToString(pk)); } };
                ((HdlcSwitchPortBase)trunk2).PacketCaptured += (id, inb, pk) => { lock (tap2) { tap2.Add((inb ? "IN  " : "OUT ") + X25PacketParser.ParsePacket(pk).PacketType + " " + BitConverter.ToString(pk)); } };

                object gate = new object();
                List<byte[]> atCallee = new List<byte[]>();
                List<byte[]> atCaller = new List<byte[]>();
                List<bool> qAtCallee = new List<bool>();
                X25Vc? calleeVc = null;

                using X25Client callee = new X25Client { ServiceName = "callee" };
                callee.Open(new X25ClientConfig { Host = "127.0.0.1", Port = access2.BoundPort, LocalAddress = "23420001", Framing = X25ClientFraming.Xot }, null);
                callee.SetOnIncoming((c, vc) =>
                {
                    lock (gate)
                    {
                        calleeVc = vc;
                    }

                    c.Accept(vc);
                });
                callee.SetOnData((c, vc, data, length, q, m, d) =>
                {
                    lock (gate)
                    {
                        atCallee.Add(data.AsSpan(0, length).ToArray());
                        qAtCallee.Add(q);
                    }
                });

                using X25Client caller = new X25Client { ServiceName = "caller" };
                caller.Open(new X25ClientConfig { Host = "127.0.0.1", Port = access1.BoundPort, LocalAddress = "23400001", Framing = X25ClientFraming.Xot }, null);
                caller.SetOnData((c, vc, data, length, q, m, d) =>
                {
                    lock (gate)
                    {
                        atCaller.Add(data.AsSpan(0, length).ToArray());
                    }
                });

                SpinWait.SpinUntil(() => access1.PeerConnected && access2.PeerConnected, 5000);
                X25Vc? callerVc = caller.CallWait("23420001", 10000);
                Assert.That(callerVc, Is.Not.Null, "the call crosses both switches and is accepted");
                Assert.That(node1.ActiveStitchCount, Is.EqualTo(1));
                Assert.That(node2.ActiveStitchCount, Is.EqualTo(1));

                byte[] full = new byte[128];
                for (int i = 0; i < full.Length; i++)
                {
                    full[i] = (byte)(255 - i);
                }

                Assert.That(caller.Send(callerVc!, full, full.Length, false), Is.True);
                Assert.That(caller.Send(callerVc!, new byte[] { 1, 2, 3 }, 3, true), Is.True);
                Assert.That(SpinWait.SpinUntil(() => { lock (gate) { return atCallee.Count == 2; } }, 10000), Is.True);
                lock (gate)
                {
                    Assert.That(atCallee[0], Is.EqualTo(full));
                    Assert.That(atCallee[1], Is.EqualTo(new byte[] { 1, 2, 3 }));
                    Assert.That(qAtCallee[0], Is.False);
                    Assert.That(qAtCallee[1], Is.True, "the Q bit crosses the trunk");
                    Assert.That(calleeVc, Is.Not.Null);
                }

                Assert.That(callee.Send(calleeVc!, full, full.Length, false), Is.True);
                Assert.That(SpinWait.SpinUntil(() => { lock (gate) { return atCaller.Count == 1; } }, 10000), Is.True);
                lock (gate)
                {
                    Assert.That(atCaller[0], Is.EqualTo(full));
                }

                // What each end of the trunk wrote is what the other took in. Compared at the LAPB level (one X.25 packet in each
                // information frame) and in bytes at the X.25 packet layer. Calls and data packets are counted once, by the
                // core; SwitchCounterTests proves those counts.
                Assert.That(trunk1.Counters.CallsOut, Is.EqualTo(1u), "the core placed one onward call out of the trunk");
                Assert.That(SpinWait.SpinUntil(() => trunk1.Counters.BytesOut == trunk2.Counters.BytesIn && trunk2.Counters.BytesOut == trunk1.Counters.BytesIn, 5000), Is.True);
                Assert.That(SpinWait.SpinUntil(() =>
                    ((HdlcSwitchPortBase)trunk2).GetLinkSnapshot().InformationFramesIn == ((HdlcSwitchPortBase)trunk1).GetLinkSnapshot().InformationFramesOut
                    && ((HdlcSwitchPortBase)trunk1).GetLinkSnapshot().InformationFramesIn == ((HdlcSwitchPortBase)trunk2).GetLinkSnapshot().InformationFramesOut, 5000), Is.True);
                Assert.That(((HdlcSwitchPortBase)trunk1).GetLinkSnapshot().Retransmissions + ((HdlcSwitchPortBase)trunk2).GetLinkSnapshot().Retransmissions,
                    Is.EqualTo(0), "a clean loopback trunk sends nothing twice");

                caller.Clear(callerVc!);
                Assert.That(SpinWait.SpinUntil(() => node1.ActiveStitchCount == 0 && node2.ActiveStitchCount == 0, 10000), Is.True, "the call clears through both switches");
            }
            finally
            {
                trunk1.Stop();
                access1.Stop();
                trunk2.Stop();
                access2.Stop();
                GC.KeepAlive(core1);
                GC.KeepAlive(core2);
            }
        }
    }
}
