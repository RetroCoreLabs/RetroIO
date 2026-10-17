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
using NUnit.Framework;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// <see cref="XotSwitchPort"/> over real TCP: a call placed on one port arrives on another,
    /// data crosses in both directions, and a clear reaches the far end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the first switch port with a transport under it. Before it the switch could only be
    /// driven by <c>LoopbackSwitchPort</c>, so nothing outside the process could call into the
    /// switch and the C emulator's client had nothing to talk to.
    /// </para>
    /// <para>
    /// Two ports are wired to each other rather than to a switch core, so what is under test is the
    /// port: its socket, its framing, its packet layer and the port contract it presents. The
    /// listening port asks the operating system for a free number, so these do not collide with
    /// anything else on the machine or with each other.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class XotSwitchPortTests
    {
        private XotSwitchPort _dce = null!;
        private XotSwitchPort _dte = null!;

        [SetUp]
        public void SetUp()
        {
            _dce = new XotSwitchPort(new PortConfig
            {
                Id = 0,
                Mode = PortMode.Dce,
                Port = 0,               // the operating system picks one
                LciBase = 1,
                LciCount = 16,
            });

            Assert.That(_dce.Start(), Is.True, "the listening port should have started");
            Assert.That(_dce.BoundPort, Is.GreaterThan(0));

            _dte = new XotSwitchPort(new PortConfig
            {
                Id = 1,
                Mode = PortMode.Dte,
                Host = "127.0.0.1",
                Port = _dce.BoundPort,
                LciBase = 1,
                LciCount = 16,
            });

            Assert.That(_dte.Start(), Is.True);
            WaitFor(() => _dte.PeerConnected && _dce.PeerConnected, 3000);
            Assert.That(_dte.PeerConnected, Is.True, "the dialling port should have connected");
            Assert.That(_dce.PeerConnected, Is.True, "the listening port should have accepted");
        }

        [TearDown]
        public void TearDown()
        {
            _dte?.Dispose();
            _dce?.Dispose();
        }

        private static void WaitFor(Func<bool> condition, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (!condition() && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(10);
            }
        }

        [Test]
        public void ACallCrossesTheConnectionAndDataFlowsBothWays()
        {
            object gate = new object();
            ushort incomingLci = 0;
            string incomingCalled = string.Empty;
            string incomingCalling = string.Empty;
            List<byte[]> toDce = new List<byte[]>();
            List<byte[]> toDte = new List<byte[]>();
            bool connected = false;

            _dce.IncomingCall += (port, lci, called, calling, cud) =>
            {
                lock (gate)
                {
                    incomingLci = lci;
                    incomingCalled = called;
                    incomingCalling = calling;
                }

                port.AcceptCall(lci);
            };

            _dce.DataReceived += (port, lci, data, length, q, m, d) =>
            {
                byte[] copy = new byte[length];
                Buffer.BlockCopy(data, 0, copy, 0, length);
                lock (gate)
                {
                    toDce.Add(copy);
                }
            };

            _dte.CallConnected += (port, lci) =>
            {
                lock (gate)
                {
                    connected = true;
                }
            };

            _dte.DataReceived += (port, lci, data, length, q, m, d) =>
            {
                byte[] copy = new byte[length];
                Buffer.BlockCopy(data, 0, copy, 0, length);
                lock (gate)
                {
                    toDte.Add(copy);
                }
            };

            ushort outLci = _dte.PlaceCall("23420001", "23400001", null);
            Assert.That(outLci, Is.Not.EqualTo(0), "the packet layer should have given the call a channel");

            WaitFor(() => { lock (gate) { return connected; } }, 3000);

            Assert.Multiple(() =>
            {
                Assert.That(connected, Is.True, "the call should have been accepted back across the connection");
                Assert.That(incomingCalled, Is.EqualTo("23420001"));
                Assert.That(incomingCalling, Is.EqualTo("23400001"));
            });

            byte[] hello = Encoding.ASCII.GetBytes("hello");
            Assert.That(_dte.SendData(outLci, hello, false, false, false), Is.EqualTo(PortSendResult.Sent));

            WaitFor(() => { lock (gate) { return toDce.Count == 1; } }, 3000);
            lock (gate)
            {
                Assert.That(toDce.Count, Is.EqualTo(1));
                Assert.That(toDce[0], Is.EqualTo(hello));
            }

            byte[] reply = Encoding.ASCII.GetBytes("hi");
            Assert.That(_dce.SendData(incomingLci, reply, false, false, false), Is.EqualTo(PortSendResult.Sent));

            WaitFor(() => { lock (gate) { return toDte.Count == 1; } }, 3000);
            lock (gate)
            {
                Assert.That(toDte.Count, Is.EqualTo(1));
                Assert.That(toDte[0], Is.EqualTo(reply));
            }
        }

        [Test]
        public void AClearReachesTheFarEnd()
        {
            object gate = new object();
            ushort incomingLci = 0;
            bool connected = false;
            byte clearedCause = 0xFF;
            bool cleared = false;

            _dce.IncomingCall += (port, lci, called, calling, cud) =>
            {
                lock (gate) { incomingLci = lci; }
                port.AcceptCall(lci);
            };
            _dce.CallCleared += (port, lci, cause, diag) =>
            {
                lock (gate)
                {
                    cleared = true;
                    clearedCause = cause;
                }
            };
            _dte.CallConnected += (port, lci) => { lock (gate) { connected = true; } };

            ushort outLci = _dte.PlaceCall("23420001", "23400001", null);
            WaitFor(() => { lock (gate) { return connected; } }, 3000);
            Assert.That(connected, Is.True);
            Assert.That(incomingLci, Is.Not.EqualTo(0));

            _dte.ClearCall(outLci, cause: 0x00, diagnostic: 0x00);

            WaitFor(() => { lock (gate) { return cleared; } }, 3000);
            Assert.That(cleared, Is.True, "the clear must reach the far end of the connection");
            Assert.That(clearedCause, Is.EqualTo(0x00));
        }

        [Test]
        public void SendingOnAChannelThatDoesNotExist_Fails()
        {
            // Failed, not WindowFull: waiting cannot make a circuit appear, and a switch that
            // queued this would hold the packet for ever.
            Assert.That(_dte.SendData(4000, new byte[] { 1 }, false, false, false),
                Is.EqualTo(PortSendResult.Failed));
        }

        [Test]
        public void SendingBeforeThePeerIsConnected_Fails()
        {
            using XotSwitchPort lonely = new XotSwitchPort(new PortConfig
            {
                Id = 9,
                Mode = PortMode.Dte,
                Host = "127.0.0.1",
                Port = 1,               // nothing listens here
                LciBase = 1,
                LciCount = 16,
            });

            Assert.That(lonely.SendData(1, new byte[] { 1 }, false, false, false),
                Is.EqualTo(PortSendResult.Failed));
            Assert.That(lonely.PlaceCall("23420001", "23400001", null), Is.EqualTo(0),
                "a call cannot be placed with no connection under the port");
        }

        [Test]
        public void AnInterruptCrossesTheConnection()
        {
            object gate = new object();
            bool connected = false;
            byte[]? got = null;

            _dce.IncomingCall += (port, lci, called, calling, cud) => port.AcceptCall(lci);
            _dce.InterruptReceived += (port, lci, userData) => { lock (gate) { got = userData; } };
            _dte.CallConnected += (port, lci) => { lock (gate) { connected = true; } };

            ushort outLci = _dte.PlaceCall("23420001", "23400001", null);
            WaitFor(() => { lock (gate) { return connected; } }, 3000);
            Assert.That(connected, Is.True);

            _dte.SendInterrupt(outLci, new byte[] { 0x42 });

            WaitFor(() => { lock (gate) { return got != null; } }, 3000);
            Assert.That(got, Is.EqualTo(new byte[] { 0x42 }), "X.25 4.3.2: the interrupt carries its user data");
        }
    }
}
