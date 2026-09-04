using System;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Packet;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Phase 1 gate for the seam packet layer: the read-only <see cref="XmsgPacketInfo"/> view and
    /// the <see cref="XmsgPacketBuilder"/> <c>Create*</c> methods must (1) round-trip every captured
    /// frame byte-for-byte and (2) reproduce the live-verified conn-to-d102 connect handshake frames
    /// (accept, port-assign, secure ACK) exactly. These are the same byte vectors the live node was
    /// validated against machine 100 with, so a green gate here means the seam builds the identical
    /// wire bytes the current proven path does.
    /// </summary>
    public sealed class PacketLayerTests
    {
        // The exact live connect request 100 -> 103 (proto DA, f1=0x0000, counter=0x13), captured.
        private const string ConnectRequestHex =
            "2113000E0067006400000400DA13210086E400670000006402F7040000410010FF072A54414441444D00FE0444313033";

        // The exact live session-setup 100 -> 103 (proto DA, f1=0x0001, counter=0x12, XMCSM 04000000).
        private const string SessionSetupHex =
            "2113000E0067006400010400DA122100868400670156006402F704000000000906001B001C0100FF00";

        /// <summary>
        /// The gate's round-trip half: parsing a captured information field into a packet and
        /// re-serialising it must reproduce the input byte-for-byte, for data and short frames alike.
        /// </summary>
        [Theory]
        [InlineData(ConnectRequestHex)]
        [InlineData(SessionSetupHex)]
        [InlineData("2113001900660064FFFF0001DE08")]                   // a short reachability request
        [InlineData("211300030064006700000001DE1D")]                   // a 14-byte secure ACK
        public void ParseThenReEncode_IsByteIdentical(string capturedHex)
        {
            byte[] captured = Convert.FromHexString(capturedHex);

            XmsgPacketInfo info = XmsgPacketParser.ParsePacket(captured);
            // Re-encode via the outgoing wrapper over the SAME decoded frame (RawBytes authoritative).
            byte[] reencoded = new XmsgPacket(info.Frame).ToBytes();

            Assert.Equal(captured, reencoded);
        }

        /// <summary>
        /// The decoded view exposes the envelope fields and the derived <c>Base = Flags1 + Counter</c>
        /// identity that the universal model (section 18.5) is built on.
        /// </summary>
        [Fact]
        public void PacketInfo_ExposesEnvelopeFieldsAndDerivedBase()
        {
            XmsgPacketInfo info = XmsgPacketParser.ParsePacket(Convert.FromHexString(ConnectRequestHex));

            Assert.Equal(XmsgPacketType.Data, info.Type);
            Assert.Equal(0x0064, info.SourceNode);                    // 100 (the connecting machine)
            Assert.Equal(0x0067, info.DestinationNode);               // 103 (us)
            Assert.Equal(0x0000, info.Flags1);
            Assert.Equal((byte)SintranProtocolId.Pad, info.ChecksumHigh);      // 0xDA
            Assert.True(info.HasSubHeader);
            Assert.Equal(0x13, info.ChecksumLow);
            Assert.Equal(0x04000041u, info.XmcsmWithBodyWord);
            Assert.Equal(0x02F7, info.SourcePort);                    // 100's asking port (XMSPT)
            // Base = Flags1 (0x0000) + Counter (0x13) = 0x0013.
            Assert.Equal(0x0013, info.Base);
        }

        /// <summary>
        /// A non-XMSG / too-short span is classified without throwing.
        /// </summary>
        [Fact]
        public void TryParsePacket_RejectsShortOrNonMarkerSpans()
        {
            Assert.False(XmsgPacketParser.TryParsePacket(new byte[] { 0x00, 0x01 }, out XmsgPacketInfo? tooShort));
            Assert.Null(tooShort);

            byte[] notMarker = new byte[13];
            notMarker[0] = 0x99;
            Assert.False(XmsgPacketParser.TryParsePacket(notMarker, out XmsgPacketInfo? wrongMarker));
            Assert.Null(wrongMarker);

            Assert.True(XmsgPacketParser.TryParsePacket(Convert.FromHexString(ConnectRequestHex), out XmsgPacketInfo? ok));
            Assert.NotNull(ok);
        }

        /// <summary>
        /// The gate's build half (accept): <see cref="XmsgPacketBuilder.CreateData"/> reproduces the
        /// live-verified conn-to-d102 connect-accept byte-for-byte.
        /// </summary>
        [Fact]
        public void CreateData_ReproducesConnectAccept()
        {
            XmsgDataFields fields = new XmsgDataFields
            {
                DestinationNode = 0x0064,   // back to 100
                SourceNode = 0x0067,        // from us (103)
                Flags1 = 0x0000,            // echo connect datagram sequence
                Flags2 = 0x0400,            // setup-frame class word
                ChecksumHigh = (byte)SintranProtocolId.Pad,   // 0xDA - echo the connect channel
                ChecksumLow = 0x13,             // echo connect counter
                FrameFlags = 0x86,
                Role = 0x40,                // responder role
                DestinationSystem = 0x0064, // 100
                DestinationPort = 0x02F7,   // 100's port
                SourceSystem = 0x0067,      // 103
                SourcePort = 0x0156,        // our TADADM port
                XmcsmWithBodyWord = 0x04000041u,
                XroutPayload = Convert.FromHexString("01020000" + "0202000A"),
            };

            byte[] accept = XmsgPacketBuilder.CreateData(fields).ToBytes();

            byte[] expected = Convert.FromHexString(
                "2113000E00640067000004" + "00DA" + "13" + "2100" + "86" + "40"
                + "0064" + "02F7" + "0067" + "0156" + "04000041" + "00" + "08"
                + "01020000" + "0202000A");
            Assert.Equal(expected, accept);
        }

        /// <summary>
        /// The gate's build half (port-assign): <see cref="XmsgPacketBuilder.CreateData"/> reproduces
        /// the live-verified conn-to-d102 port-assignment frame byte-for-byte, including the 24-byte
        /// TAD 0x07 endpoint trailer and XMLEN.
        /// </summary>
        [Fact]
        public void CreateData_ReproducesPortAssign()
        {
            XmsgDataFields fields = new XmsgDataFields
            {
                DestinationNode = 0x0064,
                SourceNode = 0x0067,
                Flags1 = 0x0001,            // echo session-setup datagram sequence
                Flags2 = 0x0400,
                ChecksumHigh = (byte)SintranProtocolId.Pad,   // 0xDA
                ChecksumLow = 0x12,             // echo session-setup counter
                FrameFlags = 0x86,
                Role = 0x40,
                DestinationSystem = 0x0064,
                DestinationPort = 0x02F7,
                SourceSystem = 0x0067,
                SourcePort = 0x0156,
                XmcsmWithBodyWord = 0x04000000u,
                // 24-byte trailer: 0x07 endpoint (system 0x67, session port 0x0211) then options.
                XroutPayload = Convert.FromHexString(
                    "00" + "0705" + "00006702" + "11" + "1F03" + "4C0000"
                    + "00" + "0B02" + "0300" + "1502" + "0108" + "FF00"),
            };

            byte[] assign = XmsgPacketBuilder.CreateData(fields).ToBytes();

            byte[] expected = Convert.FromHexString(
                "2113000E00640067000104" + "00DA" + "12" + "2100" + "86" + "40"
                + "0064" + "02F7" + "0067" + "0156" + "04000000" + "00" + "18"
                + "00" + "0705" + "00006702" + "11" + "1F03" + "4C0000"
                + "00" + "0B02" + "0300" + "1502" + "0108" + "FF00");
            Assert.Equal(expected, assign);
        }

        /// <summary>
        /// <see cref="XmsgPacketBuilder.CreateAck"/> reproduces the exact 14-byte secure ACK machine
        /// 100 accepts: channel = connect-channel + 4 (DA -> DE), trailing = connect counter + 0x0A.
        /// </summary>
        [Fact]
        public void CreateAck_ReproducesSecureAck()
        {
            byte[] ack = XmsgPacketBuilder
                .CreateAck(0x0064, 0x0067, 0x0000, SintranProtocolId.Routing, 0x1D)
                .ToBytes();

            byte[] expected = Convert.FromHexString("211300030064006700000001" + "DE" + "1D");
            Assert.Equal(expected, ack);
        }

        /// <summary>
        /// <see cref="XmsgPacketBuilder.CreateReachabilityReply"/> reproduces the subtype-0x13 reply
        /// (swap direction, Flags1 0xFFFF, Flags2 0x0001, trailing = request counter + 6).
        /// </summary>
        [Fact]
        public void CreateReachabilityReply_ReproducesReply()
        {
            // Request 100 -> 102 was "...DE 08"; the reply swaps to 102 -> 100 with trailing 0x0E.
            byte[] reply = XmsgPacketBuilder
                .CreateReachabilityReply(0x0064, 0x0066, SintranProtocolId.Routing, 0x0E)
                .ToBytes();

            byte[] expected = Convert.FromHexString("2113001300640066FFFF0001DE0E");
            Assert.Equal(expected, reply);
        }

        /// <summary>
        /// <see cref="XmsgPacketBuilder.CreateNetworkError"/> builds a subtype-0x07 reject whose
        /// Flags2 carries the negative XE* code (XEIMA -19 = 0xFFED invalid magic).
        /// </summary>
        [Fact]
        public void CreateNetworkError_CarriesNegativeXeCode()
        {
            byte[] err = XmsgPacketBuilder
                .CreateNetworkError(0x0064, 0x0067, 0x0002, 0xFFED, SintranProtocolId.Routing, 0x00)
                .ToBytes();

            XmsgPacketInfo info = XmsgPacketParser.ParsePacket(err);
            Assert.Equal(XmsgPacketType.NetworkError, info.Type);
            Assert.Equal(0xFFED, info.Flags2);          // XEIMA -19
            Assert.Equal(0x0002, info.Flags1);
            Assert.Equal((byte)SintranProtocolId.Routing, info.ChecksumHigh);
        }
    }
}
