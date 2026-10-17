//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

// =============================================================================
// Tests for the merged HDLC framer, RetroIO.Link.Hdlc.
//
// This class replaces five separate framing engines. The job of this file is to
// prove it can be BOTH of the two wire formats that were in use, and that it
// fixes the defects the characterisation tests pinned, without inventing any
// third behaviour.
//
// The two decisive tests are the exact-wire-bytes pair:
//
//   MergedEncoder_WithXmsgOptions_ReproducesTheCapturedSabmBytes
//   MergedEncoder_WithX25Options_ReproducesTheCodecWireBytes
//
// One is anchored on a real captured ND frame, the other on the byte vector the
// existing X.25 codec test already asserts. If the merged encoder satisfies both,
// it is a drop-in for both stacks. If it satisfies only one, the merge would have
// silently changed what one of them puts on the wire.
// =============================================================================

using System;
using System.Collections.Generic;

using NUnit.Framework;

using RetroIO.Link.Hdlc;

namespace RetroIO.Protocols.X25.Tests
{
    [TestFixture]
    public sealed class HdlcMergedFramerTests
    {
        private const byte FLAG = 0x7E;

        /// <summary>
        /// Runs a whole buffer through a decoder and collects every frame it reports.
        /// </summary>
        /// <remarks>
        /// This is the caller loop from <see cref="HdlcDecoder.Decode"/>'s own documentation, and
        /// it is what drains several frames out of one read.
        /// </remarks>
        private static List<byte[]> DecodeAll(HdlcDecoder decoder, byte[] input, List<HdlcDecodeStatus>? statuses = null)
        {
            List<byte[]> frames = new List<byte[]>();
            int offset = 0;
            while (offset < input.Length)
            {
                HdlcDecodeStatus status = decoder.Decode(new ReadOnlySpan<byte>(input, offset, input.Length - offset), out int consumed);
                offset += consumed;

                if (status == HdlcDecodeStatus.NeedMoreData)
                {
                    break;
                }

                statuses?.Add(status);
                if (status == HdlcDecodeStatus.FrameComplete || (status == HdlcDecodeStatus.BadFcs && decoder.Frame.Length > 0))
                {
                    frames.Add(decoder.Frame.ToArray());
                }
            }
            return frames;
        }

        // ------------------------------------------------------- the FCS

        [Test]
        public void MergedFcs_MatchesTheVerifiedImplementation()
        {
            // The generated table must equal the hand-transcribed one that is already verified
            // against a bit-by-bit reference and a real captured frame.
            for (int i = 0; i < 512; i++)
            {
                byte[] data = { (byte)i, 0x3F, (byte)(i >> 1), 0x64 };
                Assert.That(HdlcFcs.ComputeTransmitFcs(data),
                    Is.EqualTo(RetroIO.Link.Lapb.Fcs16.Compute(data)),
                    $"transmit FCS differs from the verified implementation for i={i}");
            }
        }

        [Test]
        public void MergedFcs_CapturedSabmAnchor_Is0x092E()
        {
            // Real captured frame: body 01 3F 00 64 has FCS 0x092E, on the wire 2E 09.
            byte[] body = { 0x01, 0x3F, 0x00, 0x64 };

            Assert.That(HdlcFcs.ComputeTransmitFcs(body), Is.EqualTo((ushort)0x092E));
        }

        [Test]
        public void MergedFcs_ResidueOfAValidFrame_IsTheMagicNumber()
        {
            // RFC 1662 PPPGOODFCS16. X.25 clause 2.2.7.4 states the same value bit-reversed as
            // 0001110100001111 = 0x1D0F.
            byte[] frame = { 0x01, 0x3F, 0x00, 0x64, 0x2E, 0x09 };

            Assert.That(HdlcFcs.Update(HdlcFcs.Init, frame), Is.EqualTo(HdlcFcs.Good));
            Assert.That(HdlcFcs.Good, Is.EqualTo((ushort)0xF0B8));
            Assert.That(HdlcFcs.IsValid(frame), Is.True);
        }

        // ------------------------------------------------------- exact wire bytes

        [Test]
        public void MergedEncoder_WithXmsgOptions_ReproducesTheCapturedSabmBytes()
        {
            // THE interop test for the ND side. Body 01 3F 00 64 must go on the wire as
            // 7E 01 3F 00 64 2E 09 7E - note 0x01 and 0x00 travel UNESCAPED, because the real ND
            // machines do not ACCM-escape and the whole capture corpus agrees: the only octets
            // ever seen after 0x7D anywhere in it are 0x5D and 0x5E.
            byte[] body = { 0x01, 0x3F, 0x00, 0x64 };
            byte[] expected = { 0x7E, 0x01, 0x3F, 0x00, 0x64, 0x2E, 0x09, 0x7E };

            byte[] wire = HdlcEncoder.Encode(body, HdlcFramerOptions.Xmsg);

            Assert.That(wire, Is.EqualTo(expected));
        }

