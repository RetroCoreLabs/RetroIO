using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Hdlc;
using RetroIO.Protocols.Xmsg.Live;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Live.Tests
{
    /// <summary>
    /// Layer 1 proof: HDLC send-framing is the exact byte-for-byte inverse of the
    /// receive-side de-framer, anchored on the captured SABM frame.
    /// </summary>
    public sealed class HdlcEncoderTests
    {
        /// <summary>
        /// The captured SABM body (node 100) must encode to the exact on-wire bytes,
        /// including FCS <c>0x092E</c> stored low-first and the two flags.
        /// </summary>
        [Fact]
        public void SabmAnchor_EncodesToExactOnWireBytes()
        {
            // VERIFIED anchor (XMSG-PROTOCOL.md section 2): body 01 3F 00 64,
            // FCS 0x092E -> wire 7E 01 3F 00 64 2E 09 7E.
            byte[] body = { 0x01, 0x3F, 0x00, 0x64 };
            byte[] expected = { 0x7E, 0x01, 0x3F, 0x00, 0x64, 0x2E, 0x09, 0x7E };

            byte[] wire = HdlcEncoder.Encode(body);

            Assert.Equal(expected, wire);
        }

        /// <summary>
        /// Encoding then de-framing a set of LAPB bodies returns the identical body and the
        /// reconstructed frame passes the FCS-16 check.
        /// </summary>
        [Fact]
        public void EncodeThenDeframe_RoundTrips_AndPassesFcs()
        {
            // A spread of realistic bodies: SABM, UA, RR, and a full SINTRAN I-frame body
            // (LAPB data address 0x09 + control + the captured reachability-request info).
            byte[][] bodies =
            {
                new byte[] { 0x01, 0x3F, 0x00, 0x64 },                                   // SABM node 100
                new byte[] { 0x01, 0x73, 0x00, 0x66 },                                   // UA node 102
                new byte[] { 0x09, 0x01, 0x00, 0x67 },                                   // RR node 103
                LiveTestHex.Parse("09 00 21 13 00 19 00 66 00 64 FF FF 00 01 DE 08"),    // I-frame
            };

            for (int i = 0; i < bodies.Length; i++)
            {
                byte[] body = bodies[i];
                byte[] wire = HdlcEncoder.Encode(body);

                IReadOnlyList<byte[]> frames = HdlcDeframer.SplitFrames(wire);
                Assert.Single(frames);

                byte[] reconstructed = frames[0];

                // The reconstructed frame = body + 2 FCS bytes and must validate.
                Assert.True(Fcs16.IsValid(reconstructed));
                Assert.Equal(body.Length + 2, reconstructed.Length);

                // Stripping the FCS yields the original body byte-for-byte.
                byte[] recoveredBody = new byte[body.Length];
                Array.Copy(reconstructed, recoveredBody, body.Length);
                Assert.Equal(body, recoveredBody);
            }
        }

        /// <summary>
        /// A body containing bytes that collide with the flag/escape must be byte-stuffed
        /// in the output and still round-trip cleanly.
        /// </summary>
        [Fact]
        public void EncodeBodyWithFlagAndEscape_StuffsCorrectly_AndRoundTrips()
        {
            // Body deliberately contains 0x7E and 0x7D so stuffing is exercised.
            byte[] body = { 0x09, 0x20, 0x7E, 0x7D, 0x00 };

            byte[] wire = HdlcEncoder.Encode(body);

            // The 0x7E in the body must appear as the escape sequence 0x7D 0x5E, and the
            // 0x7D as 0x7D 0x5D, somewhere between the two flags.
            Assert.True(ContainsSequence(wire, new byte[] { 0x7D, 0x5E }), "expected 0x7D 0x5E for stuffed 0x7E");
            Assert.True(ContainsSequence(wire, new byte[] { 0x7D, 0x5D }), "expected 0x7D 0x5D for stuffed 0x7D");

            // Exactly two flags: one opening, one closing (no stray flag leaked into body).
            int flagCount = 0;
            for (int i = 0; i < wire.Length; i++)
            {
                if (wire[i] == 0x7E)
                {
                    flagCount++;
                }
            }

            Assert.Equal(2, flagCount);

            // Round-trip recovers the original body.
            IReadOnlyList<byte[]> frames = HdlcDeframer.SplitFrames(wire);
            Assert.Single(frames);
            Assert.True(Fcs16.IsValid(frames[0]));

            byte[] recoveredBody = new byte[body.Length];
            Array.Copy(frames[0], recoveredBody, body.Length);
            Assert.Equal(body, recoveredBody);
        }

        /// <summary>
        /// The exact logged bodies of the live username-response burst (ns=5 routing ack, ns=6
        /// username-accepted, ns=7 password prompt). 100 accepted ns=5 and ns=7 but sent REJ nr=6,
        /// so it treated ns=6 as not-received. This proves whether our ENCODER produced a valid HDLC
        /// frame for ns=6 (good FCS, clean stuffing) - if it round-trips, the corruption is NOT in the
        /// encoder and 100's rejection is at a higher level or in the transmit path.
        /// </summary>
        [Fact]
        public void LiveUsernameBurst_AllThreeFrames_AreValidHdlc()
        {
            string[] bodiesHex =
            {
                "09CA211300030064006600050001DE19",                                                                    // ns=5 routing ack (accepted)
                "09CC2113000E0064006600070108DD0521009600006402AA0066021101080000000B01020D0A130200030E0100",          // ns=6 username-accepted (REJ'd)
                "09CE2113000E0064006600080108DD0421009600006402AA00660211010800000012010A50415353574F52443A20000301FF0200", // ns=7 password prompt (accepted)
            };

            for (int i = 0; i < bodiesHex.Length; i++)
            {
                byte[] body = Convert.FromHexString(bodiesHex[i]);
                byte[] wire = HdlcEncoder.Encode(body);

                IReadOnlyList<byte[]> frames = HdlcDeframer.SplitFrames(wire);
                Assert.Single(frames);
                Assert.True(Fcs16.IsValid(frames[0]), $"frame index {i} produced an INVALID FCS");

                byte[] recovered = new byte[body.Length];
                Array.Copy(frames[0], recovered, body.Length);
                Assert.Equal(body, recovered);
            }
        }

        /// <summary>
        /// Searches a byte array for a contiguous subsequence.
        /// </summary>
        /// <param name="haystack">
        /// The bytes to search.
        /// </param>
        /// <param name="needle">
        /// The subsequence to find.
        /// </param>
        /// <returns>
        /// <c>true</c> when <paramref name="needle"/> occurs in <paramref name="haystack"/>.
        /// </returns>
        private static bool ContainsSequence(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Octets below <c>0x20</c> must travel UNESCAPED on the XMSG wire.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the single most dangerous divergence in the framing merge. There are two
        /// escape policies in this repository and they produce different wire bytes for the
        /// same LAPB body:
        /// </para>
        /// <list type="bullet">
        ///   <item>this encoder escapes ONLY the flag and the escape octet, which is the
        ///   minimum required by ITU-T X.25 clause 2.2.2.2;</item>
        ///   <item>the X.25 side (HdlcFramer, HdlcFrameCodec) additionally escapes every
        ///   octet below <c>0x20</c> - the RFC 1662 section 7.1 default ACCM.</item>
        /// </list>
        /// <para>
        /// The measurement settles which one belongs here: across every capture in this
        /// repository, the only octets ever following <c>0x7D</c> are <c>0x5D</c> and
        /// <c>0x5E</c>. The real ND machines do not ACCM-escape. If the merged framer
        /// adopts the wider policy by default, every XMSG frame changes on the wire.
        /// </para>
        /// <para>
        /// The existing SABM anchor test covers this only by luck, because its body
        /// happens to contain one <c>0x00</c>. This test covers it on purpose, with several
        /// control octets in the information field.
        /// </para>
        /// </remarks>
        [Fact]
        public void ControlOctetsBelow0x20_AreNotEscaped()
        {
            // 0x00, 0x01, 0x11 (XON), 0x13 (XOFF) and 0x1F are all in the RFC 1662 default
            // ACCM. None of them may be escaped here. No 0x7E or 0x7D anywhere, so the
            // body itself needs no transparency at all.
            byte[] body = { 0x09, 0x00, 0x01, 0x11, 0x13, 0x1F, 0x64 };

            byte[] wire = HdlcEncoder.Encode(body);

            // Every body octet appears verbatim, in order, between the flags.
            Assert.Equal(0x7E, wire[0]);
            Assert.Equal(0x7E, wire[wire.Length - 1]);
            for (int i = 0; i < body.Length; i++)
            {
                Assert.Equal(body[i], wire[1 + i]);
            }

            // And the frame is exactly two flags + body + two FCS octets. Any escaping at
            // all would make it longer. This assertion is what actually fails if somebody
            // switches the policy.
            Assert.Equal(body.Length + 4, wire.Length);

            // Belt and braces: no escape octet was emitted.
            Assert.DoesNotContain((byte)0x7D, wire);
        }

        /// <summary>
        /// An FCS octet that happens to equal a flag or an escape must itself be stuffed.
        /// </summary>
        /// <remarks>
        /// A classic HDLC bug: escape the body in one pass, then append the raw FCS. It
        /// works until an FCS octet lands on <c>0x7E</c> or <c>0x7D</c>, which happens for
        /// roughly one frame in 130 - often enough to be a maddening intermittent fault and
        /// rare enough to survive a casual round-trip test. ITU-T X.25 clause 2.2.2.2 puts
        /// the transparency step "following completion of the FCS calculation", so the FCS
        /// is inside the escaped region.
        /// </remarks>
        [Fact]
        public void AnFcsOctetEqualToAFlag_IsStuffed()
        {
            // Search for a body whose FCS contains 0x7E or 0x7D rather than hoping a
            // hand-picked one does. Deterministic: the same body is found every run.
            byte[] body = null!;
            for (int i = 0; i < 4096; i++)
            {
                byte[] candidate = { 0x09, 0x01, (byte)(i >> 8), (byte)(i & 0xFF) };
                ushort fcs = (ushort)(Fcs16.Compute(candidate) ^ 0xFFFF);
                byte low = (byte)(fcs & 0xFF);
                byte high = (byte)((fcs >> 8) & 0xFF);
                if (low == 0x7E || low == 0x7D || high == 0x7E || high == 0x7D)
                {
                    body = candidate;
                    break;
                }
            }

            Assert.NotNull(body);

            byte[] wire = HdlcEncoder.Encode(body);

            // The frame must be longer than the unescaped form, proving the FCS octet was
            // stuffed rather than written raw...
            Assert.True(wire.Length > body.Length + 4, "the FCS octet was not stuffed");

            // ...and it must still de-frame back to the original body with a valid FCS.
            IReadOnlyList<byte[]> frames = HdlcDeframer.SplitFrames(wire);
            Assert.Single(frames);
            Assert.True(Fcs16.IsValid(frames[0]));
            Assert.Equal(body, frames[0][..body.Length]);

            // And exactly two flags: the stuffed FCS octet must not look like a frame end.
            int flagCount = 0;
            for (int i = 0; i < wire.Length; i++)
            {
                if (wire[i] == 0x7E)
                {
                    flagCount++;
                }
            }
            Assert.Equal(2, flagCount);
        }

    }
}
