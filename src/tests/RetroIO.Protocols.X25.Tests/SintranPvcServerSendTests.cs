//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Sintran;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Pins what <c>SintranPvcServer.SendX25Data</c> and <c>SendX25Interrupt</c> put on the circuit.
    /// Both used to throw <see cref="NotImplementedException"/> whatever the arguments were.
    /// </summary>
    [TestFixture]
    public class SintranPvcServerSendTests : TestBase
    {
        private const ushort Lcn = 7;

        private SintranPvcServer _server = null!;
        private List<X25PacketEventArgs> _sent = null!;

        public override void Setup()
        {
            base.Setup();
            _server = new SintranPvcServer(Logger);
            _server.Initialize();
            _sent = new List<X25PacketEventArgs>();
            _server.PacketGenerated += (s, e) => { lock (_sent) { _sent.Add(e); } };
        }

        public override void TearDown()
        {
            _server.Shutdown();
            base.TearDown();
        }

        /// <summary>
        /// Opens a circuit the way a peer does: a Call Request. Called address 500 is not one of the TAD
        /// addresses, and the call user data byte 0x01 selects the routing handler.
        /// </summary>
        private void EstablishCircuit(ushort lcn)
        {
            byte[] call = X25PacketBuilder.CreateCallRequest(lcn, "500", "100", null, new byte[] { 0x01 });
            _server.OnX25Event(new X25PacketEventArgs
            {
                Packet = new X25Packet(call) { LCN = lcn },
                Direction = PacketDirection.Receive,
                Timestamp = DateTime.UtcNow
            });
            Assert.That(_server.GetActiveHandlers().ContainsKey(lcn), Is.True, "the call must have created a handler for the circuit");
            lock (_sent) { _sent.Clear(); }
        }

        private X25PacketEventArgs LastSent()
        {
            lock (_sent)
            {
                Assert.That(_sent.Count, Is.GreaterThan(0), "nothing was generated");
                return _sent[_sent.Count - 1];
            }
        }

        // ------------------------------------------------------------------ SendX25Data

        [Test]
        public void SendX25Data_PutsADataPacketOnTheCircuit()
        {
            EstablishCircuit(Lcn);
            byte[] payload = new byte[] { 0x41, 0x42, 0x43, 0x00, 0xFF };

            _server.SendX25Data(Lcn, payload);

            X25PacketEventArgs sent = LastSent();
            Assert.That(sent.Direction, Is.EqualTo(PacketDirection.Transmit));
            Assert.That(sent.Packet.PacketType, Is.EqualTo(X25PacketType.Data));
            Assert.That(sent.Packet.LCN, Is.EqualTo(Lcn));
            Assert.That(sent.Packet.Data, Is.EqualTo(payload));
        }

        [Test]
        public void SendX25Data_Throws_WhenThereIsNoCircuit()
        {
            InvalidOperationException? ex = Assert.Throws<InvalidOperationException>(() => _server.SendX25Data(99, new byte[] { 1 }));

            Assert.That(ex!.Message, Does.Contain("99"));
            Assert.That(_sent.Count, Is.EqualTo(0), "data must not be generated for a circuit that does not exist");
        }

        [Test]
        public void SendX25Data_Throws_ForNullOrEmptyData()
        {
            EstablishCircuit(Lcn);

            Assert.Throws<ArgumentNullException>(() => _server.SendX25Data(Lcn, null!));
            Assert.Throws<ArgumentException>(() => _server.SendX25Data(Lcn, Array.Empty<byte>()));
            Assert.That(_sent.Count, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ SendX25Interrupt

        [Test]
        public void SendX25Interrupt_PutsAnInterruptPacketOnTheCircuit()
        {
            EstablishCircuit(Lcn);

            _server.SendX25Interrupt(Lcn, new byte[] { 0x5A });

            X25PacketEventArgs sent = LastSent();
            Assert.That(sent.Direction, Is.EqualTo(PacketDirection.Transmit));
            Assert.That(sent.Packet.PacketType, Is.EqualTo(X25PacketType.Interrupt));
            Assert.That(sent.Packet.LCN, Is.EqualTo(Lcn));
            Assert.That(sent.Packet.Data, Is.EqualTo(new byte[] { 0x5A }));
        }

        [Test]
        public void SendX25Interrupt_Throws_WhenThereIsNoCircuit()
        {
            Assert.Throws<InvalidOperationException>(() => _server.SendX25Interrupt(99, new byte[] { 1 }));
            Assert.That(_sent.Count, Is.EqualTo(0));
        }

        [Test]
        public void SendX25Interrupt_Throws_ForDataOutsideOneToThirtyTwoOctets()
        {
            // X.25 (10/96) 5.3.2.1, figure 5-9: the interrupt user data field holds 1 to 32 octets.
            EstablishCircuit(Lcn);

            Assert.Throws<ArgumentNullException>(() => _server.SendX25Interrupt(Lcn, null!));
            Assert.Throws<ArgumentException>(() => _server.SendX25Interrupt(Lcn, Array.Empty<byte>()));
            Assert.Throws<ArgumentException>(() => _server.SendX25Interrupt(Lcn, new byte[33]));
            Assert.That(_sent.Count, Is.EqualTo(0));

            _server.SendX25Interrupt(Lcn, new byte[32]);
            Assert.That(LastSent().Packet.PacketType, Is.EqualTo(X25PacketType.Interrupt), "32 octets is the largest legal field");
        }

        [Test]
        public void SendX25Interrupt_RefusesASecondInterrupt_UntilTheFirstIsConfirmed()
        {
            // X.25 (10/96) 4.3.7: "The DTE should not transmit a second DTE interrupt packet until the
            // first one is confirmed with a DCE interrupt confirmation packet."
            EstablishCircuit(Lcn);
            _server.SendX25Interrupt(Lcn, new byte[] { 1 });

            Assert.Throws<InvalidOperationException>(() => _server.SendX25Interrupt(Lcn, new byte[] { 2 }));

            _server.OnX25Event(new X25PacketEventArgs
            {
                Packet = new X25Packet(X25PacketBuilder.CreateInterruptConfirmation(Lcn)) { LCN = Lcn },
                Direction = PacketDirection.Receive,
                Timestamp = DateTime.UtcNow
            });
            lock (_sent) { _sent.Clear(); }

            _server.SendX25Interrupt(Lcn, new byte[] { 2 });
            Assert.That(LastSent().Packet.Data, Is.EqualTo(new byte[] { 2 }));
        }

        [Test]
        public void SendX25Interrupt_ForgetsTheUnconfirmedInterrupt_WhenTheCircuitIsCleared()
        {
            EstablishCircuit(Lcn);
            _server.SendX25Interrupt(Lcn, new byte[] { 1 });
            _server.OnX25Clear(Lcn, 0, 0);

            EstablishCircuit(Lcn);

            _server.SendX25Interrupt(Lcn, new byte[] { 3 });
            Assert.That(LastSent().Packet.Data, Is.EqualTo(new byte[] { 3 }), "a new circuit starts with no interrupt outstanding");
        }
    }
}
