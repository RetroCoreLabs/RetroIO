//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Node.Seam;

using Xunit;
using Xunit.Abstractions;
using static RetroIO.Protocols.Xmsg.TestSupport.TestHex;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Checks our relay against a real ND machine's own relay output, byte for byte.
    /// </summary>
    /// <remarks>
    /// <para>Why this exists beside <see cref="DatagramRelayTests"/></para>
    /// Those tests build their own datagram and then assert our rule against it, so they can only
    /// ever agree with our idea of the format. That is the exact shape of test that let four
    /// defects survive a fully green suite in this project. The expected values here are not ours:
    /// they are what a real D100 actually emitted.
    /// <para><b>The capture</b></para>
    /// <c>E:\Dev\Ronny\X25Emulator\pcap\ethernet-hdlc-ROUTE-THROUGH-WORKING-102-via-100-to-103-2026-08-01.pcapng</c>
    /// holds BOTH links of a live route-through: D102 and D103 talking through D100, one on the
    /// Ethernet segment and one on an HDLC line. So the same datagram appears twice - once arriving
    /// at the relay and once leaving it - and the pair is a complete oracle for the relay rule.
    /// <para>
    /// The four pairs below cover both directions and both transport crossings. Byte offsets are
    /// the TCP payloads; the 802.3 or LAPB framing around them has been stripped, because the relay
    /// rule is about the SINTRAN datagram and nothing below it.
    /// </para>
    /// </remarks>
    public sealed class DatagramRelayCaptureTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public DatagramRelayCaptureTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// Ethernet to HDLC: D102 to D103, capture frames 3 (in) and 9 (out).
        /// </summary>
        private const string In1 =
            "2113000e00670066001b0400d9f6"
            + "210086e4006700000066073b040000410010ff072a54414441444d00fe0444313033";

        private const string Out1 =
            "2112000e00670066001b0400d9f7"
            + "210086e4006700000066073b040000410010ff072a54414441444d00fe0444313033";

        /// <summary>
        /// HDLC to Ethernet: D103 to D102, capture frames 11 (in) and 19 (out).
        /// </summary>
        private const string In2 =
            "2113000e0066006700200400d9f1"
            + "210086400066073b006701600400004100080102000102020009";

        private const string Out2 =
            "2112000e0066006700200400d9f2"
            + "210086400066073b006701600400004100080102000102020009";

        /// <summary>
        /// HDLC to Ethernet: D103 to D102, capture frames 51 (in) and 63 (out).
        /// </summary>
        private const string In3 =
            "2113000e0066006700210400d9f0"
            + "210086400066073b0067016004000000001800070500006703341f034c0000000b02030215020108ff00";

        private const string Out3 =
            "2112000e0066006700210400d9f1"
            + "210086400066073b0067016004000000001800070500006703341f034c0000000b02030215020108ff00";

        /// <summary>
        /// HDLC to Ethernet, the carry case: checksum <c>dce7</c> to <c>dce8</c>, frames 51 and 67.
        /// </summary>
        private const string In4 =
            "2113000e0066006700220108dce7"
            + "210092000066073b006703340108000000021800";

        private const string Out4 =
            "2112000e0066006700220108dce8"
            + "210092000066073b006703340108000000021800";

        /// <summary>
        /// Our relay reproduces the real machine's output exactly, on every captured pair.
        /// </summary>
        [Theory]
        [InlineData(In1, Out1, "Ethernet to HDLC, 102 to 103")]
        [InlineData(In2, Out2, "HDLC to Ethernet, 103 to 102")]
        [InlineData(In3, Out3, "HDLC to Ethernet, 103 to 102, longer body")]
        [InlineData(In4, Out4, "HDLC to Ethernet, checksum carry dce7 to dce8")]
        public void RelayingACapturedDatagramReproducesTheRealOutput(
            string arrivedHex, string expectedHex, string what)
        {
            byte[] arrived = FromHex(arrivedHex);
            byte[] expected = FromHex(expectedHex);

            // Guard: the pair must be the SAME datagram, or the comparison proves nothing. A
            // mistyped byte in either constant fails here as itself rather than as a relay bug.
            Assert.Equal(expected.Length, arrived.Length);

            byte[]? actual = SintranDatagramRelay.ToRelayed(new ReadOnlySpan<byte>(arrived));

            Assert.NotNull(actual);
            _output.WriteLine(what + ": " + ToHex(actual!));
            Assert.Equal(expected, actual!);
        }

        /// <summary>
        /// The body is carried through untouched, which is what keeps acknowledgements end-to-end.
        /// </summary>
        /// <remarks>
        /// Stated separately from the byte-equality test because it is the property that MATTERS:
        /// a relay that rewrote endpoints or resequenced anything would break the end-to-end ACK,
        /// and the captured evidence is that a real relay touches neither.
        /// </remarks>
        [Fact]
        public void EverythingAfterWordZeroAndTheChecksumIsUntouched()
        {
            byte[] arrived = FromHex(In1);
            byte[] relayed = SintranDatagramRelay.ToRelayed(new ReadOnlySpan<byte>(arrived))!;

            // Word 0 low byte and word 6 are the only two the rule may change.
            for (int i = 0; i < arrived.Length; i++)
            {
                bool mayChange = i == 1 || i == 12 || i == 13;

                if (!mayChange)
                {
                    Assert.Equal(arrived[i], relayed[i]);
                }
            }

            // Endpoints specifically: destination 103, source 102 - the ORIGINAL pair, not ours.
            Assert.Equal(0x00, relayed[4]);
            Assert.Equal(0x67, relayed[5]);
            Assert.Equal(0x00, relayed[6]);
            Assert.Equal(0x66, relayed[7]);
        }

        /// <summary>
        /// The checksum rises by exactly one, because word 0 fell by exactly one.
        /// </summary>
        /// <remarks>
        /// This is what retro-explains the old and wrong note that a relay "re-stamps a counter".
        /// There is no counter: <c>0x2113</c> to <c>0x2112</c> drops the ones-complement sum by 1,
        /// so its complement rises by 1. The fourth pair carries it across a byte boundary.
        /// </remarks>
        [Theory]
        [InlineData(In1, Out1)]
        [InlineData(In2, Out2)]
        [InlineData(In3, Out3)]
        [InlineData(In4, Out4)]
        public void TheChecksumRisesByExactlyOne(string arrivedHex, string expectedHex)
        {
            byte[] arrived = FromHex(arrivedHex);
            byte[] expected = FromHex(expectedHex);

            int before = (arrived[12] << 8) | arrived[13];
            int after = (expected[12] << 8) | expected[13];

            Assert.Equal((before + 1) & 0xFFFF, after);
        }

        /// <summary>
        /// Converts bytes to a hex string, for failure output.
        /// </summary>
        /// <param name="bytes">
        /// The bytes to render.
        /// </param>
        /// <returns>
        /// The hex text.
        /// </returns>
        private static string ToHex(byte[] bytes)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder(bytes.Length * 2);

            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("x2"));
            }

            return builder.ToString();
        }
    }
}
