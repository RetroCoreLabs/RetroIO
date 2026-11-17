//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.X25.Switch.Ports;
using RetroIO.Protocols.X25.PacketLayer;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The responder's answer to a PVC setup packet, each status from RFC 1613 6.5.
    /// </summary>
    [TestFixture]
    public class XotPvcResponderTests
    {
        private static XotPvcResponder Responder()
        {
            XotPvcResponder r = new XotPvcResponder("Serial1", window: 2, packetSize: 128, maxPacketSize: 1024);
            r.AddPvc(5);
            return r;
        }

        private static byte[] Asking(string iface = "serial1", ushort lci = 5, byte window = 2, int size = 128, byte status = 0)
        {
            return new XotPvcSetup
            {
                Status = status,
                InitiatorInterface = "Serial0",
                InitiatorLci = 1,
                ResponderInterface = iface,
                ResponderLci = lci,
                IncomingWindow = window,
                OutgoingWindow = window,
                IncomingPacketSize = size,
                OutgoingPacketSize = size,
            }.Build();
        }

        [Test]
        public void AMatchingRequest_IsAccepted_NamesCompareWithoutCase()
        {
            Assert.That(Responder().Answer(Asking(), out XotPvcSetup reply), Is.EqualTo((byte)0x00));
            Assert.That(reply.ResponderLci, Is.EqualTo((ushort)5));
        }

        [Test]
        public void AnotherInterface_Is0x13()
        {
            Assert.That(Responder().Answer(Asking(iface: "Serial9"), out _), Is.EqualTo((byte)0x13));
        }

        [Test]
        public void AChannelThatIsNoPvc_Is0x16()
        {
            Assert.That(Responder().Answer(Asking(lci: 6), out _), Is.EqualTo((byte)0x16));
        }

        [Test]
        public void APvcWhoseDestinationIsDown_Is0x14()
        {
            XotPvcResponder r = Responder();
            r.InterfaceUp = lci => false;
            Assert.That(r.Answer(Asking(), out _), Is.EqualTo((byte)0x14));
            r.InterfaceUp = lci => true;
            Assert.That(r.Answer(Asking(), out _), Is.EqualTo((byte)0x00), "a refusal does not count as joined");
        }

        [Test]
        public void ASecondJoinOfTheSamePvc_Is0x17_UntilTheFirstCloses()
        {
            XotPvcResponder r = Responder();
            r.Answer(Asking(), out _);
            Assert.That(r.Answer(Asking(), out _), Is.EqualTo((byte)0x17));
            r.Disconnected(5);
            Assert.That(r.Answer(Asking(), out _), Is.EqualTo((byte)0x00));
        }

        [Test]
        public void OtherFlowControl_Is0x18_AndBeyondModulo8_Is0x19()
        {
            Assert.That(Responder().Answer(Asking(window: 3), out _), Is.EqualTo((byte)0x18));
            Assert.That(Responder().Answer(Asking(size: 2048), out _), Is.EqualTo((byte)0x19));
        }

        [Test]
        public void AnInitiatorStatusOtherThanZero_OrAnUnreadablePacket_Is0x1A()
        {
            Assert.That(Responder().Answer(Asking(status: 0x13), out _), Is.EqualTo((byte)0x1A));
            Assert.That(Responder().Answer(new byte[] { 0, 1, 0xF5 }, out _), Is.EqualTo((byte)0x1A));
        }
    }
}