        [Test]
        public void MergedEncoder_WithX25Options_ReproducesTheCodecWireBytes()
        {
            // THE interop test for the X.25 side, taken from the vector
            // FramingCodecTests.Hdlc_SendFrame_ProducesExactWireBytes already asserts against the
            // codec being replaced. Only the flag and the escape appear in this payload, so the
            // difference between the two policies does not show here - which is exactly why the
            // control-octet test below exists as well.
            byte[] body = { 0xAA, 0x7E, 0xBB, 0x7D, 0xCC };

            byte[] wire = HdlcEncoder.Encode(body, HdlcFramerOptions.X25);

            // Body portion, before the FCS: 7E AA 7D 5E BB 7D 5D CC ...
            byte[] expectedPrefix = { 0x7E, 0xAA, 0x7D, 0x5E, 0xBB, 0x7D, 0x5D, 0xCC };
            Assert.That(new ReadOnlySpan<byte>(wire, 0, expectedPrefix.Length).ToArray(),
                Is.EqualTo(expectedPrefix));
            Assert.That(wire[wire.Length - 1], Is.EqualTo(FLAG));
        }

        [Test]
        public void MergedEncoder_ControlOctets_AreEscapedOnlyUnderTheX25Policy()
        {
            // The one difference that would silently change a wire format if the merge picked a
            // single behaviour. Same body, two policies, two different lengths.
            byte[] body = { 0x09, 0x00, 0x01, 0x11, 0x13, 0x1F, 0x64 };

            byte[] xmsg = HdlcEncoder.Encode(body, HdlcFramerOptions.Xmsg);
            byte[] x25 = HdlcEncoder.Encode(body, HdlcFramerOptions.X25);

            Assert.Multiple(() =>
            {
                // XMSG: nothing escaped at all, so two flags + body + two FCS octets.
                Assert.That(xmsg.Length, Is.EqualTo(body.Length + 4), "XMSG must not escape control octets");
                Assert.That(Array.IndexOf(xmsg, (byte)0x7D), Is.EqualTo(-1), "XMSG emitted an escape");

                // X.25: five of the seven body octets are below 0x20 and must be escaped.
                Assert.That(x25.Length, Is.GreaterThan(xmsg.Length), "X.25 must ACCM-escape control octets");
                Assert.That(Array.IndexOf(x25, (byte)0x7D), Is.GreaterThanOrEqualTo(0));
            });
        }

        [Test]
        public void MergedEncoder_AnFcsOctetEqualToAFlag_IsStuffed()
        {
            // Transparency is applied after the FCS is computed (X.25 clause 2.2.2.2), so an FCS
            // octet that happens to be 0x7E or 0x7D must itself be escaped. About one frame in 130.
            byte[]? body = null;
            for (int i = 0; i < 4096 && body is null; i++)
            {
                byte[] candidate = { 0x09, 0x01, (byte)(i >> 8), (byte)(i & 0xFF) };
                ushort fcs = HdlcFcs.ComputeTransmitFcs(candidate);
                byte low = (byte)(fcs & 0xFF);
                byte high = (byte)((fcs >> 8) & 0xFF);
                if (low == 0x7E || low == 0x7D || high == 0x7E || high == 0x7D)
                {
                    body = candidate;
                }
            }

            Assert.That(body, Is.Not.Null, "no qualifying body found");

            byte[] wire = HdlcEncoder.Encode(body!, HdlcFramerOptions.Xmsg);

            int flagCount = 0;
            for (int i = 0; i < wire.Length; i++)
            {
                if (wire[i] == FLAG)
                {
                    flagCount++;
                }
            }

            Assert.That(flagCount, Is.EqualTo(2), "a stuffed FCS octet must not look like a frame boundary");
            Assert.That(wire.Length, Is.GreaterThan(body!.Length + 4), "the FCS octet was not stuffed");
        }

        // ------------------------------------------------------- the fixed defects

