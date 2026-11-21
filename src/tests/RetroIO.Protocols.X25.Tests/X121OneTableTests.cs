//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Protocols.X25.Routing;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The one routing table's lookup, <see cref="X121RoutingTable.Resolve(string, int, Func{int, X121PortStatus}?, bool, X121RouteDirection, byte[])"/>, against the number analysis
    /// of X.121 (10/2000) 7.6-7.12: the own network, the international prefix, the order the kinds win in,
    /// the port that is down, the hunt member that is down, the backup port, and the number sent on.
    /// </summary>
    [TestFixture]
    public class X121OneTableTests
    {
        private static X121Route Dnic(string dnic, int port, string name = "")
        {
            return new X121Route { Type = X121RouteType.Dnic, Dnic = dnic, DestPort = port, Name = name };
        }

        private static X121Route Prefix(string digits, int port)
        {
            return new X121Route { Type = X121RouteType.Prefix, Address = digits, PrefixLen = digits.Length, DestPort = port };
        }

        private static X121Route Dcc(string dcc, int port)
        {
            return new X121Route { Type = X121RouteType.Dcc, Dcc = dcc, DestPort = port };
        }

        private static X121Route Default(int port)
        {
            return new X121Route { Type = X121RouteType.Default, DestPort = port };
        }

        private static X121Route Exact(string number, int port)
        {
            return new X121Route { Type = X121RouteType.Exact, Address = number, DestPort = port };
        }

        private static Func<int, X121PortStatus> PortsUp(params int[] down)
        {
            return port =>
            {
                for (int i = 0; i < down.Length; i++)
                {
                    if (down[i] == port)
                    {
                        return X121PortStatus.Down;
                    }
                }

                return X121PortStatus.Up;
            };
        }

        [Test]
        public void LongestMatch_AFullAddressPrefix_BeatsTheDnic()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Dnic("2402", 1));
            t.AddRoute(Prefix("240200", 2));

            X121RouteResult r = t.Resolve("24020001730", 0, PortsUp());

            Assert.That(r.Outcome, Is.EqualTo(X121RouteOutcome.Forward));
            Assert.That(r.Port, Is.EqualTo(2));
        }

        [Test]
        public void LongestMatch_ADnicWithItsFifthDigit_BeatsTheDnic()
        {
            // X.121 7.12.2: the first digit after the DNIC may pick one of several networks sharing it.
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Dnic("2402", 1));
            t.AddRoute(Dnic("24020", 2));

            Assert.That(t.Resolve("24020001730", 0, PortsUp()).Port, Is.EqualTo(2), "24020 is longer than 2402");
            Assert.That(t.Resolve("24021001730", 0, PortsUp()).Port, Is.EqualTo(1), "only the 4-digit DNIC matches");
        }

        [Test]
        public void LongestMatch_TheDnic_BeatsTheCountryCode()
        {
            // X.121 7.12.1: the DCC is the first three digits; the network digit is read after it (7.12.2).
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Dcc("240", 1));
            t.AddRoute(Dnic("2402", 2));

            Assert.That(t.Resolve("24020001730", 0, PortsUp()).Port, Is.EqualTo(2));
            Assert.That(t.Resolve("24050001730", 0, PortsUp()).Port, Is.EqualTo(1), "another Swedish network: only the DCC matches");
        }

        [Test]
        public void LongestMatch_TheDefaultRoute_IsLast()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Default(9));
            t.AddRoute(Dcc("240", 1));

            Assert.That(t.Resolve("24020001730", 0, PortsUp()).Port, Is.EqualTo(1), "a country route beats the default even when written after it");
            Assert.That(t.Resolve("31060001730", 0, PortsUp()).Port, Is.EqualTo(9), "nothing else matches: the default");
        }

        [Test]
        public void ALocalNumber_IsCompletedWithTheOwnDnic()
        {
            // X.121 7.6.1: international data number = DNIC + NTN; 7.6.2: the NTN is the number used inside
            // the network. With a prefix set, a number without it is a national number.
            X121RoutingTable t = new X121RoutingTable();
            t.SetOwnDnics(new[] { "2422" });
            t.InternationalPrefix = "0";
            t.AddRoute(Exact("2422110006", 4));

            X121RouteResult r = t.Resolve("110006", 0, PortsUp());

            Assert.That(r.InternationalAddress, Is.EqualTo("2422110006"));
            Assert.That(r.Outcome, Is.EqualTo(X121RouteOutcome.Forward));
            Assert.That(r.Port, Is.EqualTo(4));
        }

        /// <summary>
        /// An escape code (8, 9 or 0) dialled after the international prefix is carried forward with the number after
        /// it; the own DNIC is not put in front.
        /// </summary>
        /// <remarks>
        /// X.121 (10/2000) 7.9: an escape code "indicates that the following digits represent a number from a
        /// different numbering plan" and "has to be carried forward through the originating network"; it is "part of
        /// the international X.121 format", and Figure 2 shows that format dialled behind the prefix P. Table 2: 8 is
        /// F.69, 9 and 0 are E.164. Before 2026-11-21 such a number became 2422 plus the number (RetroIO BUGS 37).
        /// </remarks>
        [TestCase("947221234567")]
        [TestCase("047221234567")]
        [TestCase("8123456")]
        public void AnEscapeCodeAfterThePrefix_IsCarriedForward(string number)
        {
            X121RoutingTable t = new X121RoutingTable();
            t.SetOwnDnics(new[] { "2422" });
            t.InternationalPrefix = "1";
            t.AddRoute(Default(9));

            string dialled = "1" + number;
            Assert.That(t.ToInternational(dialled), Is.EqualTo(number));
            X121RouteResult r = t.Resolve(dialled, 0, PortsUp());
            Assert.That(r.InternationalAddress, Is.EqualTo(number));
            Assert.That(r.Port, Is.EqualTo(9), "no route knows another numbering plan: the default (the gateway) takes it");
        }

        /// <summary>
        /// A number dialled without the prefix is a national data number, whatever its first digit: 9001 in Norway is
        /// 24229001.
        /// </summary>
        /// <remarks>
        /// X.121 7.8.2: the prefix is what tells the national format from the international one, and 7.9 places the
        /// escape code inside the international format (Figure 2), so a 9 at the start of a national number is a
        /// digit of the network terminal number, not an escape code. From 2026-11-21 until this fix the same day, a
        /// national number starting with 8, 9 or 0 was taken as an escape code and lost its DNIC; RetroX25's echo
        /// services (9001 in every country) were then refused as not obtainable.
        /// </remarks>
        [TestCase("9001", "24229001")]
        [TestCase("8123", "24228123")]
        public void ANationalNumber_StartingWithAnEscapeDigit_StillGetsTheOwnDnic(string called, string international)
        {
            X121RoutingTable t = new X121RoutingTable();
            t.SetOwnDnics(new[] { "2422" });
            t.InternationalPrefix = "0";

            Assert.That(t.ToInternational(called), Is.EqualTo(international));
        }

        /// <summary>
        /// When the international prefix is itself an escape digit (0 is common), a number starting with it is the
        /// prefix (X.121 7.8.2, "a national matter"); an escape code may follow it.
        /// </summary>
        [Test]
        public void APrefixOfZero_IsThePrefix_AndAnEscapeCodeMayFollowIt()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.SetOwnDnics(new[] { "2422" });
            t.InternationalPrefix = "0";

            Assert.That(t.ToInternational("023421234"), Is.EqualTo("23421234"), "the prefix is taken off");
            Assert.That(t.ToInternational("0947221234567"), Is.EqualTo("947221234567"), "prefix, then an escape code");
        }

        [Test]
        public void AnExactLocalNumberRoute_MatchesTheNumberWrittenWithTheOwnDnic()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.SetOwnDnics(new[] { "2422" });
            t.AddRoute(Exact("110006", 4));

            Assert.That(t.Resolve("2422110006", 0, PortsUp()).Port, Is.EqualTo(4));
            Assert.That(t.Resolve("110006", 0, PortsUp()).Port, Is.EqualTo(4));
        }

        [Test]
        public void TheInternationalPrefix_IsTakenOffBeforeAGateway()
        {
            // X.121 7.8.1: a prefix is not signalled across internetwork boundaries; 7.12.6/7.12.7: the
            // complete international data number is sent on.
            X121RoutingTable t = new X121RoutingTable();
            t.SetOwnDnics(new[] { "2422" });
            t.InternationalPrefix = "0";
            t.AddRoute(Dnic("2402", 3, "Sweden"));
            Func<int, X121PortStatus> ports = p => p == 3
                ? new X121PortStatus(true, true, X121AddressForm.International)
                : X121PortStatus.Up;

            X121RouteResult r = t.Resolve("024020001730", 0, ports);

            Assert.That(r.Port, Is.EqualTo(3));
            Assert.That(r.Route!.Name, Is.EqualTo("Sweden"));
            Assert.That(r.CalledAddress, Is.EqualTo("24020001730"), "no prefix toward the gateway");
        }

        [Test]
        public void APortThatKeepsTheNumberAsReceived_GetsItWithThePrefix()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.InternationalPrefix = "0";
            t.AddRoute(Dnic("2402", 3));

            Assert.That(t.Resolve("024020001730", 0, PortsUp()).CalledAddress, Is.EqualTo("024020001730"));
        }

        [Test]
        public void APortAskingForTheNationalNumber_GetsItWithoutTheOwnDnic()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.SetOwnDnics(new[] { "2422" });
            t.AddRoute(Prefix("242211", 5));
            Func<int, X121PortStatus> ports = p => new X121PortStatus(true, true, X121AddressForm.National);

            Assert.That(t.Resolve("2422110006", 0, ports).CalledAddress, Is.EqualTo("110006"));
        }

        [Test]
        public void ARouteBackOutOfTheArrivalPort_IsNeverChosen()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Dnic("2402", 1));

            Assert.That(t.Resolve("24020001730", 1, PortsUp()).Outcome, Is.EqualTo(X121RouteOutcome.NoRoute));

            t.AddRoute(Default(5));
            Assert.That(t.Resolve("24020001730", 1, PortsUp()).Port, Is.EqualTo(5), "skipped, and the search goes on");
        }

        [Test]
        public void ADisabledRoute_IsSkipped()
        {
            X121RoutingTable t = new X121RoutingTable();
            X121Route off = Dnic("2402", 1);
            off.Enabled = false;
            t.AddRoute(off);
            t.AddRoute(Default(5));

            Assert.That(t.Resolve("24020001730", 0, PortsUp()).Port, Is.EqualTo(5));
        }

        [Test]
        public void AHuntGroup_SkipsAMemberThatIsDown()
        {
            X121RoutingTable t = new X121RoutingTable();
            X121HuntGroup hg = new X121HuntGroup { Id = 0 };
            hg.Members.Add(4);
            hg.Members.Add(5);
            t.AddHuntGroup(hg);
            t.AddRoute(new X121Route { Type = X121RouteType.Hunt, Address = "24220555", HuntGroupId = 0 });

            for (int i = 0; i < 4; i++)
            {
                X121RouteResult r = t.Resolve("24220555", 0, PortsUp(4));
                Assert.That(r.Outcome, Is.EqualTo(X121RouteOutcome.Forward));
                Assert.That(r.Port, Is.EqualTo(5), "member 4 is down, every call goes to 5");
            }

            Assert.That(t.Resolve("24220555", 0, PortsUp(4, 5)).Outcome, Is.EqualTo(X121RouteOutcome.OutOfOrder), "every member down");
        }

        [Test]
        public void APortThatIsDown_UsesTheRoutesBackupPort_AndIsOutOfOrderWithoutOne()
        {
            X121RoutingTable t = new X121RoutingTable();
            X121Route r = Dnic("2402", 1);
            r.BackupPort = 2;
            t.AddRoute(r);

            X121RouteResult viaBackup = t.Resolve("24020001730", 0, PortsUp(1));
            Assert.That(viaBackup.Port, Is.EqualTo(2));
            Assert.That(viaBackup.UsedBackup, Is.True);

            Assert.That(t.Resolve("24020001730", 0, PortsUp(1, 2)).Outcome, Is.EqualTo(X121RouteOutcome.OutOfOrder));
        }

        [Test]
        public void TheBackupPorts_AreTriedInTheirOrder()
        {
            // X.110 Appendix I.1: direct route, then alternative route 1, then alternative route 2.
            X121RoutingTable t = new X121RoutingTable();
            X121Route r = Dnic("2402", 1);
            r.BackupPorts.Add(2);
            r.BackupPorts.Add(3);
            t.AddRoute(r);

            Assert.That(t.Resolve("24020001730", 0, PortsUp()).Port, Is.EqualTo(1));
            Assert.That(t.Resolve("24020001730", 0, PortsUp(1)).Port, Is.EqualTo(2));
            Assert.That(t.Resolve("24020001730", 0, PortsUp(1, 2)).Port, Is.EqualTo(3));
            Assert.That(t.Resolve("24020001730", 3, PortsUp(1, 2)).Outcome, Is.EqualTo(X121RouteOutcome.OutOfOrder),
                "the only one left is the arrival port, which is never used");
        }

        [Test]
        public void ARouteToAPortTheNodeDoesNotHave_IsNoRoute()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Dnic("2402", 7));

            Assert.That(t.Resolve("24020001730", 0, p => X121PortStatus.Missing).Outcome, Is.EqualTo(X121RouteOutcome.NoRoute));
        }

        [Test]
        public void ALocalRoute_IsDeliveredHere()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.AddRoute(Exact("200", X121RoutingTable.LocalPort));

            X121RouteResult r = t.Resolve("200", 0, PortsUp());

            Assert.That(r.Outcome, Is.EqualTo(X121RouteOutcome.Local));
        }

        [Test]
        public void TheWinner_CountsAHit_AndTheExplanationNamesIt()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.SetPortName(3, "x75-gw");
            t.AddRoute(Dnic("2402", 3, "Sweden"));

            X121RouteResult r = t.Resolve("24020001730", 0, PortsUp());

            Assert.That(r.Route!.Hits, Is.EqualTo(1));
            Assert.That(r.Explanation, Does.Contain("Sweden"));
            Assert.That(r.Explanation, Does.Contain("x75-gw"));
        }

        [Test]
        public void TheLookupOrder_IsTheOrderTheRoutesWin()
        {
            X121RoutingTable t = new X121RoutingTable();
            X121Route def = Default(9);
            X121Route dcc = Dcc("240", 1);
            X121Route dnic = Dnic("2402", 2);
            X121Route dnic5 = Dnic("24020", 3);
            X121Route prefix = Prefix("24020001", 4);
            X121Route exact = Exact("24020001730", 5);
            t.AddRoute(def);
            t.AddRoute(dcc);
            t.AddRoute(dnic);
            t.AddRoute(dnic5);
            t.AddRoute(prefix);
            t.AddRoute(exact);

            List<X121Route> order = t.RoutesInLookupOrder();

            Assert.That(order, Is.EqualTo(new List<X121Route> { exact, prefix, dnic5, dnic, dcc, def }));
        }

        [Test]
        public void BadDigits_AreRefusedByTheRules()
        {
            Assert.That(X121RouteRules.CheckMatch(X121RouteType.Dnic, "240", out _), Is.False, "a DNIC is 4 or 5 digits");
            Assert.That(X121RouteRules.CheckMatch(X121RouteType.Dnic, "8402", out _), Is.False, "no zone 8");
            Assert.That(X121RouteRules.CheckMatch(X121RouteType.Dcc, "2402", out _), Is.False, "a DCC is 3 digits");
            Assert.That(X121RouteRules.CheckMatch(X121RouteType.Exact, "1234567890123456", out _), Is.False, "16 digits");
            Assert.That(X121RouteRules.CheckMatch(X121RouteType.Prefix, "24a", out _), Is.False);
            Assert.That(X121RouteRules.CheckMatch(X121RouteType.Regex, "([", out _), Is.False);
            Assert.That(X121RouteRules.CheckMatch(X121RouteType.Dnic, "24020", out _), Is.True);
        }

        [Test]
        public void TheConfigForm_RoundTrips_WithPortsByName()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.SetPortName(0, "local-ports");
            t.SetPortName(1, "x75-gw");
            X121NetworkConfig cfg = new X121NetworkConfig { InternationalPrefix = "0" };
            cfg.OwnDnics.Add("2422");
            cfg.Routes.Add(new X121RouteConfig { Name = "Sweden", Kind = "dnic", Match = "2402", Port = "x75-gw", Note = "via Tor" });
            X121RouteConfig here = new X121RouteConfig { Name = "Here", Kind = "prefix", Match = "242201", Port = "local-ports" };
            here.BackupPorts.Add("x75-gw");
            cfg.Routes.Add(here);
            cfg.Routes.Add(new X121RouteConfig { Name = "Out", Kind = "default", Port = "x75-gw", Enabled = false });

            X121TableBuilder.Fill(t, cfg);
            X121NetworkConfig back = X121TableBuilder.ToConfig(t);

            Assert.That(t.OwnDnics, Is.EqualTo(new[] { "2422" }));
            Assert.That(t.InternationalPrefix, Is.EqualTo("0"));
            Assert.That(t.Routes.Count, Is.EqualTo(3));
            Assert.That(t.Routes[0].Type, Is.EqualTo(X121RouteType.Dnic));
            Assert.That(t.Routes[0].DestPort, Is.EqualTo(1));
            Assert.That(t.Routes[1].BackupPort, Is.EqualTo(1));
            Assert.That(t.Routes[2].Enabled, Is.False);
            Assert.That(back.Routes[0].Port, Is.EqualTo("x75-gw"));
            Assert.That(back.Routes[0].Note, Is.EqualTo("via Tor"));
            Assert.That(back.Routes[1].Match, Is.EqualTo("242201"));
            Assert.That(back.Routes[1].BackupPorts, Is.EqualTo(new[] { "x75-gw" }));
            Assert.That(back.Routes[2].Kind, Is.EqualTo("default"));
        }

        [Test]
        public void TheConfigForm_RefusesAPortTheNodeDoesNotHave_NamingTheRoute()
        {
            X121RoutingTable t = new X121RoutingTable();
            t.SetPortName(0, "x75-gw");
            X121NetworkConfig cfg = new X121NetworkConfig();
            cfg.Routes.Add(new X121RouteConfig { Name = "Sweden", Kind = "dnic", Match = "2402", Port = "no-such-port" });

            System.IO.InvalidDataException? ex = Assert.Throws<System.IO.InvalidDataException>(() => X121TableBuilder.Fill(t, cfg));
            Assert.That(ex!.Message, Does.Contain("Sweden"));
            Assert.That(ex.Message, Does.Contain("no-such-port"));
        }
    }
}
