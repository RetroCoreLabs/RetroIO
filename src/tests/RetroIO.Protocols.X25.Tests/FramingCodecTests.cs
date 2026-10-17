//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Link.Framing;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Unit tests for the framing-codec sublayer (SLIP RFC 1055, HDLC/PPP RFC 1662).
    /// </summary>
    /// <remarks>
    /// Covers round-trip encode/decode, escape handling, all byte values, multiple frames in one
    /// stream, empty-frame skipping, and exact on-the-wire byte vectors for interoperability with the
    /// C reference (x25emu src/framing/slip.c and hdlc.c).
    /// </remarks>
    [TestFixture]
    public class FramingCodecTests
    {
        // Captures framed bytes written by a codec's SendFrame so they can be replayed into a receiver.
        private sealed class CapturingTransport : IFrameTransport
        {
            public readonly List<byte[]> Sent = new List<byte[]>();

            public void Send(ReadOnlySpan<byte> bytes)
            {
                Sent.Add(bytes.ToArray());
            }

            public byte[] Last => Sent[Sent.Count - 1];
        }

        // Sends each frame through the sender, then replays the exact wire bytes into the receiver.
        private static List<byte[]> RoundTrip(FrameCodecBase sender, CapturingTransport sink,
            FrameCodecBase receiver, params byte[][] frames)
        {
            List<byte[]> received = new List<byte[]>();
            receiver.FrameReceived += (linkId, frame) => received.Add(frame.ToArray());

            for (int i = 0; i < frames.Length; i++)
            {
                sender.SendFrame(frames[i]);
                receiver.ProcessBytes(sink.Last);
            }

            return received;
        }

        // ---- SLIP ----

        [Test]
        public void Slip_RoundTrip_BasicPayload()
        {
            CapturingTransport sink = new CapturingTransport();
            SlipFrameCodec sender = new SlipFrameCodec("A", sink);
            SlipFrameCodec receiver = new SlipFrameCodec("B", new CapturingTransport());

            byte[] payload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
            List<byte[]> received = RoundTrip(sender, sink, receiver, payload);

            Assert.That(received.Count, Is.EqualTo(1));
            Assert.That(received[0], Is.EqualTo(payload));
        }

        [Test]
        public void Slip_RoundTrip_EscapesEndAndEsc()
        {
            CapturingTransport sink = new CapturingTransport();
            SlipFrameCodec sender = new SlipFrameCodec("A", sink);
            SlipFrameCodec receiver = new SlipFrameCodec("B", new CapturingTransport());

            // Payload contains END (0xC0) and ESC (0xDB), which must survive escaping.
            byte[] payload = new byte[] { 0xAA, 0xC0, 0xBB, 0xDB, 0xCC };
            List<byte[]> received = RoundTrip(sender, sink, receiver, payload);

            Assert.That(received.Count, Is.EqualTo(1));
            Assert.That(received[0], Is.EqualTo(payload));
        }

        [Test]
        public void Slip_RoundTrip_AllByteValues()
        {
            CapturingTransport sink = new CapturingTransport();
            SlipFrameCodec sender = new SlipFrameCodec("A", sink);
            SlipFrameCodec receiver = new SlipFrameCodec("B", new CapturingTransport());

            byte[] payload = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                payload[i] = (byte)i;
            }

            List<byte[]> received = RoundTrip(sender, sink, receiver, payload);

            Assert.That(received.Count, Is.EqualTo(1));
            Assert.That(received[0], Is.EqualTo(payload));
        }

        [Test]
        public void Slip_MultipleFrames_InOneStream()
        {
            CapturingTransport sink = new CapturingTransport();
            SlipFrameCodec sender = new SlipFrameCodec("A", sink);

            byte[] f1 = new byte[] { 0x11 };
            byte[] f2 = new byte[] { 0x22, 0x33 };
            byte[] f3 = new byte[] { 0x44, 0x55, 0x66 };
            sender.SendFrame(f1);
            byte[] w1 = sink.Last;
            sender.SendFrame(f2);
            byte[] w2 = sink.Last;
            sender.SendFrame(f3);
            byte[] w3 = sink.Last;

            // Concatenate all three framed messages into a single receive buffer.
            byte[] combined = new byte[w1.Length + w2.Length + w3.Length];
            Buffer.BlockCopy(w1, 0, combined, 0, w1.Length);
            Buffer.BlockCopy(w2, 0, combined, w1.Length, w2.Length);
            Buffer.BlockCopy(w3, 0, combined, w1.Length + w2.Length, w3.Length);

            SlipFrameCodec receiver = new SlipFrameCodec("B", new CapturingTransport());
            List<byte[]> received = new List<byte[]>();
            receiver.FrameReceived += (linkId, frame) => received.Add(frame.ToArray());
            receiver.ProcessBytes(combined);

            Assert.That(received.Count, Is.EqualTo(3));
            Assert.That(received[0], Is.EqualTo(f1));
            Assert.That(received[1], Is.EqualTo(f2));
            Assert.That(received[2], Is.EqualTo(f3));
        }

        [Test]
        public void Slip_EmptyFramesBetweenEnds_AreSkipped()
        {
            SlipFrameCodec receiver = new SlipFrameCodec("B", new CapturingTransport());
            List<byte[]> received = new List<byte[]>();
            receiver.FrameReceived += (linkId, frame) => received.Add(frame.ToArray());

            // END END END <payload> END END -- only one real frame, empties skipped.
            byte[] stream = new byte[] { 0xC0, 0xC0, 0xC0, 0x07, 0x08, 0xC0, 0xC0 };
            receiver.ProcessBytes(stream);

            Assert.That(received.Count, Is.EqualTo(1));
            Assert.That(received[0], Is.EqualTo(new byte[] { 0x07, 0x08 }));
        }

        [Test]
        public void Slip_SendFrame_ProducesExactWireBytes()
        {
            CapturingTransport sink = new CapturingTransport();
            SlipFrameCodec sender = new SlipFrameCodec("A", sink);

            sender.SendFrame(new byte[] { 0x01, 0xC0, 0x02, 0xDB, 0x03 });

            // END, 0x01, ESC ESC_END, 0x02, ESC ESC_ESC, 0x03, END
            byte[] expected = new byte[] { 0xC0, 0x01, 0xDB, 0xDC, 0x02, 0xDB, 0xDD, 0x03, 0xC0 };
            Assert.That(sink.Last, Is.EqualTo(expected));
        }

        // ---- HDLC / PPP (RFC 1662) ----

        [Test]
        public void Hdlc_RoundTrip_BasicPayload()
        {
            CapturingTransport sink = new CapturingTransport();
            HdlcFrameCodec sender = new HdlcFrameCodec("A", sink);
            HdlcFrameCodec receiver = new HdlcFrameCodec("B", new CapturingTransport());

            byte[] payload = new byte[] { 0x41, 0x42, 0x43, 0x44 };
            List<byte[]> received = RoundTrip(sender, sink, receiver, payload);

            Assert.That(received.Count, Is.EqualTo(1));
            Assert.That(received[0], Is.EqualTo(payload));
        }

        [Test]
        public void Hdlc_RoundTrip_EscapesFlagAndEsc()
        {
            CapturingTransport sink = new CapturingTransport();
            HdlcFrameCodec sender = new HdlcFrameCodec("A", sink);
            HdlcFrameCodec receiver = new HdlcFrameCodec("B", new CapturingTransport());

            // Payload contains FLAG (0x7E) and ESC (0x7D).
            byte[] payload = new byte[] { 0xAA, 0x7E, 0xBB, 0x7D, 0xCC };
            List<byte[]> received = RoundTrip(sender, sink, receiver, payload);

            Assert.That(received.Count, Is.EqualTo(1));
            Assert.That(received[0], Is.EqualTo(payload));
        }

        [Test]
        public void Hdlc_RoundTrip_ControlCharsBelow0x20()
        {
            CapturingTransport sink = new CapturingTransport();
            HdlcFrameCodec sender = new HdlcFrameCodec("A", sink);
            HdlcFrameCodec receiver = new HdlcFrameCodec("B", new CapturingTransport());

            // Control octets below 0x20 are ACCM-escaped on transmit and must round-trip.
            byte[] payload = new byte[] { 0x00, 0x01, 0x11, 0x13, 0x1F, 0x20 };
            List<byte[]> received = RoundTrip(sender, sink, receiver, payload);

            Assert.That(received.Count, Is.EqualTo(1));
            Assert.That(received[0], Is.EqualTo(payload));
        }

        [Test]
        public void Hdlc_RoundTrip_AllByteValues()
        {
            CapturingTransport sink = new CapturingTransport();
            HdlcFrameCodec sender = new HdlcFrameCodec("A", sink);
            HdlcFrameCodec receiver = new HdlcFrameCodec("B", new CapturingTransport());

            byte[] payload = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                payload[i] = (byte)i;
            }

            List<byte[]> received = RoundTrip(sender, sink, receiver, payload);

            Assert.That(received.Count, Is.EqualTo(1));
            Assert.That(received[0], Is.EqualTo(payload));
        }

        [Test]
        public void Hdlc_SendFrame_ProducesExactWireBytes()
        {
            CapturingTransport sink = new CapturingTransport();
            HdlcFrameCodec sender = new HdlcFrameCodec("A", sink);

            sender.SendFrame(new byte[] { 0xAA, 0x7E, 0xBB, 0x7D, 0xCC });

            // FLAG, 0xAA, ESC (0x7E^0x20=0x5E), 0xBB, ESC (0x7D^0x20=0x5D), 0xCC, FLAG
            byte[] expected = new byte[] { 0x7E, 0xAA, 0x7D, 0x5E, 0xBB, 0x7D, 0x5D, 0xCC, 0x7E };
            Assert.That(sink.Last, Is.EqualTo(expected));
        }

        // ---- HDLC gaps found by the 2026-09-06 coverage audit ----
        //
        // These pin the behaviours that nothing covered before the framing merge. Real
        // captures say they matter: 27% of reads are not frame-aligned, 1183 reads carry
        // two or more frames, and 1571 carry a run of flags.

        [Test]
        public void Hdlc_MultipleFrames_InOneStream()
        {
            CapturingTransport sink = new CapturingTransport();
            HdlcFrameCodec sender = new HdlcFrameCodec("A", sink);
            HdlcFrameCodec receiver = new HdlcFrameCodec("B", new CapturingTransport());

            byte[] a = new byte[] { 0x11 };
            byte[] b = new byte[] { 0x22, 0x33 };
            byte[] c = new byte[] { 0x44, 0x55, 0x66 };

            sender.SendFrame(a);
            byte[] wa = sink.Last;
            sender.SendFrame(b);
            byte[] wb = sink.Last;
            sender.SendFrame(c);
            byte[] wc = sink.Last;

            byte[] combined = new byte[wa.Length + wb.Length + wc.Length];
            Array.Copy(wa, 0, combined, 0, wa.Length);
            Array.Copy(wb, 0, combined, wa.Length, wb.Length);
            Array.Copy(wc, 0, combined, wa.Length + wb.Length, wc.Length);

            List<byte[]> received = new List<byte[]>();
            receiver.FrameReceived += (linkId, frame) => received.Add(frame.ToArray());
            receiver.ProcessBytes(combined);

            Assert.That(received.Count, Is.EqualTo(3), "all three frames should come out of one buffer");
            Assert.That(received[0], Is.EqualTo(a));
            Assert.That(received[1], Is.EqualTo(b));
            Assert.That(received[2], Is.EqualTo(c));
        }

        [Test]
        public void Hdlc_PartialFrame_ReassemblesAcrossCalls()
        {
            CapturingTransport sink = new CapturingTransport();
            HdlcFrameCodec sender = new HdlcFrameCodec("A", sink);
            HdlcFrameCodec receiver = new HdlcFrameCodec("B", new CapturingTransport());

            // Contains both a flag and an escape octet, so a split can land between an
            // escape and the octet it escapes - the case a naive rewrite gets wrong.
            byte[] payload = new byte[] { 0xAA, 0x7E, 0xBB, 0x7D, 0xCC };
            sender.SendFrame(payload);
            byte[] wire = sink.Last;

            List<byte[]> received = new List<byte[]>();
            receiver.FrameReceived += (linkId, frame) => received.Add(frame.ToArray());

            // Deliver one octet at a time: every possible split point at once.
            for (int i = 0; i < wire.Length; i++)
            {
                receiver.ProcessBytes(new ReadOnlySpan<byte>(wire, i, 1));
                if (i < wire.Length - 1)
                {
                    Assert.That(received.Count, Is.EqualTo(0),
                        "a frame was reported before its closing flag arrived");
                }
            }

            Assert.That(received.Count, Is.EqualTo(1));
            Assert.That(received[0], Is.EqualTo(payload));
        }

        [Test]
        public void Hdlc_FlagRunBetweenFrames_ProducesNoEmptyFrames()
        {
            CapturingTransport sink = new CapturingTransport();
            HdlcFrameCodec sender = new HdlcFrameCodec("A", sink);
            HdlcFrameCodec receiver = new HdlcFrameCodec("B", new CapturingTransport());

            byte[] payload = new byte[] { 0x07, 0x08 };
            sender.SendFrame(payload);
            byte[] wire = sink.Last;

            // Bracket the frame with extra flags: inter-frame time fill, ITU-T X.25
            // clause 2.2.4. Both an odd and an even run, because HdlcFramer - the other
            // implementation in this repository - is sensitive to the parity and this one
            // must not be.
            byte[] stream = new byte[3 + wire.Length + 2];
            stream[0] = 0x7E;
            stream[1] = 0x7E;
            stream[2] = 0x7E;
            Array.Copy(wire, 0, stream, 3, wire.Length);
            stream[3 + wire.Length] = 0x7E;
            stream[4 + wire.Length] = 0x7E;

            List<byte[]> received = new List<byte[]>();
            receiver.FrameReceived += (linkId, frame) => received.Add(frame.ToArray());
            receiver.ProcessBytes(stream);

            Assert.That(received.Count, Is.EqualTo(1), "flag fill produced spurious empty frames");
            Assert.That(received[0], Is.EqualTo(payload));
        }

        [Test]
        public void Hdlc_SharedFlag_OpensTheNextFrame()
        {
            HdlcFrameCodec receiver = new HdlcFrameCodec("B", new CapturingTransport());

            // ONE flag serving as both the closing flag of the first frame and the opening
            // flag of the second. ITU-T X.25 clause 2.2.1: "A single flag may be used as
            // both the closing flag for one frame and the opening flag for the next frame."
            byte[] stream = new byte[] { 0x7E, 0x11, 0x22, 0x7E, 0x33, 0x44, 0x7E };

            List<byte[]> received = new List<byte[]>();
            receiver.FrameReceived += (linkId, frame) => received.Add(frame.ToArray());
            receiver.ProcessBytes(stream);

            Assert.That(received.Count, Is.EqualTo(2), "a shared flag must open the following frame");
            Assert.That(received[0], Is.EqualTo(new byte[] { 0x11, 0x22 }));
            Assert.That(received[1], Is.EqualTo(new byte[] { 0x33, 0x44 }));
        }

        [Test]
        public void Hdlc_AfterReset_HuntsForAFlagAndDiscardsThePartialTail()
        {
            CapturingTransport sink = new CapturingTransport();
            HdlcFrameCodec sender = new HdlcFrameCodec("A", sink);
            HdlcFrameCodec receiver = new HdlcFrameCodec("B", new CapturingTransport());

            sender.SendFrame(new byte[] { 0x11, 0x22, 0x33, 0x44 });
            byte[] wire = sink.Last;

            List<byte[]> received = new List<byte[]>();
            receiver.FrameReceived += (linkId, frame) => received.Add(frame.ToArray());

            receiver.ProcessBytes(new ReadOnlySpan<byte>(wire, 0, 3));
            receiver.Reset();
            receiver.ProcessBytes(new ReadOnlySpan<byte>(wire, 3, wire.Length - 3));

            // FIXED by the framing merge, 2026-09-06, and this test was updated deliberately.
            //
            // Before the merge this codec had no flag-hunt state: a flag always delimited, so
            // whatever had arrived since the last one became a frame, and the tail of a frame
            // discarded by Reset was delivered as a frame of its own. It was harmless in practice
            // because the FCS check above threw the fragment away, but it was wrong, and it
            // differed from the other framer in this repository, which ignored everything received
            // while it was not inside a frame.
            //
            // The shared engine hunts. ITU-T X.25 clause 2.3.5.3 a makes a frame invalid unless it
            // is "properly bounded by two flags", so content with no opening flag is not a frame
            // and is discarded rather than handed up.
            Assert.That(received.Count, Is.EqualTo(0),
                "after Reset the decoder must hunt for an opening flag and discard the partial tail");
        }

        // ---- XOT (RFC 1613) ----

        [Test]
        public void Xot_RoundTrip_BasicPayload()
        {
            CapturingTransport sink = new CapturingTransport();
            XotFrameCodec sender = new XotFrameCodec("A", sink);
            XotFrameCodec receiver = new XotFrameCodec("B", new CapturingTransport());

            // Payload may contain any bytes, including 0x7E/0xC0 -- XOT is length-prefixed, not stuffed.
            byte[] payload = new byte[] { 0x10, 0x00, 0x0B, 0x7E, 0xC0, 0xDB };
            List<byte[]> received = RoundTrip(sender, sink, receiver, payload);

            Assert.That(received.Count, Is.EqualTo(1));
            Assert.That(received[0], Is.EqualTo(payload));
        }

        [Test]
        public void Xot_SendFrame_ProducesExactWireBytes()
        {
            CapturingTransport sink = new CapturingTransport();
            XotFrameCodec sender = new XotFrameCodec("A", sink);

            sender.SendFrame(new byte[] { 0xAA, 0xBB, 0xCC });

            // version 0x0000, length 0x0003 (big-endian), then payload
            byte[] expected = new byte[] { 0x00, 0x00, 0x00, 0x03, 0xAA, 0xBB, 0xCC };
            Assert.That(sink.Last, Is.EqualTo(expected));
        }

        [Test]
        public void Xot_MultipleFrames_InOneStream()
        {
            CapturingTransport sink = new CapturingTransport();
            XotFrameCodec sender = new XotFrameCodec("A", sink);

            // Legal X.25 packet lengths. These were one and two octets until 2026-09-06:
            // RFC 1613 gives the length field only legal X.25 packet lengths and the shortest
            // X.25 packet is three octets (5.1), so the codec refuses shorter ones now.
            byte[] f1 = new byte[] { 0x10, 0x01, 0x11 };
            byte[] f2 = new byte[] { 0x10, 0x02, 0x22, 0x33 };
            sender.SendFrame(f1);
            byte[] w1 = sink.Last;
            sender.SendFrame(f2);
            byte[] w2 = sink.Last;

            byte[] combined = new byte[w1.Length + w2.Length];
            Buffer.BlockCopy(w1, 0, combined, 0, w1.Length);
            Buffer.BlockCopy(w2, 0, combined, w1.Length, w2.Length);

            XotFrameCodec receiver = new XotFrameCodec("B", new CapturingTransport());
            List<byte[]> received = new List<byte[]>();
            receiver.FrameReceived += (linkId, frame) => received.Add(frame.ToArray());
            receiver.ProcessBytes(combined);

            Assert.That(received.Count, Is.EqualTo(2));
            Assert.That(received[0], Is.EqualTo(f1));
            Assert.That(received[1], Is.EqualTo(f2));
        }

        [Test]
        public void Xot_PartialFrame_ReassemblesAcrossCalls()
        {
            CapturingTransport sink = new CapturingTransport();
            XotFrameCodec sender = new XotFrameCodec("A", sink);
            sender.SendFrame(new byte[] { 0x41, 0x42, 0x43, 0x44, 0x45 });
            byte[] wire = sink.Last; // 4-byte header + 5-byte payload = 9 bytes

            XotFrameCodec receiver = new XotFrameCodec("B", new CapturingTransport());
            List<byte[]> received = new List<byte[]>();
            receiver.FrameReceived += (linkId, frame) => received.Add(frame.ToArray());

            // Deliver in three fragments: split the header, then split the payload.
            receiver.ProcessBytes(new ReadOnlySpan<byte>(wire, 0, 2));
            Assert.That(received.Count, Is.EqualTo(0), "no frame until header + payload complete");
            receiver.ProcessBytes(new ReadOnlySpan<byte>(wire, 2, 4));
            Assert.That(received.Count, Is.EqualTo(0));
            receiver.ProcessBytes(new ReadOnlySpan<byte>(wire, 6, wire.Length - 6));

            Assert.That(received.Count, Is.EqualTo(1));
            Assert.That(received[0], Is.EqualTo(new byte[] { 0x41, 0x42, 0x43, 0x44, 0x45 }));
        }

        [Test]
        public void Xot_BadVersion_IsFatalAndNothingIsDeliveredAfterIt()
        {
            // RFC 1613: the first two octets are the version and are always zero. There is no flag
            // or marker in XOT to find the next frame boundary with, so a wrong header means every
            // byte after it is at an unknown offset. Carrying on would read whatever came next as a
            // header and hand rubbish up as X.25 packets, which is what discarding and
            // "resynchronizing" did.
            XotFrameCodec receiver = new XotFrameCodec("B", new CapturingTransport());
            List<byte[]> received = new List<byte[]>();
            List<string> failures = new List<string>();
            receiver.FrameReceived += (linkId, frame) => received.Add(frame.ToArray());
            receiver.FramingFailed += (linkId, reason) => failures.Add(reason);

            receiver.ProcessBytes(new byte[] { 0x00, 0x01, 0x00, 0x02, 0xAA, 0xBB });

            Assert.That(failures.Count, Is.EqualTo(1), "a bad version must be reported as fatal");
            Assert.That(received.Count, Is.EqualTo(0));

            // A well-formed frame after the failure is still not delivered: the connection is going.
            CapturingTransport sink = new CapturingTransport();
            XotFrameCodec sender = new XotFrameCodec("A", sink);
            sender.SendFrame(new byte[] { 0x10, 0x01, 0x41 });
            receiver.ProcessBytes(sink.Last);

            Assert.That(received.Count, Is.EqualTo(0), "nothing is framed from a stream that has failed");
            Assert.That(failures.Count, Is.EqualTo(1), "and it is reported once, not on every byte");
        }

        [Test]
        public void Xot_LengthAboveTheMaximum_IsFatal()
        {
            XotFrameCodec receiver = new XotFrameCodec("B", new CapturingTransport());
            List<string> failures = new List<string>();
            receiver.FramingFailed += (linkId, reason) => failures.Add(reason);

            // 0x2000 = 8192, twice the 4096-octet maximum.
            receiver.ProcessBytes(new byte[] { 0x00, 0x00, 0x20, 0x00 });

            Assert.That(failures.Count, Is.EqualTo(1));
        }

        [Test]
        public void Xot_LengthBelowThreeOctets_IsFatal()
        {
            // There is no XOT keep-alive. RFC 1613 gives the length field only legal X.25 packet
            // lengths, and the shortest X.25 packet is three octets: a general format identifier
            // with the channel group, a channel number, and a packet type identifier (X.25 5.1).
            // A zero length is simply an illegal length, and the RFC says close the connection.
            // This codec used to hand a zero-length frame up as an empty one and the links above
            // skipped it as a keep-alive - while sending one themselves every minute, which a
            // conforming peer drops the connection for.
            for (int length = 0; length < 3; length++)
            {
                XotFrameCodec receiver = new XotFrameCodec("B", new CapturingTransport());
                List<string> failures = new List<string>();
                receiver.FramingFailed += (linkId, reason) => failures.Add(reason);

                receiver.ProcessBytes(new byte[] { 0x00, 0x00, 0x00, (byte)length });

                Assert.That(failures.Count, Is.EqualTo(1), $"length {length} must be refused");
            }
        }

        [Test]
        public void Xot_SendingAnIllegalLengthIsRefused()
        {
            // The same rule on the way out, so a caller's own bug does not become a peer closing
            // the connection on us.
            XotFrameCodec sender = new XotFrameCodec("A", new CapturingTransport());

            Assert.Throws<ArgumentException>(() => sender.SendFrame(Array.Empty<byte>()));
            Assert.Throws<ArgumentException>(() => sender.SendFrame(new byte[] { 0x10, 0x01 }));
        }

        [Test]
        public void Xot_ResetLetsTheCodecBeUsedAgain()
        {
            // A codec is reset when its connection is remade, and the new stream starts clean.
            XotFrameCodec receiver = new XotFrameCodec("B", new CapturingTransport());
            List<byte[]> received = new List<byte[]>();
            receiver.FrameReceived += (linkId, frame) => received.Add(frame.ToArray());

            receiver.ProcessBytes(new byte[] { 0x00, 0x01, 0x00, 0x02 });
            receiver.Reset();

            CapturingTransport sink = new CapturingTransport();
            XotFrameCodec sender = new XotFrameCodec("A", sink);
            sender.SendFrame(new byte[] { 0x10, 0x01, 0x41, 0x42 });
            receiver.ProcessBytes(sink.Last);

            Assert.That(received.Count, Is.EqualTo(1));
            Assert.That(received[0], Is.EqualTo(new byte[] { 0x10, 0x01, 0x41, 0x42 }));
        }

        [Test]
        public void Xot_RoundTrip_AllByteValues()
        {
            CapturingTransport sink = new CapturingTransport();
            XotFrameCodec sender = new XotFrameCodec("A", sink);
            XotFrameCodec receiver = new XotFrameCodec("B", new CapturingTransport());

            byte[] payload = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                payload[i] = (byte)i;
            }

            List<byte[]> received = RoundTrip(sender, sink, receiver, payload);

            Assert.That(received.Count, Is.EqualTo(1));
            Assert.That(received[0], Is.EqualTo(payload));
        }
    }
}