        [Test]
        public void MergedDecoder_SharedFlag_OpensTheNextFrame()
        {
            // The defect HdlcFramer had. ITU-T X.25 clause 2.2.1: "A single flag may be used as
            // both the closing flag for one frame and the opening flag for the next frame."
            using HdlcDecoder decoder = new HdlcDecoder(HdlcFramerOptions.Xmsg);

            byte[] a = HdlcEncoder.Encode(new byte[] { 0x11, 0x22, 0x33, 0x44 }, HdlcFramerOptions.Xmsg);
            byte[] b = HdlcEncoder.Encode(new byte[] { 0x55, 0x66, 0x77, 0x88 }, HdlcFramerOptions.Xmsg);

            // Drop a's closing flag so b's opening flag serves for both.
            byte[] shared = new byte[a.Length - 1 + b.Length];
            Array.Copy(a, shared, a.Length - 1);
            Array.Copy(b, 0, shared, a.Length - 1, b.Length);

            List<byte[]> frames = DecodeAll(decoder, shared);

            Assert.That(frames.Count, Is.EqualTo(2), "a shared flag must open the following frame");
            Assert.That(StripFcs(frames[0]), Is.EqualTo(new byte[] { 0x11, 0x22, 0x33, 0x44 }));
            Assert.That(StripFcs(frames[1]), Is.EqualTo(new byte[] { 0x55, 0x66, 0x77, 0x88 }));
        }

        [Test]
        public void MergedDecoder_FlagRunParity_DoesNotMatter()
        {
            // The other HdlcFramer defect: a run of flags used to alternate its state, so whether
            // the next frame was seen depended on whether the run length was odd or even. Inter-
            // frame time fill IS a run of flags (X.25 clause 2.2.4).
            byte[] frame = HdlcEncoder.Encode(new byte[] { 0x11, 0x22, 0x33, 0x44 }, HdlcFramerOptions.Xmsg);

            for (int leading = 0; leading <= 5; leading++)
            {
                using HdlcDecoder decoder = new HdlcDecoder(HdlcFramerOptions.Xmsg);

                byte[] wire = new byte[leading + frame.Length];
                for (int i = 0; i < leading; i++)
                {
                    wire[i] = FLAG;
                }
                Array.Copy(frame, 0, wire, leading, frame.Length);

                List<byte[]> frames = DecodeAll(decoder, wire);

                Assert.That(frames.Count, Is.EqualTo(1), $"failed with {leading} leading flags");
                Assert.That(StripFcs(frames[0]), Is.EqualTo(new byte[] { 0x11, 0x22, 0x33, 0x44 }));
            }
        }

        [Test]
        public void MergedDecoder_SplitAcrossEveryPossibleBoundary_StillDeliversOnce()
        {
            // 27% of reads in the capture corpus are not frame-aligned. Feeding one octet at a
            // time exercises every split point at once, including the one between an escape and
            // the octet it escapes.
            byte[] body = { 0xAA, 0x7E, 0xBB, 0x7D, 0xCC };
            byte[] wire = HdlcEncoder.Encode(body, HdlcFramerOptions.Xmsg);

            using HdlcDecoder decoder = new HdlcDecoder(HdlcFramerOptions.Xmsg);

            List<byte[]> frames = new List<byte[]>();
            for (int i = 0; i < wire.Length; i++)
            {
                HdlcDecodeStatus status = decoder.Decode(new ReadOnlySpan<byte>(wire, i, 1), out int consumed);
                Assert.That(consumed, Is.EqualTo(1));
                if (status == HdlcDecodeStatus.FrameComplete)
                {
                    frames.Add(decoder.Frame.ToArray());
                }
            }

            Assert.That(frames.Count, Is.EqualTo(1));
            Assert.That(StripFcs(frames[0]), Is.EqualTo(body));
        }

        [Test]
        public void MergedDecoder_TruncatedTail_IsHeldNotDelivered()
        {
            // The defect HdlcDeframer had: it treated end of buffer as a frame terminator and
            // handed back the truncated tail as though it were a frame. A streaming decoder must
            // hold it and resume on the next read.
            byte[] complete = HdlcEncoder.Encode(new byte[] { 0x11, 0x22, 0x33, 0x44 }, HdlcFramerOptions.Xmsg);

            byte[] wire = new byte[complete.Length + 3];
            Array.Copy(complete, wire, complete.Length);
            wire[complete.Length] = 0x09;
            wire[complete.Length + 1] = 0x02;
            wire[complete.Length + 2] = 0x00; // cut here: no FCS, no closing flag

            using HdlcDecoder decoder = new HdlcDecoder(HdlcFramerOptions.Xmsg);
            List<byte[]> frames = DecodeAll(decoder, wire);

            Assert.That(frames.Count, Is.EqualTo(1), "the truncated tail must not be delivered");
            Assert.That(StripFcs(frames[0]), Is.EqualTo(new byte[] { 0x11, 0x22, 0x33, 0x44 }));
        }

        // ------------------------------------------------------- error statuses

