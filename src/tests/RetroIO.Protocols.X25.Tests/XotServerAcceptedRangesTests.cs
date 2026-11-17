//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Client;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Runtime.Models;
using ClientVc = RetroIO.Protocols.X25.Client.X25Vc;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// An XOT server entry with a list of accepted called addresses refuses a call to any other
    /// address, with clearing cause 0x0B (access barred, X.25 Table 5/X.25).
    /// </summary>
    /// <remarks>
    /// Until 2026-11-17 the list sat in <see cref="XotServerConfig.AcceptedX121Ranges"/> and was read
    /// only by <c>XotServerTransport.OnClientIncomingCall</c>, which nothing ever called: incoming calls
    /// arrive through the connection's own X.25 engine (<c>ProtocolRuntime.OnEndpointIncomingCall</c>).
    /// </remarks>
    [TestFixture]
    public class XotServerAcceptedRangesTests
    {
        private static int FreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static ProtocolRuntime StartRuntime(int port, string acceptedPattern)
        {
            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = "23430200";
            XotInterfaceConfig xot = new XotInterfaceConfig
            {
                Name = "XOT_Server",
                Enabled = true,
                Mode = XotMode.Server,
                LocalPort = port,
                RemoteMachineId = "23430001"
            };
            xot.AcceptedX121Ranges.Add(acceptedPattern);
            config.Routing.XotInterfaces.Add(xot);

            // 23430200 has a route to the local TAD, so only the accepted list can refuse it.
            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "LocalTAD",
                X121AddressPattern = "23430200",
                Type = RetroIO.Protocols.X25.Runtime.RouteType.Local,
                Destination = "LocalTAD",
                Priority = 200,
                Enabled = true
            });

            ProtocolRuntime runtime = new ProtocolRuntime(NullLogger.Instance, config);
            Assert.That(runtime.StartTransportInterfaces(), Is.True, "the XOT server should start listening");
            return runtime;
        }

        [Test]
        public void CallToAnAddressOutsideTheList_IsClearedWithAccessBarred()
        {
            int port = FreePort();
            using ProtocolRuntime runtime = StartRuntime(port, "^99999999$");

            int cause = -1;
            int diag = -1;
            ManualResetEventSlim cleared = new ManualResetEventSlim(false);
            using X25Client client = new X25Client { ServiceName = "barred-caller" };
            client.SetOnClear((c, vc, clearCause, diagnostic) =>
            {
                cause = clearCause;
                diag = diagnostic;
                cleared.Set();
            });
            client.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = (ushort)port,
                LocalAddress = "23430001",
                Framing = X25ClientFraming.Xot
            }, NullLogger.Instance);

            ClientVc? vc = client.CallWait("23430200", 5000);

            Assert.That(vc, Is.Null, "a call to an address the entry does not accept must not connect");
            Assert.That(cleared.Wait(5000), Is.True, "the call must be cleared");
            Assert.That(cause, Is.EqualTo(0x0B), "access barred");

            // Decided by the router (X25Router.DecideIncomingCall), not the line: X.25 Table C.1 row 25,
            // "Incoming call barred", gives Access barred with diagnostic 70. Until 2026-11-18 the line
            // cleared it itself with diagnostic 0.
            Assert.That(diag, Is.EqualTo(70), "Table C.1 row 25: # 70");
        }

        [Test]
        public void CallToAnAddressInTheList_Connects()
        {
            int port = FreePort();
            using ProtocolRuntime runtime = StartRuntime(port, "^23430200$");

            using X25Client client = new X25Client { ServiceName = "allowed-caller" };
            client.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = (ushort)port,
                LocalAddress = "23430001",
                Framing = X25ClientFraming.Xot
            }, NullLogger.Instance);

            ClientVc? vc = client.CallWait("23430200", 5000);
            Assert.That(vc, Is.Not.Null, "a call to an accepted address connects");
        }
    }
}
