//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Client;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// What each per-port counter counts, proved with one call across three real switches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The definition is written on <see cref="PortCounters"/>. In short: a call is counted once on each port it
    /// touches (in on the port it arrived on, out on the port it left by), a data packet is counted once as it
    /// arrives on a port and once as it leaves a port, and a call is counted as cleared once per port, when its
    /// channel on that port is free again.
    /// </para>
    /// <para>
    /// Path: access 1 (XOT) - switch 1 - HDLC trunk - switch 2 - XOT trunk - switch 3 - access 3 (XOT). So all three
    /// kinds of port that count are in the path, and every switch has one port the call comes in on and one it
    /// leaves by.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class SwitchCounterTests
    {
        private const int DataPackets = 20;

        private sealed class Chain : IDisposable
        {
            public SwitchNode Node1 = null!;
            public SwitchNode Node2 = null!;
            public SwitchNode Node3 = null!;
            public ISwitchPort Access1 = null!;
            public ISwitchPort Trunk1 = null!;
            public ISwitchPort Trunk2 = null!;
            public ISwitchPort XotTrunk2 = null!;
            public ISwitchPort XotTrunk3 = null!;
            public ISwitchPort Access3 = null!;
            public SwitchCore Core1 = null!;
            public SwitchCore Core2 = null!;
            public SwitchCore Core3 = null!;

            public ISwitchPort[] AllPorts()
            {
                return new ISwitchPort[] { Access1, Trunk1, Trunk2, XotTrunk2, XotTrunk3, Access3 };
            }

            public void Dispose()
            {
                for (int i = 0; i < 6; i++)
                {
                    AllPorts()[i].Stop();
                }
            }
        }

        private static SwitchConfig ConfigWithRoute(string dnic)
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = dnic };
            cfg.Routing.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = "2342", PrefixLen = 4, DestPort = 1 });
            return cfg;
        }

        private static PortConfig XotConfig(int id, PortMode mode, string host, ushort port)
        {
            return new PortConfig { Id = id, Mode = mode, Host = host, Port = port, LciBase = 1, LciCount = 16 };
        }

        private static Chain Build()
        {
            Chain c = new Chain();
            c.Node1 = new SwitchNode(ConfigWithRoute("2340"));
            c.Node2 = new SwitchNode(ConfigWithRoute("2341"));
            c.Node3 = new SwitchNode(ConfigWithRoute("2342"));

            // Switch 3 first: the others dial it.
            c.XotTrunk3 = new XotSwitchPort(XotConfig(0, PortMode.Dce, "127.0.0.1", 0));
            c.Access3 = new XotSwitchPort(XotConfig(1, PortMode.Dce, "127.0.0.1", 0));
            c.Node3.AddPort(c.XotTrunk3);
            c.Node3.AddPort(c.Access3);
            c.Core3 = new SwitchCore(c.Node3);
            Assert.That(c.XotTrunk3.Start() && c.Access3.Start(), Is.True);

            c.Trunk2 = SwitchPortFactory.Create(TrunkPair.Config(0, PortMode.Dce, "127.0.0.1", 0));
            c.XotTrunk2 = new XotSwitchPort(XotConfig(1, PortMode.Dte, "127.0.0.1", ((XotSwitchPort)c.XotTrunk3).BoundPort));
            c.Node2.AddPort(c.Trunk2);
            c.Node2.AddPort(c.XotTrunk2);
            c.Core2 = new SwitchCore(c.Node2);
            Assert.That(c.Trunk2.Start() && c.XotTrunk2.Start(), Is.True);

            c.Access1 = new XotSwitchPort(XotConfig(0, PortMode.Dce, "127.0.0.1", 0));
            c.Trunk1 = SwitchPortFactory.Create(TrunkPair.Config(1, PortMode.Dte, "127.0.0.1", ((HdlcTcpSwitchPort)c.Trunk2).BoundPort));
            c.Node1.AddPort(c.Access1);
            c.Node1.AddPort(c.Trunk1);
            c.Core1 = new SwitchCore(c.Node1);
            Assert.That(c.Access1.Start() && c.Trunk1.Start(), Is.True);

            Assert.That(SpinWait.SpinUntil(() => c.Trunk1.PeerConnected && c.Trunk2.PeerConnected, 10000), Is.True, "the HDLC trunk comes up");
            return c;
        }

        /// <summary>
        /// One line per port: what it counted.
        /// </summary>
        private static string Table(Chain c)
        {
            string[] names = { "access1  ", "trunk1   ", "trunk2   ", "xotTrunk2", "xotTrunk3", "access3  " };
            ISwitchPort[] ports = c.AllPorts();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < ports.Length; i++)
            {
                PortCounters k = ports[i].Counters;
                sb.Append(names[i]).Append(" calls_in=").Append(k.CallsIn).Append(" calls_out=").Append(k.CallsOut)
                  .Append(" cleared=").Append(k.CallsCleared)
                  .Append(" data_in=").Append(k.PacketsIn).Append(" data_out=").Append(k.PacketsOut).Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>
        /// What the table must say: the three ports a call arrives on count one call in, the three it leaves by
        /// count one out; every port saw N data packets arrive and N leave; every port cleared one call.
        /// </summary>
        private static string Expected(int dataPackets)
        {
            string d = " data_in=" + dataPackets + " data_out=" + dataPackets + "\n";
            return "access1   calls_in=1 calls_out=0 cleared=1" + d
                 + "trunk1    calls_in=0 calls_out=1 cleared=1" + d
                 + "trunk2    calls_in=1 calls_out=0 cleared=1" + d
                 + "xotTrunk2 calls_in=0 calls_out=1 cleared=1" + d
                 + "xotTrunk3 calls_in=1 calls_out=0 cleared=1" + d
                 + "access3   calls_in=0 calls_out=1 cleared=1" + d;
        }

        private static void SendWhenAllowed(X25Client client, X25Vc vc, byte[] data)
        {
            long deadline = Environment.TickCount64 + 20000;
            while (!client.Send(vc, data, data.Length, false))
            {
                Assert.That(Environment.TickCount64, Is.LessThan(deadline), "the window did not open again");
                Thread.Sleep(2);
            }
        }

        /// <summary>
        /// One call across three switches and three kinds of link, N data packets each way, then clear. Every counter is
        /// compared as a whole table, so a failure prints what each port really counted next to what it should have.
        /// </summary>
        [Test]
        public void OneCallAcrossThreeSwitches_IsCountedOncePerPort_AndNDataPacketsEachWayAreCountedNInAndNOut()
        {
            using Chain c = Build();
            object gate = new object();
            int atCallee = 0;
            int atCaller = 0;
            X25Vc? calleeVc = null;

            using X25Client callee = new X25Client { ServiceName = "callee" };
            callee.Open(new X25ClientConfig { Host = "127.0.0.1", Port = ((XotSwitchPort)c.Access3).BoundPort, LocalAddress = "23420001", Framing = X25ClientFraming.Xot }, NullLogger.Instance);
            callee.SetOnIncoming((cl, vc) => { lock (gate) { calleeVc = vc; } cl.Accept(vc); });
            callee.SetOnData((cl, vc, data, length, q, m, d) => { lock (gate) { atCallee++; } });

            using X25Client caller = new X25Client { ServiceName = "caller" };
            caller.Open(new X25ClientConfig { Host = "127.0.0.1", Port = ((XotSwitchPort)c.Access1).BoundPort, LocalAddress = "23400001", Framing = X25ClientFraming.Xot }, NullLogger.Instance);
            caller.SetOnData((cl, vc, data, length, q, m, d) => { lock (gate) { atCaller++; } });

            SpinWait.SpinUntil(() => c.Access1.PeerConnected && c.Access3.PeerConnected, 5000);
            X25Vc? callerVc = caller.CallWait("23420001", 10000);
            Assert.That(callerVc, Is.Not.Null, "the call crosses all three switches");

            byte[] packet = new byte[64];
            for (int i = 0; i < DataPackets; i++)
            {
                packet[0] = (byte)i;
                SendWhenAllowed(caller, callerVc!, packet);
            }

            Assert.That(SpinWait.SpinUntil(() => { lock (gate) { return atCallee == DataPackets; } }, 15000), Is.True, "all packets arrive at the callee");
            X25Vc cv;
            lock (gate)
            {
                cv = calleeVc!;
            }

            for (int i = 0; i < DataPackets; i++)
            {
                packet[0] = (byte)i;
                SendWhenAllowed(callee, cv, packet);
            }

            Assert.That(SpinWait.SpinUntil(() => { lock (gate) { return atCaller == DataPackets; } }, 15000), Is.True, "all packets arrive at the caller");

            caller.Clear(callerVc!);
            Assert.That(SpinWait.SpinUntil(() => c.Node1.ActiveStitchCount == 0 && c.Node2.ActiveStitchCount == 0 && c.Node3.ActiveStitchCount == 0, 10000), Is.True, "the call clears everywhere");

            // Wait until every port has counted its clear at least once, then give a double count time to show.
            ISwitchPort[] ports = c.AllPorts();
            SpinWait.SpinUntil(() =>
            {
                for (int i = 0; i < ports.Length; i++)
                {
                    if (ports[i].Counters.CallsCleared == 0)
                    {
                        return false;
                    }
                }

                return true;
            }, 10000);
            Thread.Sleep(700);

            string actual = Table(c);
            TestContext.Out.WriteLine(actual);
            Assert.That(actual, Is.EqualTo(Expected(DataPackets)), "counters per port (actual on the left, wanted on the right)");

            // The wire-level counters: both ends of a link count the same packets and bytes (one end's out is the
            // other end's in), and they include the data packets plus the control packets (call set-up, RR, clear).
            // Each pair is printed side by side before it is compared.
            StringBuilder links = new StringBuilder();
            ISwitchPort[][] pairs = { new[] { c.Trunk1, c.Trunk2 }, new[] { c.XotTrunk2, c.XotTrunk3 } };
            for (int i = 0; i < pairs.Length; i++)
            {
                PortCounters a = pairs[i][0].Counters;
                PortCounters b = pairs[i][1].Counters;
                SpinWait.SpinUntil(() => a.AllPacketsOut == b.AllPacketsIn && b.AllPacketsOut == a.AllPacketsIn && a.BytesOut == b.BytesIn && b.BytesOut == a.BytesIn, 5000);
                links.Append("link ").Append(i).Append(": left out/in packets ").Append(a.AllPacketsOut).Append('/').Append(a.AllPacketsIn)
                     .Append(" bytes ").Append(a.BytesOut).Append('/').Append(a.BytesIn)
                     .Append(" | right out/in packets ").Append(b.AllPacketsOut).Append('/').Append(b.AllPacketsIn)
                     .Append(" bytes ").Append(b.BytesOut).Append('/').Append(b.BytesIn).Append('\n');
                Assert.That(a.AllPacketsOut, Is.EqualTo(b.AllPacketsIn), links.ToString());
                Assert.That(b.AllPacketsOut, Is.EqualTo(a.AllPacketsIn), links.ToString());
                Assert.That(a.BytesOut, Is.EqualTo(b.BytesIn), links.ToString());
                Assert.That(b.BytesOut, Is.EqualTo(a.BytesIn), links.ToString());
                Assert.That(a.AllPacketsOut, Is.GreaterThan(a.PacketsOut), "a call set-up and a clear crossed the link as well as the data: " + links);
            }

            TestContext.Out.WriteLine(links.ToString());
        }
    }
}
