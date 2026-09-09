using RetroIO.Protocols.Xmsg.Hdlc;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Live.Tests
{
    /// <summary>
    /// Control-byte decode proof for <see cref="LapbFrame"/>: supervisory subtype (RR/RNR/REJ via
    /// <c>ctrl AND 0x0F</c>) and unnumbered type (SABM/DISC/UA/DM/FRMR via <c>ctrl AND ~0x10</c>),
    /// per ND LAPB spec sections 2.2.2 and 2.2.3. Every field the parser exposes is asserted, and the
    /// expected values are derived from the spec's encode rules, not from the parser.
    /// </summary>
    public sealed class LapbFrameDecodeTests
    {
        /// <summary>
        /// Each supervisory control byte decodes to the correct subtype, N(R) and P/F, and reports
        /// no unnumbered type. Control encoding (spec 2.2.2): <c>(N(R) shifted left 5) | (PF shifted left 4) | nibble</c>.
        /// </summary>
        /// <param name="address">
        /// The LAPB address byte (data-transfer role <c>0x09</c> for S-frames).
        /// </param>
        /// <param name="control">
        /// The supervisory control byte under test.
        /// </param>
        /// <param name="expected">
        /// The subtype the low nibble must decode to.
        /// </param>
        /// <param name="expectedNr">
        /// The receive sequence number encoded in bits 5..7.
        /// </param>
        /// <param name="expectedPf">
        /// Whether the poll/final bit (<c>0x10</c>) is set.
        /// </param>
        [Theory]
        [InlineData(0x09, 0x01, LapbSupervisoryKind.ReceiveReady, 0, false)]     // RR   N(R)=0
        [InlineData(0x09, 0x21, LapbSupervisoryKind.ReceiveReady, 1, false)]     // RR   N(R)=1
        [InlineData(0x09, 0x11, LapbSupervisoryKind.ReceiveReady, 0, true)]      // RR   N(R)=0 P/F
        [InlineData(0x09, 0x05, LapbSupervisoryKind.ReceiveNotReady, 0, false)]  // RNR  N(R)=0
        [InlineData(0x09, 0x25, LapbSupervisoryKind.ReceiveNotReady, 1, false)]  // RNR  N(R)=1
        [InlineData(0x09, 0x09, LapbSupervisoryKind.Reject, 0, false)]           // REJ  N(R)=0 (nibble 0x9)
        [InlineData(0x09, 0x29, LapbSupervisoryKind.Reject, 1, false)]           // REJ  N(R)=1
        public void SupervisoryFrame_DecodesSubtype_NrAndPollFinal(
            byte address, byte control, LapbSupervisoryKind expected, int expectedNr, bool expectedPf)
        {
            LapbFrame frame = MakeFrame(address, control, System.Array.Empty<byte>());

            Assert.Equal(LapbFrameKind.Supervisory, frame.Kind);
            Assert.Equal(expected, frame.SupervisoryKind);
            Assert.Equal(LapbUnnumberedKind.Unknown, frame.UnnumberedKind);   // not an unnumbered frame
            Assert.Equal(address, frame.Address);
            Assert.Equal(control, frame.Control);
            Assert.Equal(expectedNr, frame.ReceiveSequence);
            Assert.Equal(-1, frame.SendSequence);                              // S-frames carry no N(S)
            Assert.Equal(expectedPf, frame.PollFinal);
        }

        /// <summary>
        /// A REJ control byte <c>0x09</c> is decoded as Reject by field position, never confused with
        /// the identically-valued data-transfer address <c>0x09</c> (spec 2.2.2 note).
        /// </summary>
        [Fact]
        public void RejControlByte_NotConfusedWithDataAddress()
        {
            // Address 0x09 (data role) AND control 0x09 (REJ) in the same frame: both are 0x09 but
            // decode independently by position.
            LapbFrame frame = MakeFrame(0x09, 0x09, System.Array.Empty<byte>());

            Assert.Equal(0x09, frame.Address);
            Assert.Equal(LapbFrameKind.Supervisory, frame.Kind);
            Assert.Equal(LapbSupervisoryKind.Reject, frame.SupervisoryKind);
        }

        /// <summary>
        /// Each unnumbered control byte decodes to the correct type with the P/F bit masked off, and
        /// reports no supervisory subtype. Base patterns (spec 2.2.3): SABM <c>0x2F</c>, DISC
        /// <c>0x43</c>, UA <c>0x63</c>, DM <c>0x0F</c>, FRMR <c>0x87</c>.
        /// </summary>
        /// <param name="control">
        /// The unnumbered control byte under test (base or with the P/F bit set).
        /// </param>
        /// <param name="expected">
        /// The type the masked control byte must decode to.
        /// </param>
        /// <param name="expectedPf">
        /// Whether the poll/final bit (<c>0x10</c>) is set.
        /// </param>
        [Theory]
        [InlineData(0x2F, LapbUnnumberedKind.Sabm, false)]   // SABM base
        [InlineData(0x3F, LapbUnnumberedKind.Sabm, true)]    // SABM P=1 (as seen on the wire)
        [InlineData(0x43, LapbUnnumberedKind.Disc, false)]   // DISC base
        [InlineData(0x53, LapbUnnumberedKind.Disc, true)]    // DISC P=1
        [InlineData(0x63, LapbUnnumberedKind.Ua, false)]     // UA base
        [InlineData(0x73, LapbUnnumberedKind.Ua, true)]      // UA F=1 (as seen on the wire)
        [InlineData(0x0F, LapbUnnumberedKind.Dm, false)]     // DM base
        [InlineData(0x1F, LapbUnnumberedKind.Dm, true)]      // DM F=1
        [InlineData(0x87, LapbUnnumberedKind.Frmr, false)]   // FRMR base
        [InlineData(0x97, LapbUnnumberedKind.Frmr, true)]    // FRMR F=1
        public void UnnumberedFrame_DecodesType_MaskingPollFinal(
            byte control, LapbUnnumberedKind expected, bool expectedPf)
        {
            // Link-management role address 0x01 for all unnumbered frames (spec 2.1).
            LapbFrame frame = MakeFrame(0x01, control, new byte[] { 0x00, 0x64 });

            Assert.Equal(LapbFrameKind.Unnumbered, frame.Kind);
            Assert.Equal(expected, frame.UnnumberedKind);
            Assert.Equal(LapbSupervisoryKind.Unknown, frame.SupervisoryKind);   // not a supervisory frame
            Assert.Equal(0x01, frame.Address);
            Assert.Equal(control, frame.Control);
            Assert.Equal(-1, frame.SendSequence);
            Assert.Equal(-1, frame.ReceiveSequence);
            Assert.Equal(expectedPf, frame.PollFinal);
        }

        /// <summary>
        /// An information frame reports neither a supervisory subtype nor an unnumbered type, and
        /// still exposes its N(S)/N(R) (spec 2.2.1).
        /// </summary>
        [Fact]
        public void InformationFrame_ReportsNoSupervisoryOrUnnumberedKind()
        {
            // I-frame control 0x22 = (N(R)=1 << 5) | (N(S)=1 << 1); bit 0 clear -> information.
            LapbFrame frame = MakeFrame(0x09, 0x22, new byte[] { 0x21, 0x13 });

            Assert.Equal(LapbFrameKind.Information, frame.Kind);
            Assert.Equal(LapbSupervisoryKind.Unknown, frame.SupervisoryKind);
            Assert.Equal(LapbUnnumberedKind.Unknown, frame.UnnumberedKind);
            Assert.Equal(1, frame.SendSequence);
            Assert.Equal(1, frame.ReceiveSequence);
            Assert.False(frame.PollFinal);
        }

        /// <summary>
        /// An unrecognised control pattern in each family decodes to the Unknown member rather than
        /// throwing or mis-mapping.
        /// </summary>
        [Fact]
        public void UnknownPatterns_DecodeToUnknown()
        {
            // Supervisory family (bits 1..0 = 01) with an undefined low nibble 0xD.
            LapbFrame sFrame = MakeFrame(0x09, 0x0D, System.Array.Empty<byte>());
            Assert.Equal(LapbFrameKind.Supervisory, sFrame.Kind);
            Assert.Equal(LapbSupervisoryKind.Unknown, sFrame.SupervisoryKind);

            // Unnumbered family (bits 1..0 = 11) with an undefined base pattern 0x23.
            LapbFrame uFrame = MakeFrame(0x01, 0x23, System.Array.Empty<byte>());
            Assert.Equal(LapbFrameKind.Unnumbered, uFrame.Kind);
            Assert.Equal(LapbUnnumberedKind.Unknown, uFrame.UnnumberedKind);
        }

        /// <summary>
        /// Builds a de-framable <see cref="LapbFrame"/> from an address, control byte and info field.
        /// The trailing two bytes are FCS placeholders (the parser strips but does not validate them).
        /// </summary>
        /// <param name="address">
        /// The LAPB address byte.
        /// </param>
        /// <param name="control">
        /// The LAPB control byte.
        /// </param>
        /// <param name="info">
        /// The information field.
        /// </param>
        /// <returns>
        /// A parsed <see cref="LapbFrame"/>.
        /// </returns>
        private static LapbFrame MakeFrame(byte address, byte control, byte[] info)
        {
            byte[] frameBytes = new byte[2 + info.Length + 2];
            frameBytes[0] = address;
            frameBytes[1] = control;
            System.Array.Copy(info, 0, frameBytes, 2, info.Length);
            return LapbFrame.FromCapture(frameBytes);
        }
    }
}
