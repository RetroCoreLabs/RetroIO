// =============================================================================
// Characterisation tests for HdlcFramer.
//
// WHY THIS FILE EXISTS
//
// HdlcFramer has three production callers - SerialLink, TcpLapbLink and XotLink
// (Cisco mode) - and until 2026-09-06 it had NO tests of its own at all. It is
// one of five separate HDLC framing implementations in this repository, and it
// is about to be merged into a single shared class.
//
// These tests were written BEFORE the merge, to pin what the code did at the
// time, and two of them deliberately asserted behaviour that was wrong: a shared
// flag lost the following frame, and a run of flags decided by its parity
// whether the next frame was seen at all.
//
// Both went red when HdlcFramer was reimplemented on the shared engine on
// 2026-09-06, which is exactly what they were for - the change showed up as a
// failing test somebody had to look at, rather than as a silent change of what
// goes on the wire. They have been updated to assert the fixed behaviour, and
// each carries a comment recording what it used to say and why.
// =============================================================================

using System;
using System.Collections.Generic;

using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RetroIO.Link;
using RetroIO.Link.Lapb;

namespace RetroIO.Protocols.X25.Tests
{
    [TestFixture]
    public sealed class HdlcFramerTests
    {
        private const byte FLAG = 0x7E;
        private const byte ESC = 0x7D;

        /// <summary>
        /// Builds a framer and captures everything it emits and every frame it reports.
        /// </summary>
        private static HdlcFramer CreateFramer(List<byte> sent, List<FrameEventArgs> received)
        {
            HdlcFramer framer = new HdlcFramer(NullLogger.Instance, "test");

            framer.BytesToSend += (s, bytes) =>
            {
                for (int i = 0; i < bytes.Length; i++)
                {
                    sent.Add(bytes[i]);
                }
            };

            framer.FrameReceived += (s, a) => received.Add(a);
            return framer;
        }

        /// <summary>
        /// Wraps a payload as a valid on-the-wire frame: FCS appended, then byte
        /// stuffing, then flags.
        /// </summary>
        /// <remarks>
        /// Built here rather than by calling <see cref="HdlcFramer.SendFrame"/>, so that a
        /// decode test is not merely checking the framer against itself. The FCS comes
        /// from <see cref="Fcs16"/>, which is independently verified against both the
        /// bit-by-bit method and a real captured frame.
        /// <para>
        /// Escapes only the flag and the escape octet - the MINIMUM required. HdlcFramer
        /// itself escapes more than this (every octet below 0x20); a receiver must accept
        /// either, because unescaping is a blind XOR 0x20.
        /// </para>
        /// </remarks>
        private static byte[] WrapFrame(byte[] payload)
        {
            byte[] withFcs = new byte[payload.Length + 2];
            Array.Copy(payload, withFcs, payload.Length);
            Fcs16.WriteFcs(payload, withFcs.AsSpan(payload.Length));

            List<byte> wire = new List<byte>(withFcs.Length + 8);
            wire.Add(FLAG);
            for (int i = 0; i < withFcs.Length; i++)
            {
                byte b = withFcs[i];
                if (b == FLAG || b == ESC)
                {
                    wire.Add(ESC);
                    wire.Add((byte)(b ^ 0x20));
                }
                else
                {
                    wire.Add(b);
                }
            }
            wire.Add(FLAG);
            return wire.ToArray();
        }

        // ---------------------------------------------------------------- encode

