//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Client;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.PacketLayer;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Transport;
using ClientVc = RetroIO.Protocols.X25.Client.X25Vc;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// An XOT server entry set to wait for the program's answer offers each incoming call through the
    /// transport's IncomingCall event, and the program's AcceptCall or RejectCall goes to the X.25
    /// engine of the connection the call came in on.
    /// </summary>
    /// <remarks>
    /// X.25 4.1.4: "The called DTE shall indicate its acceptance of the call by transferring ... a
    /// call accepted packet specifying the same logical channel as that of the incoming call packet.
    /// This places the specified logical channel in the data transfer state (p4)." A refusal is a
    /// clear request (4.1.7). Until 2026-11-17 XotServerTransport.AcceptCall and RejectCall threw
    /// NotSupportedException. The caller here is the X25Client over a real loopback TCP connection;
    /// T11 is run out here by driving the connection's engine clock by hand (EndpointForTest), as
    /// on the serial line (SerialIncomingCallTests) and in the engine (X25PacketLayerRuleTests).
    /// </remarks>
    [TestFixture]
    public class XotServerAnswerModeTests
    {
        private const string ServiceAddress = "23430200";

        private static int FreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static ProtocolRuntime StartRuntime(int port, bool answerAutomatically, bool withRoute = true)
        {
            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = ServiceAddress;
            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT_Server",
                Enabled = true,
                Mode = XotMode.Server,
                LocalPort = port,
                RemoteMachineId = "23430001",
                AnswerIncomingCallsAutomatically = answerAutomatically,
            });

            // The called address routes to the local TAD, so an accepted call has a service to go to.
            // The router answers every incoming call first (decided by Ronny, 2026-11-17): a call with
            // no inbound route is refused with access barred before the program hears of it, so the
            // tests that want the program to be asked must have this route.
            if (withRoute)
            {
                config.Routing.RoutingTable.Add(new X25RouteEntry
                {
                    Name = "LocalTAD",
                    X121AddressPattern = "^" + ServiceAddress + "$",
                    Type = RetroIO.Protocols.X25.Runtime.RouteType.Local,
                    Destination = "LocalTAD",
                    Priority = 200,
                    Enabled = true
                });
            }

            ProtocolRuntime runtime = new ProtocolRuntime(NullLogger.Instance, config);
            Assert.That(runtime.StartTransportInterfaces(), Is.True, "the XOT server should start listening");
            return runtime;
        }

        private static X25Client OpenClient(int port, string name)
        {
            X25Client client = new X25Client { ServiceName = name };
            client.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = (ushort)port,
                LocalAddress = "23430001",
                Framing = X25ClientFraming.Xot
            }, NullLogger.Instance);
            return client;
        }

        [Test]
        public void TheDefault_IsToAnswerAutomatically()
        {
            Assert.That(new XotInterfaceConfig().AnswerIncomingCallsAutomatically, Is.True,
                "nothing that works today may change: an entry answers by itself unless told to wait");
            Assert.That(new XotServerConfig().AnswerIncomingCallsAutomatically, Is.True);
        }

        [Test]
        public void WaitMode_AcceptCall_ConnectsTheCaller()
        {
            int port = FreePort();
            using ProtocolRuntime runtime = StartRuntime(port, answerAutomatically: false);
            XotServerTransport server = (XotServerTransport)runtime.TransportInterfaces["XOT_Server"];

            List<X25CallRequest> offered = new List<X25CallRequest>();
            bool accepted = false;
            server.IncomingCall += (s, call) =>
            {
                offered.Add(call);
                accepted = server.AcceptCall(call);
            };

            using X25Client client = OpenClient(port, "accept-caller");
            ClientVc? vc = client.CallWait(ServiceAddress, 5000);

            Assert.That(offered.Count, Is.EqualTo(1), "the call must be offered to the program once");
            Assert.That(offered[0].CalledAddress, Is.EqualTo(ServiceAddress));
            Assert.That(accepted, Is.True, "AcceptCall must report that the Call Accepted went out");
            Assert.That(vc, Is.Not.Null, "4.1.4: the Call Accepted puts the caller's channel in data transfer");
        }

        [Test]
        public void WaitMode_RejectCall_ClearsTheCallerWithTheGivenCause()
        {
            int port = FreePort();
            using ProtocolRuntime runtime = StartRuntime(port, answerAutomatically: false);
            XotServerTransport server = (XotServerTransport)runtime.TransportInterfaces["XOT_Server"];

            // A cause other than the router's own 0x0D, so the test can tell the program's refusal
            // from a refusal by the router.
            bool rejected = false;
            server.IncomingCall += (s, call) => rejected = server.RejectCall(call, X25Cause.OutOfOrder);

            int cause = -1;
            ManualResetEventSlim cleared = new ManualResetEventSlim(false);
            using X25Client client = OpenClient(port, "reject-caller");
            client.SetOnClear((c, vc, clearCause, diagnostic) =>
            {
                cause = clearCause;
                cleared.Set();
            });

            ClientVc? connected = client.CallWait(ServiceAddress, 5000);

            Assert.That(rejected, Is.True, "RejectCall must report that the Clear Request went out");
            Assert.That(connected, Is.Null, "a refused call must not connect");
            Assert.That(cleared.Wait(5000), Is.True, "the caller must be told the call is cleared");
            Assert.That(cause, Is.EqualTo(X25Cause.OutOfOrder), "the cause the program gave (Table 5-7: 0x09 out of order)");
        }

        [Test]
        public void AutomaticMode_ConnectsWithoutAskingTheProgram()
        {
            int port = FreePort();
            using ProtocolRuntime runtime = StartRuntime(port, answerAutomatically: true);
            XotServerTransport server = (XotServerTransport)runtime.TransportInterfaces["XOT_Server"];
            int offered = 0;
            server.IncomingCall += (s, call) => offered++;

            using X25Client client = OpenClient(port, "auto-caller");
            ClientVc? vc = client.CallWait(ServiceAddress, 5000);

            Assert.That(vc, Is.Not.Null, "an automatic entry answers by itself, as before");
            Assert.That(offered, Is.EqualTo(0), "an automatic entry does not offer the call to the program");
        }

        // ------------------------------------------------------------------ the router answers first (2026-11-17)
        //
        // Decided by Ronny, 2026-11-17: the router answers an incoming call first. A call it has no
        // inbound route for is refused at once with a Clear Request, cause 0x0D not obtainable (X.25
        // 4.1.4 and 4.1.7, Table C.1 row 24; it was 0x0B access barred until 2026-11-18), on the connection the call came in on; only a call it has a
        // route for is offered to the program (wait mode) or answered by the local service
        // (automatic mode).

        private const string UnroutedAddress = "23439999";

        /// <summary>
        /// Calls <paramref name="called"/> and waits for the clear; returns the cause. Fails when no
        /// clear comes within five seconds or when the call connects.
        /// </summary>
        private static int CallAndWaitForClear(X25Client client, string called)
        {
            return CallAndWaitForClear(client, called, out _);
        }

        /// <summary>
        /// As <see cref="CallAndWaitForClear(X25Client, string)"/>, and also gives the diagnostic of the clear.
        /// </summary>
        private static int CallAndWaitForClear(X25Client client, string called, out int diagnostic)
        {
            int cause = -1;
            int diag = -1;
            bool connected = false;
            ManualResetEventSlim cleared = new ManualResetEventSlim(false);
            client.SetOnConnected((c, vc) => connected = true);
            client.SetOnClear((c, vc, clearCause, clearDiagnostic) =>
            {
                cause = clearCause;
                diag = clearDiagnostic;
                cleared.Set();
            });

            Assert.That(client.Call(called), Is.Not.Null, "the client must place the call");
            Assert.That(cleared.Wait(5000), Is.True, "the caller must be told the call is cleared");
            Assert.That(connected, Is.False, "a refused call must not connect");
            diagnostic = diag;
            return cause;
        }

        private static bool WaitUntil(System.Func<bool> condition, int timeoutMs)
        {
            long deadline = System.Environment.TickCount64 + timeoutMs;
            while (System.Environment.TickCount64 < deadline)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(10);
            }

            return condition();
        }

        private static List<X25CallRequest> CollectOffers(XotServerTransport server)
        {
            List<X25CallRequest> offered = new List<X25CallRequest>();
            server.IncomingCall += (s, call) =>
            {
                lock (offered)
                {
                    offered.Add(call);
                }
            };
            return offered;
        }

        private static int CountOf(List<X25CallRequest> offered)
        {
            lock (offered)
            {
                return offered.Count;
            }
        }

        [Test]
        public void WaitMode_NoRoute_IsRefusedWithNotObtainable_AndNotOfferedToTheProgram()
        {
            int port = FreePort();
            using ProtocolRuntime runtime = StartRuntime(port, answerAutomatically: false, withRoute: false);
            XotServerTransport server = (XotServerTransport)runtime.TransportInterfaces["XOT_Server"];
            List<X25CallRequest> offered = CollectOffers(server);

            using X25Client client = OpenClient(port, "noroute-wait");
            int cause = CallAndWaitForClear(client, ServiceAddress, out int diagnostic);

            // X.25 Table C.1 row 24, "Unknown number": Not obtainable, diagnostic 67 (X25CallDecision).
            // Until 2026-11-18 this was 0x0B access barred.
            Assert.That(cause, Is.EqualTo(X25Cause.NotObtainable), "no inbound route: Table C.1 row 24, 0x0D not obtainable");
            Assert.That(diagnostic, Is.EqualTo(X25Diagnostic.InvalidCalledAddress), "Table C.1 row 24: # 67");
            Assert.That(CountOf(offered), Is.EqualTo(0), "the router refused it; the program must not be offered the call");
        }

        [Test]
        public void AutomaticMode_NoRoute_IsRefusedWithNotObtainable()
        {
            int port = FreePort();
            using ProtocolRuntime runtime = StartRuntime(port, answerAutomatically: true, withRoute: false);

            using X25Client client = OpenClient(port, "noroute-auto");
            int cause = CallAndWaitForClear(client, ServiceAddress, out int diagnostic);

            Assert.That(cause, Is.EqualTo(X25Cause.NotObtainable), "the router answers first in automatic mode too");
            Assert.That(diagnostic, Is.EqualTo(X25Diagnostic.InvalidCalledAddress), "Table C.1 row 24: # 67");
        }

        [Test]
        public void WaitMode_NoAnswer_IsClearedWhenT11RunsOut()
        {
            int port = FreePort();
            using ProtocolRuntime runtime = StartRuntime(port, answerAutomatically: false);
            XotServerTransport server = (XotServerTransport)runtime.TransportInterfaces["XOT_Server"];
            List<X25CallRequest> offered = CollectOffers(server);

            using X25Client client = OpenClient(port, "t11-caller");
            int diagnostic = -1;
            ManualResetEventSlim cleared = new ManualResetEventSlim(false);
            client.SetOnClear((c, vc, clearCause, clearDiagnostic) =>
            {
                diagnostic = clearDiagnostic;
                cleared.Set();
            });
            Assert.That(client.Call(ServiceAddress), Is.Not.Null);
            Assert.That(WaitUntil(() => CountOf(offered) == 1, 5000), Is.True, "the routed call must be offered once");

            // The engine of the connection the call came in on; its clock is driven by hand.
            X25Endpoint? endpoint = runtime.EndpointForTest(offered[0].SourceConnection!);
            Assert.That(endpoint, Is.Not.Null);
            endpoint!.Tick(System.Environment.TickCount64 + 170_000);
            Assert.That(cleared.Wait(300), Is.False, "T11 is 180 s (Table D.1); it has not run out");

            endpoint.Tick(System.Environment.TickCount64 + 181_000);

            Assert.That(cleared.Wait(5000), Is.True, "4.1.4: at T11 the call is cleared");
            Assert.That(diagnostic, Is.EqualTo(X25Diagnostic.TimeExpiredCall), "Table D.1: # 49");
            Assert.That(server.AcceptCall(offered[0]), Is.False, "a call cleared at T11 can no longer be accepted");
        }

        [Test]
        public void AnswersForARefusedAnAnsweredOrAnUnknownCall_ReturnFalse()
        {
            int port = FreePort();
            using ProtocolRuntime runtime = StartRuntime(port, answerAutomatically: false);
            XotServerTransport server = (XotServerTransport)runtime.TransportInterfaces["XOT_Server"];
            List<X25CallRequest> offered = CollectOffers(server);

            // Refused by the router: the program never had the call, and a request with the same
            // addresses is not that call.
            using X25Client refusedCaller = OpenClient(port, "refused");
            Assert.That(CallAndWaitForClear(refusedCaller, UnroutedAddress), Is.EqualTo(X25Cause.NotObtainable));
            Assert.That(CountOf(offered), Is.EqualTo(0));
            X25CallRequest lookalike = new X25CallRequest(UnroutedAddress, "23430001");
            Assert.That(server.AcceptCall(lookalike), Is.False, "a call the router refused cannot be accepted");
            Assert.That(server.RejectCall(lookalike, X25Cause.OutOfOrder), Is.False, "nor refused a second time");

            // Unknown: never offered at all.
            Assert.That(server.AcceptCall(new X25CallRequest(ServiceAddress, "23430001")), Is.False);

            // Answered: accepted once, then every further answer is refused and sends nothing.
            using X25Client acceptedCaller = OpenClient(port, "answered");
            ManualResetEventSlim connected = new ManualResetEventSlim(false);
            int clears = 0;
            acceptedCaller.SetOnConnected((c, vc) => connected.Set());
            acceptedCaller.SetOnClear((c, vc, x, y) => Interlocked.Increment(ref clears));
            Assert.That(acceptedCaller.Call(ServiceAddress), Is.Not.Null);
            Assert.That(WaitUntil(() => CountOf(offered) == 1, 5000), Is.True);
            Assert.That(server.AcceptCall(offered[0]), Is.True);
            Assert.That(connected.Wait(5000), Is.True);
            Assert.That(server.AcceptCall(offered[0]), Is.False, "a call can be answered once");
            Assert.That(server.RejectCall(offered[0], X25Cause.OutOfOrder), Is.False, "an accepted call cannot then be refused");
            Thread.Sleep(200);
            Assert.That(Volatile.Read(ref clears), Is.EqualTo(0), "nothing was sent for the refused answers");
        }

        [Test]
        public void TwoCallsAtOnce_OneRoutedOneNot_AreEachAnsweredOnTheirOwnConnection()
        {
            int port = FreePort();
            using ProtocolRuntime runtime = StartRuntime(port, answerAutomatically: false);
            XotServerTransport server = (XotServerTransport)runtime.TransportInterfaces["XOT_Server"];
            List<X25CallRequest> offered = CollectOffers(server);

            using X25Client routed = OpenClient(port, "routed");
            using X25Client unrouted = OpenClient(port, "unrouted");
            ManualResetEventSlim routedConnected = new ManualResetEventSlim(false);
            int routedClears = 0;
            routed.SetOnConnected((c, vc) => routedConnected.Set());
            routed.SetOnClear((c, vc, x, y) => Interlocked.Increment(ref routedClears));
            int unroutedCause = -1;
            bool unroutedConnected = false;
            ManualResetEventSlim unroutedCleared = new ManualResetEventSlim(false);
            unrouted.SetOnConnected((c, vc) => unroutedConnected = true);
            unrouted.SetOnClear((c, vc, x, y) =>
            {
                unroutedCause = x;
                unroutedCleared.Set();
            });

            // Both calls are out before either is answered.
            Assert.That(routed.Call(ServiceAddress), Is.Not.Null);
            Assert.That(unrouted.Call(UnroutedAddress), Is.Not.Null);

            Assert.That(unroutedCleared.Wait(5000), Is.True, "the call with no route is refused at once");
            Assert.That(unroutedCause, Is.EqualTo(X25Cause.NotObtainable));
            Assert.That(WaitUntil(() => CountOf(offered) == 1, 5000), Is.True, "only the routed call is offered");
            Assert.That(offered[0].CalledAddress, Is.EqualTo(ServiceAddress));

            Assert.That(server.AcceptCall(offered[0]), Is.True);
            Assert.That(routedConnected.Wait(5000), Is.True, "the routed call connects");
            Assert.That(unroutedConnected, Is.False, "the refused call never connects");
            Assert.That(Volatile.Read(ref routedClears), Is.EqualTo(0), "the routed call was not cleared by the other call's refusal");
            Assert.That(CountOf(offered), Is.EqualTo(1));
        }

        [Test]
        public void WaitMode_CallerClearsWhileTheProgramDecides_ALaterAcceptReturnsFalse()
        {
            int port = FreePort();
            using ProtocolRuntime runtime = StartRuntime(port, answerAutomatically: false);
            XotServerTransport server = (XotServerTransport)runtime.TransportInterfaces["XOT_Server"];
            List<X25CallRequest> offered = CollectOffers(server);

            using X25Client client = OpenClient(port, "gives-up");
            bool connected = false;
            client.SetOnConnected((c, v) => connected = true);
            ClientVc? vc = client.Call(ServiceAddress);
            Assert.That(vc, Is.Not.Null);
            Assert.That(WaitUntil(() => CountOf(offered) == 1, 5000), Is.True);

            // The caller gives up (4.1.7: a DTE may clear in any state) before the program answers.
            client.Clear(vc!, 0, 0);
            X25Endpoint endpoint = runtime.EndpointForTest(offered[0].SourceConnection!)!;
            ushort lci = offered[0].SourceLcn;
            Assert.That(WaitUntil(() =>
            {
                lock (endpoint.Sync)
                {
                    return !endpoint.Layer.Circuits.TryGetValue(lci, out PacketLayer.X25Vc? circuit)
                        || circuit.State != PacketLayer.X25VcState.CallIncoming;
                }
            }, 5000), Is.True, "the server side must see the caller's clear");

            Assert.That(server.AcceptCall(offered[0]), Is.False, "the call is gone; it cannot be accepted");
            Assert.That(server.RejectCall(offered[0], X25Cause.OutOfOrder), Is.False, "nor refused");
            Thread.Sleep(200);
            Assert.That(connected, Is.False, "no Call Accepted went out");
        }
    }
}
