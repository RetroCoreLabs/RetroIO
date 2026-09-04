using System;

using RetroIO.Protocols.Xmsg.Ethernet;

using Xunit;
using static RetroIO.Protocols.Xmsg.TestSupport.TestHex;

namespace RetroIO.Protocols.Xmsg.Ethernet.Tests
{
    /// <summary>
    /// Tests the NPDU type encoding in the ND link header's kind byte, against frames captured on
    /// 2026-08-03 from both ends at once (D100 and D102 on one emulated segment).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mapping under test - kind byte HIGH NIBBLE = NPDU type index - was established two ways
    /// that did not depend on each other: the type table was carved out of the ENCOS monitor
    /// <c>encos-mon-ii-b01.prog</c> (dispatch <c>ram:2556</c>, arm table <c>ram:26ae</c>), while
    /// <c>0x20</c> = data and <c>0x3F</c> = acknowledge had already been pinned from behaviour on
    /// the wire hours earlier. Both routes give the same answer.
    /// </para>
    /// <para>
    /// Doc: <c>SINTRAN/XMSG/DOC/COSMOS-ETHERNET-LINK-CONTROL-FRAMES-2026-08-03.md</c> sections 2b,
    /// 2c and 2d.
    /// </para>
    /// </remarks>
    public sealed class NdNpduTypeTests
    {
        /// <summary>
        /// A real connection request, D102 to D100, frame #48061-ish of the both-ends capture.
        /// Declared 802.3 length <c>0x000E</c> = 3 LLC + 11 header + 0 payload; everything from
        /// byte 28 on is Ethernet padding to the 60-byte minimum, written here as zeroes.
        /// </summary>
        private const string CapturedConnectionRequestFrameHex =
            "080026640000080026660000000ea8a8030b020f006300002f4e0066" +
            "00000000000000000000000000000000000000000000000000000000000000";

        /// <summary>
        /// A real data frame, D100 to D102, frame #48079 of the same capture, up to the end of its
        /// declared region. MAC header, 802.3 length <c>0x001C</c> = 3 + 11 + 14, LLC, then the
        /// 11-byte ND link header.
        /// </summary>
        private const string CapturedDataFrameHeaderHex =
            "080026660000080026640000001ca8a8030b022000712f4828a4000e";

        /// <summary>
        /// The 14-byte payload of that data frame: the 7-word SINTRAN datagram header
        /// <c>2113 0019 0066 0064 FFFF 0001 DE08</c>, where <c>0x66</c> = 102 and <c>0x64</c> = 100
        /// are the two system numbers and word 6 is the checksum.
        /// </summary>
        private const string CapturedDataPayloadHex = "2113001900660064ffff0001de08";

        /// <summary>
        /// Builds a whole 60-byte Ethernet frame from a hex string that covers only the bytes up to
        /// the end of the declared region; the rest is zero padding.
        /// </summary>
        /// <param name="hex">
        /// The leading bytes of the frame, in hexadecimal.
        /// </param>
        /// <returns>
        /// A 60-byte frame.
        /// </returns>
        private static byte[] PadToMinimumFrame(string hex)
        {
            byte[] head = FromHex(hex);
            byte[] frame = new byte[Ieee8023Frame.MinimumFrameLength];
            for (int i = 0; i < head.Length && i < frame.Length; i++)
            {
                frame[i] = head[i];
            }

            return frame;
        }

        /// <summary>
        /// The four kind bytes with a confirmed wire encoding map to their NPDU type through the
        /// high nibble.
        /// </summary>
        /// <param name="kind">
        /// The raw kind byte at header offset +2.
        /// </param>
        /// <param name="expectedType">
        /// The NPDU type index the high nibble must produce.
        /// </param>
        [Theory]
        [InlineData(0x0F, (int)NdNpduType.ConnectionRequest)]
        [InlineData(0x20, (int)NdNpduType.Data)]
        [InlineData(0x3F, (int)NdNpduType.Acknowledge)]
        [InlineData(0x6F, (int)NdNpduType.DisconnectRequestByNetworkService)]
        public void ConfirmedKindBytesMapToTheirNpduType(int kind, int expectedType)
        {
            NdLinkHeader header = new NdLinkHeader((byte)kind, 0x01, 0x1111, 0x2222, 0);

            Assert.Equal((NdNpduType)expectedType, header.NpduType);
            Assert.Equal((byte)kind, header.Kind);
        }

        /// <summary>
        /// The four confirmed kind bytes are exactly the four members of
        /// <see cref="NdLinkFrameKind"/>, so the enum has not drifted from the capture.
        /// </summary>
        [Fact]
        public void FrameKindEnumHoldsTheFourConfirmedWireBytes()
        {
            Assert.Equal(0x0F, (int)NdLinkFrameKind.ConnectionRequest);
            Assert.Equal(0x20, (int)NdLinkFrameKind.Data);
            Assert.Equal(0x3F, (int)NdLinkFrameKind.Acknowledge);
            Assert.Equal(0x6F, (int)NdLinkFrameKind.DisconnectRequestByNetworkService);
        }

