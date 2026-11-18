//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RetroIO.Protocols.X25.Common;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The routing part of the switch file: the old <c>type =</c> keys still load, the new easy keys
    /// (<c>[network] own_dnic</c>, <c>[route.N] dnic = / port = name</c>) load into the same table, and a
    /// route the loader does not understand stops the load and names the line.
    /// </summary>
    [TestFixture]
    public class SwitchRoutingConfigTests
    {
        private static SwitchConfig Load(params string[] lines)
        {
            return SwitchConfigLoader.Load(IniConfig.Parse(new List<string>(lines)));
        }

        private static readonly string[] TwoPorts =
        {
            "[port.0]", "type = access", "name = local-ports", "port = 1998",
            "[port.1]", "type = trunk", "name = x75-gw", "host = 10.0.0.1", "port = 1999",
        };

        private static string[] With(params string[] more)
        {
            List<string> all = new List<string>(TwoPorts);
            all.AddRange(more);
            return all.ToArray();
        }

        [Test]
        public void TheOldTypeKeys_StillLoad()
        {
            SwitchConfig sw = Load(With(
                "[route.0]", "type = exact", "address = 23420001", "port = 0",
                "[route.1]", "type = prefix", "address = 2342", "prefix_len = 4", "port = 1", "backup_port = 0",
                "[route.2]", "type = dnic", "dnic = 2402", "port = 1",
                "[route.3]", "type = default", "port = 1",
                "[route.4]", "type = hunt", "address = 555", "hunt_group = 0",
                "[hunt.0]", "members = 0,1"));

            IReadOnlyList<X121Route> r = sw.Routing.Routes;
            Assert.That(r.Count, Is.EqualTo(5));
            Assert.That(r[0].Type, Is.EqualTo(X121RouteType.Exact));
            Assert.That(r[0].Address, Is.EqualTo("23420001"));
            Assert.That(r[1].Type, Is.EqualTo(X121RouteType.Prefix));
            Assert.That(r[1].PrefixDigits, Is.EqualTo("2342"));
            Assert.That(r[1].BackupPort, Is.EqualTo(0));
            Assert.That(r[2].Type, Is.EqualTo(X121RouteType.Dnic));
            Assert.That(r[3].Type, Is.EqualTo(X121RouteType.Default));
            Assert.That(r[4].Type, Is.EqualTo(X121RouteType.Hunt));
            Assert.That(sw.Routing.HuntGroups[0].Members, Is.EqualTo(new[] { 0, 1 }));
        }

        [Test]
        public void TheEasyKeys_Load_WithTheOwnNetworkAndPortsByName()
        {
            SwitchConfig sw = Load(With(
                "[network]", "own_dnic = 2422, 24221", "international_prefix = 0",
                "[route.0]", "name = Sweden", "dnic = 2402", "port = x75-gw", "backup_ports = local-ports", "note = via Tor",
                "[route.1]", "name = Denmark", "dcc = 238", "port = x75-gw",
                "[route.2]", "name = Here", "prefix = 242201", "port = local-ports", "enabled = no",
                "[route.3]", "name = Rest", "default = yes", "port = x75-gw",
                "[route.4]", "name = Odd", "regex = ^9[0-9]+$", "port = local-ports"));

            Assert.That(sw.Routing.OwnDnics, Is.EqualTo(new[] { "2422", "24221" }));
            Assert.That(sw.Dnic, Is.EqualTo("2422"), "[network] own_dnic is the switch's DNIC");
            Assert.That(sw.Routing.InternationalPrefix, Is.EqualTo("0"));
            Assert.That(sw.Routing.GetPortName(1), Is.EqualTo("x75-gw"));

            IReadOnlyList<X121Route> r = sw.Routing.Routes;
            Assert.That(r[0].Name, Is.EqualTo("Sweden"));
            Assert.That(r[0].Type, Is.EqualTo(X121RouteType.Dnic));
            Assert.That(r[0].DestPort, Is.EqualTo(1));
            Assert.That(r[0].BackupPorts, Is.EqualTo(new[] { 0 }));
            Assert.That(r[0].Note, Is.EqualTo("via Tor"));
            Assert.That(r[1].Type, Is.EqualTo(X121RouteType.Dcc));
            Assert.That(r[1].Dcc, Is.EqualTo("238"));
            Assert.That(r[2].Type, Is.EqualTo(X121RouteType.Prefix));
            Assert.That(r[2].Enabled, Is.False);
            Assert.That(r[3].Type, Is.EqualTo(X121RouteType.Default));
            Assert.That(r[4].Type, Is.EqualTo(X121RouteType.Regex));
            Assert.That(r[4].Pattern, Is.EqualTo("^9[0-9]+$"));
        }

        [Test]
        public void TheSwitchDnicKey_IsTheOwnDnic_WhenThereIsNoNetworkSection()
        {
            SwitchConfig sw = Load(With("[switch]", "dnic = 2422"));

            Assert.That(sw.Routing.OwnDnics, Is.EqualTo(new[] { "2422" }));
        }

        [Test]
        public void AnUnknownRouteType_IsALoadError_NamingTheSectionAndTheLine()
        {
            string[] lines = With("[route.0]", "type = nearest", "port = 1");

            InvalidDataException? ex = Assert.Throws<InvalidDataException>(() => Load(lines));

            Assert.That(ex!.Message, Does.Contain("[route.0]"));
            Assert.That(ex.Message, Does.Contain("nearest"));
            Assert.That(ex.Message, Does.Contain("line " + (TwoPorts.Length + 2)), "the type line is the second line of the section");
        }

        [Test]
        public void ARouteThatSaysNothingToMatch_IsALoadError()
        {
            Assert.Throws<InvalidDataException>(() => Load(With("[route.0]", "name = Empty", "port = 1")));
        }

        [Test]
        public void ARouteToAPortThatDoesNotExist_IsALoadError()
        {
            InvalidDataException? ex = Assert.Throws<InvalidDataException>(() => Load(With("[route.0]", "dnic = 2402", "port = nowhere")));
            Assert.That(ex!.Message, Does.Contain("nowhere"));
        }

        [Test]
        public void ABadDnic_IsALoadError()
        {
            Assert.Throws<InvalidDataException>(() => Load(With("[route.0]", "dnic = 240", "port = 1")));
            Assert.Throws<InvalidDataException>(() => Load(With("[network]", "own_dnic = 99")));
        }

        [Test]
        public void TheRegexType_CanBeWrittenWithTheOldTypeKey()
        {
            SwitchConfig sw = Load(With("[route.0]", "type = regex", "address = ^24.*", "port = 1"));

            Assert.That(sw.Routing.Routes[0].Type, Is.EqualTo(X121RouteType.Regex));
            Assert.That(sw.Routing.Routes[0].Pattern, Is.EqualTo("^24.*"));
        }

        [Test]
        public void APortsCalledAddressForm_IsRead_EveryPortDefaultsToInternational()
        {
            SwitchConfig sw = Load(With("[port.2]", "type = access", "name = terminals", "called_address = national"));

            // Since 2026-11-18 an access port gives its machine the full number too (decided by Ronny; x25emu did so
            // already). Before, it kept the number as the caller typed it.
            Assert.That(sw.Ports[0].CalledAddressForm, Is.EqualTo(X121AddressForm.International), "access port default: the full international number");
            Assert.That(sw.Ports[1].CalledAddressForm, Is.EqualTo(X121AddressForm.International), "trunk default: the full international number");
            Assert.That(sw.Ports[2].CalledAddressForm, Is.EqualTo(X121AddressForm.National));
        }
    }
}
