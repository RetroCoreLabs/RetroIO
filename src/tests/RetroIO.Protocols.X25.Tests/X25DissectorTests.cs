//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Monitor;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Tests for <see cref="X25Dissector"/>: the decoded X.25 fields must carry the correct byte offsets
    /// into the raw frame so the viewer can highlight the matching hex.
    /// </summary>
    [TestFixture]
    public class X25DissectorTests
    {
        private static MonitorField? Field(MonitorLayer layer, string name)
        {
            for (int i = 0; i < layer.Fields.Count; i++)
            {
                if (layer.Fields[i].Name == name)
                {
                    return layer.Fields[i];
                }
            }

            return null;
        }

        private static MonitorLayer Dissect(byte[] packet)
        {
            return X25Dissector.Dissect(packet, X25PacketParser.ParsePacket(packet));
        }

        [Test]
        public void Header_LciAndType_HaveByteOffsets()
        {
            byte[] packet = X25PacketBuilder.CreateRR(5, 3);
            MonitorLayer layer = Dissect(packet);

            MonitorField? lci = Field(layer, "LCI");
            Assert.That(lci, Is.Not.Null);
            Assert.That(lci!.Offset, Is.EqualTo(0));
            Assert.That(lci.Length, Is.EqualTo(2));

            MonitorField? type = Field(layer, "Type");
            Assert.That(type!.Offset, Is.EqualTo(2));
            Assert.That(type.Length, Is.EqualTo(1));

            // RR carries P(R) in the packet-type octet (modulo 8).
            MonitorField? pr = Field(layer, "P(R)");
            Assert.That(pr!.Offset, Is.EqualTo(2));
        }

        [Test]
        public void CallRequest_CalledAndCalling_HaveByteOffsets()
        {
            byte[] packet = X25PacketBuilder.CreateCallRequest(1, "23410001", "23400001", null, null);
            MonitorLayer layer = Dissect(packet);

            MonitorField? addrLen = Field(layer, "Addr Lengths");
            Assert.That(addrLen, Is.Not.Null);
            Assert.That(addrLen!.Offset, Is.EqualTo(3), "address-length octet is byte 3");

            MonitorField? called = Field(layer, "Called");
            Assert.That(called, Is.Not.Null);
            Assert.That(called!.Value, Is.EqualTo("23410001"));
            Assert.That(called.Offset, Is.EqualTo(4), "the packed address block starts at byte 4");
            Assert.That(called.Length, Is.EqualTo(4), "8 called digits occupy 4 octets");

            MonitorField? calling = Field(layer, "Calling");
            Assert.That(calling!.Value, Is.EqualTo("23400001"));
            Assert.That(calling.Offset, Is.EqualTo(8), "calling digits follow the 8 called digits (4 octets)");
        }

        [Test]
        public void DataPacket_SequenceNumbers_HaveByteOffsets()
        {
            byte[] payload = { 0x41, 0x42, 0x43 };
            byte[] packet = X25PacketBuilder.CreateDataPacket(5, 2, 3, payload);
            MonitorLayer layer = Dissect(packet);

            MonitorField? ps = Field(layer, "P(S)");
            Assert.That(ps, Is.Not.Null);
            Assert.That(ps!.Value, Is.EqualTo("2"));
            Assert.That(ps.Offset, Is.EqualTo(2), "modulo-8 sequence numbers live in the packet-type octet");

            MonitorField? pr = Field(layer, "P(R)");
            Assert.That(pr!.Value, Is.EqualTo("3"));
            Assert.That(pr.Offset, Is.EqualTo(2));

            MonitorField? ud = Field(layer, "User Data");
            Assert.That(ud!.Offset, Is.EqualTo(3), "user data starts after the 3-byte header");
            Assert.That(ud.Length, Is.EqualTo(3));
        }

        [Test]
        public void ClearRequest_CauseAndDiagnostic_HaveByteOffsets()
        {
            byte[] packet = X25PacketBuilder.CreateClearRequest(7, 0x11, 0x22);
            MonitorLayer layer = Dissect(packet);

            MonitorField? cause = Field(layer, "Cause");
            Assert.That(cause, Is.Not.Null);
            Assert.That(cause!.Offset, Is.EqualTo(3));
            Assert.That(cause.Length, Is.EqualTo(1));

            MonitorField? diag = Field(layer, "Diagnostic");
            Assert.That(diag!.Offset, Is.EqualTo(4));
            Assert.That(diag.Length, Is.EqualTo(1));
        }
    }
}
