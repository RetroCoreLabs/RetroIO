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
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// <see cref="SwitchRouteWriter"/>: the [route.N] sections are written so the loader reads the same routes back,
    /// and nothing outside them changes. Added 2026-11-21 for the RetroX25 routing screen.
    /// </summary>
    [TestFixture]
    public class SwitchRouteWriterTests
    {
        private const string Sample =
            "# header comment\n" +
            "[switch]\n" +
            "name = S\n" +
            "dnic = 2350\n" +
            "\n" +
            "[port.0]\n" +
            "name = pad\n" +
            "type = access\n" +
            "transport = xot\n" +
            "port = 2200\n" +
            "\n" +
            "[port.1]\n" +
            "name = trunk\n" +
            "type = trunk\n" +
            "transport = xot\n" +
            "port = 2201\n" +
            "\n" +
            "# routes follow\n" +
            "[route.0]\n" +
            "# inside the route\n" +
            "type = prefix\n" +
            "address = 234\n" +
            "prefix_len = 3\n" +
            "port = 1\n" +
            "\n" +
            "[route.1]\n" +
            "name = to the pad\n" +
            "number = 23500001\n" +
            "port = pad\n" +
            "direction = incoming\n" +
            "\n" +
            "[route.2]\n" +
            "type = prefix\n" +
            "address = 2351\n" +
            "prefix_len = 3\n" +
            "port = 1\n" +
            "backup_ports = 0\n" +
            "enabled = no\n" +
            "\n" +
            "[route.3]\n" +
            "default = yes\n" +
            "port = trunk\n" +
            "\n" +
            "# after the routes\n" +
            "[user.0]\n" +
            "name = admin\n";

        private static List<string> Lines(string text)
        {
            return new List<string>(text.Split('\n'));
        }

        private static SwitchConfig Load(List<string> lines)
        {
            return SwitchConfigLoader.Load(IniConfig.Parse(lines));
        }

        private static void AssertSameRoutes(IReadOnlyList<X121Route> expected, IReadOnlyList<X121Route> actual)
        {
            Assert.That(actual.Count, Is.EqualTo(expected.Count), "route count");
            for (int i = 0; i < expected.Count; i++)
            {
                X121Route e = expected[i];
                X121Route a = actual[i];
                string at = "route " + i;
                Assert.That(a.Type, Is.EqualTo(e.Type), at + " type");
                Assert.That(a.Name, Is.EqualTo(e.Name), at + " name");
                Assert.That(a.Note, Is.EqualTo(e.Note), at + " note");
                Assert.That(a.Address, Is.EqualTo(e.Address), at + " address");
                Assert.That(a.PrefixLen, Is.EqualTo(e.PrefixLen), at + " prefix length");
                Assert.That(a.Dnic, Is.EqualTo(e.Dnic), at + " dnic");
                Assert.That(a.Dcc, Is.EqualTo(e.Dcc), at + " dcc");
                Assert.That(a.Pattern, Is.EqualTo(e.Pattern), at + " pattern");
                Assert.That(a.DestPort, Is.EqualTo(e.DestPort), at + " port");
                Assert.That(a.BackupPorts, Is.EqualTo(e.BackupPorts), at + " backup ports");
                Assert.That(a.Direction, Is.EqualTo(e.Direction), at + " direction");
                Assert.That(a.Enabled, Is.EqualTo(e.Enabled), at + " enabled");
                Assert.That(a.HuntGroupId, Is.EqualTo(e.HuntGroupId), at + " hunt group");
            }
        }

        [Test]
        public void WritingTheRoutesBack_LoadsTheSameRoutes()
        {
            List<string> lines = Lines(Sample);
            SwitchConfig before = Load(lines);

            SwitchRouteWriter.ReplaceRoutes(lines, before.Routing);

            AssertSameRoutes(before.Routing.Routes, Load(lines).Routing.Routes);
        }

        [Test]
        public void EveryLineOutsideTheRouteSections_IsKept()
        {
            List<string> lines = Lines(Sample);
            SwitchConfig before = Load(lines);

            SwitchRouteWriter.ReplaceRoutes(lines, before.Routing);
            string text = string.Join("\n", lines);

            Assert.That(text, Does.StartWith("# header comment\n[switch]\nname = S\ndnic = 2350\n"));
            Assert.That(text, Does.Contain("# routes follow\n[route.0]\n"), "a comment above the first route stays against it");
            Assert.That(text, Does.Contain("# after the routes\n[user.0]\nname = admin"), "the sections after the routes stay");
            Assert.That(text, Does.Not.Contain("# inside the route"), "a comment inside a route goes with the route (see the class remarks)");
            Assert.That(text.IndexOf("# routes follow", StringComparison.Ordinal), Is.LessThan(text.IndexOf("[route.0]", StringComparison.Ordinal)),
                "the routes are written where the first one was");
        }

        [Test]
        public void AnEditedRoute_IsWrittenWithItsNewPort_AndAPortNameIsKeptAsAName()
        {
            List<string> lines = Lines(Sample);
            SwitchConfig before = Load(lines);
            List<X121Route> routes = new List<X121Route>(before.Routing.Routes);
            routes[0].DestPort = 0;
            routes.RemoveAt(2);

            SwitchRouteWriter.ReplaceRoutes(lines, routes, before.Routing);
            SwitchConfig after = Load(lines);

            Assert.That(after.Routing.Routes.Count, Is.EqualTo(3));
            Assert.That(after.Routing.Routes[0].DestPort, Is.EqualTo(0));
            Assert.That(string.Join("\n", lines), Does.Contain("port = pad"), "a port with a name is written by its name");
            Assert.That(string.Join("\n", lines), Does.Not.Contain("[route.3]"), "a removed route leaves no section behind");
        }

        /// <summary>
        /// A hunt route names a [hunt.N] section by its number; the [hunt.N] section is left as it was.
        /// </summary>
        [Test]
        public void AHuntRoute_KeepsItsGroup_AndTheHuntSectionIsLeftAlone()
        {
            string text =
                "[switch]\nname = S\ndnic = 2350\n" +
                "[port.0]\ntype = access\ntransport = xot\nport = 2200\n" +
                "[port.1]\ntype = access\ntransport = xot\nport = 2201\n" +
                "[hunt.0]\nmembers = 0, 1\n" +
                "[route.0]\nhunt = 23500100\nhunt_group = 0\n" +
                "[route.1]\ndefault = yes\nport = 1\n";
            List<string> lines = Lines(text);
            SwitchConfig before = Load(lines);

            SwitchRouteWriter.ReplaceRoutes(lines, before.Routing);
            SwitchConfig after = Load(lines);

            List<X121Route> beforeRoutes = new List<X121Route>(before.Routing.Routes);
            List<X121Route> afterRoutes = new List<X121Route>(after.Routing.Routes);
            AssertSameRoutes(beforeRoutes, afterRoutes);
            Assert.That(after.Routing.HuntGroups.Count, Is.EqualTo(1));
            Assert.That(after.Routing.HuntGroups[0].Members, Is.EqualTo(new[] { 0, 1 }));
        }

        [Test]
        public void TheDemoSwitchFiles_RoundTrip()
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            string[] files = { "switch-a.ini", "switch-b.ini", "switch-c.ini" };
            for (int f = 0; f < files.Length; f++)
            {
                string path = Path.Combine(root, "demo", files[f]);
                Assert.That(File.Exists(path), Is.True, path);
                List<string> lines = new List<string>(File.ReadAllLines(path));
                SwitchConfig before = Load(lines);

                SwitchRouteWriter.ReplaceRoutes(lines, before.Routing);

                AssertSameRoutes(before.Routing.Routes, Load(lines).Routing.Routes);
            }
        }
    }
}
