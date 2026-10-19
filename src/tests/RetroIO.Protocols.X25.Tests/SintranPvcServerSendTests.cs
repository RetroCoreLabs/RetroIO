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

        /// <summary>
        /// The Data packet has the header the builder gives for P(S) 0 and P(R) 0, and its time stamp
        /// is the one in the event.
        /// </summary>
        [Test]
        public void SendX25Data_PutsTheBytesOfADataPacketWithSequenceNumbersZero()
        {
            EstablishCircuit(Lcn);
            byte[] payload = new byte[] { 0x01, 0x02, 0x03 };

            _server.SendX25Data(Lcn, payload);

            X25PacketEventArgs sent = LastSent();
            Assert.That(sent.Packet.FullPacket, Is.EqualTo(X25PacketBuilder.CreateDataPacket(Lcn, 0, 0, payload)));
            Assert.That(sent.Packet.Timestamp, Is.EqualTo(sent.Timestamp));
        }

        /// <summary>
        /// Without a listener on PacketGenerated both send methods still work: the data call returns
        /// and the interrupt call counts the interrupt as waiting for its confirmation.
        /// </summary>
        [Test]
        public void SendX25Data_And_SendX25Interrupt_WorkWithoutAListener()
        {
            SintranPvcServer quiet = new SintranPvcServer(Logger);
            quiet.Initialize();
            try
            {
                byte[] call = X25PacketBuilder.CreateCallRequest(Lcn, "500", "100", null, new byte[] { 0x01 });
                quiet.OnX25Event(new X25PacketEventArgs
                {
                    Packet = new X25Packet(call) { LCN = Lcn },
                    Direction = PacketDirection.Receive,
                    Timestamp = DateTime.UtcNow
                });

                Assert.DoesNotThrow(() => quiet.SendX25Data(Lcn, new byte[] { 1 }));
                Assert.DoesNotThrow(() => quiet.SendX25Interrupt(Lcn, new byte[] { 1 }));
                Assert.Throws<InvalidOperationException>(() => quiet.SendX25Interrupt(Lcn, new byte[] { 2 }));
            }
            finally
            {
                quiet.Shutdown();
            }
        }

        private void ReceiveInterruptConfirmation(ushort lcn)
        {
            _server.OnX25Event(new X25PacketEventArgs
            {
                Packet = new X25Packet(X25PacketBuilder.CreateInterruptConfirmation(lcn)) { LCN = lcn },
                Direction = PacketDirection.Receive,
                Timestamp = DateTime.UtcNow
            });
        }

        /// <summary>
        /// The one-interrupt rule is per circuit: an interrupt outstanding on one circuit does not
        /// block another, and a confirmation frees only the circuit it came on.
        /// </summary>
        [Test]
        public void SendX25Interrupt_TracksTheUnconfirmedInterruptPerCircuit()
        {
            EstablishCircuit(Lcn);
            EstablishCircuit(8);
            _server.SendX25Interrupt(Lcn, new byte[] { 1 });

            _server.SendX25Interrupt(8, new byte[] { 2 });
            Assert.That(LastSent().Packet.LCN, Is.EqualTo((ushort)8), "circuit 8 has its own allowance");

            ReceiveInterruptConfirmation(8);
            Assert.Throws<InvalidOperationException>(() => _server.SendX25Interrupt(Lcn, new byte[] { 3 }),
                "the confirmation on circuit 8 must not free circuit 7");

            ReceiveInterruptConfirmation(Lcn);
            _server.SendX25Interrupt(Lcn, new byte[] { 3 });
            Assert.That(LastSent().Packet.Data, Is.EqualTo(new byte[] { 3 }));
        }

        /// <summary>
        /// A confirmation that matches no interrupt is ignored, and an outstanding interrupt does not
        /// stop data from being sent on the same circuit.
        /// </summary>
        [Test]
        public void InterruptConfirmation_WithNothingOutstanding_IsHarmless_AndDataIsNotBlockedByAnInterrupt()
        {
            EstablishCircuit(Lcn);

            Assert.DoesNotThrow(() => ReceiveInterruptConfirmation(Lcn));
            _server.SendX25Interrupt(Lcn, new byte[] { 1 });

            _server.SendX25Data(Lcn, new byte[] { 0x41 });
            Assert.That(LastSent().Packet.PacketType, Is.EqualTo(X25PacketType.Data));
        }

        /// <summary>
        /// A Clear Request that arrives from the peer clears the circuit, and with it the interrupt
        /// that was waiting for a confirmation.
        /// </summary>
        [Test]
        public void SendX25Interrupt_ForgetsTheUnconfirmedInterrupt_WhenThePeerClearsTheCircuit()
        {
            EstablishCircuit(Lcn);
            _server.SendX25Interrupt(Lcn, new byte[] { 1 });

            _server.OnX25Event(new X25PacketEventArgs
            {
                Packet = new X25Packet(X25PacketBuilder.CreateClearRequest(Lcn, 0, 0)) { LCN = Lcn },
                Direction = PacketDirection.Receive,
                Timestamp = DateTime.UtcNow
            });
            Assert.That(_server.GetActiveHandlers().ContainsKey(Lcn), Is.False);

            EstablishCircuit(Lcn);
            _server.SendX25Interrupt(Lcn, new byte[] { 4 });
            Assert.That(LastSent().Packet.Data, Is.EqualTo(new byte[] { 4 }));
        }

        /// <summary>
        /// A shutdown forgets every outstanding interrupt.
        /// </summary>
        [Test]
        public void SendX25Interrupt_ForgetsTheUnconfirmedInterrupt_AfterShutdown()
        {
            EstablishCircuit(Lcn);
            _server.SendX25Interrupt(Lcn, new byte[] { 1 });

            _server.Shutdown();
            _server.Initialize();
            EstablishCircuit(Lcn);

            _server.SendX25Interrupt(Lcn, new byte[] { 5 });
            Assert.That(LastSent().Packet.Data, Is.EqualTo(new byte[] { 5 }), "a restarted server has no interrupt outstanding");
        }

        /// <summary>
        /// When a listener of PacketGenerated throws, nothing is known to have gone out, so the
        /// interrupt is not counted as waiting for a confirmation and the exception reaches the caller.
        /// </summary>
        [Test]
        public void SendX25Interrupt_DoesNotWaitForAConfirmation_WhenAListenerThrows()
        {
            EstablishCircuit(Lcn);
            EventHandler<X25PacketEventArgs> thrower = (s, e) => throw new InvalidOperationException("listener failed");
            _server.PacketGenerated += thrower;

            InvalidOperationException? ex = Assert.Throws<InvalidOperationException>(() => _server.SendX25Interrupt(Lcn, new byte[] { 1 }));
            Assert.That(ex!.Message, Is.EqualTo("listener failed"));

            _server.PacketGenerated -= thrower;
            lock (_sent) { _sent.Clear(); }
            _server.SendX25Interrupt(Lcn, new byte[] { 2 });
            Assert.That(LastSent().Packet.Data, Is.EqualTo(new byte[] { 2 }), "the failed send must not leave the circuit blocked");
        }

        /// <summary>
        /// A refused interrupt (second one before the confirmation) puts no packet on the circuit.
        /// </summary>
        [Test]
        public void SendX25Interrupt_PutsNothingOnTheCircuit_WhenTheSecondOneIsRefused()
        {
            EstablishCircuit(Lcn);
            _server.SendX25Interrupt(Lcn, new byte[] { 1 });
            lock (_sent) { _sent.Clear(); }

            Assert.Throws<InvalidOperationException>(() => _server.SendX25Interrupt(Lcn, new byte[] { 2 }));

            Assert.That(_sent.Count, Is.EqualTo(0));
        }
    }
}
