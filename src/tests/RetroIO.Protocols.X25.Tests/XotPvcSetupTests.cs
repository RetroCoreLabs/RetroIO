//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using NUnit.Framework;
using RetroIO.Protocols.X25.PacketLayer;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The XOT PVC setup packet against the layout in RFC 1613 6.5, octet by octet. The expected bytes are written from
    /// the RFC's figure, not from x25emu or a router.
    /// </summary>
    [TestFixture]
    public class XotPvcSetupTests
    {
        // Initiator "Serial0" LCN 0x0101, responder "S1" LCN 0x0005, windows 2 and 3, packet sizes 128 and 256.
        private static readonly byte[] Vector =
        {
            0x10, 0x01,             // GFI and LCN, as given
            0xF5,                   // PVC setup PTI
            0x81,                   // version
            0x00,                   // status: the initiator's is 0x00
            0x07,                   // initiator interface name length N
            0x01, 0x01,             // initiator LCN
            0x02,                   // responder interface name length M
            0x00, 0x05,             // responder LCN
            0x02,                   // sender incoming window
            0x03,                   // sender outgoing window
            0x07,                   // sender incoming max packet size: log2(128)
            0x08,                   // sender outgoing max packet size: log2(256)
            (byte)'S', (byte)'e', (byte)'r', (byte)'i', (byte)'a', (byte)'l', (byte)'0',
            (byte)'S', (byte)'1',   // no padding, no trailing zero
        };

        private static XotPvcSetup Sample()
        {
            return new XotPvcSetup
            {
                GfiLcn = 0x1001,
                Status = XotPvcSetup.StatusOk,
                InitiatorInterface = "Serial0",
                InitiatorLci = 0x0101,
                ResponderInterface = "S1",
                ResponderLci = 5,
                IncomingWindow = 2,
                OutgoingWindow = 3,
                IncomingPacketSize = 128,
                OutgoingPacketSize = 256,
            };
        }

        [Test]
        public void Build_MatchesTheRfcLayout()
        {
            Assert.That(Sample().Build(), Is.EqualTo(Vector));
        }

        [Test]
        public void Parse_ReadsEveryField()
        {
            Assert.That(XotPvcSetup.IsSetup(Vector), Is.True);
            Assert.That(XotPvcSetup.TryParse(Vector, out XotPvcSetup s), Is.True);
            Assert.That(s.GfiLcn, Is.EqualTo((ushort)0x1001));
            Assert.That(s.Status, Is.EqualTo((byte)0));
            Assert.That(s.InitiatorInterface, Is.EqualTo("Serial0"));
            Assert.That(s.InitiatorLci, Is.EqualTo((ushort)0x0101));
            Assert.That(s.ResponderInterface, Is.EqualTo("S1"));
            Assert.That(s.ResponderLci, Is.EqualTo((ushort)5));
            Assert.That(s.IncomingWindow, Is.EqualTo((byte)2));
            Assert.That(s.OutgoingWindow, Is.EqualTo((byte)3));
            Assert.That(s.IncomingPacketSize, Is.EqualTo(128));
            Assert.That(s.OutgoingPacketSize, Is.EqualTo(256));
        }

        [Test]
        public void Parse_RefusesAWrongVersion()
        {
            byte[] p = (byte[])Vector.Clone();
            p[3] = 0x80;
            Assert.That(XotPvcSetup.TryParse(p, out _), Is.False, "the version exists to refuse prior experimental ones");
        }

        [Test]
        public void Parse_RefusesPaddingAfterTheNames()
        {
            byte[] p = new byte[Vector.Length + 1];
            Vector.CopyTo(p, 0);
            Assert.That(XotPvcSetup.TryParse(p, out _), Is.False, "MUST NOT be any padding or trailing zero octets");
        }

        [Test]
        public void Parse_RefusesNamesLongerThanThePacket()
        {
            byte[] p = (byte[])Vector.Clone();
            p[5] = 0x20;
            Assert.That(XotPvcSetup.TryParse(p, out _), Is.False);
        }

        [Test]
        public void Build_RefusesAPacketSizeThatIsNotAPowerOfTwo()
        {
            XotPvcSetup s = Sample();
            s.OutgoingPacketSize = 100;
            Assert.Throws<ArgumentException>(() => s.Build());
        }

        [Test]
        public void TheResponderReplyIsTheSamePacketWithItsOwnStatus()
        {
            // "designed so that the responder could simply modify a few fields of the received packet and send it back".
            XotPvcSetup.TryParse(Vector, out XotPvcSetup s);
            s.Status = XotPvcSetup.StatusNoSuchPvc;
            byte[] reply = s.Build();
            Assert.That(reply[4], Is.EqualTo((byte)0x16));
            Assert.That(reply.AsSpan(5).ToArray(), Is.EqualTo(Vector.AsSpan(5).ToArray()));
        }

        [TestCase(2, 128, (byte)0x00)]   // complementary: the sender's in is our out and the other way round
        [TestCase(3, 128, (byte)0x18)]   // our window differs: mismatched
        [TestCase(2, 256, (byte)0x18)]   // our packet size differs: mismatched
        public void CheckFlowControl_SymmetricSender(int localWindow, int localSize, byte expected)
        {
            XotPvcSetup s = Sample();
            s.IncomingWindow = 2;
            s.OutgoingWindow = 2;
            s.IncomingPacketSize = 128;
            s.OutgoingPacketSize = 128;
            Assert.That(s.CheckFlowControl((byte)localWindow, localSize, maxWindow: 7, maxPacketSize: 1024), Is.EqualTo(expected));
        }

        [Test]
        public void CheckFlowControl_AWindowAboveSevenOnModulo8_CannotBeSupported()
        {
            // RFC 1613 6.5: 0x19 "may be returned when ... a modulo 8 interface is requested to set up a PVC with a
            // window size greater than 7".
            XotPvcSetup s = Sample();
            s.IncomingWindow = 8;
            Assert.That(s.CheckFlowControl(2, 128, maxWindow: 7, maxPacketSize: 1024), Is.EqualTo((byte)0x19));
        }
    }
}
