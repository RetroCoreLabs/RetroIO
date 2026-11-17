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
    /// the T11 expiry is covered on the serial line (SerialIncomingCallTests) and in the engine
    /// (X25PacketLayerRuleTests), where the clock can be driven by hand.
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

        private static ProtocolRuntime StartRuntime(int port, bool answerAutomatically)
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
            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "LocalTAD",
                X121AddressPattern = ServiceAddress,
                Type = RetroIO.Protocols.X25.Runtime.RouteType.Local,
                Destination = "LocalTAD",
                Priority = 200,
                Enabled = true
            });

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

            bool rejected = false;
            server.IncomingCall += (s, call) => rejected = server.RejectCall(call, 0x0B);

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
            Assert.That(cause, Is.EqualTo(0x0B), "the cause the program gave (access barred)");
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
    }
}