        /// <summary>
        /// The low nibble is <c>0xF</c> on the three control-ish kinds and <c>0x0</c> on data. This
        /// pattern is recorded, NOT explained - the test pins the observation so a future change
        /// that invents a meaning has to face it.
        /// </summary>
        /// <param name="kind">
        /// The raw kind byte.
        /// </param>
        /// <param name="expectedLowNibble">
        /// The low nibble the header must report.
        /// </param>
        [Theory]
        [InlineData(0x0F, 0x0F)]
        [InlineData(0x3F, 0x0F)]
        [InlineData(0x6F, 0x0F)]
        [InlineData(0x20, 0x00)]
        public void KindLowNibbleIsRecordedButNotInterpreted(int kind, int expectedLowNibble)
        {
            NdLinkHeader header = new NdLinkHeader((byte)kind, 0x01, 0, 0, 0);

            Assert.Equal((byte)expectedLowNibble, header.KindLowNibble);
        }

        /// <summary>
        /// The captured connection request parses as CR, carries a sender link id of zero, and its
        /// trailing field is the sender's own system number rather than a payload length.
        /// </summary>
        [Fact]
        public void CapturedConnectionRequestHasZeroSenderIdAndNoPayload()
        {
            byte[] frame = PadToMinimumFrame(CapturedConnectionRequestFrameHex);

            Assert.True(Ieee8023Frame.TryParse(frame, out NdMacAddress destination, out NdMacAddress source, out int payloadOffset, out int payloadLength));

            Assert.True(destination.TryGetSystemNumber(out ushort destinationNode));
            Assert.True(source.TryGetSystemNumber(out ushort sourceNode));
            Assert.Equal(100, destinationNode);
            Assert.Equal(102, sourceNode);

            // The declared length leaves room for the link header and NOTHING else.
            Assert.Equal(NdLinkHeader.Length, payloadLength);

            Assert.True(NdLinkHeader.TryParse(frame.AsSpan(payloadOffset, payloadLength), out NdLinkHeader link));

            Assert.True(link.IsConnectionRequest);
            Assert.Equal(NdNpduType.ConnectionRequest, link.NpduType);
            Assert.False(link.IsData);
            Assert.False(link.IsAcknowledge);
            Assert.False(link.IsDisconnectRequest);

            // No link exists yet, so the sender slot is zero.
            Assert.Equal(0x0000, link.SenderLinkId);
            Assert.Equal(0x2F4E, link.ReceiverLinkId);
            Assert.Equal(0x63, link.Sequence);

            // The trailing field is 0x0066 = 102 = the SENDER's own system number. If it were read
            // as a length, a parser would go looking for 102 bytes of message in a frame whose
            // declared payload is empty - the whole point of TrailingField.
            Assert.Equal(0x0066, link.TrailingField);
            Assert.Equal(sourceNode, link.TrailingField);
            Assert.Equal(0, payloadLength - NdLinkHeader.Length);
        }

        /// <summary>
        /// The captured data frame pins the two link ids of the 2026-08-03 session and shows the
        /// trailing field really is the payload length on a data frame.
        /// </summary>
        [Fact]
        public void CapturedDataFrameCarriesTheSessionLinkIdsAndARealLength()
        {
            byte[] frame = PadToMinimumFrame(CapturedDataFrameHeaderHex + CapturedDataPayloadHex);

            Assert.True(Ieee8023Frame.TryParse(frame, out _, out _, out int payloadOffset, out int payloadLength));
            Assert.True(NdLinkHeader.TryParse(frame.AsSpan(payloadOffset, payloadLength), out NdLinkHeader link));

            Assert.True(link.IsData);
            Assert.Equal(NdNpduType.Data, link.NpduType);
            Assert.Equal(0x71, link.Sequence);

            // D100 -> D102 on the 2026-08-03 session. The 2026-08-01 session used a different pair
            // (0x5062 / 0x59C1); both are real, the ids are per-session.
            Assert.Equal(0x2F48, link.SenderLinkId);
            Assert.Equal(0x28A4, link.ReceiverLinkId);

            Assert.Equal(14, link.PayloadLength);
            Assert.Equal(14, link.TrailingField);
            Assert.Equal(NdLinkHeader.Length + 14, payloadLength);
        }

        /// <summary>
        /// A disconnect request by the network service is recognised, and its trailing field is the
        /// unexplained <c>0x0101</c> rather than a length.
        /// </summary>
        [Fact]
        public void DisconnectRequestIsRecognisedAndItsTrailingFieldIsNotALength()
        {
            // 0B 02 6F 00 65 | 28 A4 | 2F 48 | 01 01 - D102 -> D100, capture frame #48080.
            byte[] header = FromHex("0b026f006528a42f480101");

            Assert.True(NdLinkHeader.TryParse(header, out NdLinkHeader link));

            Assert.True(link.IsDisconnectRequest);
            Assert.Equal(NdNpduType.DisconnectRequestByNetworkService, link.NpduType);
            Assert.Equal(0x28A4, link.SenderLinkId);
            Assert.Equal(0x2F48, link.ReceiverLinkId);
            Assert.Equal(0x0101, link.TrailingField);

            // The frame really has no payload: its 802.3 length was 0x000E. So PayloadLength must
            // NOT be trusted here even though it reports 257.
            Assert.False(link.IsData);
        }

        /// <summary>
        /// Byte 0 of the header equals the header length, which is what it means.
        /// </summary>
        [Fact]
        public void SignatureByteZeroIsTheHeaderLength()
        {
            Assert.Equal(NdLinkHeader.Length, NdLinkHeader.Signature0);
        }
    }
}
