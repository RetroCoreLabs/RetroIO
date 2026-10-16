using NUnit.Framework;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Management;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Byte-exact tests for the switch management CLI renderer. A fixed clock and a known switch state make
    /// the output fully reproducible so the exact management-protocol formats can be asserted.
    /// </summary>
    [TestFixture]
    public class SwitchCliTests
    {
        private SwitchNode _node = null!;
        private SwitchCli _cli = null!;

        [SetUp]
        public void SetUp()
        {
            SwitchConfig cfg = new SwitchConfig
            {
                Dnic = "2340",
                Name = "Switch Alpha",
                Location = "Local Lab",
                MaxHops = 15,
                MgmtPort = 9001
            };
            cfg.Ports.Add(new PortConfig { Id = 0, Type = PortType.Access, Transport = PortTransport.Xot, Mode = PortMode.Dce, Port = 2001 });
            cfg.Ports.Add(new PortConfig { Id = 1, Type = PortType.Trunk, Transport = PortTransport.HdlcTcp, Mode = PortMode.Dte, Host = "127.0.0.1", Port = 2003 });
            cfg.Routing.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = "2341", PrefixLen = 4, DestPort = 1 });

            _node = new SwitchNode(cfg) { UptimeStartMs = 0 };
            LoopbackSwitchPort p0 = new LoopbackSwitchPort(cfg.Ports[0]);
            LoopbackSwitchPort p1 = new LoopbackSwitchPort(cfg.Ports[1]);
            p0.Start();
            p1.Start();
            _node.AddPort(p0);
            _node.AddPort(p1);

            // Fixed clock: 3661 s of uptime -> 1h 1m 1s.
            _cli = new SwitchCli(_node, () => 3661000L);
        }

        [Test]
        public void ShowInfo_ByteExact()
        {
            string expected =
                "Name:        Switch Alpha\r\n" +
                "DNIC:        2340\r\n" +
                "Location:    Local Lab\r\n" +
                "Coordinates: (not set)\r\n" +
                "Contact:     (not set)\r\n" +
                "Uptime:      3661s\r\n" +
                "Ports:       2\r\n" +
                "Routes:      1\r\n";

            Assert.That(_cli.Process("show info"), Is.EqualTo(expected));
        }

        [Test]
        public void ShowStats_ByteExact()
        {
            string expected =
                "Switch: Switch Alpha (DNIC 2340)\r\n" +
                "Location: Local Lab\r\n" +
                "Uptime: 1h 1m 1s\r\n" +
                "\r\n" +
                "Ports: 2 active, Max hops: 15\r\n" +
                "Active VCs: 0\r\n" +
                "\r\n" +
                "Calls:\r\n" +
                "  Incoming:  0\r\n" +
                "  Outgoing:  0\r\n" +
                "  Cleared:   0\r\n" +
                "\r\n" +
                "Packets:\r\n" +
                "  Received:  0\r\n" +
                "  Sent:      0\r\n" +
                "\r\n" +
                "Bytes forwarded: 0\r\n" +
                "Bytes reversed:  0\r\n" +
                "Total bytes:     0\r\n";

            Assert.That(_cli.Process("show x25 stats"), Is.EqualTo(expected));
        }

        [Test]
        public void ShowRoute_HeaderAndRow_ByteExact()
        {
            string expected =
                "Type     Address          Port  Prefix\r\n" +
                "-------- ---------------- ----- ------\r\n" +
                "prefix   2341                 1 4\r\n";

            Assert.That(_cli.Process("show x25 route"), Is.EqualTo(expected));
        }

        [Test]
        public void ShowPorts_HeaderAndRows_ByteExact()
        {
            string expected =
                "Port Type   Mode Transport Active CallsIn CallsOut\r\n" +
                "---- ------ ---- --------- ------ ------- --------\r\n" +
                "   0 access DCE  xot       yes          0        0\r\n" +
                "   1 trunk  DTE  hdlc-tcp  yes          0        0\r\n";

            Assert.That(_cli.Process("show x25 ports"), Is.EqualTo(expected));
        }

        [Test]
        public void Help_ReturnedForUnknownCommand_ByteExact()
        {
            string expected =
                "Commands:\r\n" +
                "  show info           - Display switch identity and uptime\r\n" +
                "  show x25 route      - Display routing table\r\n" +
                "  show x25 vc         - Display active virtual circuits\r\n" +
                "  show x25 pvc        - Display PVC cross-connects\r\n" +
                "  show x25 port       - Display port summary\r\n" +
                "  show x25 port <N>   - Display detailed port stats\r\n" +
                "  show x25 hunt       - Display hunt groups\r\n" +
                "  show x25 stats      - Display switch statistics\r\n" +
                "  show running-config - Display current config (INI)\r\n" +
                "  write memory <file> - Save running config to file\r\n" +
                "  clear x25 vc P L    - Clear VC on port P, LCI L\r\n" +
                "  x25 route add ...   - Add route (prefix/exact/dnic/default)\r\n" +
                "  x25 route del N     - Delete route index N\r\n" +
                "  debug x25 packets   - Enable packet debug logging\r\n" +
                "  debug x25 trace     - Enable trace logging (hex dumps)\r\n" +
                "  no debug            - Disable debug logging\r\n" +
                "  show x25 account    - Display account statistics\r\n" +
                "  quit                - Disconnect\r\n";

            Assert.That(_cli.Process("frobnicate"), Is.EqualTo(expected));
        }

        [Test]
        public void ShowAccount_NoneConfigured_ByteExact()
        {
            string expected =
                "Name             AddrRange            Calls    Pkts       Bytes      Duration\r\n" +
                "---------------- -------------------- -------- ---------- ---------- --------\r\n" +
                "(no accounts configured)\r\n";

            Assert.That(_cli.Process("show x25 account"), Is.EqualTo(expected));
        }

        [Test]
        public void RouteAdd_Prefix_AddsAndConfirms()
        {
            Assert.That(_cli.Process("x25 route add prefix 2343 4 3"), Is.EqualTo("Route added\r\n"));
            Assert.That(_node.Config.Routing.Routes.Count, Is.EqualTo(2));
            Assert.That(_node.Config.Routing.TryLookup("23430001", 9, out int port), Is.True);
            Assert.That(port, Is.EqualTo(3));
        }

        [Test]
        public void RouteDel_ValidAndInvalid()
        {
            Assert.That(_cli.Process("x25 route del 0"), Is.EqualTo("Route 0 deleted\r\n"));
            Assert.That(_node.Config.Routing.Routes.Count, Is.EqualTo(0));
            Assert.That(_cli.Process("x25 route del 5"), Is.EqualTo("Invalid route index\r\n"));
        }

        [Test]
        public void ClearVc_UsesHandler()
        {
            _cli.ClearVcHandler = (port, lci) => port == 1 && lci == 5;
            Assert.That(_cli.Process("clear x25 vc 1 5"), Is.EqualTo("VC cleared\r\n"));
            Assert.That(_cli.Process("clear x25 vc 2 9"), Is.EqualTo("VC not found\r\n"));
            Assert.That(_cli.Process("clear x25 vc x"), Is.EqualTo("Usage: clear x25 vc <port> <lci>\r\n"));
        }

        [Test]
        public void Debug_And_Reload_FixedMessages()
        {
            Assert.That(_cli.Process("debug x25 packets"), Is.EqualTo("Debug logging enabled\r\n"));
            Assert.That(_cli.Process("debug x25 trace"), Is.EqualTo("Trace logging enabled\r\n"));
            Assert.That(_cli.Process("no debug"), Is.EqualTo("Debug logging disabled\r\n"));
            Assert.That(_cli.Process("reload"), Is.EqualTo("Reload not supported at runtime (restart process with new config)\r\n"));
        }
    }
}
