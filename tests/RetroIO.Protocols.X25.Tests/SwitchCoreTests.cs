using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// End-to-end tests for <see cref="SwitchCore"/> using in-memory loopback ports: a call is routed
    /// across two ports, data is forwarded in both directions, and the circuit is torn down — no sockets.
    /// </summary>
    [TestFixture]
    public class SwitchCoreTests
    {
        private SwitchNode _node = null!;
        private SwitchCore _core = null!;
        private LoopbackSwitchPort _p0 = null!;
        private LoopbackSwitchPort _p1 = null!;

        [SetUp]
        public void SetUp()
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340" };
            // Calls to 2341xxxx route to port 1.
            cfg.Routing.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = "2341", PrefixLen = 4, DestPort = 1 });

            _node = new SwitchNode(cfg);
            _p0 = new LoopbackSwitchPort(new PortConfig { Id = 0, LciBase = 1 });
            _p1 = new LoopbackSwitchPort(new PortConfig { Id = 1, LciBase = 1 });
            _node.AddPort(_p0);
            _node.AddPort(_p1);

            _core = new SwitchCore(_node);
        }

        [Test]
        public void IncomingCall_RoutedAndStitched_ThenForwardsBothWays_ThenClears()
        {
            bool setupFired = false;
            bool clearFired = false;
            _core.CallSetup += (calling, called, inPort, outPort) =>
            {
                setupFired = true;
                Assert.That(calling, Is.EqualTo("23400001"));
                Assert.That(called, Is.EqualTo("23410001"));
                Assert.That(inPort, Is.EqualTo(0));
                Assert.That(outPort, Is.EqualTo(1));
            };
            _core.CallClear += (port, lci, cause, diag) => clearFired = true;

            // A DTE on port 0 calls an address that routes to port 1.
            ushort inLci = _p0.InjectIncomingCall("23410001", "23400001");

            // The onward call was placed on port 1 and the inbound call accepted on port 0.
            Assert.That(_p1.PlacedCalls.Count, Is.EqualTo(1));
            Assert.That(_p1.PlacedCalls[0].Called, Is.EqualTo("23410001"));
            Assert.That(_p0.AcceptedCalls, Does.Contain(inLci));
            Assert.That(_node.ActiveStitchCount, Is.EqualTo(1));
            Assert.That(setupFired, Is.True);

            VcStitch stitch = _node.SnapshotStitches()[0];
            ushort outLci = stitch.OutLci;

            // Forward: data from the ingress DTE reaches the egress port.
            byte[] hello = Encoding.ASCII.GetBytes("hello");
            _p0.InjectData(inLci, hello);
            Assert.That(_p1.SentData.Count, Is.EqualTo(1));
            Assert.That(_p1.SentData[0].Lci, Is.EqualTo(outLci));
            Assert.That(_p1.SentData[0].Data, Is.EqualTo(hello));
            Assert.That(stitch.PacketsForward, Is.EqualTo(1u));
            Assert.That(stitch.BytesForward, Is.EqualTo(5u));

            // Reverse: data from the egress side reaches the ingress DTE.
            byte[] reply = Encoding.ASCII.GetBytes("hi!");
            _p1.InjectData(outLci, reply);
            Assert.That(_p0.SentData.Count, Is.EqualTo(1));
            Assert.That(_p0.SentData[0].Lci, Is.EqualTo(inLci));
            Assert.That(_p0.SentData[0].Data, Is.EqualTo(reply));
            Assert.That(stitch.PacketsReverse, Is.EqualTo(1u));
            Assert.That(stitch.BytesReverse, Is.EqualTo(3u));

            // Clear from the ingress side tears down the egress leg and destroys the stitch.
            _p0.InjectClear(inLci, SwitchCore.CauseDteOriginated, 0);
            Assert.That(_p1.ClearedCalls.Count, Is.EqualTo(1));
            Assert.That(_p1.ClearedCalls[0].Lci, Is.EqualTo(outLci));
            Assert.That(_node.ActiveStitchCount, Is.EqualTo(0));
            Assert.That(clearFired, Is.True);
        }

        [Test]
        public void IncomingCall_NoRoute_IsCleared()
        {
            // 9999xxxx matches no route -> the incoming call is cleared as not obtainable.
            ushort inLci = _p0.InjectIncomingCall("99999999", "23400001");

            Assert.That(_p0.ClearedCalls.Count, Is.EqualTo(1));
            Assert.That(_p0.ClearedCalls[0].Lci, Is.EqualTo(inLci));
            Assert.That(_p0.ClearedCalls[0].Cause, Is.EqualTo(SwitchCore.CauseNotObtainable));
            Assert.That(_node.ActiveStitchCount, Is.EqualTo(0));
            Assert.That(_p1.PlacedCalls.Count, Is.EqualTo(0));
        }

        [Test]
        public void Counters_TrackCallsAndPackets()
        {
            ushort inLci = _p0.InjectIncomingCall("23410001", "23400001");
            _p0.InjectData(inLci, new byte[] { 1, 2, 3 });

            Assert.That(_p0.Counters.CallsIn, Is.EqualTo(1u));
            Assert.That(_p1.Counters.CallsOut, Is.EqualTo(1u));
            Assert.That(_p0.Counters.PacketsIn, Is.EqualTo(1u), "ingress packet counted");
            Assert.That(_p1.Counters.PacketsOut, Is.EqualTo(1u), "forwarded packet counted on egress");
        }

        [Test]
        public void Cdr_RecordedOnClear()
        {
            List<Cdr> cdrs = new List<Cdr>();
            _core.CdrRecorded += cdr => cdrs.Add(cdr);

            ushort inLci = _p0.InjectIncomingCall("23410001", "23400001");
            _p0.InjectData(inLci, new byte[] { 1, 2, 3, 4 });
            _p0.InjectClear(inLci, SwitchCore.CauseDteOriginated, 0);

            Assert.That(cdrs.Count, Is.EqualTo(1));
            Assert.That(cdrs[0].Calling, Is.EqualTo("23400001"));
            Assert.That(cdrs[0].Called, Is.EqualTo("23410001"));
            Assert.That(cdrs[0].InPort, Is.EqualTo(0));
            Assert.That(cdrs[0].OutPort, Is.EqualTo(1));
            Assert.That(cdrs[0].PacketsForward, Is.EqualTo(1u));
            Assert.That(cdrs[0].BytesForward, Is.EqualTo(4u));
        }
    }
}
