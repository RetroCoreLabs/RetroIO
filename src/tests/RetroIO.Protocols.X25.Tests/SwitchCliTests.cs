//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.X25.Common;
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
        public void Accounts_AreAddedAndRemovedAtTheConsole_AndSaved()
        {
            Assert.That(_cli.Process("x25 account add line1 range 23420000 23420099"), Is.EqualTo("Account line1 added\r\n"));
            Assert.That(_cli.Process("x25 account add card7 nui ACCT-7"), Is.EqualTo("Account card7 added\r\n"));
            Assert.That(_cli.Process("x25 account add card7 nui OTHER"), Does.Contain("already exists"));
            Assert.That(_cli.Process("x25 account add bad range 1"), Does.StartWith("Usage"));
            Assert.That(_cli.Process("x25 account add bad colour red"), Does.StartWith("Usage"));

            Assert.That(_node.Accounts.Count, Is.EqualTo(2));
            Assert.That(_node.Accounts[0].Covers("23420050"), Is.True);
            Assert.That(_node.Accounts[1].Nui, Is.EqualTo("ACCT-7"));

            // Saved the way the loader reads them back.
            string saved = _cli.Process("show running-config");
            Assert.That(saved, Does.Contain("[account.0]\r\nname = line1\r\naddr_start = 23420000\r\naddr_end = 23420099\r\n"));
            Assert.That(saved, Does.Contain("[account.1]\r\nname = card7\r\nnui = ACCT-7\r\n"));

            Assert.That(_cli.Process("x25 account del nobody"), Is.EqualTo("Error: no account named nobody\r\n"));
            Assert.That(_cli.Process("x25 account del line1"), Is.EqualTo("Account line1 removed\r\n"));
            Assert.That(_node.Accounts.Count, Is.EqualTo(1));
            Assert.That(_node.Config.Accounts.Count, Is.EqualTo(1));
            Assert.That(_cli.Process("show running-config"), Does.Not.Contain("line1"));
        }

        [Test]
        public void SavedAccounts_LoadBackIntoTheSameAccounts()
        {
            _cli.Process("x25 account add line1 range 23420000 23420099");
            _cli.Process("x25 account add card7 nui ACCT-7");
            string text = _cli.Process("show running-config").Replace("\r\n", "\n");

            SwitchConfig back = SwitchConfigLoader.Load(IniConfig.Parse(text.Split('\n')));
            Assert.That(back.Accounts.Count, Is.EqualTo(2));
            Assert.That(back.Accounts[0].Name, Is.EqualTo("line1"));
            Assert.That(back.Accounts[0].AddrStart, Is.EqualTo("23420000"));
            Assert.That(back.Accounts[0].AddrEnd, Is.EqualTo("23420099"));
            Assert.That(back.Accounts[1].Nui, Is.EqualTo("ACCT-7"));
        }

        /// <summary>
        /// A switch started from its own saved file is the same switch: every key the loader reads is written by
        /// <c>write memory</c> (show running-config is the same text) and comes back with the same value.
        /// </summary>
        [Test]
        public void SavedConfiguration_LoadsBackIntoTheSameSwitch()
        {
            string original =
                "[switch]\ndnic = 2342\nmax_hops = 9\nmgmt_port = 9100\nmonitor_port = 9123\npvc_record_minutes = 30\nsegment_size = 128\n" +
                "name = OSLO-1\nlocation = Oslo\ncoordinates = 59.9,10.7\ncontact = noc\nmgmt_addr = 23429999\ncdr_file = calls.cdr\n\n" +
                "[port.0]\ntype = access\ntransport = xot\nmode = dce\nport = 2000\npvc_base = 1\npvc_count = 2\nlci_base = 3\nlci_count = 4\n" +
                "iface_name = Serial1\nxot_pvc_role = initiator\nnui_required = yes\ndefault_nui = CARD7\ncharging_info = yes\naddr_start = 23420000\naddr_end = 23420099\n\n" +
                "[port.1]\ntype = trunk\ntransport = hdlc-tcp\nmode = dte\nhost = 10.0.0.2\nport = 2001\ntcp = listen\n" +
                "pvc_base = 1\npvc_count = 1\nlci_base = 2\nlci_count = 4\nlapb_t1 = 2500\nlapb_k = 3\n" +
                "peer_name = BERGEN\npeer_monitor_host = 10.0.0.9\npeer_monitor_port = 9124\npeer_mgmt_addr = 24429999\n\n" +
                "[port.2]\ntype = access\ntransport = hdlc-serial\nmode = dce\nport = 2002\ndevice = COM3\nbaud = 19200\n" +
                "databits = 7\nparity = even\nstopbits = 2\nflow = none\n\n" +
                "[route.0]\ntype = exact\naddress = 23400001\nport = 1\nbackup_port = 2\n\n" +
                "[route.1]\ntype = prefix\naddress = 2344\nprefix_len = 4\nport = 1\n\n" +
                "[pvc.0]\nsrc_port = 0\nsrc_lci = 2\ndst_port = 1\ndst_lci = 1\naccount = card7\n" +
                "remote_host = 10.0.0.5\nremote_iface = Serial0\nremote_lci = 7\n\n" +
                "[account.0]\nname = card7\nnui = CARD7\n\n" +
                "[user.0]\nname = ops\npassword = pw1\nlevel = 1\n";

            SwitchConfig a = SwitchConfigLoader.Load(IniConfig.Parse(original.Split('\n')));
            SwitchCli cli = new SwitchCli(new SwitchNode(a), () => 0L);
            string saved = cli.Process("show running-config").Replace("\r\n", "\n");
            SwitchConfig b = SwitchConfigLoader.Load(IniConfig.Parse(saved.Split('\n')));

            Assert.That(b.Dnic, Is.EqualTo("2342"));
            Assert.That(b.MaxHops, Is.EqualTo(9));
            Assert.That(b.MgmtPort, Is.EqualTo(9100));
            Assert.That(b.MonitorPort, Is.EqualTo(9123));
            Assert.That(b.PvcRecordMinutes, Is.EqualTo(30));
            Assert.That(b.SegmentSize, Is.EqualTo(128));
            Assert.That(b.Name, Is.EqualTo("OSLO-1"));
            Assert.That(b.Location, Is.EqualTo("Oslo"));
            Assert.That(b.Coordinates, Is.EqualTo("59.9,10.7"));
            Assert.That(b.Contact, Is.EqualTo("noc"));
            Assert.That(b.MgmtAddr, Is.EqualTo("23429999"));
            Assert.That(b.CdrFile, Is.EqualTo("calls.cdr"));

            PortConfig p0 = b.Ports[0], p1 = b.Ports[1], p2 = b.Ports[2];
            Assert.That(p0.PvcBase, Is.EqualTo(1));
            Assert.That(p0.PvcCount, Is.EqualTo(2));
            Assert.That(p0.LciBase, Is.EqualTo(3));
            Assert.That(p0.InterfaceName, Is.EqualTo("Serial1"));
            Assert.That(p0.XotPvcInitiator, Is.True);
            Assert.That(p0.NuiRequired, Is.True);
            Assert.That(p0.ChargingInfo, Is.True);
            Assert.That(p0.DefaultNui, Is.EqualTo("CARD7"));
            Assert.That(p0.AddrRangeStart, Is.EqualTo("23420000"));
            Assert.That(p1.Transport, Is.EqualTo(PortTransport.HdlcTcp));
            Assert.That(p1.Mode, Is.EqualTo(PortMode.Dte));
            Assert.That(p1.Host, Is.EqualTo("10.0.0.2"));
            Assert.That(p1.TcpRole, Is.EqualTo(PortTcpRole.Listen));
            Assert.That(p1.LapbT1Ms, Is.EqualTo(2500));
            Assert.That(p1.LapbK, Is.EqualTo(3));
            Assert.That(p1.PeerName, Is.EqualTo("BERGEN"));
            Assert.That(p1.PeerMonitorHost, Is.EqualTo("10.0.0.9"));
            Assert.That(p1.PeerMonitorPort, Is.EqualTo(9124));
            Assert.That(p1.PeerMgmtAddr, Is.EqualTo("24429999"));
            Assert.That(p2.SerialDevice, Is.EqualTo("COM3"));
            Assert.That(p2.SerialBaud, Is.EqualTo(19200u));
            Assert.That(p2.SerialDataBits, Is.EqualTo(7));
            Assert.That(p2.SerialParity, Is.EqualTo("even"));
            Assert.That(p2.SerialStopBits, Is.EqualTo(2));
            Assert.That(p2.SerialFlow, Is.EqualTo("none"));

            Assert.That(b.Pvcs.Count, Is.EqualTo(1));
            PvcConfig v = b.Pvcs[0];
            Assert.That(v.SourcePort, Is.EqualTo(0));
            Assert.That(v.SourceLci, Is.EqualTo(2));
            Assert.That(v.DestPort, Is.EqualTo(1));
            Assert.That(v.DestLci, Is.EqualTo(1));
            Assert.That(v.Account, Is.EqualTo("card7"));
            Assert.That(v.RemoteHost, Is.EqualTo("10.0.0.5"));
            Assert.That(v.RemoteInterface, Is.EqualTo("Serial0"));
            Assert.That(v.RemoteLci, Is.EqualTo(7));

            // Two routes and the PVC, the backup port on the first route still.
            Assert.That(b.Routing.Routes.Count, Is.EqualTo(3));
            Assert.That(b.Routing.Routes[0].Address, Is.EqualTo("23400001"));
            Assert.That(b.Routing.Routes[0].BackupPort, Is.EqualTo(2));
            Assert.That(b.Routing.Routes[1].PrefixLen, Is.EqualTo(4));
            Assert.That(b.Routing.Routes[1].BackupPort, Is.EqualTo(-1));

            Assert.That(b.Accounts.Count, Is.EqualTo(1));
            Assert.That(b.Accounts[0].Nui, Is.EqualTo("CARD7"));
            Assert.That(b.Users.Count, Is.EqualTo(1));
            Assert.That(b.Users[0].Name, Is.EqualTo("ops"));
            Assert.That(b.Users[0].Password, Is.EqualTo("pw1"));
            Assert.That(b.Users[0].Level, Is.EqualTo(1));
        }

        [Test]
        public void WriteMemory_WritesOnlyABareNameIntoTheConfigFolder()
        {
            Assert.That(_cli.Process("write memory x.conf"), Does.Contain("no configuration folder"));

            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "retroio-wm-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                _node.Config.ConfigDir = dir;
                Assert.That(_cli.Process("write memory ../escape.conf"), Is.EqualTo("Error: a name may not start with a dot\r\n"));
                Assert.That(_cli.Process("write memory sub/escape.conf"), Is.EqualTo("Error: a name may not contain a path\r\n"));
                Assert.That(_cli.Process("write memory sub\\escape.conf"), Is.EqualTo("Error: a name may not contain a path\r\n"));
                Assert.That(_cli.Process("write memory C:escape.conf"), Is.EqualTo("Error: a name may not contain a path\r\n"));
                Assert.That(_cli.Process("write memory .hidden"), Is.EqualTo("Error: a name may not start with a dot\r\n"));

                Assert.That(_cli.Process("write memory saved.conf"), Is.EqualTo("Config written to saved.conf\r\n"));
                Assert.That(System.IO.File.Exists(System.IO.Path.Combine(dir, "saved.conf")), Is.True);
            }
            finally
            {
                System.IO.Directory.Delete(dir, true);
            }
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
                "  show x25 invoice <account> [yyyy-mm] - The account's bill for a month\r\n" +
                "  x25 account add NAME range START END | nui NUI - Add an account\r\n" +
                "  x25 account del NAME - Remove an account (refused while it bills a call)\r\n" +
                "  quit                - Disconnect\r\n";

            Assert.That(_cli.Process("frobnicate"), Is.EqualTo(expected));
        }

        [Test]
        public void ShowAccount_NoneConfigured_ByteExact()
        {
            string expected =
                "Name             Reached by           Up Calls    Pkts       Segs       Duration\r\n" +
                "---------------- -------------------- -- -------- ---------- ---------- --------\r\n" +
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
