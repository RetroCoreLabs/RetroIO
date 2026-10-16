using NUnit.Framework;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Tests for the stateless X.25 packet codec: correct GFI sequence-numbering bits and X.121 BCD
    /// address encoding, so the clean layer builds spec-conformant packets.
    /// </summary>
    [TestFixture]
    public class X25CodecTests
    {
        [Test]
        public void Gfi_SetsModulo8Bit()
        {
            // Modulo 8 => SS = 01 => bit 0x10 set.
            Assert.That(X25PacketBuilder.GFI.Create(), Is.EqualTo((byte)0x10));
            Assert.That(X25PacketBuilder.GFI.Create(lcg: 3), Is.EqualTo((byte)0x13));
        }

        [Test]
        public void Gfi_SetsModulo128Bit()
        {
            // Modulo 128 => SS = 10 => bit 0x20 set.
            Assert.That(X25PacketBuilder.GFI.Create(modulo128: true), Is.EqualTo((byte)0x20));
        }

        [Test]
        public void Gfi_QualifierAndDelivery()
        {
            Assert.That(X25PacketBuilder.GFI.Create(qBit: true), Is.EqualTo((byte)0x90));  // 0x80 | 0x10
            Assert.That(X25PacketBuilder.GFI.Create(dBit: true), Is.EqualTo((byte)0x50));  // 0x40 | 0x10
        }

        [Test]
        public void CallRequest_HasModulo8Gfi_AndBcdAddresses()
        {
            byte[] pkt = X25PacketBuilder.CreateCallRequest(1, "12", "34");

            Assert.That(pkt[0], Is.EqualTo((byte)0x10), "GFI carries the modulo-8 SS bit");
            Assert.That(pkt[1], Is.EqualTo((byte)0x01), "LCN low byte");
            Assert.That(pkt[2], Is.EqualTo((byte)0x0B), "Call Request PTI");
            Assert.That(pkt[3], Is.EqualTo((byte)0x22), "address lengths: calling<<4 | called");
            // Addresses are packed BCD nibbles, not ASCII: '1','2','3','4' -> 0x12, 0x34.
            Assert.That(pkt[4], Is.EqualTo((byte)0x12));
            Assert.That(pkt[5], Is.EqualTo((byte)0x34));
        }

        [Test]
        public void CallRequest_RoundTripsThroughParser()
        {
            byte[] pkt = X25PacketBuilder.CreateCallRequest(5, "2341", "2340");
            X25PacketInfo info = X25PacketParser.ParsePacket(pkt);

            Assert.That(info.PacketType, Is.EqualTo(X25PacketType.CallRequest));
            Assert.That(info.LCN, Is.EqualTo((ushort)5));
            Assert.That(info.CalledAddress, Is.EqualTo("2341"));
            Assert.That(info.CallingAddress, Is.EqualTo("2340"));
            Assert.That(info.Modulo128, Is.False);
        }

        [Test]
        public void DataPacket_ParsesSequenceAndBits()
        {
            byte[] pkt = X25PacketBuilder.CreateDataPacket(3, ps: 2, pr: 4, userData: new byte[] { 0xAA, 0xBB }, mBit: true);
            X25PacketInfo info = X25PacketParser.ParsePacket(pkt);

            Assert.That(info.PacketType, Is.EqualTo(X25PacketType.Data));
            Assert.That(info.LCN, Is.EqualTo((ushort)3));
            Assert.That(info.PS, Is.EqualTo((byte)2));
            Assert.That(info.PR, Is.EqualTo((byte)4));
            Assert.That(info.MBit, Is.True);
        }
    }
}