        [Test]
        public void MergedDecoder_Runt_IsReportedSeparatelyFromAnFcsError()
        {
            // RFC 1662 sections 4.3 and 5.3: a runt is discarded silently and explicitly NOT
            // counted as an FCS error.
            using HdlcDecoder decoder = new HdlcDecoder(HdlcFramerOptions.Xmsg);

            List<HdlcDecodeStatus> statuses = new List<HdlcDecodeStatus>();
            DecodeAll(decoder, new byte[] { FLAG, 0x01, 0x02, FLAG }, statuses);

            Assert.That(statuses, Does.Contain(HdlcDecodeStatus.Runt));
            Assert.That(decoder.Statistics.Runts, Is.EqualTo(1));
            Assert.That(decoder.Statistics.FcsErrors, Is.EqualTo(0), "a runt must not be counted as an FCS error");
        }

        [Test]
        public void MergedDecoder_BadFcs_IsDroppedByDefaultAndCounted()
        {
            byte[] wire = HdlcEncoder.Encode(new byte[] { 0x11, 0x22, 0x33, 0x44 }, HdlcFramerOptions.Xmsg);
            wire[2] ^= 0xFF;

            using HdlcDecoder decoder = new HdlcDecoder(HdlcFramerOptions.Xmsg);
            List<HdlcDecodeStatus> statuses = new List<HdlcDecodeStatus>();
            List<byte[]> frames = DecodeAll(decoder, wire, statuses);

            Assert.That(statuses, Does.Contain(HdlcDecodeStatus.BadFcs));
            Assert.That(frames.Count, Is.EqualTo(0), "a corrupt frame must not be delivered by default");
            Assert.That(decoder.Statistics.FcsErrors, Is.EqualTo(1));
        }

        [Test]
        public void MergedDecoder_BadFcs_IsDeliveredWhenTheOptionIsSet()
        {
            // The old HdlcFramer raised bad frames with FcsValid=false and its callers counted
            // them. An analyser wants the same, so the capability survives - behind an option.
            byte[] wire = HdlcEncoder.Encode(new byte[] { 0x11, 0x22, 0x33, 0x44 }, HdlcFramerOptions.Xmsg);
            wire[2] ^= 0xFF;

            HdlcFramerOptions options = HdlcFramerOptions.Xmsg with { DeliverCorruptFrames = true };
            using HdlcDecoder decoder = new HdlcDecoder(options);
            List<byte[]> frames = DecodeAll(decoder, wire);

            Assert.That(frames.Count, Is.EqualTo(1), "the corrupt frame should have been delivered");
        }

        [Test]
        public void MergedDecoder_Abort_IsNotAnFcsError()
        {
            // X.25 clause 2.3.5.3 f: on an octet-stuffed link, a control escape immediately
            // followed by a flag aborts the frame. RFC 1662 section 4.3 requires a silent discard
            // that is not counted as an FCS error.
            using HdlcDecoder decoder = new HdlcDecoder(HdlcFramerOptions.Xmsg);

            byte[] wire = { FLAG, 0x11, 0x22, 0x33, 0x7D, FLAG };
            List<HdlcDecodeStatus> statuses = new List<HdlcDecodeStatus>();
            DecodeAll(decoder, wire, statuses);

            Assert.That(statuses, Does.Contain(HdlcDecodeStatus.Aborted));
            Assert.That(decoder.Statistics.Aborts, Is.EqualTo(1));
            Assert.That(decoder.Statistics.FcsErrors, Is.EqualTo(0), "an abort must not be counted as an FCS error");
        }

        [Test]
        public void MergedDecoder_OverlongFrame_IsDiscardedAndTheLinkResyncs()
        {
            // Two of the five framers this replaces had no cap at all: a peer that opened a frame
            // and never closed it grew a List<byte> without bound. The guard fires as the octet is
            // unstuffed, not when the frame closes.
            HdlcFramerOptions options = HdlcFramerOptions.Xmsg with { MaxFrameLength = 64 };
            using HdlcDecoder decoder = new HdlcDecoder(options);

            List<byte> wire = new List<byte> { FLAG };
            for (int i = 0; i < 200; i++)
            {
                wire.Add(0x41);
            }
            wire.Add(FLAG);

            byte[] good = HdlcEncoder.Encode(new byte[] { 0x11, 0x22, 0x33, 0x44 }, options);
            wire.AddRange(good);

            List<HdlcDecodeStatus> statuses = new List<HdlcDecodeStatus>();
            List<byte[]> frames = DecodeAll(decoder, wire.ToArray(), statuses);

            Assert.That(statuses, Does.Contain(HdlcDecodeStatus.Overlong));
            Assert.That(decoder.Statistics.Overlong, Is.EqualTo(1));

            // And the link recovers: the frame after the oversized one is delivered.
            Assert.That(frames.Count, Is.EqualTo(1), "the decoder did not resynchronise after an overlong frame");
            Assert.That(StripFcs(frames[0]), Is.EqualTo(new byte[] { 0x11, 0x22, 0x33, 0x44 }));
        }