        [Test]
        public void HdlcFramer_SendFrame_EscapesControlOctetsBelow0x20()
        {
            List<byte> sent = new List<byte>();
            HdlcFramer framer = CreateFramer(sent, new List<FrameEventArgs>());

            // 0x01 and 0x00 are both below 0x20; 0x3F and 0x64 are not.
            framer.SendFrame(new byte[] { 0x01, 0x3F, 0x00, 0x64 }, 4);

            byte[] wire = sent.ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(wire[0], Is.EqualTo(FLAG), "no opening flag");
                Assert.That(wire[wire.Length - 1], Is.EqualTo(FLAG), "no closing flag");

                // 0x01 -> 7D 21, 0x00 -> 7D 20. This is the RFC 1662 ACCM behaviour, and
                // it is one of the two escape policies in this repository - the XMSG side
                // escapes ONLY 7E and 7D. The merged framer must keep both selectable.
                Assert.That(IndexOfPair(wire, ESC, 0x21), Is.GreaterThanOrEqualTo(0),
                    "0x01 was not escaped as 7D 21");
                Assert.That(IndexOfPair(wire, ESC, 0x20), Is.GreaterThanOrEqualTo(0),
                    "0x00 was not escaped as 7D 20");
                Assert.That(Array.IndexOf(wire, (byte)0x3F), Is.GreaterThanOrEqualTo(0),
                    "0x3F should travel unescaped");
            });
        }

        [Test]
        public void HdlcFramer_SendFrame_AppendsAnFcsThatValidates()
        {
            List<byte> sent = new List<byte>();
            HdlcFramer framer = CreateFramer(sent, new List<FrameEventArgs>());

            byte[] payload = new byte[] { 0x41, 0x42, 0x43, 0x44 };
            framer.SendFrame(payload, payload.Length);

            // Unstuff what went on the wire and check the FCS with the independently
            // verified implementation, rather than trusting the framer's own check.
            byte[] unstuffed = Unstuff(sent.ToArray());

            Assert.That(unstuffed.Length, Is.EqualTo(payload.Length + 2), "FCS should be two octets");
            Assert.That(Fcs16.IsValid(unstuffed), Is.True, "the emitted FCS does not validate");
        }

        // ---------------------------------------------------------------- decode

        [Test]
        public void HdlcFramer_RoundTrip_AllByteValues()
        {
            List<byte> sent = new List<byte>();
            List<FrameEventArgs> received = new List<FrameEventArgs>();
            HdlcFramer framer = CreateFramer(sent, received);

            byte[] payload = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                payload[i] = (byte)i;
            }

            framer.SendFrame(payload, payload.Length);
            byte[] wire = sent.ToArray();
            framer.ProcessBytes(wire, wire.Length);

            Assert.That(received.Count, Is.EqualTo(1), "expected exactly one frame back");
            Assert.That(received[0].FcsValid, Is.True);
            Assert.That(received[0].Data, Is.EqualTo(payload), "payload did not survive the round trip");
        }

        [Test]
        public void HdlcFramer_PartialFrame_ReassemblesAcrossCalls()
        {
            List<byte> received1 = new List<byte>();
            List<FrameEventArgs> received = new List<FrameEventArgs>();
            HdlcFramer framer = CreateFramer(received1, received);

            // A payload whose stuffed form contains an escape, so that one of the splits
            // can land between the escape and the octet it escapes - the case that breaks
            // a naive rewrite. Measured on real captures, 27% of reads are not frame
            // aligned, so this is the common case, not an edge case.
            byte[] wire = WrapFrame(new byte[] { 0xAA, 0x7E, 0xBB });

            int third = wire.Length / 3;
            framer.ProcessBytes(Slice(wire, 0, third), third);
            Assert.That(received.Count, Is.EqualTo(0), "a frame was reported before it was complete");

            framer.ProcessBytes(Slice(wire, third, third), third);
            framer.ProcessBytes(Slice(wire, third * 2, wire.Length - third * 2), wire.Length - third * 2);

            Assert.That(received.Count, Is.EqualTo(1), "the reassembled frame was not reported");
            Assert.That(received[0].FcsValid, Is.True);
            Assert.That(received[0].Data, Is.EqualTo(new byte[] { 0xAA, 0x7E, 0xBB }));
        }

        [Test]
        public void HdlcFramer_TwoFramesSeparatedByTwoFlags_BothDelivered()
        {
            List<FrameEventArgs> received = new List<FrameEventArgs>();
            HdlcFramer framer = CreateFramer(new List<byte>(), received);

            // Each frame carries its own pair of flags, so the stream contains 7E 7E
            // between them. This is what every implementation here emits, and what the
            // real captures show.
            byte[] a = WrapFrame(new byte[] { 0x11, 0x22, 0x33, 0x44 });
            byte[] b = WrapFrame(new byte[] { 0x55, 0x66, 0x77, 0x88 });

            byte[] both = new byte[a.Length + b.Length];
            Array.Copy(a, both, a.Length);
            Array.Copy(b, 0, both, a.Length, b.Length);

            framer.ProcessBytes(both, both.Length);

            Assert.That(received.Count, Is.EqualTo(2), "both frames should arrive from one buffer");
            Assert.That(received[0].Data, Is.EqualTo(new byte[] { 0x11, 0x22, 0x33, 0x44 }));
            Assert.That(received[1].Data, Is.EqualTo(new byte[] { 0x55, 0x66, 0x77, 0x88 }));
        }

        [Test]
        public void HdlcFramer_SecondFrameAfterSharedFlag_IsDelivered()
        {
            List<FrameEventArgs> received = new List<FrameEventArgs>();
            HdlcFramer framer = CreateFramer(new List<byte>(), received);

            // A SINGLE flag serving as both the closing flag of one frame and the opening
            // flag of the next. ITU-T X.25 clause 2.2.1 permits it in as many words:
            // "A single flag may be used as both the closing flag for one frame and the
            // opening flag for the next frame." Q.921 clause 2.2 says the same.
            //
            // FIXED by the framing merge, 2026-09-06, and this test was updated deliberately.
            // The old hand-rolled loop set _inFrame = false when a flag closed a frame and
            // never re-opened, so the second frame's octets arrived with _inFrame false and
            // were discarded - it asserted ONE frame and said in this comment that the merge
            // should make it TWO. The shared engine applies the clause 2.2.1 rule directly: a
            // flag while a frame is in progress closes it AND opens the next.
            byte[] a = WrapFrame(new byte[] { 0x11, 0x22, 0x33, 0x44 });
            byte[] b = WrapFrame(new byte[] { 0x55, 0x66, 0x77, 0x88 });

            // Drop a's closing flag so b's opening flag serves for both.
            byte[] shared = new byte[a.Length - 1 + b.Length];
            Array.Copy(a, shared, a.Length - 1);
            Array.Copy(b, 0, shared, a.Length - 1, b.Length);

            framer.ProcessBytes(shared, shared.Length);

            Assert.That(received.Count, Is.EqualTo(2), "a shared flag must open the following frame");
            Assert.That(received[0].Data, Is.EqualTo(new byte[] { 0x11, 0x22, 0x33, 0x44 }));
            Assert.That(received[1].Data, Is.EqualTo(new byte[] { 0x55, 0x66, 0x77, 0x88 }),
                "the frame after the shared flag used to be lost entirely");
            Assert.That(received[1].FcsValid, Is.True);
        }

        [Test]
        public void HdlcFramer_BadFcs_StillRaisesTheFrame_WithFcsValidFalse()
        {
            List<FrameEventArgs> received = new List<FrameEventArgs>();
            HdlcFramer framer = CreateFramer(new List<byte>(), received);

            byte[] wire = WrapFrame(new byte[] { 0x11, 0x22, 0x33, 0x44 });
            wire[2] ^= 0xFF; // corrupt a payload octet

            framer.ProcessBytes(wire, wire.Length);

            // Unusual, and worth pinning: every other implementation here drops a bad
            // frame silently. This one reports it and lets the caller decide - all three
            // callers immediately discard it and bump an error counter.
            Assert.That(received.Count, Is.EqualTo(1), "a corrupt frame should still be reported");
            Assert.That(received[0].FcsValid, Is.False, "a corrupt frame must not be marked valid");
        }

        [Test]
        public void HdlcFramer_ShortFrame_IsDiscarded()
        {
            List<FrameEventArgs> received = new List<FrameEventArgs>();
            HdlcFramer framer = CreateFramer(new List<byte>(), received);

            // Three octets between the flags: fewer than address + control + two FCS, so
            // it cannot be a LAPB frame. RFC 1662 sections 4.3 and 5.3 require a runt to
            // be discarded silently and NOT counted as an FCS error.
            framer.ProcessBytes(new byte[] { FLAG, 0x01, 0x02, 0x03, FLAG }, 5);

            Assert.That(received.Count, Is.EqualTo(0), "a runt should not be reported as a frame");
        }

        [Test]
        public void HdlcFramer_OddFlagRunBeforeAFrame_DeliversIt()
        {
            List<FrameEventArgs> received = new List<FrameEventArgs>();
            HdlcFramer framer = CreateFramer(new List<byte>(), received);

            // Two extra flags plus the frame's own opening flag: THREE flags, odd.
            byte[] wire = WithLeadingFlags(WrapFrame(new byte[] { 0x11, 0x22, 0x33, 0x44 }), 2);

            framer.ProcessBytes(wire, wire.Length);

            Assert.That(received.Count, Is.EqualTo(1));
            Assert.That(received[0].Data, Is.EqualTo(new byte[] { 0x11, 0x22, 0x33, 0x44 }));
        }

        [Test]
        public void HdlcFramer_FlagRunParity_NoLongerDecidesWhetherAFrameIsSeen()
        {
            // FIXED by the framing merge, 2026-09-06, and this test was updated deliberately.
            //
            // The old hand-rolled loop treated a flag as a pure toggle: a flag while out of
            // frame opened one, a flag while in frame closed one and left it CLOSED. A run
            // of flags therefore alternated the state, and whether the frame that followed
            // was seen at all depended on whether the run length was odd or even. This test
            // used to assert 0 for an even run while the odd-run test above asserted 1 - the
            // same frame, two answers.
            //
            // That was never theoretical. Inter-frame time fill IS a run of flags (ITU-T
            // X.25 clause 2.2.4, "interframe time fill is accomplished by transmitting
            // contiguous flags between frames"), and the captures in this repository contain
            // 7E 7E runs on 1571 separate reads.
            //
            // The shared engine ignores a flag received while no frame is in progress, so
            // the parity cannot matter. Every run length from zero upwards now delivers the
            // frame, which is what this test checks.
            for (int extraFlags = 0; extraFlags <= 5; extraFlags++)
            {
                List<FrameEventArgs> got = new List<FrameEventArgs>();
                HdlcFramer f = CreateFramer(new List<byte>(), got);
                byte[] w = WithLeadingFlags(WrapFrame(new byte[] { 0x11, 0x22, 0x33, 0x44 }), extraFlags);

                f.ProcessBytes(w, w.Length);

                Assert.That(got.Count, Is.EqualTo(1), $"failed with {extraFlags} extra leading flags");
                Assert.That(got[0].Data, Is.EqualTo(new byte[] { 0x11, 0x22, 0x33, 0x44 }));
            }
        }

        [Test]
        public void HdlcFramer_Reset_DiscardsAPartialFrame()
        {
            List<FrameEventArgs> received = new List<FrameEventArgs>();
            HdlcFramer framer = CreateFramer(new List<byte>(), received);

            byte[] wire = WrapFrame(new byte[] { 0x11, 0x22, 0x33, 0x44 });

            framer.ProcessBytes(Slice(wire, 0, 3), 3);
            framer.Reset();
            framer.ProcessBytes(Slice(wire, 3, wire.Length - 3), wire.Length - 3);

            Assert.That(received.Count, Is.EqualTo(0),
                "the tail of a frame discarded by Reset must not be delivered");
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>Prepends a run of extra flag octets to an already-framed buffer.</summary>
        private static byte[] WithLeadingFlags(byte[] frame, int extraFlags)
        {
            byte[] wire = new byte[extraFlags + frame.Length];
            for (int i = 0; i < extraFlags; i++)
            {
                wire[i] = FLAG;
            }
            Array.Copy(frame, 0, wire, extraFlags, frame.Length);
            return wire;
        }

        private static byte[] Slice(byte[] source, int offset, int length)
        {
            byte[] result = new byte[length];
            Array.Copy(source, offset, result, 0, length);
            return result;
        }

        /// <summary>Removes flags and byte stuffing; returns the frame content with its FCS.</summary>
        private static byte[] Unstuff(byte[] wire)
        {
            List<byte> outBytes = new List<byte>(wire.Length);
            bool escaped = false;
            for (int i = 0; i < wire.Length; i++)
            {
                byte b = wire[i];
                if (b == FLAG)
                {
                    continue;
                }
                if (escaped)
                {
                    outBytes.Add((byte)(b ^ 0x20));
                    escaped = false;
                }
                else if (b == ESC)
                {
                    escaped = true;
                }
                else
                {
                    outBytes.Add(b);
                }
            }
            return outBytes.ToArray();
        }

        private static int IndexOfPair(byte[] data, byte first, byte second)
        {
            for (int i = 0; i + 1 < data.Length; i++)
            {
                if (data[i] == first && data[i + 1] == second)
                {
                    return i;
                }
            }
            return -1;
        }
    }
}
