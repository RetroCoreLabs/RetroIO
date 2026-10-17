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
using RetroIO.Link;
using RetroIO.Link.Framing;
using RetroIO.Protocols.X25.PacketLayer;
using ClientVc = RetroIO.Protocols.X25.Client.X25Vc;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// End-to-end interop tests for <see cref="X25Client"/> over a real loopback TCP connection with XOT
    /// framing. A minimal DCE-side peer built from the same clean stack accepts calls and echoes data, so
    /// these tests prove the client's on-the-wire packets (GFI, X.121 addresses, sequencing) are produced
    /// and consumed correctly across the transport, not just in a same-process unit harness.
    /// </summary>
    [TestFixture]
    public class X25ClientInteropTests
    {
        /// <summary>
        /// A call placed by <see cref="X25Client"/> connects to the peer and a data packet round-trips
        /// (echo), verifying the whole clean stack over real XOT/TCP.
        /// </summary>
        [Test]
        public void Client_CallsPeer_EchoRoundTrips()
        {
            using LoopbackX25Peer peer = new LoopbackX25Peer();
            using X25Client client = new X25Client { ServiceName = "test-client" };

            byte[]? echoed = null;
            ManualResetEventSlim gotData = new ManualResetEventSlim(false);
            client.SetOnData((c, vc, data, length, qBit, mBit, dBit) =>
            {
                echoed = new byte[length];
                Array.Copy(data, 0, echoed, 0, length);
                gotData.Set();
            });

            client.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = (ushort)peer.Port,
                LocalAddress = "100",
                Framing = X25ClientFraming.Xot
            }, NullLogger.Instance);

            ClientVc? vc = client.CallWait("200", 3000);
            Assert.That(vc, Is.Not.Null, "the call to the peer should connect");
            Assert.That(vc!.Connected, Is.True);
            Assert.That(vc.CalledAddress, Is.EqualTo("200"));

            Assert.That(client.SendText(vc, "PING"), Is.True);
            Assert.That(gotData.Wait(3000), Is.True, "the peer's echo should arrive");
            Assert.That(Encoding.UTF8.GetString(echoed!), Is.EqualTo("PING"));
        }

        /// <summary>
        /// Clearing a connected circuit is confirmed by the peer and the client's clear callback fires.
        /// </summary>
        [Test]
        public void Client_ClearsCircuit_PeerConfirms()
        {
            using LoopbackX25Peer peer = new LoopbackX25Peer();
            using X25Client client = new X25Client { ServiceName = "test-client" };

            client.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = (ushort)peer.Port,
                LocalAddress = "100",
                Framing = X25ClientFraming.Xot
            }, NullLogger.Instance);

            ClientVc? vc = client.CallWait("200", 3000);
            Assert.That(vc, Is.Not.Null);

            // A local clear does not raise the client's clear callback (the app asked for it); assert the
            // circuit is torn down and a subsequent send is refused.
            client.Clear(vc!, 0, 0);
            Assert.That(client.Send(vc!, Encoding.ASCII.GetBytes("x")), Is.False,
                "sending on a cleared circuit is refused");
        }
    }
}