        [Test]
        public void MergedDecoder_FlagFill_IsCountedAsIdleNotAsFrames()
        {
            using HdlcDecoder decoder = new HdlcDecoder(HdlcFramerOptions.Xmsg);

            DecodeAll(decoder, new byte[] { FLAG, FLAG, FLAG, FLAG });

            Assert.That(decoder.Statistics.IdleFlags, Is.EqualTo(4));
            Assert.That(decoder.Statistics.GoodFrames, Is.EqualTo(0));
            Assert.That(decoder.Statistics.Runts, Is.EqualTo(0), "time fill must never be reported as a runt");
        }

        // ------------------------------------------------------- round trips

        [Test]
        public void MergedFramer_RoundTrip_AllByteValues_UnderBothPolicies()
        {
            byte[] body = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                body[i] = (byte)i;
            }

            HdlcFramerOptions[] policies = { HdlcFramerOptions.Xmsg, HdlcFramerOptions.X25 };
            for (int p = 0; p < policies.Length; p++)
            {
                byte[] wire = HdlcEncoder.Encode(body, policies[p]);

                using HdlcDecoder decoder = new HdlcDecoder(policies[p]);
                List<byte[]> frames = DecodeAll(decoder, wire);

                Assert.That(frames.Count, Is.EqualTo(1), $"policy {p}");
                Assert.That(StripFcs(frames[0]), Is.EqualTo(body), $"policy {p}");
            }
        }

        [Test]
        public void MergedDecoder_AcceptsTheOtherPolicysEscaping()
        {
            // A receiver must accept either escape policy, because unescaping is a blind XOR 0x20.
            // This is what makes the SendAccm option safe: only transmit differs.
            byte[] body = { 0x09, 0x00, 0x01, 0x11, 0x64 };
            byte[] x25Wire = HdlcEncoder.Encode(body, HdlcFramerOptions.X25);

            using HdlcDecoder decoder = new HdlcDecoder(HdlcFramerOptions.Xmsg);
            List<byte[]> frames = DecodeAll(decoder, x25Wire);

            Assert.That(frames.Count, Is.EqualTo(1));
            Assert.That(StripFcs(frames[0]), Is.EqualTo(body));
        }

        [Test]
        public void MergedDecoder_SeveralFramesInOneRead_AllDelivered()
        {
            using HdlcDecoder decoder = new HdlcDecoder(HdlcFramerOptions.Xmsg);

            byte[] a = HdlcEncoder.Encode(new byte[] { 0x11, 0x22, 0x33, 0x44 }, HdlcFramerOptions.Xmsg);
            byte[] b = HdlcEncoder.Encode(new byte[] { 0x55, 0x66, 0x77, 0x88 }, HdlcFramerOptions.Xmsg);
            byte[] c = HdlcEncoder.Encode(new byte[] { 0x99, 0xAA, 0xBB, 0xCC }, HdlcFramerOptions.Xmsg);

            byte[] all = new byte[a.Length + b.Length + c.Length];
            Array.Copy(a, 0, all, 0, a.Length);
            Array.Copy(b, 0, all, a.Length, b.Length);
            Array.Copy(c, 0, all, a.Length + b.Length, c.Length);

            List<byte[]> frames = DecodeAll(decoder, all);

            Assert.That(frames.Count, Is.EqualTo(3));
            Assert.That(StripFcs(frames[2]), Is.EqualTo(new byte[] { 0x99, 0xAA, 0xBB, 0xCC }));
        }

        [Test]
        public void MergedDecoder_BitStuffing_IsRefusedRatherThanFaked()
        {
            // Nothing here is bit-synchronous and the whole capture corpus is octet-stuffed.
            // Shipping an untested bit-stuffing engine would be worse than refusing.
            HdlcFramerOptions options = HdlcFramerOptions.Xmsg with { Transparency = HdlcTransparency.BitStuffed };

            Assert.Throws<NotSupportedException>(() => new HdlcDecoder(options));
        }

        private static byte[] StripFcs(byte[] frameWithFcs)
        {
            byte[] body = new byte[frameWithFcs.Length - 2];
            Array.Copy(frameWithFcs, body, body.Length);
            return body;
        }
    }
}
