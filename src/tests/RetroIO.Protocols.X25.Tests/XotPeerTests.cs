//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Client;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Transport;
using ClientVc = RetroIO.Protocols.X25.Client.X25Vc;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The XOT peer entry (mode Both): one entry that dials out per call and accepts from the same peer.
    /// Everything here runs over real TCP sockets on the loopback addresses 127.0.0.1 and 127.0.0.2.
    /// RFC 1613 section 5 is the rule under test: a separate TCP connection for each virtual circuit.
    /// </summary>
    [TestFixture]
    public class XotPeerTests
    {
        private const string AddressA = "23430100";
        private const string AddressB = "23430200";

        private readonly List<IDisposable> _toDispose = new List<IDisposable>();

        /// <summary>A logger that keeps every message, so a test can read the warnings.</summary>
        private sealed class ListLogger : ILogger
        {
            private readonly List<string> _lines = new List<string>();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (_lines)
                {
                    _lines.Add(logLevel + ": " + formatter(state, exception));
                }
            }

            public bool Contains(string piece)
            {
                lock (_lines)
                {
                    for (int i = 0; i < _lines.Count; i++)
                    {
                        if (_lines[i].Contains(piece, StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _toDispose.Count - 1; i >= 0; i--)
            {
                _toDispose[i].Dispose();
            }

            _toDispose.Clear();
        }

        private static int FreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
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

        private ProtocolRuntime Start(string localAddress, string? routeInterface, params XotInterfaceConfig[] entries)
        {
            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = localAddress;
            for (int i = 0; i < entries.Length; i++)
            {
                config.Routing.XotInterfaces.Add(entries[i]);
            }

            // Calls to our own address are answered by the built-in TAD.
            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "LocalTAD",
                X121AddressPattern = localAddress,
                Type = RetroIO.Protocols.X25.Runtime.RouteType.Local,
                Destination = "LocalTAD",
                Priority = 200,
                Enabled = true,
            });

            if (routeInterface != null)
            {
                config.Routing.RoutingTable.Add(new X25RouteEntry
                {
                    Name = "To" + routeInterface,
                    X121AddressPattern = "2343.*",
                    Type = RetroIO.Protocols.X25.Runtime.RouteType.Interface,
                    Destination = routeInterface,
                    Priority = 100,
                    Enabled = true,
                });
            }

            ProtocolRuntime runtime = new ProtocolRuntime(NullLogger.Instance, config);
            _toDispose.Add(runtime);
            Assert.That(runtime.StartTransportInterfaces(), Is.True, "the XOT entries should start");
            return runtime;
        }

        private static XotPeerTransport PeerOf(ProtocolRuntime runtime, string name)
        {
            return (XotPeerTransport)runtime.TransportInterfaces[name];
        }

        private static PadClient ConnectedPad(ProtocolRuntime runtime, string padName, string called, string calling)
        {
            PadClient pad = runtime.CreatePadClient(padName);
            ManualResetEventSlim connected = new ManualResetEventSlim(false);
            pad.ConnectionStateChanged += (s, a) =>
            {
                if (a.State == PadConnectionState.Connected)
                {
                    connected.Set();
                }
            };

            Assert.That(pad.Connect(called, calling), Is.True, "the call was refused at once");
            Assert.That(connected.Wait(10000), Is.True, "the PAD never got Call Accepted");
            return pad;
        }

        // ------------------------------------------------------------------ dial out

        /// <summary>
        /// A call routed to a peer entry opens a TCP connection to the peer then, not before, and the
        /// call is answered by the far end's built-in TAD.
        /// </summary>
        [Test]
        public void PeerEntry_DialsOut_WhenACallNeedsIt()
        {
            int portB = FreePort();
            ProtocolRuntime b = Start(AddressB, null, new XotInterfaceConfig { Name = "FromA", Mode = XotMode.Server, LocalPort = portB, RemoteMachineId = "A" });
            ProtocolRuntime a = Start(AddressA, "ToB", new XotInterfaceConfig
            {
                Name = "ToB", Mode = XotMode.Both, RemoteIPAddress = "127.0.0.1", RemotePort = portB, LocalPort = FreePort(), RemoteMachineId = "B",
            });

            Assert.That(PeerOf(a, "ToB").GetPeerReport().DialedOpened, Is.EqualTo(0), "no connection before a call needs it");

            PadClient pad = ConnectedPad(a, "pad1", AddressB, AddressA);

            XotPeerReport report = PeerOf(a, "ToB").GetPeerReport();
            Assert.That(report.DialAttempts, Is.EqualTo(1));
            Assert.That(report.DialedOpened, Is.EqualTo(1));
            Assert.That(report.DialedOpenNow, Is.EqualTo(1));
            Assert.That(report.CallsPlaced, Is.EqualTo(1));
            Assert.That(report.Accepted, Is.EqualTo(0));
            Assert.That(WaitUntil(() => b.TransportInterfaces["FromA"].GetStatistics().ActiveConnections == 1), Is.True);
            pad.Disconnect();
        }

        /// <summary>
        /// RFC 1613 section 5: two calls are two TCP connections. Both show in the statistics, and the
        /// connection of a call that is cleared is closed while the other stays.
        /// </summary>
        [Test]
        public void TwoCalls_AreTwoTcpConnections_AndAClearedCallsConnectionCloses()
        {
            int portB = FreePort();
            ProtocolRuntime b = Start(AddressB, null, new XotInterfaceConfig { Name = "FromA", Mode = XotMode.Server, LocalPort = portB, RemoteMachineId = "A" });
            ProtocolRuntime a = Start(AddressA, "ToB", new XotInterfaceConfig
            {
                Name = "ToB", Mode = XotMode.Both, RemoteIPAddress = "127.0.0.1", RemotePort = portB, LocalPort = FreePort(), RemoteMachineId = "B",
            });

            PadClient pad1 = ConnectedPad(a, "pad1", AddressB, AddressA);
            PadClient pad2 = ConnectedPad(a, "pad2", AddressB, AddressA);

            XotPeerReport report = PeerOf(a, "ToB").GetPeerReport();
            Assert.That(report.DialedOpened, Is.EqualTo(2), "two calls must open two TCP connections");
            Assert.That(report.DialedOpenNow, Is.EqualTo(2));
            Assert.That(PeerOf(a, "ToB").GetStatistics().ActiveConnections, Is.EqualTo(2), "both connections must show in the transport statistics");
            Assert.That(WaitUntil(() => b.TransportInterfaces["FromA"].GetStatistics().ActiveConnections == 2), Is.True, "the far end must see two connections");

            pad1.Disconnect();
            Assert.That(WaitUntil(() => PeerOf(a, "ToB").GetPeerReport().DialedOpenNow == 1), Is.True, "the cleared call's connection must close");
            Assert.That(WaitUntil(() => b.TransportInterfaces["FromA"].GetStatistics().ActiveConnections == 1), Is.True);
            Assert.That(PeerOf(a, "ToB").GetPeerReport().DialDrops, Is.EqualTo(0), "a normal close after the call is not a drop");
            pad2.Disconnect();
        }

        /// <summary>
        /// A failed dial is reported (count, reason) and not retried by itself; the next call tries again.
        /// </summary>
        [Test]
        public void FailedDial_IsReported_AndTheNextCallTriesAgain()
        {
            int deadPort = FreePort();
            ProtocolRuntime a = Start(AddressA, "ToB", new XotInterfaceConfig
            {
                Name = "ToB", Mode = XotMode.Both, RemoteIPAddress = "127.0.0.1", RemotePort = deadPort, LocalPort = FreePort(), RemoteMachineId = "B", ConnectTimeoutMs = 2000,
            });

            PadClient pad = a.CreatePadClient("pad1");
            pad.Connect(AddressB, AddressA);
            Assert.That(WaitUntil(() => PeerOf(a, "ToB").GetPeerReport().DialFailures == 1), Is.True);
            XotPeerReport report = PeerOf(a, "ToB").GetPeerReport();
            Assert.That(report.LastDialError, Is.Not.Empty);
            Thread.Sleep(500);
            Assert.That(PeerOf(a, "ToB").GetPeerReport().DialAttempts, Is.EqualTo(1), "no retry timer: only the next call tries again");

            PadClient pad2 = a.CreatePadClient("pad2");
            pad2.Connect(AddressB, AddressA);
            Assert.That(WaitUntil(() => PeerOf(a, "ToB").GetPeerReport().DialFailures == 2), Is.True);
        }

        // ------------------------------------------------------------------ accept

        private ClientVc? CallInto(int port, string called, string calling, out X25Client client)
        {
            client = new X25Client { ServiceName = "caller" };
            _toDispose.Add(client);
            client.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = (ushort)port,
                LocalAddress = calling,
                Framing = X25ClientFraming.Xot,
            }, NullLogger.Instance);
            return client.CallWait(called, 5000);
        }

        /// <summary>
        /// The listed peer dials in: its connection is accepted by the entry and its call is routed
        /// (here to the built-in TAD).
        /// </summary>
        [Test]
        public void PeerEntry_AcceptsAnIncomingConnectionFromTheListedIp_AndRoutesItsCall()
        {
            int listen = FreePort();
            ProtocolRuntime a = Start(AddressA, "Peer", new XotInterfaceConfig
            {
                Name = "Peer", Mode = XotMode.Both, RemoteIPAddress = "127.0.0.1", RemotePort = FreePort(), LocalPort = listen, RemoteMachineId = "B",
            });

            ClientVc? vc = CallInto(listen, AddressA, AddressB, out _);
            Assert.That(vc, Is.Not.Null, "the call from the listed peer should be answered");

            // Two connections from the X25Client: the one its call went on, and the spare it opens next for calls that
            // may arrive (one connection per call, RFC 1613 section 5; since 2026-11-18).
            Assert.That(SpinWait.SpinUntil(() => PeerOf(a, "Peer").GetPeerReport().AcceptedOpenNow == 2, 5000), Is.True,
                "accepted now: " + PeerOf(a, "Peer").GetPeerReport().AcceptedOpenNow);
            XotPeerReport report = PeerOf(a, "Peer").GetPeerReport();
            Assert.That(report.Accepted, Is.EqualTo(2));
            Assert.That(report.AcceptedOpenNow, Is.EqualTo(2));
            Assert.That(report.CallsReceived, Is.EqualTo(1));
            Assert.That(report.Unlisted, Is.Empty, "127.0.0.1 is the listed peer");
            Assert.That(report.DialedOpened, Is.EqualTo(0));
        }

        /// <summary>
        /// Calls into a listening entry and waits for the clear: the cause and diagnostic, or -1 each
        /// when no clear came within five seconds.
        /// </summary>
        private (int Cause, int Diagnostic, bool Connected) CallAndWaitForClear(int port, string called, string calling)
        {
            X25Client client = new X25Client { ServiceName = "refused-caller" };
            _toDispose.Add(client);
            int cause = -1;
            int diagnostic = -1;
            bool connected = false;
            ManualResetEventSlim cleared = new ManualResetEventSlim(false);
            client.SetOnConnected((c, vc) => connected = true);
            client.SetOnClear((c, vc, clearCause, clearDiagnostic) =>
            {
                cause = clearCause;
                diagnostic = clearDiagnostic;
                cleared.Set();
            });
            client.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = (ushort)port,
                LocalAddress = calling,
                Framing = X25ClientFraming.Xot,
            }, NullLogger.Instance);

            Assert.That(client.Call(called), Is.Not.Null, "the client must place the call");
            cleared.Wait(5000);
            return (cause, diagnostic, connected);
        }

        /// <summary>
        /// A call on a connection of a peer entry is decided by the router, as on every other line
        /// (2026-11-18): a number with no inbound route is refused with Not obtainable, diagnostic 67
        /// (X.25 Table C.1 row 24). Until then a peer link skipped the router and the local service
        /// lookup cleared the call itself, with diagnostic 0.
        /// </summary>
        [Test]
        public void PeerEntry_ACallWithNoRoute_IsRefusedByTheRouter_WithNotObtainable()
        {
            int listen = FreePort();
            Start(AddressA, null, new XotInterfaceConfig
            {
                Name = "Peer", Mode = XotMode.Both, RemoteIPAddress = "127.0.0.1", RemotePort = FreePort(), LocalPort = listen, RemoteMachineId = "B",
            });

            (int cause, int diagnostic, bool connected) = CallAndWaitForClear(listen, "99990000", AddressB);

            Assert.That(connected, Is.False, "a call with no route must not connect");
            Assert.That(cause, Is.EqualTo(0x0D), "Table C.1 row 24: not obtainable");
            Assert.That(diagnostic, Is.EqualTo(67), "the router's diagnostic, # 67 invalid called DTE address");
        }

        /// <summary>
        /// The local TAD takes calls to the machine's own address; with no inbound route for it the
        /// router refuses the call before the service is asked. Until 2026-11-18 a peer link skipped the
        /// router and the TAD answered.
        /// </summary>
        [Test]
        public void PeerEntry_ACallToTheLocalAddressWithNoRoute_IsRefusedByTheRouter()
        {
            int listen = FreePort();
            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = AddressA;
            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "Peer", Mode = XotMode.Both, RemoteIPAddress = "127.0.0.1", RemotePort = FreePort(), LocalPort = listen, RemoteMachineId = "B",
            });
            ProtocolRuntime runtime = new ProtocolRuntime(NullLogger.Instance, config);
            _toDispose.Add(runtime);
            Assert.That(runtime.StartTransportInterfaces(), Is.True);

            (int cause, int diagnostic, bool connected) = CallAndWaitForClear(listen, AddressA, AddressB);

            Assert.That(connected, Is.False, "no route: the TAD must not answer");
            Assert.That(cause, Is.EqualTo(0x0D));
            Assert.That(diagnostic, Is.EqualTo(67));
        }

        /// <summary>
        /// One entry does both at the same time: it has dialled out for one call and accepted a
        /// connection for another, and the report keeps the two apart.
        /// </summary>
        [Test]
        public void OneEntry_DialsOutAndAcceptsAtTheSameTime()
        {
            int portB = FreePort();
            int listenA = FreePort();
            Start(AddressB, null, new XotInterfaceConfig { Name = "FromA", Mode = XotMode.Server, LocalPort = portB, RemoteMachineId = "A" });
            ProtocolRuntime a = Start(AddressA, "ToB", new XotInterfaceConfig
            {
                Name = "ToB", Mode = XotMode.Both, RemoteIPAddress = "127.0.0.1", RemotePort = portB, LocalPort = listenA, RemoteMachineId = "B",
            });

            PadClient pad = ConnectedPad(a, "pad1", AddressB, AddressA);
            ClientVc? vc = CallInto(listenA, AddressA, AddressB, out _);
            Assert.That(vc, Is.Not.Null);

            // The caller is an X25Client: its call took the connection Open made, and it then opens a new spare for
            // calls that may arrive (RFC 1613 section 5, one connection per call; since 2026-11-18). So two accepted
            // connections: the call's and the spare, once the spare has arrived.
            Assert.That(SpinWait.SpinUntil(() => PeerOf(a, "ToB").GetPeerReport().AcceptedOpenNow == 2, 5000), Is.True,
                "accepted now: " + PeerOf(a, "ToB").GetPeerReport().AcceptedOpenNow);
            XotPeerReport report = PeerOf(a, "ToB").GetPeerReport();
            Assert.That(report.DialedOpenNow, Is.EqualTo(1));
            Assert.That(report.AcceptedOpenNow, Is.EqualTo(2));
            Assert.That(report.CallsPlaced, Is.EqualTo(1));
            Assert.That(report.CallsReceived, Is.EqualTo(1));
            Assert.That(PeerOf(a, "ToB").GetStatistics().ActiveConnections, Is.EqualTo(3));
            pad.Disconnect();
        }

        // ------------------------------------------------------------------ source ip

        private static TcpClient ConnectFrom(string sourceIp, int port)
        {
            TcpClient c = new TcpClient(new IPEndPoint(IPAddress.Parse(sourceIp), 0));
            c.Connect(IPAddress.Loopback, port);
            return c;
        }

        private static XotPeerTransport StartPeer(ILogger logger, XotListenerRegistry registry, string name, string peerIp, int port, bool allowOnlyListed = false)
        {
            XotPeerTransport peer = new XotPeerTransport(logger, new XotPeerConfig
            {
                Name = name, PeerAddress = peerIp, PeerPort = FreePort(), ListenPort = port, Listeners = registry, AllowOnlyListedPeers = allowOnlyListed,
            });
            Assert.That(peer.Start(), Is.True);
            return peer;
        }

        /// <summary>
        /// Two peers on one port share ONE listening socket, and a connection goes to the entry whose
        /// peer address is the connection's source address.
        /// </summary>
        [Test]
        public void SourceIp_PicksTheRightEntry_OnOneSharedSocket()
        {
            int port = FreePort();
            ILogger logger = NullLogger.Instance;
            XotListenerRegistry registry = new XotListenerRegistry(logger);
            _toDispose.Add(registry);
            XotPeerTransport p1 = StartPeer(logger, registry, "Peer1", "127.0.0.1", port);
            XotPeerTransport p2 = StartPeer(logger, registry, "Peer2", "127.0.0.2", port);
            _toDispose.Add(new StopOnDispose(p1));
            _toDispose.Add(new StopOnDispose(p2));

            Assert.That(registry.ListeningSocketCount, Is.EqualTo(1), "only one socket may listen on a port");

            using TcpClient from2 = ConnectFrom("127.0.0.2", port);
            Assert.That(WaitUntil(() => p2.GetPeerReport().Accepted == 1), Is.True, "127.0.0.2 should reach Peer2");
            Assert.That(p1.GetPeerReport().Accepted, Is.EqualTo(0));

            using TcpClient from1 = ConnectFrom("127.0.0.1", port);
            Assert.That(WaitUntil(() => p1.GetPeerReport().Accepted == 1), Is.True, "127.0.0.1 should reach Peer1");
            Assert.That(p2.GetPeerReport().Accepted, Is.EqualTo(1));
            Assert.That(p1.GetPeerReport().Unlisted, Is.Empty);
            Assert.That(p2.GetPeerReport().Unlisted, Is.Empty);
        }

        /// <summary>
        /// By default a connection from an address nobody lists is accepted as an unlisted peer and
        /// shows in the report under its address.
        /// </summary>
        [Test]
        public void UnlistedIp_IsAcceptedByDefault_AndShownAsAnUnlistedPeer()
        {
            int port = FreePort();
            ListLogger logger = new ListLogger();
            XotListenerRegistry registry = new XotListenerRegistry(logger);
            _toDispose.Add(registry);
            XotPeerTransport p1 = StartPeer(logger, registry, "Peer1", "127.0.0.1", port);
            _toDispose.Add(new StopOnDispose(p1));

            using TcpClient stranger = ConnectFrom("127.0.0.2", port);
            Assert.That(WaitUntil(() => p1.GetPeerReport().Accepted == 1), Is.True);
            XotPeerReport report = p1.GetPeerReport();
            Assert.That(report.Unlisted.Count, Is.EqualTo(1));
            Assert.That(report.Unlisted[0].Address, Is.EqualTo("127.0.0.2"));
            Assert.That(report.Unlisted[0].OpenNow, Is.EqualTo(1));
            Assert.That(report.RefusedUnlisted, Is.EqualTo(0));
        }

        /// <summary>
        /// With AllowOnlyListedPeers an unlisted address is refused with a clear warning, while the
        /// listed peer still gets in.
        /// </summary>
        [Test]
        public void UnlistedIp_IsRefused_WithAllowOnlyListedPeers_AndTheWarningSaysWhy()
        {
            int port = FreePort();
            ListLogger logger = new ListLogger();
            XotListenerRegistry registry = new XotListenerRegistry(logger);
            _toDispose.Add(registry);
            XotPeerTransport p1 = StartPeer(logger, registry, "Peer1", "127.0.0.1", port, allowOnlyListed: true);
            _toDispose.Add(new StopOnDispose(p1));

            using TcpClient stranger = ConnectFrom("127.0.0.2", port);
            stranger.ReceiveTimeout = 3000;
            int read = stranger.GetStream().Read(new byte[1], 0, 1);
            Assert.That(read, Is.EqualTo(0), "a refused connection is closed");
            Assert.That(p1.GetPeerReport().Accepted, Is.EqualTo(0));
            Assert.That(p1.GetPeerReport().RefusedUnlisted, Is.EqualTo(1));
            Assert.That(logger.Contains("Warning: XOT connection refused: 127.0.0.2 is not a listed peer"), Is.True);

            using TcpClient friend = ConnectFrom("127.0.0.1", port);
            Assert.That(WaitUntil(() => p1.GetPeerReport().Accepted == 1), Is.True, "the listed peer must still get in");
        }

        // ------------------------------------------------------------------ old style entries

        /// <summary>
        /// An old-style Server entry and a peer entry share one port: the peer's address goes to the
        /// peer entry, every other address to the server entry, as before.
        /// </summary>
        [Test]
        public void ServerEntry_AndPeerEntry_ShareAPort_ServerKeepsEveryOtherAddress()
        {
            int port = FreePort();
            ProtocolRuntime a = Start(AddressA, null,
                new XotInterfaceConfig { Name = "Srv", Mode = XotMode.Server, LocalPort = port, RemoteMachineId = "x" },
                new XotInterfaceConfig { Name = "Peer", Mode = XotMode.Both, RemoteIPAddress = "127.0.0.2", RemotePort = FreePort(), LocalPort = port, RemoteMachineId = "B" });

            using TcpClient fromPeer = ConnectFrom("127.0.0.2", port);
            Assert.That(WaitUntil(() => PeerOf(a, "Peer").GetPeerReport().Accepted == 1), Is.True);
            using TcpClient other = ConnectFrom("127.0.0.1", port);
            Assert.That(WaitUntil(() => a.TransportInterfaces["Srv"].GetStatistics().ActiveConnections == 1), Is.True);
            Assert.That(PeerOf(a, "Peer").GetPeerReport().Accepted, Is.EqualTo(1));
            Assert.That(PeerOf(a, "Peer").GetPeerReport().Unlisted, Is.Empty);
        }

        /// <summary>
        /// A configuration with only the old Client and Server entries loads and behaves as before.
        /// </summary>
        [Test]
        public void OldStyle_ClientAndServerEntries_StillWork()
        {
            int portB = FreePort();
            Start(AddressB, null, new XotInterfaceConfig { Name = "Srv", Mode = XotMode.Server, LocalPort = portB, RemoteMachineId = "A" });
            ProtocolRuntime a = Start(AddressA, "Cli", new XotInterfaceConfig
            {
                Name = "Cli", Mode = XotMode.Client, RemoteIPAddress = "127.0.0.1", RemotePort = portB, RemoteMachineId = "B",
            });

            Assert.That(a.TransportInterfaces["Cli"], Is.InstanceOf<XotPeerTransport>(), "a Client entry dials one TCP connection per call, the way the peer transport does");
            PadClient pad = ConnectedPad(a, "pad1", AddressB, AddressA);
            pad.Disconnect();
        }

        /// <summary>
        /// The old Client mode opens one TCP connection for each call, as RFC 1613 section 5 asks. (Until 2026-11-13 it kept
        /// ONE connection for all calls; the real Cisco router answered that by clearing the first call when the second
        /// Call Request arrived, cause 19 diagnostic 23.) Two calls are two connections on the dialling side and two on
        /// the accepting side, and each is closed when its call ends.
        /// </summary>
        [Test]
        public void ClientMode_OpensOneTcpConnectionForEachCall()
        {
            int portB = FreePort();
            ProtocolRuntime b = Start(AddressB, null, new XotInterfaceConfig { Name = "Srv", Mode = XotMode.Server, LocalPort = portB, RemoteMachineId = "A" });
            ProtocolRuntime a = Start(AddressA, "Cli", new XotInterfaceConfig
            {
                Name = "Cli", Mode = XotMode.Client, RemoteIPAddress = "127.0.0.1", RemotePort = portB, RemoteMachineId = "B",
            });

            PadClient pad1 = ConnectedPad(a, "pad1", AddressB, AddressA);
            PadClient pad2 = ConnectedPad(a, "pad2", AddressB, AddressA);

            Assert.That(a.TransportInterfaces["Cli"].GetStatistics().ActiveConnections, Is.EqualTo(2), "two calls, two TCP connections (client mode)");
            Assert.That(b.TransportInterfaces["Srv"].GetStatistics().ActiveConnections, Is.EqualTo(2));
            pad1.Disconnect();
            pad2.Disconnect();
            Assert.That(SpinWait.SpinUntil(() => a.TransportInterfaces["Cli"].GetStatistics().ActiveConnections == 0, 5000), Is.True, "the connections close with the calls");
        }
        private sealed class StopOnDispose : IDisposable
        {
            private readonly XotPeerTransport _peer;

            public StopOnDispose(XotPeerTransport peer)
            {
                _peer = peer;
            }

            public void Dispose()
            {
                _peer.Stop();
            }
        }
    }
}
