//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Protocols.X25.PacketLayer;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The switch's router alone, with in-memory ports as the lines: every decision it can give.
    /// </summary>
    /// <remarks>
    /// Causes from X.25 (10/96), see <see cref="X25CallDecision"/>: no route or no such port is Not
    /// obtainable 0x0D, diagnostic 67 (Table C.1 row 24); a port whose line is out of order is Out of
    /// order 0x09, diagnostic 0 (Table C.1 "Called DTE out of order", 4.6.4 item 1).
    /// </remarks>
    [TestFixture]
    public class SwitchCallRouterTests
    {
        private Dictionary<int, ISwitchPort> _ports = null!;
        private X121RoutingTable _table = null!;
        private SwitchCallRouter _router = null!;
        private LoopbackSwitchPort _p0 = null!;
        private LoopbackSwitchPort _p1 = null!;

        [SetUp]
        public void SetUp()
        {
            _table = new X121RoutingTable();
            _table.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = "2341", PrefixLen = 4, DestPort = 1 });
            _p0 = new LoopbackSwitchPort(new PortConfig { Id = 0, LciBase = 1 });
            _p1 = new LoopbackSwitchPort(new PortConfig { Id = 1, LciBase = 1 });
            _p0.Start();
            _p1.Start();
            _ports = new Dictionary<int, ISwitchPort> { { 0, _p0 }, { 1, _p1 } };
            _router = new SwitchCallRouter(_table, id => _ports.TryGetValue(id, out ISwitchPort? p) ? p : null);
        }

        [Test]
        public void ARouteToAPortInService_IsForwardedToThatPort()
        {
            X25CallDecision decision = _router.Decide("23410001", inPortId: 0);

            Assert.That(decision.Action, Is.EqualTo(X25CallAction.Forward));
            Assert.That(decision.OutPort, Is.EqualTo(1));
        }

        [Test]
        public void NoRoute_IsNotObtainable67()
        {
            X25CallDecision decision = _router.Decide("99990001", inPortId: 0);

            Assert.That(decision.Action, Is.EqualTo(X25CallAction.Refuse));
            Assert.That(decision.Cause, Is.EqualTo(X25Cause.NotObtainable));
            Assert.That(decision.Diagnostic, Is.EqualTo(X25Diagnostic.InvalidCalledAddress));
        }

        [Test]
        public void ARouteBackOutOfThePortTheCallCameIn_IsNoRoute()
        {
            // X121RoutingTable never routes a call back to the party that placed it.
            X25CallDecision decision = _router.Decide("23410001", inPortId: 1);

            Assert.That(decision.Cause, Is.EqualTo(X25Cause.NotObtainable));
        }

        [Test]
        public void ARouteToAPortTheSwitchDoesNotHave_IsNotObtainable67()
        {
            _table.AddRoute(new X121Route { Type = X121RouteType.Exact, Address = "55550001", DestPort = 9 });

            X25CallDecision decision = _router.Decide("55550001", inPortId: 0);

            Assert.That(decision.Action, Is.EqualTo(X25CallAction.Refuse));
            Assert.That(decision.Cause, Is.EqualTo(X25Cause.NotObtainable));
            Assert.That(decision.Diagnostic, Is.EqualTo(X25Diagnostic.InvalidCalledAddress));
        }

        [Test]
        public void ARouteToAPortThatIsDown_IsOutOfOrder()
        {
            _p1.Stop();

            X25CallDecision decision = _router.Decide("23410001", inPortId: 0);

            Assert.That(decision.Action, Is.EqualTo(X25CallAction.Refuse));
            Assert.That(decision.Cause, Is.EqualTo(X25Cause.OutOfOrder), "X.25 4.6.4 item 1 and Table C.1: out of order");
            Assert.That(decision.Diagnostic, Is.EqualTo(X25Diagnostic.None));
        }

        [Test]
        public void TheManagementAddress_IsDeliveredLocally_EvenWithARouteForIt()
        {
            _router.ManagementAddress = "23419999";

            Assert.That(_router.Decide("23419999", inPortId: 0).Action, Is.EqualTo(X25CallAction.DeliverLocally));
            Assert.That(_router.IsForThisSwitch("23419999"), Is.True);
            Assert.That(_router.IsForThisSwitch("23410001"), Is.False);
        }

        [Test]
        public void NoManagementAddress_NothingIsForTheSwitchItself()
        {
            _router.ManagementAddress = null;

            Assert.That(_router.IsForThisSwitch(string.Empty), Is.False);
        }

        [Test]
        public void TheSwitchCore_UsesThisRouter()
        {
            // One route table, one decision: SwitchCore's router is built over the node's own table.
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340" };
            SwitchNode node = new SwitchNode(cfg);
            SwitchCore core = new SwitchCore(node);
            core.ManagementAddress = "23400000";

            Assert.That(core.Router.ManagementAddress, Is.EqualTo("23400000"));
            Assert.That(core.Router.Decide("23410001", 0).Cause, Is.EqualTo(X25Cause.NotObtainable), "an empty table routes nothing");
            cfg.Routing.AddRoute(new X121Route { Type = X121RouteType.Default, DestPort = 4 });
            Assert.That(core.Router.Decide("23410001", 0).Cause, Is.EqualTo(X25Cause.NotObtainable), "port 4 does not exist on this node");
        }
    }
}
