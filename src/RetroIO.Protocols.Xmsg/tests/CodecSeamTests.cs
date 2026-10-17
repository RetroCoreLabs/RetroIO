//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Codec;
using RetroIO.Protocols.Xmsg.Packet;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Phase 2 gate for the codec seam: bytes arriving from the link raise <c>PacketReceived</c> with
    /// the correct decoded packet (sender/link-id first), and <c>SendPacket</c> writes the exact
    /// information-field bytes to the downward transport. Also proves the events-up/interfaces-down
    /// contract: the codec never touches HDLC - it only parses up and encodes down.
    /// </summary>
    public sealed class CodecSeamTests
    {
        // A minimal fake transport that records every information field the codec sends down.
        private sealed class RecordingTransport : IXmsgTransport
        {
            public List<byte[]> Sent { get; } = new List<byte[]>();

            public void Send(ReadOnlySpan<byte> bytes)
            {
                Sent.Add(bytes.ToArray());
            }
        }

        private const string ConnectRequestHex =
            "2113000E0067006400000400DA13210086E400670000006402F7040000410010FF072A54414441444D00FE0444313033";

        [Fact]
        public void ProcessBytes_RaisesPacketReceived_WithLinkIdAndDecodedPacket()
        {
            RecordingTransport transport = new RecordingTransport();
            XmsgCodec codec = new XmsgCodec("hdlc:test", transport);

            string? gotLinkId = null;
            XmsgPacketInfo? gotPacket = null;
            codec.PacketReceived += delegate (string linkId, XmsgPacketInfo packet)
            {
                gotLinkId = linkId;
                gotPacket = packet;
            };

            codec.ProcessBytes(Convert.FromHexString(ConnectRequestHex));

            Assert.Equal("hdlc:test", gotLinkId);           // sender/link-id first, stamped by the base
            Assert.NotNull(gotPacket);
            Assert.Equal(XmsgPacketType.Data, gotPacket!.Type);
            Assert.Equal(0x04000041u, gotPacket.XmcsmWithBodyWord);
            Assert.Empty(transport.Sent);                    // parsing up must not send anything down
        }

        [Fact]
        public void ProcessBytes_IgnoresShortOrNonXmsgSpans()
        {
            XmsgCodec codec = new XmsgCodec("hdlc:test", new RecordingTransport());
            int raised = 0;
            codec.PacketReceived += delegate (string linkId, XmsgPacketInfo packet) { raised++; };

            codec.ProcessBytes(new byte[] { 0x09, 0x01, 0x00, 0x66 });   // a bare LAPB RR - no SINTRAN header
            codec.ProcessBytes(Array.Empty<byte>());

            Assert.Equal(0, raised);                          // log-and-drop, never mis-dispatched
        }

        [Fact]
        public void SendPacket_WritesExactBytesToTransport()
        {
            RecordingTransport transport = new RecordingTransport();
            XmsgCodec codec = new XmsgCodec("hdlc:test", transport);

            // A secure ACK the layer would build; the codec must emit its bytes verbatim.
            XmsgPacket ack = XmsgPacketBuilder.CreateAck(
                0x0064, 0x0067, 0x0000, SintranProtocolId.Routing, 0x1D);
            codec.SendPacket(ack);

            Assert.Single(transport.Sent);
            Assert.Equal(
                Convert.FromHexString("211300030064006700000001DE1D"),
                transport.Sent[0]);
        }

        [Fact]
        public void RoundTrip_ThroughCodec_IsByteIdentical()
        {
            // Bytes up become a packet; sending that packet's frame back down reproduces the input.
            RecordingTransport transport = new RecordingTransport();
            XmsgCodec codec = new XmsgCodec("hdlc:test", transport);

            byte[] captured = Convert.FromHexString(ConnectRequestHex);
            codec.PacketReceived += delegate (string linkId, XmsgPacketInfo packet)
            {
                codec.SendPacket(new XmsgPacket(packet.Frame));
            };

            codec.ProcessBytes(captured);

            Assert.Single(transport.Sent);
            Assert.Equal(captured, transport.Sent[0]);
        }
    }
}
