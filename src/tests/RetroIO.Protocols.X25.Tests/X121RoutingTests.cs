using NUnit.Framework;
using RetroIO.Protocols.X25.Routing;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Tests for the generic <see cref="X121RoutingTable"/>, verifying the C switch's
    /// <c>sw_route_lookup</c> semantics (tools/x25switch.c) plus the additive regex rule.
    /// </summary>
    [TestFixture]
    public class X121RoutingTests
    {
        private static X121Route Route(X121RouteType type, int destPort, string address = "", int prefixLen = 0, string dnic = "")
        {
            return new X121Route
            {
                Type = type,
                DestPort = destPort,
                Address = address,
                PrefixLen = prefixLen,
                Dnic = dnic
            };
        }

        [Test]
        public void Exact_Match_ReturnsDestPort()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Route(X121RouteType.Exact, 5, address: "23420001"));

            Assert.That(t.TryLookup("23420001", 0, out int outPort), Is.True);
            Assert.That(outPort, Is.EqualTo(5));
        }

        [Test]
        public void Exact_EarlyReturns_BeforeLaterPrefix()
        {
            // A prefix entry appears first (accumulates) but the exact entry short-circuits when reached.
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Route(X121RouteType.Prefix, 9, address: "234", prefixLen: 3));
            t.AddRoute(Route(X121RouteType.Exact, 7, address: "23420001"));

            Assert.That(t.TryLookup("23420001", 0, out int outPort), Is.True);
            Assert.That(outPort, Is.EqualTo(7), "exact must win over a prefix that also matches");
        }

        [Test]
        public void Prefix_LongestWins()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Route(X121RouteType.Prefix, 1, address: "234", prefixLen: 3));
            t.AddRoute(Route(X121RouteType.Prefix, 2, address: "23420", prefixLen: 5));

            Assert.That(t.TryLookup("2342000", 0, out int outPort), Is.True);
            Assert.That(outPort, Is.EqualTo(2), "longer prefix (5) beats shorter (3)");
        }

        [Test]
        public void Dnic_IsLowerPriorityThanPrefix()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Route(X121RouteType.Dnic, 8, dnic: "2342"));
            t.AddRoute(Route(X121RouteType.Prefix, 3, address: "23420", prefixLen: 5));

            Assert.That(t.TryLookup("2342000", 0, out int outPort), Is.True);
            Assert.That(outPort, Is.EqualTo(3), "prefix beats DNIC");
        }

        [Test]
        public void Dnic_MatchesWhenNoPrefix()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Route(X121RouteType.Dnic, 8, dnic: "2342"));

            Assert.That(t.TryLookup("2342999", 0, out int outPort), Is.True);
            Assert.That(outPort, Is.EqualTo(8));
        }

        [Test]
        public void Default_UsedWhenNothingElseMatches()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Route(X121RouteType.Exact, 1, address: "111"));
            t.AddRoute(Route(X121RouteType.Default, 4));

            Assert.That(t.TryLookup("999", 0, out int outPort), Is.True);
            Assert.That(outPort, Is.EqualTo(4));
        }

        [Test]
        public void NeverRoutesBackToIngressPort()
        {
            // Best match resolves to port 2, but the call arrived on port 2 -> must not route back.
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Route(X121RouteType.Prefix, 2, address: "234", prefixLen: 3));

            Assert.That(t.TryLookup("2342000", 2, out _), Is.False, "must not route back to ingress");
        }

        [Test]
        public void AnExactRoutePointingBackTheWayTheCallCame_IsNotUsed()
        {
            // Routing a call back out of the interface it arrived on sends it to the party that
            // placed it. The single-loop lookup this replaces returned an Exact hit without
            // checking the ingress port at all, so this made a loop.
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Route(X121RouteType.Exact, 2, address: "23420001"));

            Assert.That(t.TryLookup("23420001", 2, out _), Is.False, "must not route back to ingress");
        }

        [Test]
        public void AnExactRouteBackTheWayItCame_FallsThroughToTheDefault()
        {
            // Skipped, not fatal: the later passes still get their turn.
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Route(X121RouteType.Exact, 2, address: "23420001"));
            t.AddRoute(Route(X121RouteType.Default, 5));

            Assert.That(t.TryLookup("23420001", 2, out int outPort), Is.True);
            Assert.That(outPort, Is.EqualTo(5));
        }

        [Test]
        public void ARegexRoutePointingBackTheWayTheCallCame_IsNotUsed()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Route(X121RouteType.Regex, 2, address: "^2342.*$"));

            Assert.That(t.TryLookup("23420001", 2, out _), Is.False);
        }

        [Test]
        public void AHuntGroupSkipsTheMemberOnTheIngressPort()
        {
            X121RoutingTable t = new X121RoutingTable();
            X121HuntGroup hg = new X121HuntGroup { Id = 0 };
            hg.Members.Add(2);
            hg.Members.Add(7);
            t.AddHuntGroup(hg);
            t.AddRoute(new X121Route { Type = X121RouteType.Hunt, Address = "555", HuntGroupId = 0 });

            // Every call from port 2 goes to the other member rather than back to the caller.
            Assert.That(t.TryLookup("555", 2, out int a), Is.True);
            Assert.That(a, Is.EqualTo(7));
            Assert.That(t.TryLookup("555", 2, out int b), Is.True);
            Assert.That(b, Is.EqualTo(7));
        }

        [Test]
        public void AShortPrefixStillBeatsADnicThatWasWrittenFirst()
        {
            // The single-loop lookup gave a DNIC hit a match length of four, so a three-digit prefix
            // later in the table lost to it and the same table answered differently depending on
            // the order it was written in. DNIC ranks below every prefix, however short.
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Route(X121RouteType.Dnic, 8, dnic: "2342"));
            t.AddRoute(Route(X121RouteType.Prefix, 3, address: "234", prefixLen: 3));

            Assert.That(t.TryLookup("2342000", 0, out int outPort), Is.True);
            Assert.That(outPort, Is.EqualTo(3), "a prefix of any length beats a DNIC");
        }

        [Test]
        public void TheAnswerDoesNotDependOnTheOrderTheTableWasWrittenIn()
        {
            X121RoutingTable a = new X121RoutingTable();
            a.AddRoute(Route(X121RouteType.Dnic, 8, dnic: "2342"));
            a.AddRoute(Route(X121RouteType.Prefix, 3, address: "234", prefixLen: 3));

            X121RoutingTable b = new X121RoutingTable();
            b.AddRoute(Route(X121RouteType.Prefix, 3, address: "234", prefixLen: 3));
            b.AddRoute(Route(X121RouteType.Dnic, 8, dnic: "2342"));

            Assert.That(a.TryLookup("2342000", 0, out int fromA), Is.True);
            Assert.That(b.TryLookup("2342000", 0, out int fromB), Is.True);
            Assert.That(fromA, Is.EqualTo(fromB));
        }

        [Test]
        public void APrefixPointingBackTheWayTheCallCame_LosesToAShorterOneThatDoesNot()
        {
            // The longest prefix wins among the ones that can actually be used. A longer prefix
            // aimed at the ingress port must not shut out a shorter usable one.
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Route(X121RouteType.Prefix, 2, address: "23420", prefixLen: 5));
            t.AddRoute(Route(X121RouteType.Prefix, 6, address: "234", prefixLen: 3));

            Assert.That(t.TryLookup("2342000", 2, out int outPort), Is.True);
            Assert.That(outPort, Is.EqualTo(6));
        }

        [Test]
        public void NoRoute_ReturnsFalse()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Route(X121RouteType.Exact, 1, address: "111"));

            Assert.That(t.TryLookup("999", 0, out _), Is.False);
        }

        [Test]
        public void Hunt_RoundRobins_AcrossMembers()
        {
            X121RoutingTable t = new X121RoutingTable();
            X121HuntGroup hg = new X121HuntGroup { Id = 0 };
            hg.Members.Add(10);
            hg.Members.Add(11);
            hg.Members.Add(12);
            t.AddHuntGroup(hg);
            t.AddRoute(new X121Route { Type = X121RouteType.Hunt, Address = "555", HuntGroupId = 0 });

            t.TryLookup("555", 0, out int a);
            t.TryLookup("555", 0, out int b);
            t.TryLookup("555", 0, out int c);
            t.TryLookup("555", 0, out int d);

            Assert.That(a, Is.EqualTo(10));
            Assert.That(b, Is.EqualTo(11));
            Assert.That(c, Is.EqualTo(12));
            Assert.That(d, Is.EqualTo(10), "round-robin wraps");
        }

        [Test]
        public void Regex_Match_ReturnsDestPort()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(new X121Route { Type = X121RouteType.Regex, DestPort = 6, Pattern = "^234.*" });

            Assert.That(t.TryLookup("23420001", 0, out int outPort), Is.True);
            Assert.That(outPort, Is.EqualTo(6));
            Assert.That(t.TryLookup("99999999", 0, out _), Is.False);
        }

        [Test]
        public void Regex_InvalidPattern_NeverMatches_DoesNotThrow()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(new X121Route { Type = X121RouteType.Regex, DestPort = 6, Pattern = "[unterminated" });

            Assert.That(t.TryLookup("23420001", 0, out _), Is.False);
        }

        [Test]
        public void LookupMulti_ReturnsPrimaryThenDistinctBackups()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(new X121Route { Type = X121RouteType.Exact, DestPort = 5, Address = "23420001", BackupPort = 6 });
            t.AddRoute(new X121Route { Type = X121RouteType.Default, DestPort = 9, BackupPort = 6 });

            System.Collections.Generic.IReadOnlyList<int> ports = t.LookupMulti("23420001", 0, 4);

            Assert.That(ports.Count, Is.EqualTo(2));
            Assert.That(ports[0], Is.EqualTo(5), "primary first");
            Assert.That(ports[1], Is.EqualTo(6), "backup, de-duplicated");
        }

        [Test]
        public void DeleteRouteAt_ShiftsAndValidatesIndex()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Route(X121RouteType.Exact, 1, address: "a"));
            t.AddRoute(Route(X121RouteType.Exact, 2, address: "b"));

            Assert.That(t.DeleteRouteAt(5), Is.False, "invalid index rejected");
            Assert.That(t.DeleteRouteAt(0), Is.True);
            Assert.That(t.Routes.Count, Is.EqualTo(1));
            Assert.That(t.Routes[0].DestPort, Is.EqualTo(2), "later entry shifted down");
        }
    }
}
