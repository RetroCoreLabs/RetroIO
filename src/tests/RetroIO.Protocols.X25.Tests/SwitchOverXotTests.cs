//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
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
    /// A whole switch over real TCP: two clients dial in on different ports and the switch stitches
    /// their circuits together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything below is the one stack. Each port owns an <c>X25Layer</c> and an
    /// <c>XotFrameCodec</c>; so does each client. The switch itself decodes nothing: it routes the
    /// call, makes the stitch and passes packets across it.
    /// </para>
    /// <para>
    /// Until <c>XotSwitchPort</c> existed this could not be written at all - the only port was the
    /// in-memory one, so the switch was only ever exercised by injecting events into it directly.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class SwitchOverXotTests
    {
        private SwitchNode _node = null!;
        private SwitchCore _core = null!;
        private XotSwitchPort _portA = null!;
        private XotSwitchPort _portB = null!;

        [SetUp]
        public void SetUp()
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340" };

            // Anything starting 2342 goes out of port 1.
            cfg.Routing.AddRoute(new X121Route
            {
                Type = X121RouteType.Prefix,
                Address = "2342",
                PrefixLen = 4,
                DestPort = 1,
            });

            _node = new SwitchNode(cfg);

            // Both ports listen, because both clients dial in. The operating system picks the
            // numbers, so these tests do not collide with anything else on the machine.
            _portA = new XotSwitchPort(new PortConfig { Id = 0, Mode = PortMode.Dce, Port = 0, LciBase = 1, LciCount = 16 });
            _portB = new XotSwitchPort(new PortConfig { Id = 1, Mode = PortMode.Dce, Port = 0, LciBase = 1, LciCount = 16 });

            Assert.That(_portA.Start(), Is.True);
            Assert.That(_portB.Start(), Is.True);

            _node.AddPort(_portA);
            _node.AddPort(_portB);
            _core = new SwitchCore(_node);
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
            X25Client client = new X25Client { ServiceName = "switch-test" };
            client.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = port,
                LocalAddress = localAddress,
                Framing = X25ClientFraming.Xot,
            }, NullLogger.Instance);

            return client;
        }

        [Test]
        public void ACallIsRoutedBetweenTwoPortsAndDataCrossesTheSwitch()
        {
            object gate = new object();
            StringBuilder atCallee = new StringBuilder();
            StringBuilder atCaller = new StringBuilder();
            X25Vc? calleeVc = null;

            using X25Client callee = Dial(_portB.BoundPort, "23420001");
            callee.SetOnIncoming((c, vc) =>
            {
                lock (gate)
                {
                    calleeVc = vc;
                }

                c.Accept(vc);
            });
            callee.SetOnData((c, vc, data, length, q, m, d) =>
            {
                lock (gate)
                {
                    atCallee.Append(Encoding.ASCII.GetString(data, 0, length));
                }
            });

            using X25Client caller = Dial(_portA.BoundPort, "23400001");
            caller.SetOnData((c, vc, data, length, q, m, d) =>
            {
                lock (gate)
                {
                    atCaller.Append(Encoding.ASCII.GetString(data, 0, length));
                }
            });

            WaitFor(() => _portA.PeerConnected && _portB.PeerConnected, 3000);
            Assert.That(_portA.PeerConnected, Is.True, "the caller's connection should have reached port 0");
            Assert.That(_portB.PeerConnected, Is.True, "the callee's connection should have reached port 1");

            X25Vc? callerVc = caller.CallWait("23420001", 5000);

            Assert.That(callerVc, Is.Not.Null, "the call should have been routed to port 1 and accepted");
            WaitFor(() => { lock (gate) { return calleeVc != null; } }, 3000);
            Assert.That(_node.ActiveStitchCount, Is.EqualTo(1), "the switch should be holding one stitch");

            Assert.That(caller.SendText(callerVc!, "hello"), Is.True);
            WaitFor(() => { lock (gate) { return atCallee.Length >= 5; } }, 3000);
            lock (gate)
            {
                Assert.That(atCallee.ToString(), Is.EqualTo("hello"));
            }

            lock (gate)
            {
                Assert.That(calleeVc, Is.Not.Null);
            }

            Assert.That(callee.SendText(calleeVc!, "hi"), Is.True);
            WaitFor(() => { lock (gate) { return atCaller.Length >= 2; } }, 3000);
            lock (gate)
            {
                Assert.That(atCaller.ToString(), Is.EqualTo("hi"));
            }
        }

        [Test]
        public void ACallToAnAddressWithNoRoute_IsCleared()
        {
            using X25Client caller = Dial(_portA.BoundPort, "23400001");
            WaitFor(() => _portA.PeerConnected, 3000);
            Assert.That(_portA.PeerConnected, Is.True);

            // 2399 matches no route and there is no default, so the switch clears it rather than
            // leaving the caller waiting for a Call Accepted that will never come (X.25 4.1).
            X25Vc? vc = caller.CallWait("23990001", 3000);

            Assert.That(vc, Is.Null, "a call with nowhere to go must be cleared");
            Assert.That(_node.ActiveStitchCount, Is.EqualTo(0));
        }
    }
}
