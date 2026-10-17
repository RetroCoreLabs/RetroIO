//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Packet;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Round-trip tests for a full data frame built with the fluent builders.
    /// </summary>
    public sealed class FrameRoundTripTests
    {
        [Fact]
        public void BuildDataFrame_Serialize_Decode_Equal()
        {
            XroutMessage body = new XroutMessageBuilder()
                // CORRECTED 2026-08-04: the serial IS on the wire, at absolute 28. It is the
                // high byte of the old 32-bit control/service value, so it must agree with the
                // 0x0100014B passed to WithControlService below: serial 0x01, service 0x4B.
                .WithSerial(1)
                .WithService(XroutService.XSGSY)
                .AddInteger16(1, 102)                 // first system number
                .AddInteger16(2, (ushort)XroutConnectionType.Via)
                .Build();

            XmsgFrame frame = new XmsgFrameBuilder()
                .Between(102, 100)
                .WithDatagramSequence(7)
                .WithProtocol(SintranProtocolId.Routing)
                .WithFlags2(0x0400)
                .WithSubHeaderControl(0x04, 0x82, 0xC4)
                .WithEndpoints(102, 0x02C1, 100, 0x02AD)
                .WithControlService(0x0100014B)
                .WithBody(body)
                .Build();

            byte[] wire = frame.ToArray();

            XmsgFrame decoded = XmsgFrame.Parse(wire);

            // Header equality.
            Assert.Equal(SintranPacketSubtype.Data, decoded.Header.Subtype);
            Assert.Equal(102, decoded.Header.DestinationNode);
            Assert.Equal(100, decoded.Header.SourceNode);
            Assert.Equal(7, decoded.Header.Flags1);
            Assert.Equal(0x0400, decoded.Header.Flags2);

            // Word 6 is the DERIVED checksum and survives the round trip.
            //
            // This used to assert ProtocolId == Routing and Counter == 0x04 - the two obsolete
            // facades over bytes 12 and 13. That pinned a FABRICATED word 6: the builder wrote
            // whatever the caller passed and never computed the real value. Corrected 2026-08-07
            // when Build() started deriving it, as BuildShort always did.
            Assert.Equal(
                XmsgEnvelope.ComputeHeaderChecksum(
                    (ushort)((SintranHeader.Marker1Value << 8) | SintranHeader.Marker2Normal),
                    (ushort)((decoded.Header.PacketType << 8) | (byte)SintranPacketSubtype.Data),
                    102,
                    100,
                    7,
                    0x0400),
                decoded.Header.Checksum);

            // Sub-header equality.
            Assert.NotNull(decoded.SubHeader);
            Assert.Equal(0x82, decoded.SubHeader.FrameFlags);
            Assert.Equal(0xC4, decoded.SubHeader.Role);
            Assert.Equal(102, decoded.SubHeader.DestinationSystem);
            Assert.Equal(0x02C1, decoded.SubHeader.DestinationPort);
            Assert.Equal(100, decoded.SubHeader.SourceSystem);
            Assert.Equal(0x02AD, decoded.SubHeader.SourcePort);
            Assert.Equal(0x0100014Bu, decoded.XmcsmWithBodyWord);

            // Body equality. The 4-byte XROUT header IS carried, at absolute 28-31, so the serial
            // and service survive the round trip. They are the same two bytes the historical
            // 32-bit ControlService asserted above shows in its low half.
            Assert.NotNull(decoded.Body);
            Assert.Equal(1, decoded.Body!.Serial);
            Assert.Equal((byte)XroutService.XSGSY, decoded.Body.Service);
            Assert.Equal(2, decoded.Body.Parameters.Count);
            Assert.Equal(1, decoded.Body.Parameters[0].ParameterNumber);
            Assert.Equal(new byte[] { 0x00, 0x66 }, decoded.Body.Parameters[0].Data);
            Assert.Equal(2, decoded.Body.Parameters[1].ParameterNumber);
            Assert.Equal(new byte[] { 0x00, (byte)XroutConnectionType.Via }, decoded.Body.Parameters[1].Data);

            // Decode -> re-serialize must be byte-identical.
            Assert.Equal(wire, decoded.ToArray());
        }
    }
}
