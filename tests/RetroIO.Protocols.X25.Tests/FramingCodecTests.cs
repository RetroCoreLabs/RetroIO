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

            byte[] f1 = new byte[] { 0x11 };
            byte[] f2 = new byte[] { 0x22, 0x33 };
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
