//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// What a monitoring switch reads from an <see cref="XotSwitchPort"/>: the packet tap, the byte
    /// totals, the link statistics, the log lines, and the address a listening port binds to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The C emulator's switch reports the same things (connect attempts, last connected and
    /// disconnected times, bytes sent and received at the X.25 packet layer), and its source says it
    /// mirrors this port. These tests pin the C# side to the rules the C side counts by, so a monitor
    /// connected to either shows the same kind of number.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class XotSwitchPortMonitoringTests
    {
        private const int WaitMs = 5000;

        private sealed class Captured
        {
            public bool Inbound;
            public byte[] Packet = Array.Empty<byte>();
        }

        private sealed class ListLogger : ILogger
        {
            private readonly List<string> _lines = new List<string>();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (_lines)
                {
                    _lines.Add(logLevel + ": " + formatter(state, exception));
                }
            }

            public bool Contains(string fragment)
            {
                lock (_lines)
                {
                    for (int i = 0; i < _lines.Count; i++)
                    {
                        if (_lines[i].Contains(fragment, StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
        }

        private static bool WaitFor(Func<bool> condition)
        {
            return SpinWait.SpinUntil(condition, WaitMs);
        }

        private static XotSwitchPort NewDce(ILogger? logger = null, string host = "")
        {
            return new XotSwitchPort(
                new PortConfig { Id = 0, Mode = PortMode.Dce, Host = host, Port = 0, LciBase = 1, LciCount = 16 },
                logger);
        }

        private static XotSwitchPort NewDte(ushort port, ILogger? logger = null)
        {
            return new XotSwitchPort(
                new PortConfig { Id = 1, Mode = PortMode.Dte, Host = "127.0.0.1", Port = port, LciBase = 1, LciCount = 16 },
                logger);
        }

        private static void Tap(XotSwitchPort port, List<Captured> into)
        {
            port.PacketCaptured += (portId, inbound, packet) =>
            {
                lock (into)
                {
                    into.Add(new Captured { Inbound = inbound, Packet = packet });
                }
            };
        }

        private static int CountOf(List<Captured> list, bool inbound, X25PacketType type)
        {
            int count = 0;
            lock (list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Inbound == inbound && X25PacketParser.ParsePacket(list[i].Packet).PacketType == type)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static long BytesOf(List<Captured> list, bool inbound)
        {
            long total = 0;
            lock (list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Inbound == inbound)
                    {
                        total += list[i].Packet.Length;
                    }
                }
            }

            return total;
        }

        /// <summary>
        /// A call across the connection shows up in the tap of both ports, each packet once, in the
        /// direction it travelled.
        /// </summary>
        [Test]
        public void ACall_IsSeenByTheTapOnBothPorts_InTheRightDirection()
        {
            using XotSwitchPort dce = NewDce();
            Assert.That(dce.Start(), Is.True);
            using XotSwitchPort dte = NewDte(dce.BoundPort);
            List<Captured> onDce = new List<Captured>();
            List<Captured> onDte = new List<Captured>();
            Tap(dce, onDce);
            Tap(dte, onDte);
            bool connected = false;
            dce.IncomingCall += (port, lci, called, calling, cud) => port.AcceptCall(lci);
            dte.CallConnected += (port, lci) => connected = true;

            Assert.That(dte.Start(), Is.True);
            Assert.That(WaitFor(() => dte.PeerConnected && dce.PeerConnected), Is.True);
            Assert.That(dte.PlaceCall("23420001", "23400001", null), Is.Not.EqualTo((ushort)0));
            Assert.That(WaitFor(() => connected), Is.True, "the call must be accepted back");

            Assert.That(CountOf(onDte, false, X25PacketType.CallRequest), Is.EqualTo(1), "dialling side sent the call request");
            Assert.That(CountOf(onDte, true, X25PacketType.CallAccepted), Is.EqualTo(1), "dialling side received the call accepted");
            Assert.That(CountOf(onDce, true, X25PacketType.CallRequest), Is.EqualTo(1), "listening side received the call request");
            Assert.That(CountOf(onDce, false, X25PacketType.CallAccepted), Is.EqualTo(1), "listening side sent the call accepted");
        }

        /// <summary>
        /// The byte totals count X.25 packet bytes, with no XOT header: they equal the sum of the
        /// packets the tap showed, and what one port sent the other received.
        /// </summary>
        [Test]
        public void ByteTotals_CountPacketBytes_AndMatchTheTapAndTheOtherEnd()
        {
            using XotSwitchPort dce = NewDce();
            Assert.That(dce.Start(), Is.True);
            using XotSwitchPort dte = NewDte(dce.BoundPort);
            List<Captured> onDce = new List<Captured>();
            List<Captured> onDte = new List<Captured>();
            Tap(dce, onDce);
            Tap(dte, onDte);
            bool connected = false;
            dce.IncomingCall += (port, lci, called, calling, cud) => port.AcceptCall(lci);
            dte.CallConnected += (port, lci) => connected = true;

            Assert.That(dte.Start(), Is.True);
            Assert.That(WaitFor(() => dte.PeerConnected && dce.PeerConnected), Is.True);
            ushort lci = dte.PlaceCall("23420001", "23400001", null);
            Assert.That(WaitFor(() => connected), Is.True);
            Assert.That(dte.SendData(lci, new byte[] { 1, 2, 3, 4, 5 }, false, false, false), Is.EqualTo(PortSendResult.Sent));

            Assert.That(WaitFor(() => dce.Counters.BytesIn == dte.Counters.BytesOut && dte.Counters.BytesOut > 0),
                Is.True, "what the dialling port sent, the listening port received");
            Assert.That(dte.Counters.BytesOut, Is.EqualTo(BytesOf(onDte, false)), "sent total equals the tap");
            Assert.That(dce.Counters.BytesIn, Is.EqualTo(BytesOf(onDce, true)), "received total equals the tap");
            Assert.That(dte.Counters.BytesOut, Is.GreaterThan(5), "more than the five data bytes: the headers count");
        }

        /// <summary>
        /// A listening port counts a connection attempt for each connection it accepts, and records
        /// when the peer connected; nothing has disconnected yet.
        /// </summary>
        [Test]
        public void ListeningPort_CountsEachAccept_AndStampsTheConnectTime()
        {
            using XotSwitchPort dce = NewDce();
            Assert.That(dce.Start(), Is.True);
            Assert.That(dce.ConnectAttempts, Is.EqualTo(0), "binding is not a connection attempt");
            Assert.That(dce.LastConnectedMs, Is.EqualTo(0));

            using XotSwitchPort dte = NewDte(dce.BoundPort);
            Assert.That(dte.Start(), Is.True);

            Assert.That(WaitFor(() => dce.PeerConnected), Is.True);
            Assert.That(dce.ConnectAttempts, Is.EqualTo(1));
            Assert.That(dce.LastConnectedMs, Is.GreaterThan(0));
            Assert.That(dce.LastDisconnectedMs, Is.EqualTo(0));
        }

        /// <summary>
        /// When the peer goes away the disconnect time is stamped, once, and the port is no longer
        /// marked as having a peer.
        /// </summary>
        [Test]
        public void WhenThePeerGoesAway_TheDisconnectTimeIsStamped()
        {
            using XotSwitchPort dce = NewDce();
            Assert.That(dce.Start(), Is.True);
            XotSwitchPort dte = NewDte(dce.BoundPort);
            Assert.That(dte.Start(), Is.True);
            Assert.That(WaitFor(() => dce.PeerConnected && dte.PeerConnected), Is.True);

            dte.Dispose();

            Assert.That(WaitFor(() => !dce.PeerConnected), Is.True, "the listening port must notice the peer leaving");
            Assert.That(dce.LastDisconnectedMs, Is.GreaterThan(0));
            Assert.That(dce.LastDisconnectedMs, Is.GreaterThanOrEqualTo(dce.LastConnectedMs));
        }

        /// <summary>
        /// A dialling port whose peer is not there counts every attempt it makes, and never records a connection.
        /// </summary>
        [Test]
        public void DialingPortWithNoPeer_CountsEachAttempt_AndNeverConnects()
        {
            using TcpListener closedPort = new TcpListener(System.Net.IPAddress.Loopback, 0);
            closedPort.Start();
            ushort port = (ushort)((System.Net.IPEndPoint)closedPort.LocalEndpoint).Port;
            closedPort.Stop();

            using XotSwitchPort dte = NewDte(port);
            Assert.That(dte.Start(), Is.True, "a dialling port starts even when the peer is down");

            Assert.That(WaitFor(() => dte.ConnectAttempts >= 2), Is.True, "it must keep trying");
            Assert.That(dte.PeerConnected, Is.False);
            Assert.That(dte.LastConnectedMs, Is.EqualTo(0));
            Assert.That(dte.LastDisconnectedMs, Is.EqualTo(0), "nothing connected, so nothing disconnected");
        }

        /// <summary>
        /// A listening port binds to the address in its configuration, not to every address.
        /// </summary>
        [Test]
        public void ListeningPort_BindsToTheConfiguredAddress()
        {
            using XotSwitchPort dce = NewDce(host: "127.0.0.1");
            Assert.That(dce.Start(), Is.True);

            using TcpClient client = new TcpClient();
            client.Connect("127.0.0.1", dce.BoundPort);
            Assert.That(WaitFor(() => dce.PeerConnected), Is.True);
        }

        /// <summary>
        /// An empty host and <c>0.0.0.0</c> both mean every address, as in the C emulator's
        /// <c>(any)</c>.
        /// </summary>
        [TestCase("")]
        [TestCase("0.0.0.0")]
        public void ListeningPort_WithNoSpecificAddress_ListensOnAll(string host)
        {
            using XotSwitchPort dce = NewDce(host: host);
            Assert.That(dce.Start(), Is.True);

            using TcpClient client = new TcpClient();
            client.Connect("127.0.0.1", dce.BoundPort);
            Assert.That(WaitFor(() => dce.PeerConnected), Is.True);
        }

        /// <summary>
        /// A listening port that cannot bind says so and returns false, and the log carries the reason:
        /// an address that is not on this machine, and a host that is not an address.
        /// </summary>
        [TestCase("203.0.113.1")]
        [TestCase("not-an-address")]
        public void ListeningPort_ThatCannotBind_ReturnsFalse_AndLogsIt(string host)
        {
            ListLogger log = new ListLogger();
            using XotSwitchPort dce = NewDce(log, host);

            Assert.That(dce.Start(), Is.False);
            Assert.That(dce.Active, Is.False);
            Assert.That(log.Contains("could NOT start"), Is.True);
            Assert.That(log.Contains("Error"), Is.True);
        }

        /// <summary>
        /// A listening port whose TCP port is already taken does not start.
        /// </summary>
        [Test]
        public void ListeningPort_WhoseTcpPortIsTaken_DoesNotStart()
        {
            using TcpListener holder = new TcpListener(System.Net.IPAddress.Any, 0);
            holder.Start();
            ushort taken = (ushort)((System.Net.IPEndPoint)holder.LocalEndpoint).Port;

            using XotSwitchPort dce = new XotSwitchPort(
                new PortConfig { Id = 0, Mode = PortMode.Dce, Port = taken, LciBase = 1, LciCount = 16 });

            Assert.That(dce.Start(), Is.False);
            Assert.That(dce.Active, Is.False);
        }

        /// <summary>
        /// The log gets one line for the port listening, one for the connection coming up and one for
        /// it going down.
        /// </summary>
        [Test]
        public void TheLog_RecordsListening_ConnectionUp_AndConnectionDown()
        {
            ListLogger log = new ListLogger();
            using XotSwitchPort dce = NewDce(log);
            Assert.That(dce.Start(), Is.True);
            Assert.That(log.Contains("listening on"), Is.True);

            XotSwitchPort dte = NewDte(dce.BoundPort);
            Assert.That(dte.Start(), Is.True);
            Assert.That(WaitFor(() => log.Contains("connection up")), Is.True);

            dte.Dispose();
            Assert.That(WaitFor(() => log.Contains("connection down")), Is.True);
        }
    }
}
