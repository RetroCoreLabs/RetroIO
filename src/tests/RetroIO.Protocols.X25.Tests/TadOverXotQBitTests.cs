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
using System.Text;
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
    /// The built-in TAD answering a call that arrives over XOT, seen from the caller's side of the wire.
    /// </summary>
    /// <remarks>
    /// This is the interop case that a Cisco router showed: the TAD's X.29 SET reached the router
    /// without the Q bit, so the router printed the SET's bytes as text (a stray "~") and kept
    /// echoing what was typed. The TAD used to build its own X.25 packets and the runtime then
    /// re-sent only their data. These tests read the packets as they leave the XOT circuit.
    /// </remarks>
    [TestFixture]
    public class TadOverXotQBitTests
    {
        private sealed record Received(byte[] Data, bool QBit, bool MBit);

        private static int FreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        /// <summary>
        /// The X.29 SET leaves the circuit with the Q bit, the text leaves without it, and the long
        /// banner is split with the M bit set on all but its last packet.
        /// </summary>
        [Test]
        public void TadOverXot_SetCarriesTheQBit_TextDoesNot()
        {
            int port = FreePort();

            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = "23430200";
            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT_Server",
                Enabled = true,
                Mode = XotMode.Server,
                LocalPort = port,
                RemoteMachineId = "23430001"
            });
            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "LocalTAD",
                X121AddressPattern = "23430200",
                Type = RetroIO.Protocols.X25.Runtime.RouteType.Local,
                Destination = "LocalTAD",
                Priority = 200,
                Enabled = true
            });

            using ProtocolRuntime runtime = new ProtocolRuntime(NullLogger.Instance, config);
            Assert.That(runtime.StartTransportInterfaces(), Is.True, "the XOT server should start listening");

            List<Received> received = new List<Received>();
            ManualResetEventSlim gotPrompt = new ManualResetEventSlim(false);
            using X25Client client = new X25Client { ServiceName = "tad-caller" };
            client.SetOnData((c, vc, data, length, qBit, mBit, dBit) =>
            {
                byte[] copy = new byte[length];
                Array.Copy(data, 0, copy, 0, length);
                lock (received)
                {
                    received.Add(new Received(copy, qBit, mBit));
                }

                if (!qBit && !mBit && Encoding.ASCII.GetString(copy).EndsWith("TAD> ", StringComparison.Ordinal))
                {
                    gotPrompt.Set();
                }
            });

            client.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = (ushort)port,
                LocalAddress = "23430001",
                Framing = X25ClientFraming.Xot
            }, NullLogger.Instance);

            ClientVc? vc = client.CallWait("23430200", 5000);
            Assert.That(vc, Is.Not.Null, "the call to the TAD should connect");
            Assert.That(gotPrompt.Wait(5000), Is.True, "the banner should end with the TAD> prompt");

            lock (received)
            {
                Received? set = null;
                StringBuilder text = new StringBuilder();
                for (int i = 0; i < received.Count; i++)
                {
                    if (received[i].QBit)
                    {
                        Assert.That(set, Is.Null, "only one control message is expected before the banner");
                        set = received[i];
                    }
                    else
                    {
                        text.Append(Encoding.ASCII.GetString(received[i].Data));
                    }
                }

                Assert.That(set, Is.Not.Null, "the X.29 SET must arrive with the Q bit set");
                Assert.That(set!.Data[0], Is.EqualTo(0x02), "X.29 message type SET");
                Assert.That(text.ToString(), Does.StartWith("*** X.25 TAD Loopback Echo Service ***"),
                    "no control bytes may appear in front of the banner");

                // The last data packet closes the message; every one before it in the banner has M set.
                Received last = received[received.Count - 1];
                Assert.That(last.MBit, Is.False);
                int withM = 0;
                for (int i = 0; i < received.Count; i++)
                {
                    if (received[i].MBit)
                    {
                        withM++;
                    }
                }

                Assert.That(text.Length, Is.GreaterThan(128), "the banner is longer than one packet");
                Assert.That(withM, Is.GreaterThan(0), "a banner split over several packets carries the M bit");
            }
        }
    }
}
