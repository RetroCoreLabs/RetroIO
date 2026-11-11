//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Client;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;
using ClientVc = RetroIO.Protocols.X25.Client.X25Vc;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// A switch port that has run out of channels, seen from a caller that sends more calls than the
    /// port has channels.
    /// </summary>
    /// <remarks>
    /// Found with <c>x25traffic -C</c>: a port with 64 channels connected 64 of 200 calls, and the other
    /// 136 got no answer at all and hung. The port answered the 65th call with a Diagnostic on channel
    /// 0, which a caller does not take as the answer to a call. X.25 has the network clear such a call
    /// with cause 5, network congestion (Table 5-7).
    /// </remarks>
    [TestFixture]
    public class SwitchPortFullTests
    {
        private const int Channels = 3;
        private const int Calls = 5;

        private SwitchNode _node = null!;
        private XotSwitchPort _portA = null!;
        private XotSwitchPort _portB = null!;

        [SetUp]
        public void SetUp()
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340" };
            cfg.Routing.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = "2342", PrefixLen = 4, DestPort = 1 });

            _node = new SwitchNode(cfg);
            _portA = new XotSwitchPort(new PortConfig { Id = 0, Mode = PortMode.Dce, Port = 0, LciBase = 1, LciCount = Channels });
            _portB = new XotSwitchPort(new PortConfig { Id = 1, Mode = PortMode.Dce, Port = 0, LciBase = 1, LciCount = 64 });
            Assert.That(_portA.Start(), Is.True);
            Assert.That(_portB.Start(), Is.True);
            _node.AddPort(_portA);
            _node.AddPort(_portB);
            new SwitchCore(_node);
        }

        [TearDown]
        public void TearDown()
        {
            _portA?.Dispose();
            _portB?.Dispose();
        }

        private static void WaitFor(Func<bool> condition, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (!condition() && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(10);
            }
        }

        private static X25Client Dial(ushort port, string localAddress)
        {
            X25Client client = new X25Client { ServiceName = "full-port-test" };
            client.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = port,
                LocalAddress = localAddress,
                Framing = X25ClientFraming.Xot,
            }, NullLogger.Instance);

            return client;
        }

        /// <summary>
        /// With three channels and five calls on one connection: three calls connect, the other two
        /// are cleared at once with cause 5, and the port writes no Diagnostic.
        /// </summary>
        [Test]
        public void MoreCallsThanChannels_TheExtraCallsAreClearedWithNetworkCongestion()
        {
            object gate = new object();
            int diagnostics = 0;
            _portA.PacketCaptured += (id, inbound, packet) =>
            {
                // Packet type 0xF1 is a Diagnostic (Table 5-2/5-3).
                if (!inbound && packet.Length >= 3 && packet[2] == 0xF1)
                {
                    Interlocked.Increment(ref diagnostics);
                }
            };

            using X25Client callee = Dial(_portB.BoundPort, "23420001");
            callee.SetOnIncoming((c, vc) => c.Accept(vc));

            using X25Client caller = Dial(_portA.BoundPort, "23400001");
            List<byte> causes = new List<byte>();
            caller.SetOnClear((c, vc, cause, diag) =>
            {
                lock (gate)
                {
                    causes.Add(cause);
                }
            });

            WaitFor(() => _portA.PeerConnected && _portB.PeerConnected, 3000);

            List<ClientVc> circuits = new List<ClientVc>();
            for (int i = 0; i < Calls; i++)
            {
                ClientVc? vc = caller.Call("23420001");
                Assert.That(vc, Is.Not.Null, "the client should be able to send call " + i);
                circuits.Add(vc!);
            }

            WaitFor(() =>
            {
                lock (gate)
                {
                    return causes.Count >= Calls - Channels;
                }
            }, 3000);

            int connected = 0;
            for (int i = 0; i < circuits.Count; i++)
            {
                if (circuits[i].Connected)
                {
                    connected++;
                }
            }

            lock (gate)
            {
                Assert.That(connected, Is.EqualTo(Channels), "the port has three channels");
                Assert.That(causes.Count, Is.EqualTo(Calls - Channels), "each call over the limit must be cleared, not left hanging");
                for (int i = 0; i < causes.Count; i++)
                {
                    Assert.That(causes[i], Is.EqualTo(0x05), "cause 5: network congestion");
                }
            }

            // Let the caller's clear confirmations arrive, then check the port answered none of them
            // with a Diagnostic on an unassigned channel.
            Thread.Sleep(300);
            Assert.That(Volatile.Read(ref diagnostics), Is.EqualTo(0), "no Diagnostic: the refused calls were answered with clears");
        }
    }
}
