using System;

using RetroIO.Protocols.Xmsg.Hdlc;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Live.Tests
{
    /// <summary>
    /// Gate for the decode of header word 0: it is <c>XDROU</c> - version/protocol plus a HOP
    /// COUNT - and NOT two magic marker bytes.
    /// </summary>
    /// <remarks>
    /// <para><b>What these tests are defending</b></para>
    /// <c>LapbFrame.IsSintranInfo</c> used to demand byte 1 be <c>0x13</c> or <c>0x12</c>, an
    /// allow-list of the only two values any capture had ever shown. The L03 kernel tables name the
    /// field and settle it:
    ///  - <c>INTEGER XDROU % NETWORK INFO (VERSION, PROTOCOL, HOP COUNT)</c>.
    ///  - <c>SYMBOL X5VRS=20400 % L.H. BYTE OF XDROU (OP=0, VERSION=2, PROTOCOL=1)</c>.
    /// <para>
    /// <c>20400</c> octal is <c>0x2100</c>. So byte 0 is version/protocol and byte 1 counts hops
    /// DOWN - which is exactly why a relayed frame reads <c>0x12</c> against a direct <c>0x13</c>,
    /// and exactly what <c>SintranDatagramRelay</c> already does when it "decrements marker 2".
    /// </para>
    /// <para>
    /// The allow-list therefore capped the stack at ONE relay hop, silently: a twice-forwarded
    /// frame carries <c>0x11</c> and was classified as not-SINTRAN. No capture caught it because
    /// nothing has yet been relayed twice - which is precisely why it needs a test rather than a
    /// capture.
    /// </para>
    /// </remarks>
    public sealed class SintranHopCountRecognitionTests
    {
        /// <summary>
        /// Wraps an information field in a LAPB I-frame and parses it back.
        /// </summary>
        /// <param name="info">
        /// The information field bytes.
        /// </param>
        /// <returns>
        /// The parsed frame.
        /// </returns>
        private static LapbFrame ParseInformationFrame(byte[] info)
        {
            // addr + ctrl + info + 2-byte FCS. The FCS is never checked by the constructor, so
            // zeroes are fine - these tests are about the information field, not the frame check.
            byte[] raw = new byte[2 + info.Length + 2];
            raw[0] = 0x09;          // address
            raw[1] = 0x00;          // I-frame control, N(S)=0 N(R)=0
            Array.Copy(info, 0, raw, 2, info.Length);

            LapbFrame frame = LapbFrame.FromCapture(raw);
            Assert.Equal(LapbFrameKind.Information, frame.Kind);
            return frame;
        }

        /// <summary>
        /// A direct frame carries the initial hop count and is recognised.
        /// </summary>
        [Fact]
        public void DirectFrameAtTheInitialHopCountIsSintran()
        {
            LapbFrame frame = ParseInformationFrame(new byte[] { 0x21, 0x13, 0x00, 0x0E });

            Assert.True(frame.IsSintranInfo);
        }

        /// <summary>
        /// A once-relayed frame is recognised - this is the case the old allow-list also allowed.
        /// </summary>
        [Fact]
        public void OnceRelayedFrameIsSintran()
        {
            LapbFrame frame = ParseInformationFrame(new byte[] { 0x21, 0x12, 0x00, 0x0E });

            Assert.True(frame.IsSintranInfo);
        }

        /// <summary>
        /// A TWICE-relayed frame is recognised. This is the defect: the old allow-list rejected it.
        /// </summary>
        [Fact]
        public void TwiceRelayedFrameIsStillSintran()
        {
            LapbFrame frame = ParseInformationFrame(new byte[] { 0x21, 0x11, 0x00, 0x0E });

            Assert.True(frame.IsSintranInfo);
        }

        /// <summary>
        /// A hop count ABOVE the initial value is not a hop count, so the frame is not ours.
        /// </summary>
        /// <remarks>
        /// The bound is what keeps this test worth making at all: <c>0x21 0x13</c> is also a valid
        /// X.25 GFI/LCN, so loosening byte 1 without an upper bound would weaken the only
        /// discrimination this predicate has.
        /// </remarks>
        [Fact]
        public void AHopCountAboveTheInitialValueIsNotSintran()
        {
            LapbFrame frame = ParseInformationFrame(new byte[] { 0x21, 0x14, 0x00, 0x0E });

            Assert.False(frame.IsSintranInfo);
        }

        /// <summary>
        /// A different version/protocol byte is not this protocol, whatever the hop count.
        /// </summary>
        [Fact]
        public void AForeignVersionProtocolByteIsNotSintran()
        {
            LapbFrame frame = ParseInformationFrame(new byte[] { 0x22, 0x13, 0x00, 0x0E });

            Assert.False(frame.IsSintranInfo);
        }

        /// <summary>
        /// The named constants are the carved values, so a future edit cannot drift from the
        /// kernel tables without this failing.
        /// </summary>
        [Fact]
        public void TheCarvedConstantsAreTheKernelValues()
        {
            // X5VRS = 20400 octal = 0x2100; its left-hand byte is the version/protocol byte.
            Assert.Equal(0x2100 >> 8, LapbFrame.SintranVersionProtocol);
            Assert.Equal(0x13, LapbFrame.SintranInitialHopCount);
        }
    }
}
