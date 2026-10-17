//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Packet;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Pins the 14 + 14 header / sub-header boundary and the message body at absolute offset 28,
    /// against real captured datagrams from
    /// <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-read.txt</c>.
    /// </summary>
    /// <remarks>
    /// These frames were taken off the live D100 to/from D102 link on 2026-08-04. A synthetic
    /// fixture can agree with a wrong model - that is exactly how the old 13 + 19 split survived -
    /// so everything asserted here is measured against bytes a real SINTRAN machine emitted.
    /// </remarks>
    public sealed class HeaderSubHeaderBoundaryTests
    {
        /// <summary>
        /// D102's answer to the <c>*FA-SERVER</c> connect letter: a data frame whose body at
        /// absolute 28 is an FA ConnectionConfirm.
        /// </summary>
        private const string FaConfirmDatagramHex =
            "2113000E0064006601F90008DC13"          // SINTRAN header, 14 bytes
            + "2100828400640812006606B60008"        // XMSG sub-header, 14 bytes, XMCSM 0x0008
            + "07D2000200426400";                   // the body, 8 bytes, at absolute 28

        /// <summary>
        /// D100's <c>*FA-SERVER</c> connect letter, whose body opens with the XROUT header
        /// <c>1B41 0012</c> and then carries eight bytes past that declared length.
        /// </summary>
        private const string FaConnectLetterHex =
            "2113000E0066006401F90022DBF9"          // SINTRAN header
            + "210086E400660000006408120022"     // XMSG sub-header, 14 bytes, XMCSM 0x0022
            + "1B410012"                            // XROUT header: serial 0x1B, service 0x41, length 18
            + "FF0A2A46412D534552564552"            // string parameter 1: *FA-SERVER
            + "FE0444313032"                        // string parameter 2: D102
            + "07E2000000026400A200FF00";           // eight bytes past the declared length, then more

        /// <summary>
        /// A captured secure ACK: FOURTEEN bytes, all header, no trailing byte.
        /// </summary>
        private const string AckDatagramHex = "21130003006400660" + "1F90001DC25";

        /// <summary>
        /// The two size constants sum to the measured body offset.
        /// </summary>
        [Fact]
        public void HeaderPlusSubHeader_Is28()
        {
            Assert.Equal(14, SintranHeader.Size);
            Assert.Equal(14, XmsgSubHeader.Size);
            Assert.Equal(28, SintranHeader.Size + XmsgSubHeader.Size);
        }

        /// <summary>
        /// A captured datagram decodes with its body starting at 28 and its XMCSM as one word.
        /// </summary>
        [Fact]
        public void CapturedFaConfirm_BodyStartsAt28_AndXmcsmIsOneWord()
        {
            byte[] wire = Convert.FromHexString(FaConfirmDatagramHex);
            XmsgFrame frame = XmsgFrame.Parse(wire);

            Assert.Equal(0xDC13, frame.Header.Checksum);
            Assert.NotNull(frame.SubHeader);

            // XMCSM equals Flags 2 - the relation that holds on 1449 of 1449 captured data frames.
            Assert.Equal(0x0008, frame.SubHeader!.Xmcsm);
            Assert.Equal(frame.Header.Flags2, frame.SubHeader.Xmcsm);

            // The body is the eight bytes from absolute 28, and its first word is application
            // layer: 0x07D2 = FaMessageType.ConnectionConfirm.
            byte[] body = frame.GetBodyBytes();
            Assert.Equal(wire.Length - 28, body.Length);
            Assert.Equal(0x07D2, (ushort)((body[0] << 8) | body[1]));

            for (int i = 0; i < body.Length; i++)
            {
                Assert.Equal(wire[28 + i], body[i]);
            }
        }

        /// <summary>
        /// The header checksum a captured frame carries is the one the kernel rule computes.
        /// </summary>
        [Theory]
        [InlineData(FaConfirmDatagramHex)]
        [InlineData(FaConnectLetterHex)]
        [InlineData(AckDatagramHex)]
        public void CapturedFrames_CarryTheComputedHeaderChecksum(string hex)
        {
            byte[] wire = Convert.FromHexString(hex);
            XmsgFrame frame = XmsgFrame.Parse(wire);

            ushort computed = XmsgEnvelope.ComputeHeaderChecksum(
                (ushort)((frame.Header.Marker1 << 8) | frame.Header.Marker2),
                (ushort)((frame.Header.PacketType << 8) | (byte)frame.Header.Subtype),
                frame.Header.DestinationNode,
                frame.Header.SourceNode,
                frame.Header.Flags1,
                frame.Header.Flags2);

            Assert.Equal(computed, frame.Header.Checksum);
        }

        /// <summary>
        /// A captured ACK is fourteen bytes with nothing after the header.
        /// </summary>
        [Fact]
        public void CapturedAck_IsHeaderOnly()
        {
            byte[] wire = Convert.FromHexString(AckDatagramHex);
            Assert.Equal(14, wire.Length);

            XmsgFrame frame = XmsgFrame.Parse(wire);
            Assert.Equal(SintranPacketSubtype.Ack, frame.Header.Subtype);
            Assert.Empty(frame.TrailingBytes);
            Assert.Null(frame.SubHeader);
            Assert.Equal(0xDC25, frame.Header.Checksum);
        }

        /// <summary>
        /// The captured connect letter's XROUT header is really on the wire at 28-31.
        /// </summary>
        [Fact]
        public void CapturedConnectLetter_HasItsXroutHeaderAt28()
        {
            byte[] wire = Convert.FromHexString(FaConnectLetterHex);
            XmsgFrame frame = XmsgFrame.Parse(wire);

            Assert.NotNull(frame.Body);
            Assert.Equal(0x1B, frame.Body!.Serial);
            Assert.Equal(0x41, frame.Body.Service);      // XSLET
            Assert.Equal(0x0012, frame.Body.Length);     // 18 bytes of parameter blocks

            // The historical 32-bit view is XMCSM plus that serial/service pair, and nothing else.
            Assert.Equal(0x00221B41u, frame.XmcsmWithBodyWord);

            // Both string parameters survive - *FA-SERVER and D102.
            Assert.Equal(2, frame.Body.Parameters.Count);

            // The bytes past the declared length are kept, not dropped: 07E2 0000 0002 6400 A200
            // FF00 - twelve of them on this letter. Their meaning is UNKNOWN.
            Assert.Equal(12, frame.TrailingBytes.Length);
        }

        /// <summary>
        /// Decode then re-encode of a real captured datagram is byte-identical, both with the
        /// retained raw bytes and when the model alone drives serialisation.
        /// </summary>
        [Theory]
        [InlineData(FaConfirmDatagramHex)]
        [InlineData(FaConnectLetterHex)]
        [InlineData(AckDatagramHex)]
        public void CapturedFrames_RoundTripByteIdentical(string hex)
        {
            byte[] wire = Convert.FromHexString(hex);

            XmsgFrame frame = XmsgFrame.Parse(wire);
            Assert.Equal(wire, frame.ToArray());

            // The retained copy makes the first check easy, so drop it and make the structured
            // model produce the bytes on its own. That is what a frame WE build has to do.
            frame.ClearRawBytes();
            Assert.Equal(wire, frame.ToArray());
        }

        /// <summary>
        /// A frame built from <see cref="XmsgDataFields"/> puts its body at absolute 28, whichever
        /// order the legacy compatibility setters are used in.
        /// </summary>
        [Fact]
        public void BuiltFrame_PlacesBodyAt28_RegardlessOfSetterOrder()
        {
            byte[] payload = new byte[] { 0x18, 0x00 };

            XmsgDataFields forward = default(XmsgDataFields);
            forward.DestinationNode = 100;
            forward.SourceNode = 102;
            forward.Flags1 = 0x0131;
            forward.Flags2 = 0x0108;
            forward.XmcsmWithBodyWord = 0x01080000u;
            forward.XroutPayload = payload;

            XmsgDataFields reversed = default(XmsgDataFields);
            reversed.DestinationNode = 100;
            reversed.SourceNode = 102;
            reversed.Flags1 = 0x0131;
            reversed.Flags2 = 0x0108;
            reversed.XroutPayload = payload;
            reversed.XmcsmWithBodyWord = 0x01080000u;

            byte[] forwardBytes = XmsgPacketBuilder.CreateSessionData(forward).ToBytes();
            byte[] reversedBytes = XmsgPacketBuilder.CreateSessionData(reversed).ToBytes();

            // Order-independence: the compatibility setters each own their own bytes.
            Assert.Equal(forwardBytes, reversedBytes);

            // The body sits at 28 and the payload at 32, after the 4-byte serial/service/length.
            Assert.Equal(28 + 4 + payload.Length, forwardBytes.Length);
            Assert.Equal(0x00, forwardBytes[28]);
            Assert.Equal(0x00, forwardBytes[29]);
            Assert.Equal(0x00, forwardBytes[30]);
            Assert.Equal(payload.Length, forwardBytes[31]);
            Assert.Equal(payload[0], forwardBytes[32]);
            Assert.Equal(payload[1], forwardBytes[33]);

            // XMCSM landed in the sub-header's LAST word, at 26-27.
            Assert.Equal(0x01, forwardBytes[26]);
            Assert.Equal(0x08, forwardBytes[27]);
        }
    }
}
