//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Management;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Byte-exact tests for the machine management API, the async event formatters, and the management
    /// session's login handshake and request funnel.
    /// </summary>
    [TestFixture]
    public class NmcApiTests
    {
        private SwitchNode _node = null!;

        [SetUp]
        public void SetUp()
        {
            SwitchConfig cfg = new SwitchConfig
            {
                Dnic = "2340",
                Name = "Switch Alpha",
                Location = "Local Lab",
                Coordinates = "12N,34E"
            };
            cfg.Ports.Add(new PortConfig { Id = 0, Type = PortType.Access, Transport = PortTransport.Xot, Mode = PortMode.Dce });
            cfg.Routing.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = "2341", PrefixLen = 4, DestPort = 1 });
            cfg.Accounts.Add(new AccountConfig { Name = "pad_users", AddrStart = "23400000", AddrEnd = "23409999" });

            _node = new SwitchNode(cfg) { UptimeStartMs = 0 };
            LoopbackSwitchPort p0 = new LoopbackSwitchPort(cfg.Ports[0]);
            p0.Start();
            _node.AddPort(p0);
        }

        private SwitchApiProcessor Api()
        {
            return new SwitchApiProcessor(_node, () => 5000L);
        }

        [Test]
        public void Api_Ping()
        {
            Assert.That(Api().Process("PING"), Is.EqualTo("PONG\n"));
        }

        [Test]
        public void Api_GetInfo_ByteExact()
        {
            Assert.That(Api().Process("GET_INFO"),
                Is.EqualTo("INFO dnic=2340|name=Switch Alpha|location=Local Lab|coords=12N,34E\n"));
        }

        [Test]
        public void Api_GetStats_ByteExact()
        {
            Assert.That(Api().Process("GET_STATS"),
                Is.EqualTo("STATS dnic=2340|ports=1|stitches=0|calls=0|pkts_in=0|pkts_out=0|bytes=0|up=5\n"));
        }

        [Test]
        public void Api_GetPorts_ByteExact()
        {
            Assert.That(Api().Process("GET_PORTS"),
                Is.EqualTo("PORT id=0 type=access mode=dce active=yes calls_in=0 calls_out=0\n"));
        }

        [Test]
        public void Api_GetRoutes_ByteExact()
        {
            Assert.That(Api().Process("GET_ROUTES"),
                Is.EqualTo("ROUTE type=prefix addr=2341 port=1 prefix=4\n"));
        }

        [Test]
        public void Api_GetTopology_Empty_WhenNoPeers()
        {
            // The setup port is an access port with no declared peer, so no NEIGHBOUR lines.
            Assert.That(Api().Process("GET_TOPOLOGY"), Is.EqualTo(string.Empty));
        }

        [Test]
        public void Api_GetTopology_ByteExact()
        {
            // Add a trunk with a declared neighbour and its management address; a started loopback port
            // reports PeerConnected, so up=1. Must be byte-identical to the C switch's GET_TOPOLOGY.
            PortConfig trunk = new PortConfig
            {
                Id = 1,
                Type = PortType.Trunk,
                Mode = PortMode.Dte,
                PeerName = "Switch-B",
                PeerMgmtAddr = "234100"
            };
            LoopbackSwitchPort p1 = new LoopbackSwitchPort(trunk);
            p1.Start();
            _node.AddPort(p1);

            Assert.That(Api().Process("GET_TOPOLOGY"),
                Is.EqualTo("NEIGHBOUR name=Switch-B mgmt=234100 via=1 up=1\n"));
        }

        [Test]
        public void Api_GetAccounts_ByteExact()
        {
            Assert.That(Api().Process("GET_ACCOUNTS"),
                // x25emu's GET_ACCOUNTS line (tools/x25switch.c), field for field.
                Is.EqualTo("ACCOUNT name=pad_users|addr_start=23400000|addr_end=23409999|nui=|active=0|calls_made=0|calls_recv=0"
                    + "|pkts_sent=0|pkts_recv=0|bytes_sent=0|bytes_recv=0|segs_sent=0|segs_recv=0|up=0\n"));
        }

        [Test]
        public void Api_SubscribeUnsubscribe_And_Unknown()
        {
            Assert.That(Api().Process("SUBSCRIBE"), Is.EqualTo("OK subscribed\n"));
            Assert.That(Api().Process("UNSUBSCRIBE"), Is.EqualTo("OK unsubscribed\n"));
            Assert.That(Api().Process("FROB"), Is.EqualTo(string.Empty));
        }

        [Test]
        public void ReadOnlyUser_MayLookButNotChange()
        {
            // [user.N] level 0 is read-only; level 1 may change things. A command not on the read-only list is refused.
            Assert.That(MgmtSession.LevelAllows(0, "show x25 route"), Is.True);
            Assert.That(MgmtSession.LevelAllows(0, "show x25 account"), Is.True);
            Assert.That(MgmtSession.LevelAllows(0, "help"), Is.True);
            Assert.That(MgmtSession.LevelAllows(0, "x25 route add exact 1 1"), Is.False);
            Assert.That(MgmtSession.LevelAllows(0, "clear x25 vc 0 1"), Is.False);
            Assert.That(MgmtSession.LevelAllows(0, "write memory a.conf"), Is.False);
            Assert.That(MgmtSession.LevelAllows(0, "x25 account del a"), Is.False);
            Assert.That(MgmtSession.LevelAllows(0, "a command nobody has written yet"), Is.False);
            Assert.That(MgmtSession.LevelAllows(1, "write memory a.conf"), Is.True);

            // Through a session: a level 0 login is refused a change, and the route table is untouched.
            SwitchCli cli = new SwitchCli(_node, () => 5000L);
            List<UserConfig> users = new List<UserConfig> { new UserConfig { Name = "look", Password = "pw", Level = 0 } };
            MgmtSession s = new MgmtSession(cli, Api(), users, "Switch Alpha", "2340");
            s.FeedLine("look", false);
            s.FeedLine("pw", false);
            int routesBefore = _node.Config.Routing.Routes.Count;
            Assert.That(s.FeedLine("x25 route add exact 23499999 1", false).Output,
                Is.EqualTo("Error: your user is read-only (level 0)\r\n> "));
            Assert.That(_node.Config.Routing.Routes.Count, Is.EqualTo(routesBefore));
        }

        [Test]
        public void Events_ByteExact()
        {
            Assert.That(ManagementEvents.CallSetup("23400001", "23410001", 0, 1),
                Is.EqualTo("EVENT call_setup calling=23400001 called=23410001 in_port=0 out_port=1\n"));
            Assert.That(ManagementEvents.CallClear(1, 5, 0x0D, 0x00),
                Is.EqualTo("EVENT call_clear port=1 lci=5 cause=0D diag=00\n"));
        }

        private MgmtSession NewSession()
        {
            SwitchCli cli = new SwitchCli(_node, () => 5000L);
            SwitchApiProcessor api = Api();
            List<UserConfig> users = new List<UserConfig> { new UserConfig { Name = "admin", Password = "admin", Level = 1 } };
            return new MgmtSession(cli, api, users, "Switch Alpha", "2340");
        }

        [Test]
        public void Session_Banner_ByteExact()
        {
            Assert.That(NewSession().Banner(),
                Is.EqualTo("Switch Alpha (DNIC 2340) - X.25 Switch Management\r\nUsername: "));
        }

        [Test]
        public void Session_LoginSuccess_ThenTextAndApi()
        {
            MgmtSession s = NewSession();

            MgmtResult r1 = s.FeedLine("admin", false);
            Assert.That(r1.Output, Is.EqualTo("\r\nPassword: "));
            Assert.That(s.State, Is.EqualTo(MgmtAuthState.Password));

            MgmtResult r2 = s.FeedLine("admin", false);
            Assert.That(r2.Output, Is.EqualTo("\r\nWelcome, admin.\r\n> "));
            Assert.That(s.State, Is.EqualTo(MgmtAuthState.Authenticated));

            // Plain text -> CLI (with prompt), Q=0.
            MgmtResult r3 = s.FeedLine("show info", false);
            Assert.That(r3.QBit, Is.False);
            Assert.That(r3.Output, Does.Contain("Name:        Switch Alpha"));
            Assert.That(r3.Output, Does.EndWith("> "));

            // Qualifier-bit -> machine API, Q=1.
            MgmtResult r4 = s.FeedLine("GET_INFO", true);
            Assert.That(r4.QBit, Is.True);
            Assert.That(r4.Output, Does.StartWith("INFO dnic=2340"));

            MgmtResult r5 = s.FeedLine("SUBSCRIBE", true);
            Assert.That(r5.Output, Is.EqualTo("OK subscribed\n"));
            Assert.That(s.Subscribed, Is.True);

            MgmtResult r6 = s.FeedLine("quit", false);
            Assert.That(r6.Clear, Is.True);
        }

        [Test]
        public void Session_LoginFailure_ReturnsToUsername()
        {
            MgmtSession s = NewSession();
            s.FeedLine("admin", false);
            MgmtResult r = s.FeedLine("wrongpass", false);

            Assert.That(r.Output, Is.EqualTo("\r\nLogin incorrect.\r\nUsername: "));
            Assert.That(s.State, Is.EqualTo(MgmtAuthState.Username));
        }

        [Test]
        public void Session_QBitBeforeAuth_IsIgnored()
        {
            MgmtSession s = NewSession();
            MgmtResult r = s.FeedLine("GET_INFO", true);
            Assert.That(r.Output, Is.EqualTo(string.Empty));
            Assert.That(s.State, Is.EqualTo(MgmtAuthState.Username));
        }
    }
}
