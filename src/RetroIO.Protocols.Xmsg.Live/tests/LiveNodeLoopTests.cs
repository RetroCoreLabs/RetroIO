using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Node;
using RetroIO.Protocols.Xmsg.Hdlc;
using RetroIO.Protocols.Xmsg.Live;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Live.Tests
{
    /// <summary>
    /// Layer 4 proof: the full receive->decode->respond->encode->send loop over an
    /// in-memory transport answers a SABM with UA and a captured request with the right
    /// response, without touching a real socket.
    /// </summary>
    public sealed class LiveNodeLoopTests
    {
        /// <summary>
        /// Feeding an HDLC-encoded SABM then a reachability-request I-frame makes the loop
        /// emit a UA and the reachability reply.
        /// </summary>
        [Fact]
        public async Task Loop_AnswersSabm_ThenReachabilityReply()
        {
            // Peer (node 100) initiates: SABM, then an I-frame N(S)=0 N(R)=0 carrying the
            // captured reachability request 100 -> 102.
            byte[] sabm = HdlcEncoder.Encode(new byte[] { 0x01, 0x3F, 0x00, 0x64 });
            byte[] reqInfo = LiveTestHex.Parse("21 13 00 19 00 66 00 64 FF FF 00 01 DE 08");
            byte[] reqFrame = HdlcEncoder.Encode(BuildIFrameBody(0x00, reqInfo));

            byte[] inbound = Concat(sabm, reqFrame);

            InMemoryDuplex duplex = new InMemoryDuplex(inbound);
            LapbLayer link = new LapbLayer(ownNode: 102);
            XmsgNode node = new XmsgNode(nodeNumber: 102, ackCounter: 0x2D);
            LiveNode live = new LiveNode(duplex, link, node);

            await live.RunWithoutTimersAsync(CancellationToken.None);

            IReadOnlyList<LapbFrame> replies = DeframeWritten(duplex);

            // UA must be present (addr 0x01, ctrl 0x73).
            Assert.True(HasControlFrame(replies, 0x01, 0x73), "expected a UA reply");

            // The reachability reply must be carried in an emitted I-frame, byte-identical.
            byte[] expectedReply = LiveTestHex.Parse("21 13 00 13 00 64 00 66 FF FF 00 01 DE 0E");
            Assert.True(HasInformationFrame(replies, expectedReply), "expected the reachability reply I-frame");
        }

        /// <summary>
        /// Feeding a SABM then a data I-frame makes the loop emit the secure ACK that
        /// echoes the data frame's Flags 1.
        /// </summary>
        [Fact]
        public async Task Loop_AnswersDataFrame_WithSecureAck()
        {
            byte[] sabm = HdlcEncoder.Encode(new byte[] { 0x01, 0x3F, 0x00, 0x64 });

            // A minimal data frame (subtype 0x0E) 100 -> 102, datagram sequence 0x0007.
            // CORRECTED 2026-08-04: the SINTRAN header is FOURTEEN bytes, so this fixture gained
            // its missing last byte. Word 6 here is 0xDE00 - SYNTHETIC, not a real checksum (the
            // real one for these six words is 0xDA0D). What the test exercises is the legacy ACK
            // path, which echoes the data frame's word-6 high byte and its own counter 0x2D, so
            // only the 0xDE half matters to the expected reply below.
            byte[] dataInfo = LiveTestHex.Parse("21 13 00 0E 00 66 00 64 00 07 04 00 DE 00");
            byte[] dataFrame = HdlcEncoder.Encode(BuildIFrameBody(0x00, dataInfo));

            byte[] inbound = Concat(sabm, dataFrame);

            InMemoryDuplex duplex = new InMemoryDuplex(inbound);
            LapbLayer link = new LapbLayer(ownNode: 102);
            XmsgNode node = new XmsgNode(nodeNumber: 102, ackCounter: 0x2D);
            LiveNode live = new LiveNode(duplex, link, node);

            await live.RunWithoutTimersAsync(CancellationToken.None);

            IReadOnlyList<LapbFrame> replies = DeframeWritten(duplex);

            // The secure ACK: subtype 0x03, direction swapped, Flags1 echoes 0x0007,
            // counter byte 0x2D.
            byte[] expectedAck = LiveTestHex.Parse("21 13 00 03 00 64 00 66 00 07 00 01 DE 2D");
            Assert.True(HasInformationFrame(replies, expectedAck), "expected the secure ACK I-frame");
        }

        /// <summary>
        /// Builds an I-frame body (LAPB data address 0x09) with a control byte and info.
        /// </summary>
        /// <param name="control">
        /// The LAPB control byte.
        /// </param>
        /// <param name="info">
        /// The information field.
        /// </param>
        /// <returns>
        /// The LAPB body: <c>09</c>, control, then the info field.
        /// </returns>
        private static byte[] BuildIFrameBody(byte control, byte[] info)
        {
            byte[] body = new byte[2 + info.Length];
            body[0] = LapbLayer.AddressData;   // 0x09
            body[1] = control;
            Array.Copy(info, 0, body, 2, info.Length);
            return body;
        }

        /// <summary>
        /// De-frames every FCS-valid LAPB frame the loop wrote back to the transport.
        /// </summary>
        /// <param name="duplex">
        /// The in-memory transport whose written bytes are decoded.
        /// </param>
        /// <returns>
        /// The FCS-valid frames in write order.
        /// </returns>
        private static IReadOnlyList<LapbFrame> DeframeWritten(InMemoryDuplex duplex)
        {
            byte[] written = duplex.GetWrittenBytes();
            IReadOnlyList<byte[]> frames = HdlcDeframer.SplitFrames(written);
            List<LapbFrame> result = new List<LapbFrame>();
            for (int i = 0; i < frames.Count; i++)
            {
                if (Fcs16.IsValid(frames[i]))
                {
                    result.Add(new LapbFrame(default, frames[i]));
                }
            }

            return result;
        }

        /// <summary>
        /// Determines whether a control (supervisory/unnumbered) frame with the given
        /// address and control byte is present.
        /// </summary>
        /// <param name="frames">
        /// The decoded reply frames.
        /// </param>
        /// <param name="address">
        /// The expected LAPB address byte.
        /// </param>
        /// <param name="control">
        /// The expected LAPB control byte.
        /// </param>
        /// <returns>
        /// <c>true</c> when a matching frame exists.
        /// </returns>
        private static bool HasControlFrame(IReadOnlyList<LapbFrame> frames, byte address, byte control)
        {
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i].Address == address && frames[i].Control == control)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Determines whether an information frame whose info field equals the expected
        /// bytes is present.
        /// </summary>
        /// <param name="frames">
        /// The decoded reply frames.
        /// </param>
        /// <param name="expectedInfo">
        /// The expected information field bytes.
        /// </param>
        /// <returns>
        /// <c>true</c> when a matching I-frame exists.
        /// </returns>
        private static bool HasInformationFrame(IReadOnlyList<LapbFrame> frames, byte[] expectedInfo)
        {
            for (int i = 0; i < frames.Count; i++)
            {
                LapbFrame frame = frames[i];
                if (frame.Kind != LapbFrameKind.Information)
                {
                    continue;
                }

                if (SpanEquals(frame.Info.Span, expectedInfo))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Compares a span against an expected byte array for exact equality.
        /// </summary>
        /// <param name="actual">
        /// The actual bytes.
        /// </param>
        /// <param name="expected">
        /// The expected bytes.
        /// </param>
        /// <returns>
        /// <c>true</c> when both have the same length and content.
        /// </returns>
        private static bool SpanEquals(ReadOnlySpan<byte> actual, byte[] expected)
        {
            if (actual.Length != expected.Length)
            {
                return false;
            }

            for (int i = 0; i < expected.Length; i++)
            {
                if (actual[i] != expected[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Concatenates two byte arrays.
        /// </summary>
        /// <param name="a">
        /// The first array.
        /// </param>
        /// <param name="b">
        /// The second array.
        /// </param>
        /// <returns>
        /// A new array holding <paramref name="a"/> followed by <paramref name="b"/>.
        /// </returns>
        private static byte[] Concat(byte[] a, byte[] b)
        {
            byte[] result = new byte[a.Length + b.Length];
            Array.Copy(a, 0, result, 0, a.Length);
            Array.Copy(b, 0, result, a.Length, b.Length);
            return result;
        }
    }
}
