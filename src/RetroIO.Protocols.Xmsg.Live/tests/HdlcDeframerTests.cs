// =============================================================================
// Characterisation tests for HdlcDeframer.
//
// WHY THIS FILE EXISTS
//
// HdlcDeframer is the receive half of the XMSG framing path and, until
// 2026-09-06, it had no test file of its own - it was only ever exercised as the
// back half of an HdlcEncoder round trip. That is the classic trap: encoder and
// decoder tested only against each other pass every symmetric mistake, so a
// merge that changed the escape policy, the FCS byte order or the flag rules on
// BOTH sides at once would stay green all the way to the wire.
//
// Five HDLC framing implementations in this repository are about to be merged
// into one. These tests therefore drive the deframer with HAND-BUILT wire bytes,
// never with the encoder's output, so they still mean something when the
// implementation underneath is replaced.
//
// Where a test pins behaviour that is arguably wrong, its name and comment say so.
// =============================================================================

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Hdlc;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Live.Tests
{
    public sealed class HdlcDeframerTests
    {
        private const byte Flag = 0x7E;
        private const byte Esc = 0x7D;

        /// <summary>
        /// Appends the FCS to a body, low byte first, without any flags or stuffing.
        /// </summary>
        /// <remarks>
        /// The FCS is the one's complement of the reflected CRC-16 (ITU-T X.25 clause
        /// 2.2.7.4), transmitted low octet first (RFC 1662 section 3.1). Verified in this
        /// repository against the captured SABM anchor: body <c>01 3F 00 64</c> gives
        /// <c>0x092E</c>, on the wire <c>2E 09</c>.
        /// </remarks>
        private static byte[] WithFcs(params byte[] body)
        {
            ushort fcs = (ushort)(Fcs16.Compute(body) ^ 0xFFFF);
            byte[] framed = new byte[body.Length + 2];
            Array.Copy(body, framed, body.Length);
            framed[body.Length] = (byte)(fcs & 0xFF);
            framed[body.Length + 1] = (byte)((fcs >> 8) & 0xFF);
            return framed;
        }

        /// <summary>
        /// Byte-stuffs a frame's content, escaping only the flag and the escape octet.
        /// </summary>
        /// <remarks>
        /// This is the minimum transparency required by ITU-T X.25 clause 2.2.2.2 and it
        /// is what the real ND machines emit: across every capture in this repository the
        /// only octets ever seen after <c>0x7D</c> are <c>0x5D</c> and <c>0x5E</c>. It is
        /// deliberately NOT the RFC 1662 ACCM policy used on the X.25 side of this
        /// repository, which also escapes every octet below <c>0x20</c>.
        /// </remarks>
        private static void StuffInto(List<byte> wire, byte[] content)
        {
            for (int i = 0; i < content.Length; i++)
            {
                byte b = content[i];
                if (b == Flag || b == Esc)
                {
                    wire.Add(Esc);
                    wire.Add((byte)(b ^ 0x20));
                }
                else
                {
                    wire.Add(b);
                }
            }
        }

        [Fact]
        public void StuffedFlagInTheInfoField_UnstuffsBackToTheOriginalOctet()
        {
            // A body containing a literal 0x7E, which must travel as 7D 5E and come back
            // as 0x7E. Get this wrong and the frame either ends early or the payload is
            // silently corrupted.
            byte[] frame = WithFcs(0x09, 0x24, 0x21, 0x7E, 0x13, 0x00);

            List<byte> wire = new List<byte> { Flag };
            StuffInto(wire, frame);
            wire.Add(Flag);

            IReadOnlyList<byte[]> frames = HdlcDeframer.SplitFrames(wire.ToArray());

            Assert.Single(frames);
            Assert.Equal(frame, frames[0]);
            Assert.True(Fcs16.IsValid(frames[0]), "the recovered frame should pass the FCS check");
        }

        [Fact]
        public void StuffedEscapeInTheInfoField_UnstuffsBackToTheOriginalOctet()
        {
            byte[] frame = WithFcs(0x09, 0x8C, 0x7D, 0x21, 0x00, 0x82);

            List<byte> wire = new List<byte> { Flag };
            StuffInto(wire, frame);
            wire.Add(Flag);

            IReadOnlyList<byte[]> frames = HdlcDeframer.SplitFrames(wire.ToArray());

            Assert.Single(frames);
            Assert.Equal(frame, frames[0]);
            Assert.True(Fcs16.IsValid(frames[0]));
        }

        [Fact]
        public void TwoFramesInOneRead_BothRecovered()
        {
            // 1183 reads in the capture corpus carry two or more frames, so this is the
            // ordinary case rather than an edge case.
            byte[] first = WithFcs(0x09, 0x8A, 0x21, 0x13);
            byte[] second = WithFcs(0x09, 0x8C, 0x21, 0x14, 0x00);

            List<byte> wire = new List<byte> { Flag };
            StuffInto(wire, first);
            wire.Add(Flag);
            wire.Add(Flag);
            StuffInto(wire, second);
            wire.Add(Flag);

            IReadOnlyList<byte[]> frames = HdlcDeframer.SplitFrames(wire.ToArray());

            Assert.Equal(2, frames.Count);
            Assert.Equal(first, frames[0]);
            Assert.Equal(second, frames[1]);
        }

        [Fact]
        public void SharedFlagBetweenFrames_OpensTheSecondFrame()
        {
            // ONE flag closing the first frame and opening the second. ITU-T X.25 clause
            // 2.2.1: "A single flag may be used as both the closing flag for one frame and
            // the opening flag for the next frame."
            //
            // HdlcFramer on the X.25 side of this repository gets this wrong and loses the
            // second frame; HdlcDeframer gets it right. The merged framer must keep this
            // answer, not that one.
            byte[] first = WithFcs(0x09, 0x8A, 0x21, 0x13);
            byte[] second = WithFcs(0x09, 0x8C, 0x21, 0x14, 0x00);

            List<byte> wire = new List<byte> { Flag };
            StuffInto(wire, first);
            wire.Add(Flag);
            StuffInto(wire, second);
            wire.Add(Flag);

            IReadOnlyList<byte[]> frames = HdlcDeframer.SplitFrames(wire.ToArray());

            Assert.Equal(2, frames.Count);
            Assert.Equal(first, frames[0]);
            Assert.Equal(second, frames[1]);
        }

        [Fact]
        public void RunsOfFlags_ProduceNoEmptyFrames()
        {
            // Contiguous flags are inter-frame time fill (ITU-T X.25 clause 2.2.4), and
            // the capture corpus contains 7E 7E on 1571 separate reads. A run must never
            // turn into empty frames, and - unlike HdlcFramer - the result must not depend
            // on whether the run length is odd or even.
            byte[] frame = WithFcs(0x09, 0x01, 0x00, 0x64);

            for (int leading = 1; leading <= 4; leading++)
            {
                List<byte> wire = new List<byte>();
                for (int i = 0; i < leading; i++)
                {
                    wire.Add(Flag);
                }
                StuffInto(wire, frame);
                wire.Add(Flag);
                wire.Add(Flag);
                wire.Add(Flag);

                IReadOnlyList<byte[]> frames = HdlcDeframer.SplitFrames(wire.ToArray());

                Assert.Single(frames);
                Assert.Equal(frame, frames[0]);
            }
        }

        [Fact]
        public void RuntBetweenFlags_IsReturnedAndLeftForTheFcsCheckToReject()
        {
            // Two octets cannot be a LAPB frame - the minimum is address, control and two
            // FCS octets (ITU-T X.25 clause 2.3.5.3 b, "fewer than 32 bits between flags";
            // RFC 1662 section 4.3 says the same and requires a silent discard that is NOT
            // counted as an FCS error).
            //
            // HdlcDeframer does not enforce that: it returns whatever lay between the
            // flags, and the callers drop it because Fcs16.IsValid returns false below
            // four octets. Pinning today's split of responsibility so the merge shows any
            // change - if the merged framer starts dropping runts itself, this test fails
            // and somebody decides deliberately.
            IReadOnlyList<byte[]> frames = HdlcDeframer.SplitFrames(new byte[] { Flag, 0x01, 0x02, Flag });

            Assert.Single(frames);
            Assert.Equal(new byte[] { 0x01, 0x02 }, frames[0]);
            Assert.False(Fcs16.IsValid(frames[0]), "a two-octet runt must not pass the FCS check");
        }

        [Fact]
        public void TruncatedTail_IsDeliveredAsAFrame_WHICH_IS_WRONG_FOR_A_STREAM()
        {
            // A read that ends in the middle of a frame. 27% of the reads in the capture
            // corpus are not frame-aligned, and 468 of them contain no flag at all, so
            // this is the normal case on a real link, not a rarity.
            //
            // MEASURED 2026-09-06: SplitFrames treats END OF BUFFER as a frame terminator
            // and hands back the truncated tail as if it were a frame. It is whole-buffer
            // by design and has no way to say "hold this until more arrives" - which is
            // precisely why LiveNode.FeedByte and LapbLayerAdapter.FeedByte each hand-roll
            // a streaming accumulator around it and then re-wrap the result in flags just
            // to call back in here.
            //
            // Nothing breaks today because the fragment fails the FCS check above. But a
            // merged framer must STREAM natively: hold the tail, deliver nothing, and
            // resume on the next read. When it does, this test should report one frame and
            // must be updated - deliberately, by someone who has read this.
            byte[] complete = WithFcs(0x09, 0x01, 0x00, 0x64);

            List<byte> wire = new List<byte> { Flag };
            StuffInto(wire, complete);
            wire.Add(Flag);
            wire.Add(0x09);
            wire.Add(0x02);
            wire.Add(0x00); // cut here: no FCS, no closing flag

            IReadOnlyList<byte[]> frames = HdlcDeframer.SplitFrames(wire.ToArray());

            Assert.Equal(2, frames.Count);
            Assert.Equal(complete, frames[0]);
            Assert.Equal(new byte[] { 0x09, 0x02, 0x00 }, frames[1]);
            Assert.False(Fcs16.IsValid(frames[1]), "the truncated tail must not pass the FCS check");
        }

        [Fact]
        public void EmptyInput_ProducesNoFrames()
        {
            Assert.Empty(HdlcDeframer.SplitFrames(ReadOnlySpan<byte>.Empty));
            Assert.Empty(HdlcDeframer.SplitFrames(new byte[] { Flag, Flag, Flag }));
        }
    }
}
