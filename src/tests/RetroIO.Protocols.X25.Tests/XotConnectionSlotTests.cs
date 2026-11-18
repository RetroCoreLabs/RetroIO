//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Ports;
using RetroIO.Protocols.X25.Transport;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The XOT listeners have one connection slot per X.25 channel, copied from x25emu's XOT port
    /// (tools/x25switch.c: conn_max = vc_count; the listener is only polled while conn_slot_free finds
    /// a slot). With every slot taken the next connection is neither accepted-and-closed nor served: it
    /// waits, and is taken when a connection closes.
    /// </summary>
    /// <remarks>
    /// Until 2026-11-18 the XOT server entry had a made-up limit of 100 and accepted the 101st
    /// connection and closed it at once; when that was a call's connection, the call was cleared with
    /// cause 9 (RetroX25 BurstTests.CallStorm_IntoTheTad_IsCountedExactly_AndLeavesNoCircuit, 1-2 runs
    /// in 10). The switch port refused the same way.
    /// </remarks>
    [TestFixture]
    public class XotConnectionSlotTests
    {
        private const int WaitMs = 10000;

        // A Call Request in an XOT frame (RFC 1613: version 0, length), channel 1, empty addresses.
        private static readonly byte[] XotCallRequest = new byte[] { 0x00, 0x00, 0x00, 0x04, 0x10, 0x01, 0x0B, 0x00 };

        [Test]
        public void ServerOwnSocket_FullSlots_NextConnectionWaits_AndIsTakenWhenACallEnds()
        {
            int port = FreePort();
            XotServerTransport server = new XotServerTransport(NullLogger.Instance, new XotServerConfig
            {
                Name = "slots",
                ListenAddress = "127.0.0.1",
                ListenPort = port,
                MaxConnections = 2,
            });
            List<TcpClient> clients = new List<TcpClient>();
            try
            {
                Assert.That(server.Start(), Is.True);
                CheckFullThenFreed(server, port, 2, clients);
            }
            finally
            {
                server.Stop();
                CloseAll(clients);
            }
        }

        [Test]
        public void ServerSharedSocket_FullSlots_NextConnectionWaits_AndIsTakenWhenACallEnds()
        {
            int port = FreePort();
            using XotListenerRegistry registry = new XotListenerRegistry(NullLogger.Instance);
            XotServerTransport server = new XotServerTransport(NullLogger.Instance, new XotServerConfig
            {
                Name = "slots-shared",
                ListenAddress = "127.0.0.1",
                ListenPort = port,
                MaxConnections = 2,
                Listeners = registry,
            });
            List<TcpClient> clients = new List<TcpClient>();
            try
            {
                Assert.That(server.Start(), Is.True);
                CheckFullThenFreed(server, port, 2, clients);
                Assert.That(server.WaitingConnections, Is.EqualTo(0), "the shared socket did not accept while the only entry was full");
            }
            finally
            {
                server.Stop();
                CloseAll(clients);
            }
        }

        /// <summary>
        /// 100 calls and a caller's spare against a line of 100 channels, on the shared socket the app
        /// uses: every call is served, the spare waits, and the spare is taken when a call ends.
        /// </summary>
        [Test]
        public void HundredCallsAndASpare_OnHundredChannels_EveryCallIsServed_TheSpareWaits()
        {
            const int channels = 100;
            int port = FreePort();
            using XotListenerRegistry registry = new XotListenerRegistry(NullLogger.Instance);
            XotServerTransport server = new XotServerTransport(NullLogger.Instance, new XotServerConfig
            {
                Name = "hundred",
                ListenAddress = "127.0.0.1",
                ListenPort = port,
                MaxConnections = channels,
                Listeners = registry,
            });
            int callsSeen = 0;
            server.DataReceived += (s, e) => Interlocked.Increment(ref callsSeen);
            List<TcpClient> calls = new List<TcpClient>();
            TcpClient? spare = null;
            try
            {
                Assert.That(server.Start(), Is.True);
                for (int i = 0; i < channels; i++)
                {
                    TcpClient call = new TcpClient();
                    call.Connect(IPAddress.Loopback, port);
                    call.GetStream().Write(XotCallRequest, 0, XotCallRequest.Length);
                    calls.Add(call);
                }

                Assert.That(WaitFor(() => Volatile.Read(ref callsSeen) == channels), Is.True,
                    "every call reached the server: " + Volatile.Read(ref callsSeen) + " of " + channels);

                spare = new TcpClient();
                spare.Connect(IPAddress.Loopback, port);
                Assert.That(server.GetStatistics().ActiveConnections, Is.EqualTo(channels), "the spare has no slot");
                Assert.That(IsClosedByFarEnd(spare), Is.False, "the spare is not closed");

                // A call ends: its connection closes, and the spare gets the slot.
                calls[0].Close();
                calls.RemoveAt(0);
                spare.GetStream().Write(XotCallRequest, 0, XotCallRequest.Length);
                Assert.That(WaitFor(() => Volatile.Read(ref callsSeen) == channels + 1), Is.True,
                    "the spare was taken once a call ended");
                Assert.That(server.GetStatistics().ActiveConnections, Is.EqualTo(channels));
            }
            finally
            {
                server.Stop();
                CloseAll(calls);
                spare?.Close();
            }
        }

        [Test]
        public void SwitchPort_FullSlots_NextConnectionWaits_AndIsTakenWhenACallEnds()
        {
            using XotSwitchPort port = new XotSwitchPort(new PortConfig
            {
                Id = 0,
                Mode = PortMode.Dce,
                Port = 0,
                LciBase = 1,
                LciCount = 2,
            });
            Assert.That(port.Start(), Is.True);
            List<TcpClient> clients = new List<TcpClient>();
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    TcpClient c = new TcpClient();
                    c.Connect(IPAddress.Loopback, port.BoundPort);
                    clients.Add(c);
                }

                Assert.That(WaitFor(() => port.ConnectionCount == 2), Is.True, "two connections take the two slots");

                TcpClient third = new TcpClient();
                third.Connect(IPAddress.Loopback, port.BoundPort);
                clients.Add(third);
                Assert.That(IsClosedByFarEnd(third), Is.False, "the third connection is not closed");
                Assert.That(port.ConnectionCount, Is.EqualTo(2), "the third connection has no slot");
                Assert.That(port.ConnectAttempts, Is.EqualTo(2), "the third connection was not accepted");

                clients[0].Close();
                Assert.That(WaitFor(() => port.ConnectAttempts == 3), Is.True, "the waiting connection was accepted once a connection closed");
                Assert.That(WaitFor(() => port.ConnectionCount == 2), Is.True);
                Assert.That(IsClosedByFarEnd(third), Is.False);
            }
            finally
            {
                port.Stop();
                CloseAll(clients);
            }
        }

        [Test]
        public void Slots_BelowOne_Throw()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new XotConnectionSlots(0));
        }

        [Test]
        public void Slots_ReleasedWithNoneTaken_Throw()
        {
            XotConnectionSlots slots = new XotConnectionSlots(1);
            Assert.Throws<InvalidOperationException>(() => slots.Release());
        }

        private static void CheckFullThenFreed(XotServerTransport server, int port, int slots, List<TcpClient> clients)
        {
            int packets = 0;
            server.DataReceived += (s, e) => Interlocked.Increment(ref packets);

            for (int i = 0; i < slots; i++)
            {
                TcpClient c = new TcpClient();
                c.Connect(IPAddress.Loopback, port);
                clients.Add(c);
            }

            Assert.That(WaitFor(() => server.GetStatistics().ActiveConnections == slots), Is.True, "the first connections take every slot");

            TcpClient extra = new TcpClient();
            extra.Connect(IPAddress.Loopback, port);
            clients.Add(extra);
            extra.GetStream().Write(XotCallRequest, 0, XotCallRequest.Length);

            Assert.That(server.GetStatistics().ActiveConnections, Is.EqualTo(slots), "the extra connection has no slot");
            Assert.That(IsClosedByFarEnd(extra), Is.False, "the extra connection is not closed");
            Assert.That(Volatile.Read(ref packets), Is.EqualTo(0), "nothing is read from a connection without a slot");

            // A call ends: its connection closes. The waiting connection gets the slot and its packet is read.
            clients[0].Close();
            Assert.That(WaitFor(() => Volatile.Read(ref packets) == 1), Is.True, "the waiting connection was taken once a slot freed");
            Assert.That(server.GetStatistics().ActiveConnections, Is.EqualTo(slots));
            Assert.That(IsClosedByFarEnd(extra), Is.False);
        }

        /// <summary>
        /// True when the far end has closed the connection: readable with nothing to read. The far end
        /// never sends anything in these tests, so readable means closed.
        /// </summary>
        private static bool IsClosedByFarEnd(TcpClient client)
        {
            try
            {
                return client.Client.Poll(200000, SelectMode.SelectRead) && client.Client.Available == 0;
            }
            catch (SocketException)
            {
                return true;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }

        private static bool WaitFor(Func<bool> condition)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < WaitMs)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(10);
            }

            return condition();
        }

        private static int FreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static void CloseAll(List<TcpClient> clients)
        {
            for (int i = 0; i < clients.Count; i++)
            {
                clients[i].Close();
            }
        }
    }
}
