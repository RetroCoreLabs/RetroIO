//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Link.Lapb;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Common;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Tests for <see cref="SwitchConfigLoader"/>, using the real demo config (demo/switch_a.conf) as the
    /// fixture and verifying the C <c>sw_load_config</c> defaults and section-by-index semantics.
    /// </summary>
    [TestFixture]
    public class SwitchConfigLoaderTests
    {
        // The demo/switch_a.conf content (route sections deliberately ordered 0, 2, 1 in the file).
        private static readonly string[] SwitchA =
        {
            "[switch]",
            "dnic = 2340",
            "max_hops = 15",
            "mgmt_port = 9001",
            "name = Switch Alpha",
            "location = Local Lab",
            "mgmt_addr = 23400000",
            "[user.0]",
            "name = admin",
            "password = admin",
            "level = 1",
            "[port.0]",
            "type = access",
            "transport = xot",
            "mode = dce",
            "port = 2001",
            "packet_size = 128",
            "window_size = 7",
            "max_packet_size = 1024",
            "max_window_size = 7",
            "lci_base = 1",
            "lci_count = 2048",
            "[port.1]",
            "type = trunk",
            "transport = hdlc-tcp",
            "mode = dte",
            "host = 127.0.0.1",
            "port = 2003",
            "window_size = 7",
            "lci_count = 2048",
            "[port.2]",
            "type = access",
            "transport = xot",
            "mode = dce",
            "port = 2002",
            "lci_count = 32",
            "[port.3]",
            "type = access",
            "transport = xot",
            "mode = dce",
            "port = 2007",
            "lci_count = 2048",
            "[route.0]",
            "type = prefix",
            "address = 2340",
            "prefix_len = 4",
            "port = 0",
            "[route.2]",
            "type = prefix",
            "address = 2343",
            "prefix_len = 4",
            "port = 3",
            "[route.1]",
            "type = default",
            "port = 1",
            "[account.0]",
            "name = pad_users",
            "addr_start = 23400000",
            "addr_end = 23409999"
        };

        private static SwitchConfig Load(string[] lines)
        {
            return SwitchConfigLoader.Load(IniConfig.Parse(new List<string>(lines)));
        }

        [Test]
        public void Identity_LoadedFromSwitchSection()
        {
            SwitchConfig sw = Load(SwitchA);

            Assert.That(sw.Dnic, Is.EqualTo("2340"));
            Assert.That(sw.MaxHops, Is.EqualTo(15));
            Assert.That(sw.MgmtPort, Is.EqualTo(9001));
            Assert.That(sw.Name, Is.EqualTo("Switch Alpha"));
            Assert.That(sw.Location, Is.EqualTo("Local Lab"));
            Assert.That(sw.MgmtAddr, Is.EqualTo("23400000"));
        }

        [Test]
        public void Ports_LoadedInOrder_WithFieldsAndDefaults()
        {
            SwitchConfig sw = Load(SwitchA);

            Assert.That(sw.Ports.Count, Is.EqualTo(4));

            Assert.That(sw.Ports[0].Id, Is.EqualTo(0));
            Assert.That(sw.Ports[0].Type, Is.EqualTo(PortType.Access));
            Assert.That(sw.Ports[0].Transport, Is.EqualTo(PortTransport.Xot));
            Assert.That(sw.Ports[0].Mode, Is.EqualTo(PortMode.Dce));
            Assert.That(sw.Ports[0].Port, Is.EqualTo(2001));
            Assert.That(sw.Ports[0].LciCount, Is.EqualTo(2048));

            Assert.That(sw.Ports[1].Type, Is.EqualTo(PortType.Trunk));
            Assert.That(sw.Ports[1].Transport, Is.EqualTo(PortTransport.HdlcTcp));
            Assert.That(sw.Ports[1].Mode, Is.EqualTo(PortMode.Dte));
            Assert.That(sw.Ports[1].Host, Is.EqualTo("127.0.0.1"));
            Assert.That(sw.Ports[1].Port, Is.EqualTo(2003));

            Assert.That(sw.Ports[2].LciCount, Is.EqualTo(32));
            Assert.That(sw.Ports[3].Port, Is.EqualTo(2007));

            // Defaults applied where the section omitted them (port.2 has no max_packet_size etc.).
            Assert.That(sw.Ports[2].MaxPacketSize, Is.EqualTo(1024));
            Assert.That(sw.Ports[2].DefaultPacketSize, Is.EqualTo(128));
            Assert.That(sw.Ports[2].LciBase, Is.EqualTo(1));
        }

        [Test]
        public void PeerDeclaration_LoadedForMap_AndHostDefaultsToPortHost()
        {
            // A trunk that declares a neighbour for the network map; peer_monitor_host omitted so it should
            // default to the port's host.
            string[] lines =
            {
                "[switch]",
                "dnic = 2352",
                "name = SwitchA",
                "[port.0]",
                "type = access",
                "port = 2001",
                "[port.1]",
                "type = trunk",
                "mode = dte",
                "host = 192.168.1.181",
                "port = 2210",
                "peer_name = SwitchB",
                "peer_monitor_port = 9102"
            };

            SwitchConfig sw = Load(lines);

            // The access port declares no peer.
            Assert.That(sw.Ports[0].PeerName, Is.EqualTo(string.Empty));

            // The trunk declares SwitchB; peer_monitor_host defaults to the port host.
            Assert.That(sw.Ports[1].PeerName, Is.EqualTo("SwitchB"));
            Assert.That(sw.Ports[1].PeerMonitorPort, Is.EqualTo(9102));
            Assert.That(sw.Ports[1].PeerMonitorHost, Is.EqualTo("192.168.1.181"));
        }

        [Test]
        public void Routes_LoadedByIndex_AndResolveCorrectly()
        {
            SwitchConfig sw = Load(SwitchA);

            // route.0 prefix 2340 -> 0, route.1 default -> 1, route.2 prefix 2343 -> 3.
            Assert.That(sw.Routing.Routes.Count, Is.EqualTo(3));

            Assert.That(sw.Routing.TryLookup("23401234", 9, out int a), Is.True);
            Assert.That(a, Is.EqualTo(0), "2340 prefix");

            Assert.That(sw.Routing.TryLookup("23431234", 9, out int b), Is.True);
            Assert.That(b, Is.EqualTo(3), "2343 prefix");

            Assert.That(sw.Routing.TryLookup("99999999", 9, out int c), Is.True);
            Assert.That(c, Is.EqualTo(1), "default");
        }

        [Test]
        public void Users_And_Accounts_Loaded()
        {
            SwitchConfig sw = Load(SwitchA);

            Assert.That(sw.Users.Count, Is.EqualTo(1));
            Assert.That(sw.Users[0].Name, Is.EqualTo("admin"));
            Assert.That(sw.Users[0].Password, Is.EqualTo("admin"));
            Assert.That(sw.Users[0].Level, Is.EqualTo(1));

            Assert.That(sw.Accounts.Count, Is.EqualTo(1));
            Assert.That(sw.Accounts[0].Name, Is.EqualTo("pad_users"));
            Assert.That(sw.Accounts[0].AddrStart, Is.EqualTo("23400000"));
            Assert.That(sw.Accounts[0].AddrEnd, Is.EqualTo("23409999"));
        }

        [Test]
        public void WindowSize_DefaultsTo2_WhenOmitted()
        {
            // A port with no window_size must default to 2 (the C default), not 7.
            SwitchConfig sw = Load(new[]
            {
                "[port.0]",
                "type = access"
            });

            Assert.That(sw.Ports.Count, Is.EqualTo(1));
            Assert.That(sw.Ports[0].DefaultWindowSize, Is.EqualTo(2));
            Assert.That(sw.Ports[0].Port, Is.EqualTo(1998), "default TCP port");
            Assert.That(sw.Ports[0].Transport, Is.EqualTo(PortTransport.Xot), "default transport");
            Assert.That(sw.Ports[0].Mode, Is.EqualTo(PortMode.Dce), "default mode");
        }

        [Test]
        public void HuntGroups_And_Pvcs_Loaded()
        {
            SwitchConfig sw = Load(new[]
            {
                "[hunt.0]",
                "members = 1, 2, 3",
                "[route.0]",
                "type = hunt",
                "address = 555",
                "hunt_group = 0",
                "[pvc.0]",
                "src_port = 2",
                "src_lci = 5",
                "dst_port = 4",
                "dst_lci = 6"
            });

            Assert.That(sw.Routing.HuntGroups.Count, Is.EqualTo(1));
            Assert.That(sw.Routing.HuntGroups[0].Members, Is.EqualTo(new List<int> { 1, 2, 3 }));

            // Hunt route round-robins across members.
            sw.Routing.TryLookup("555", 9, out int h1);
            sw.Routing.TryLookup("555", 9, out int h2);
            Assert.That(h1, Is.EqualTo(1));
            Assert.That(h2, Is.EqualTo(2));

            // The PVC entry is present in the routing table (a route of type Pvc).
            bool foundPvc = false;
            for (int i = 0; i < sw.Routing.Routes.Count; i++)
            {
                if (sw.Routing.Routes[i].Type == X121RouteType.Pvc)
                {
                    foundPvc = true;
                    Assert.That(sw.Routing.Routes[i].SourcePort, Is.EqualTo(2));
                    Assert.That(sw.Routing.Routes[i].SourceLci, Is.EqualTo(5));
                    Assert.That(sw.Routing.Routes[i].DestPort, Is.EqualTo(4));
                    Assert.That(sw.Routing.Routes[i].DestLci, Is.EqualTo(6));
                }
            }

            Assert.That(foundPvc, Is.True, "PVC route present");
        }
    }
}
