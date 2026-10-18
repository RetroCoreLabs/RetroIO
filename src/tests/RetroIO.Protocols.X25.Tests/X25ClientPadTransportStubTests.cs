//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Client;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The PAD adapter over the clean X.25 client carries one circuit with the client's own address and
    /// plain data. These tests pin that a request for anything else is refused with an exception,
    /// instead of being ignored while the call or the data goes out in the plain form.
    /// </summary>
    [TestFixture]
    public class X25ClientPadTransportStubTests
    {
        private static X25Client OpenClient(LoopbackX25Peer peer)
        {
            X25Client client = new X25Client { ServiceName = "pad-stub" };
            client.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = (ushort)peer.Port,
                LocalAddress = "10000",
                Framing = X25ClientFraming.Xot
            }, NullLogger.Instance);
            return client;
        }

        [Test]
        public void PlaceCall_WithAnotherCallingAddress_IsRefused()
        {
            using LoopbackX25Peer peer = new LoopbackX25Peer("200");
            using X25Client client = OpenClient(peer);
            X25ClientPadTransport transport = new X25ClientPadTransport(client);

            NotSupportedException? ex = Assert.Throws<NotSupportedException>(() => transport.PlaceCall("200", "99999", null));

            Assert.That(ex!.Message, Does.Contain("not implemented"));
            Assert.That(transport.Circuit, Is.Null, "no call may be placed when the calling address cannot be honoured");
        }

        [Test]
        public void PlaceCall_WithTheClientsOwnAddress_PlacesTheCall()
        {
            using LoopbackX25Peer peer = new LoopbackX25Peer("200");
            using X25Client client = OpenClient(peer);
            X25ClientPadTransport transport = new X25ClientPadTransport(client);

            Assert.That(transport.PlaceCall("200", "10000", null), Is.True);
            Assert.That(transport.Circuit, Is.Not.Null);
        }

        [Test]
        public void SendData_WithMoreDataBit_IsRefused()
        {
            using LoopbackX25Peer peer = new LoopbackX25Peer("200");
            using X25Client client = OpenClient(peer);
            X25ClientPadTransport transport = new X25ClientPadTransport(client);
            Assert.That(transport.PlaceCall("200", "10000", null), Is.True);
            ushort lcn = (ushort)transport.Circuit!.Lcn;

            NotSupportedException? ex = Assert.Throws<NotSupportedException>(() => transport.SendData(lcn, new byte[] { 1 }, true, false, false));

            Assert.That(ex!.Message, Does.Contain("not implemented"));
        }

        [Test]
        public void SendData_WithDeliveryConfirmationBit_IsRefused()
        {
            using LoopbackX25Peer peer = new LoopbackX25Peer("200");
            using X25Client client = OpenClient(peer);
            X25ClientPadTransport transport = new X25ClientPadTransport(client);
            Assert.That(transport.PlaceCall("200", "10000", null), Is.True);
            ushort lcn = (ushort)transport.Circuit!.Lcn;

            NotSupportedException? ex = Assert.Throws<NotSupportedException>(() => transport.SendData(lcn, new byte[] { 1 }, false, false, true));

            Assert.That(ex!.Message, Does.Contain("not implemented"));
        }

        [Test]
        public void SendData_OnAnotherChannel_IsRefused()
        {
            using LoopbackX25Peer peer = new LoopbackX25Peer("200");
            using X25Client client = OpenClient(peer);
            X25ClientPadTransport transport = new X25ClientPadTransport(client);
            Assert.That(transport.PlaceCall("200", "10000", null), Is.True);
            ushort other = (ushort)(transport.Circuit!.Lcn + 1);

            NotSupportedException? ex = Assert.Throws<NotSupportedException>(() => transport.SendData(other, new byte[] { 1 }, false, false, false));

            Assert.That(ex!.Message, Does.Contain("not implemented"));
        }
    }
}
