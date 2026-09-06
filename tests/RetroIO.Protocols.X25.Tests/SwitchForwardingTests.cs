using System;
using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// What <see cref="SwitchCore"/> does when a leg of a stitch will not take a packet, and what
    /// it does with a reset or an interrupt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These replace <c>SwitchCorePinTests</c>, which pinned the old behaviour as wrong and said so
    /// in its names. The old forwarding called SendData as a statement: the bool came back false,
    /// nobody looked, the packet was gone, and the egress counter still said it went out. The far
    /// end then saw a gap in P(S) and reset the circuit (X.25 4.4.1.3). The same shape lost packets
    /// in five places in the C switch.
    /// </para>
    /// <para>
    /// The port contract now answers with <see cref="PortSendResult"/> and carries reset, interrupt
    /// and ready-to-send, so all of this can be tested without sockets.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class SwitchForwardingTests
    {
        private SwitchNode _node = null!;
        private SwitchCore _core = null!;
        private LoopbackSwitchPort _ingress = null!;
        private LoopbackSwitchPort _egress = null!;

        [SetUp]
        public void SetUp()
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340" };
            cfg.Routing.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = "2341", PrefixLen = 4, DestPort = 1 });

            _node = new SwitchNode(cfg);
            _ingress = new LoopbackSwitchPort(new PortConfig { Id = 0, LciBase = 1 });
            _egress = new LoopbackSwitchPort(new PortConfig { Id = 1, LciBase = 1 });
            _node.AddPort(_ingress);
            _node.AddPort(_egress);
            _core = new SwitchCore(_node);
        }

        /// <summary>
        /// Sets up one routed call and returns the ingress channel and the stitch.
        /// </summary>
        private (ushort InLci, VcStitch Stitch) Call()
        {
            ushort inLci = _ingress.InjectIncomingCall("23410001", "23400001");
            Assert.That(_node.ActiveStitchCount, Is.EqualTo(1), "the call should have been routed to port 1");
            return (inLci, _node.SnapshotStitches()[0]);
        }

        [Test]
        public void AnAcceptedPacketIsForwardedAndCounted()
        {
            (ushort inLci, VcStitch stitch) = Call();

            _ingress.InjectData(inLci, new byte[] { 9, 8, 7 });

            Assert.That(_egress.SentData.Count, Is.EqualTo(1));
            Assert.That(_egress.SentData[0].Lci, Is.EqualTo(stitch.OutLci));
            Assert.That(_egress.SentData[0].Data, Is.EqualTo(new byte[] { 9, 8, 7 }));
            Assert.That(_egress.Counters.PacketsOut, Is.EqualTo(1u));
            Assert.That(stitch.PacketsForward, Is.EqualTo(1));

            // And back the other way.
            _egress.InjectData(stitch.OutLci, new byte[] { 5 });
            Assert.That(_ingress.SentData.Count, Is.EqualTo(1));
            Assert.That(_ingress.SentData[0].Lci, Is.EqualTo(inLci));
            Assert.That(stitch.PacketsReverse, Is.EqualTo(1));
        }

        [Test]
        public void AShutEgressWindow_HoldsThePacketAndTellsTheSenderToStop()
        {
            (ushort inLci, VcStitch stitch) = Call();
            _egress.WindowShut.Add(stitch.OutLci);

            _ingress.InjectData(inLci, new byte[] { 1, 2, 3, 4 });

            Assert.Multiple(() =>
            {
                Assert.That(_egress.SentData.Count, Is.EqualTo(0), "nothing left the egress port");
                Assert.That(stitch.ForwardQueue.Count, Is.EqualTo(1), "the packet is held, not dropped");
                Assert.That(_egress.Counters.PacketsOut, Is.EqualTo(0u), "a packet that did not leave is not counted");
                Assert.That(stitch.PacketsForward, Is.EqualTo(0));
                Assert.That(_ingress.ClearedCalls.Count, Is.EqualTo(0), "a shut window is a pause, not a failure");
            });

            // X.25 4.4.2: the sender is told RNR rather than left to fill the queue.
            Assert.That(_ingress.BusySignals.Count, Is.EqualTo(1));
            Assert.That(_ingress.BusySignals[0], Is.EqualTo((inLci, true)));
        }

        [Test]
        public void WhenTheWindowOpens_TheHeldPacketsGoOutInOrderAndTheSenderIsReleased()
        {
            (ushort inLci, VcStitch stitch) = Call();
            _egress.WindowShut.Add(stitch.OutLci);

            _ingress.InjectData(inLci, new byte[] { 1 });
            _ingress.InjectData(inLci, new byte[] { 2 });
            _ingress.InjectData(inLci, new byte[] { 3 });
            Assert.That(stitch.ForwardQueue.Count, Is.EqualTo(3));

            _egress.OpenWindow(stitch.OutLci);

            Assert.Multiple(() =>
            {
                Assert.That(stitch.ForwardQueue.Count, Is.EqualTo(0));
                Assert.That(_egress.SentData.Count, Is.EqualTo(3));
                Assert.That(_egress.SentData[0].Data, Is.EqualTo(new byte[] { 1 }));
                Assert.That(_egress.SentData[1].Data, Is.EqualTo(new byte[] { 2 }));
                Assert.That(_egress.SentData[2].Data, Is.EqualTo(new byte[] { 3 }));
                Assert.That(_egress.Counters.PacketsOut, Is.EqualTo(3u));
                Assert.That(stitch.PacketsForward, Is.EqualTo(3));
            });

            // RNR then RR, in that order.
            Assert.That(_ingress.BusySignals, Is.EqualTo(new List<(ushort, bool)> { (inLci, true), (inLci, false) }));
        }

        [Test]
        public void APacketArrivingWhileOthersAreHeld_GoesBehindThem()
        {
            // A later packet must never overtake an earlier one. There is no way to say "out of
            // order" in X.25, and a gap in P(S) resets the circuit.
            (ushort inLci, VcStitch stitch) = Call();
            _egress.WindowShut.Add(stitch.OutLci);

            _ingress.InjectData(inLci, new byte[] { 1 });
            _egress.WindowShut.Remove(stitch.OutLci);   // open again, but say nothing
            _ingress.InjectData(inLci, new byte[] { 2 });

            Assert.That(_egress.SentData.Count, Is.EqualTo(0), "the second packet must not pass the first");
            Assert.That(stitch.ForwardQueue.Count, Is.EqualTo(2));

            _egress.OpenWindow(stitch.OutLci);

            Assert.That(_egress.SentData.Count, Is.EqualTo(2));
            Assert.That(_egress.SentData[0].Data, Is.EqualTo(new byte[] { 1 }));
            Assert.That(_egress.SentData[1].Data, Is.EqualTo(new byte[] { 2 }));
        }

        [Test]
        public void TheQBitAndMBitTravelWithAHeldPacket()
        {
            // The bits are part of the packet: Q says the data is for the far PAD rather than the
            // terminal (4.3.1) and M says more of the message follows (4.3.4). A queue that kept
            // only the bytes would change what they mean.
            (ushort inLci, VcStitch stitch) = Call();
            _egress.WindowShut.Add(stitch.OutLci);

            _ingress.InjectData(inLci, new byte[] { 0xAA }, qBit: true, mBit: true, dBit: false);
            _egress.OpenWindow(stitch.OutLci);

            Assert.That(_egress.SentData.Count, Is.EqualTo(1));
            Assert.That(_egress.SentData[0].Q, Is.True);
            Assert.That(_egress.SentData[0].M, Is.True);
            Assert.That(_egress.SentData[0].D, Is.False);
        }

        [Test]
        public void AnEgressLegThatFailsOutright_ClearsBothLegs()
        {
            // Not a pause: no such circuit, or the port is down. Holding the packet would only
            // delay the clear and pile every later packet up behind it.
            (ushort inLci, VcStitch stitch) = Call();
            _egress.SendFails.Add(stitch.OutLci);

            _ingress.InjectData(inLci, new byte[] { 1 });

            Assert.Multiple(() =>
            {
                Assert.That(_ingress.ClearedCalls.Count, Is.EqualTo(1));
                Assert.That(_ingress.ClearedCalls[0].Lci, Is.EqualTo(inLci));
                Assert.That(_ingress.ClearedCalls[0].Cause, Is.EqualTo(SwitchCore.CauseOutOfOrder));
                Assert.That(_egress.ClearedCalls.Count, Is.EqualTo(1));
                Assert.That(_node.ActiveStitchCount, Is.EqualTo(0), "the stitch is gone");
            });
        }

        [Test]
        public void ASenderThatIgnoresRnr_HasTheCircuitResetRatherThanTheQueueGrown()
        {
            // A switch that buffers without limit turns one stuck circuit into a memory problem for
            // every circuit. X.25 4.4: a reset puts both legs back to the start of data transfer
            // and discards what is in flight, which is the honest answer here.
            (ushort inLci, VcStitch stitch) = Call();
            _egress.WindowShut.Add(stitch.OutLci);

            for (int i = 0; i <= SwitchCore.MaxQueuedPacketsPerDirection; i++)
            {
                _ingress.InjectData(inLci, new byte[] { (byte)i });
            }

            Assert.Multiple(() =>
            {
                Assert.That(stitch.ForwardQueue.Count, Is.EqualTo(0), "what was held is discarded");
                Assert.That(_ingress.ResetCircuits.Count, Is.EqualTo(1));
                Assert.That(_egress.ResetCircuits.Count, Is.EqualTo(1));
                Assert.That(_ingress.ResetCircuits[0].Cause, Is.EqualTo(SwitchCore.ResetCauseNetworkCongestion));
            });
        }

        [Test]
        public void AResetOnOneLeg_ReachesTheOtherAndThrowsAwayWhatWasHeld()
        {
            (ushort inLci, VcStitch stitch) = Call();
            _egress.WindowShut.Add(stitch.OutLci);
            _ingress.InjectData(inLci, new byte[] { 1 });
            Assert.That(stitch.ForwardQueue.Count, Is.EqualTo(1));

            _ingress.InjectReset(inLci, cause: 0x00, diagnostic: 0x00);

            Assert.Multiple(() =>
            {
                Assert.That(_egress.ResetCircuits.Count, Is.EqualTo(1), "the reset must reach the far leg");
                Assert.That(_egress.ResetCircuits[0].Lci, Is.EqualTo(stitch.OutLci));
                Assert.That(stitch.ForwardQueue.Count, Is.EqualTo(0),
                    "sequence numbers went back to zero, so the held packet must not be sent afterwards");
            });

            // The ingress sender was released as part of the reset: RNR then RR.
            Assert.That(_ingress.BusySignals, Is.EqualTo(new List<(ushort, bool)> { (inLci, true), (inLci, false) }));
        }

        [Test]
        public void AResetOnTheEgressLeg_ReachesTheIngressLeg()
        {
            (ushort inLci, VcStitch stitch) = Call();

            _egress.InjectReset(stitch.OutLci, cause: 0x00, diagnostic: 0x00);

            Assert.That(_ingress.ResetCircuits.Count, Is.EqualTo(1));
            Assert.That(_ingress.ResetCircuits[0].Lci, Is.EqualTo(inLci));
        }

        [Test]
        public void AnInterruptIsForwardedAndDoesNotWaitBehindHeldData()
        {
            // X.25 4.3.2: an interrupt passes the data it overtakes.
            (ushort inLci, VcStitch stitch) = Call();
            _egress.WindowShut.Add(stitch.OutLci);
            _ingress.InjectData(inLci, new byte[] { 1 });

            _ingress.InjectInterrupt(inLci, new byte[] { 0x42 });

            Assert.Multiple(() =>
            {
                Assert.That(_egress.SentInterrupts.Count, Is.EqualTo(1));
                Assert.That(_egress.SentInterrupts[0].Lci, Is.EqualTo(stitch.OutLci));
                Assert.That(_egress.SentInterrupts[0].UserData, Is.EqualTo(new byte[] { 0x42 }));
                Assert.That(stitch.ForwardQueue.Count, Is.EqualTo(1), "the held data is still held");
            });
        }

        [Test]
        public void AnInterruptOnTheEgressLegReachesTheIngressLeg()
        {
            (ushort inLci, VcStitch stitch) = Call();

            _egress.InjectInterrupt(stitch.OutLci, new byte[] { 1, 2 });

            Assert.That(_ingress.SentInterrupts.Count, Is.EqualTo(1));
            Assert.That(_ingress.SentInterrupts[0].Lci, Is.EqualTo(inLci));
        }

        [Test]
        public void TheTwoDirectionsAreHeldSeparately()
        {
            // A shut window one way must not stop the other way: they are separate flows with
            // separate sequence numbers (X.25 4.4.1).
            (ushort inLci, VcStitch stitch) = Call();
            _egress.WindowShut.Add(stitch.OutLci);

            _ingress.InjectData(inLci, new byte[] { 1 });
            _egress.InjectData(stitch.OutLci, new byte[] { 2 });

            Assert.Multiple(() =>
            {
                Assert.That(stitch.ForwardQueue.Count, Is.EqualTo(1));
                Assert.That(stitch.ReverseQueue.Count, Is.EqualTo(0));
                Assert.That(_ingress.SentData.Count, Is.EqualTo(1), "the reverse direction still flows");
                Assert.That(stitch.PacketsReverse, Is.EqualTo(1));
            });
        }
    }
}
