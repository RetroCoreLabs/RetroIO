//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
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
    /// What an operator needs to see about an XOT link to a peer: is TCP up, how often did a connect
    /// fail and why, how often was the link dropped. Real sockets on the loopback address; the peer is
    /// a plain listener that the test starts and stops.
    /// </summary>
    [TestFixture]
    public class XotLinkHealthTests
    {
        private static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(20);
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

        /// <summary>
        /// A dialling switch port whose peer is down counts each failed dial and keeps the reason; when
        /// the peer comes up it connects by itself, the failure count and the reason stay (they are history),
        /// and a connection that the peer then closes is counted as a disconnect.
        /// </summary>
        [Test]
        public void DialingSwitchPort_CountsFailedDials_ConnectsWhenThePeerAppears_AndCountsDisconnects()
        {
            int port = FreePort();
            using XotSwitchPort dte = new XotSwitchPort(new PortConfig
            {
                Id = 1,
                Mode = PortMode.Dte,
                Host = "127.0.0.1",
                Port = (ushort)port,
                LciBase = 1,
                LciCount = 16,
            });

            Assert.That(dte.RedialDelay, Is.EqualTo(2000), "the redial delay shown to the operator must be the one the port uses");
            Assert.That(dte.Start(), Is.True);

            // Nobody listens: dials fail, one every redial delay.
            Assert.That(WaitUntil(() => dte.ConnectFailures >= 2, 8000), Is.True, "no failed dials counted");
            Assert.That(dte.PeerConnected, Is.False);
            Assert.That(dte.LastError, Is.Not.Empty, "a failed dial must keep its reason");
            Assert.That(dte.LastFailureMs, Is.GreaterThan(0));
            Assert.That(dte.Disconnects, Is.EqualTo(0));
            Assert.That(dte.LastConnectedMs, Is.EqualTo(0));

            // The peer appears: the port connects by itself.
            TcpListener peer = new TcpListener(IPAddress.Loopback, port);
            peer.Start();
            try
            {
                Assert.That(WaitUntil(() => dte.PeerConnected, 8000), Is.True, "the port did not connect when the peer came up");
                int failuresWhenUp = dte.ConnectFailures;
                Assert.That(failuresWhenUp, Is.GreaterThanOrEqualTo(2));
                Assert.That(dte.LastError, Is.Not.Empty, "the reason of the last failure stays as history");
                Assert.That(dte.LastConnectedMs, Is.GreaterThan(0));
                Assert.That(dte.Disconnects, Is.EqualTo(0));

                // The peer closes the connection: one disconnect.
                using (TcpClient accepted = peer.AcceptTcpClient())
                {
                    accepted.Close();
                }

                Assert.That(WaitUntil(() => dte.Disconnects >= 1, 5000), Is.True, "a closed connection was not counted");
                Assert.That(dte.LastDisconnectedMs, Is.GreaterThan(0));
            }
            finally
            {
                peer.Stop();
            }
        }

        /// <summary>
        /// A client link opens its TCP connection only when a call needs it. A connect to a dead peer is
        /// counted with its reason and time; a connect that works records its time; a connection the peer
        /// then closes is counted as a disconnect, while closing it because the transport is stopping is not.
        /// </summary>
        [Test]
        public void ClientTransport_RecordsFailedConnect_SuccessTime_AndDisconnects()
        {
            int port = FreePort();
            XotClientTransport transport = new XotClientTransport(NullLogger.Instance, new XotClientConfig { Name = "xotc", ConnectTimeout = 2000 });
            Assert.That(transport.Start(), Is.True);

            Assert.That(transport.ConnectToXotGateway("127.0.0.1", port), Is.False, "nobody listens");
            TransportStatistics failed = transport.GetStatistics();
            Assert.That(failed.ConnectionErrors, Is.EqualTo(1));
            Assert.That(failed.LastError, Is.Not.Empty);
            Assert.That(failed.LastFailureUtc, Is.Not.Null);
            Assert.That(failed.LastConnectedUtc, Is.Null);
            Assert.That(failed.ActiveConnections, Is.EqualTo(0));

            TcpListener peer = new TcpListener(IPAddress.Loopback, port);
            peer.Start();
            try
            {
                Assert.That(transport.ConnectToXotGateway("127.0.0.1", port), Is.True);
                TransportStatistics up = transport.GetStatistics();
                Assert.That(up.LastConnectedUtc, Is.Not.Null);
                Assert.That(up.ActiveConnections, Is.EqualTo(1));
                Assert.That(up.Disconnects, Is.EqualTo(0));
                Assert.That(up.LastError, Is.Not.Empty, "the reason of the earlier failure stays as history");

                using (TcpClient accepted = peer.AcceptTcpClient())
                {
                    accepted.Close();
                }

                Assert.That(WaitUntil(() => transport.GetStatistics().Disconnects == 1, 5000), Is.True, "the closed connection was not counted");
                Assert.That(transport.GetStatistics().ActiveConnections, Is.EqualTo(0));
            }
            finally
            {
                peer.Stop();
                transport.Stop();
            }
        }

        /// <summary>
        /// Several calls placed at the same moment must share the ONE connection a client link keeps per peer address: no connection
        /// may be opened and then forgotten. Found 2026-11-13 against the real router R1: two calls at once opened two TCP connections,
        /// the second replaced the first in the table, and the first stayed open after the transport was stopped.
        /// </summary>
        [Test]
        public void ClientTransport_ConnectsFromSeveralThreadsAtOnce_KeepsOneConnection_AndStopClosesIt()
        {
            int port = FreePort();
            XotClientTransport transport = new XotClientTransport(NullLogger.Instance, new XotClientConfig { Name = "xotc", ConnectTimeout = 2000 });
            Assert.That(transport.Start(), Is.True);
            TcpListener peer = new TcpListener(IPAddress.Loopback, port);
            peer.Start();
            System.Collections.Generic.List<TcpClient> accepted = new System.Collections.Generic.List<TcpClient>();
            try
            {
                const int Threads = 8;
                Barrier start = new Barrier(Threads);
                Thread[] threads = new Thread[Threads];
                for (int i = 0; i < Threads; i++)
                {
                    threads[i] = new Thread(() =>
                    {
                        start.SignalAndWait();
                        Assert.That(transport.ConnectToXotGateway("127.0.0.1", port), Is.True);
                    });
                    threads[i].Start();
                }

                for (int i = 0; i < Threads; i++)
                {
                    threads[i].Join();
                }

                Thread.Sleep(300);
                while (peer.Pending())
                {
                    accepted.Add(peer.AcceptTcpClient());
                }

                Assert.That(accepted.Count, Is.EqualTo(1), "TCP connections the peer saw");
                Assert.That(transport.GetStatistics().ActiveConnections, Is.EqualTo(1));

                transport.Stop();
                for (int i = 0; i < accepted.Count; i++)
                {
                    Assert.That(WaitUntil(() => accepted[i].Client.Poll(0, SelectMode.SelectRead) && accepted[i].Client.Available == 0, 3000), Is.True,
                        "connection " + i + " is still open after Stop");
                }
            }
            finally
            {
                for (int i = 0; i < accepted.Count; i++)
                {
                    accepted[i].Close();
                }

                peer.Stop();
                transport.Stop();
            }
        }
    }
}
