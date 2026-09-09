using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Hdlc
{
    /// <summary>
    /// A LAPB frame together with the TCP flow it was reassembled from.
    /// </summary>
    /// <remarks>
    /// The flow is a property of the CAPTURE, not of the frame. A frame that arrived on a
    /// real link has no TCP flow at all, which is why <see cref="LapbFrame"/> no longer
    /// carries one: every live path was passing a default key that meant nothing, and a
    /// wire-frame type was carrying capture provenance it could not always have.
    /// </remarks>
    public readonly struct CapturedFrame
    {
        /// <summary>
        /// The directional TCP flow the frame was reassembled from.
        /// </summary>
        public readonly StreamKey Key;

        /// <summary>
        /// The frame itself.
        /// </summary>
        public readonly LapbFrame Frame;

        /// <summary>
        /// Initialises the pairing.
        /// </summary>
        /// <param name="key">
        /// The directional TCP flow.
        /// </param>
        /// <param name="frame">
        /// The frame reassembled from it.
        /// </param>
        public CapturedFrame(StreamKey key, LapbFrame frame)
        {
            Key = key;
            Frame = frame;
        }
    }

    /// <summary>
    /// Top-level pipeline that turns a pcapng capture of the <c>nd100x --hdlc</c> TCP
    /// bridge into FCS-valid LAPB frames and their SINTRAN information fields.
    /// </summary>
    /// <remarks>
    /// <para><b>Pipeline</b></para>
    /// The stages, in order:
    ///  - read the pcapng file and extract Ethernet/IPv4/TCP payload segments.
    ///  - reassemble each directional TCP flow by concatenating its segments in
    ///    ascending sequence order (naive; overlaps and retransmits are not specially
    ///    handled, matching the reference validator - bad joins simply fail the FCS).
    ///  - de-frame each reassembled stream on <c>0x7E</c> flags and unstuff.
    ///  - keep only frames whose trailing FCS validates.
    /// </remarks>
    public static class HdlcPcap
    {
        /// <summary>
        /// Reads all FCS-valid LAPB frames from a pcapng capture.
        /// </summary>
        /// <param name="path">
        /// The path of the pcapng capture file.
        /// </param>
        /// <returns>
        /// The FCS-valid LAPB frames across every directional TCP flow, grouped by flow
        /// and in stream order within each flow.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="path"/> is null.
        /// </exception>
        public static IReadOnlyList<LapbFrame> ReadFrames(string path)
        {
            IReadOnlyList<CapturedFrame> captured = ReadCapturedFrames(path);
            List<LapbFrame> frames = new List<LapbFrame>(captured.Count);

            for (int i = 0; i < captured.Count; i++)
            {
                frames.Add(captured[i].Frame);
            }

            return frames;
        }

        /// <summary>
        /// Reads all FCS-valid LAPB frames from a pcapng capture, each with the TCP flow it
        /// came from.
        /// </summary>
        /// <param name="path">
        /// The path of the pcapng capture file.
        /// </param>
        /// <returns>
        /// The FCS-valid frames with their flows, grouped by flow and in stream order within
        /// each flow.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="path"/> is null.
        /// </exception>
        public static IReadOnlyList<CapturedFrame> ReadCapturedFrames(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            IReadOnlyList<TcpSegment> segments = PcapngReader.ReadTcpSegments(path);

            // Group segments by directional flow, preserving first-seen flow order.
            Dictionary<StreamKey, List<TcpSegment>> flows = new Dictionary<StreamKey, List<TcpSegment>>();
            List<StreamKey> flowOrder = new List<StreamKey>();
            for (int i = 0; i < segments.Count; i++)
            {
                TcpSegment segment = segments[i];
                if (!flows.TryGetValue(segment.Key, out List<TcpSegment>? list))
                {
                    list = new List<TcpSegment>();
                    flows.Add(segment.Key, list);
                    flowOrder.Add(segment.Key);
                }

                list.Add(segment);
            }

            List<CapturedFrame> result = new List<CapturedFrame>();
            for (int f = 0; f < flowOrder.Count; f++)
            {
                StreamKey key = flowOrder[f];
                List<TcpSegment> list = flows[key];

                // Naive reassembly: sort by TCP sequence, breaking ties by capture order.
                list.Sort(CompareSegments);

                byte[] stream = Concatenate(list);
                IReadOnlyList<byte[]> frames = HdlcDeframer.SplitFrames(stream);
                for (int i = 0; i < frames.Count; i++)
                {
                    byte[] frameBytes = frames[i];
                    if (Fcs16.IsValid(frameBytes))
                    {
                        result.Add(new CapturedFrame(key, LapbFrame.FromCapture(frameBytes)));
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Reads all FCS-valid LAPB frames from a pcapng capture, ordered as they appeared on the
        /// wire rather than grouped by flow.
        /// </summary>
        /// <param name="path">
        /// The path of the pcapng capture file.
        /// </param>
        /// <returns>
        /// The FCS-valid LAPB frames from every flow, interleaved in capture order.
        /// </returns>
        /// <remarks>
        /// <para>
        /// <b>Why this exists.</b> <see cref="ReadFrames"/> reassembles each directional TCP flow
        /// separately and returns the flows one after another, so the two directions are never
        /// interleaved. That is fine for anything measured within one direction, but it makes any
        /// question of the form "did the sender wait for this reply before sending the next request?"
        /// unanswerable - and worse, invites wrong answers, which has happened at least twice: once
        /// producing an impossible count of 16 outstanding LAPB frames, and once nearly turning a
        /// one-acknowledgement-per-message COUNT into a stop-and-wait claim about ORDERING.
        /// </para>
        /// <para>
        /// Each frame is placed using the capture ordinal of the TCP segment that carried its closing
        /// flag - that is, the point at which the frame became complete on the wire. Frames completed
        /// by the same segment keep their stream order.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="path"/> is null.
        /// </exception>
        public static IReadOnlyList<LapbFrame> ReadFramesInCaptureOrder(string path)
        {
            IReadOnlyList<CapturedFrame> captured = ReadCapturedFramesInCaptureOrder(path);
            List<LapbFrame> frames = new List<LapbFrame>(captured.Count);

            for (int i = 0; i < captured.Count; i++)
            {
                frames.Add(captured[i].Frame);
            }

            return frames;
        }

        /// <summary>
        /// Reads all FCS-valid LAPB frames in capture order, each with the TCP flow it came from.
        /// </summary>
        /// <param name="path">
        /// The path of the pcapng capture file.
        /// </param>
        /// <returns>
        /// The FCS-valid frames with their flows, interleaved in capture order.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="path"/> is null.
        /// </exception>
        public static IReadOnlyList<CapturedFrame> ReadCapturedFramesInCaptureOrder(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            IReadOnlyList<TcpSegment> segments = PcapngReader.ReadTcpSegments(path);

            Dictionary<StreamKey, List<TcpSegment>> flows = new Dictionary<StreamKey, List<TcpSegment>>();
            List<StreamKey> flowOrder = new List<StreamKey>();
            for (int i = 0; i < segments.Count; i++)
            {
                TcpSegment segment = segments[i];
                if (!flows.TryGetValue(segment.Key, out List<TcpSegment>? list))
                {
                    list = new List<TcpSegment>();
                    flows.Add(segment.Key, list);
                    flowOrder.Add(segment.Key);
                }

                list.Add(segment);
            }

            // Each entry pairs a frame with the capture ordinal of the segment that completed it.
            List<OrderedFrame> ordered = new List<OrderedFrame>();

            for (int f = 0; f < flowOrder.Count; f++)
            {
                StreamKey key = flowOrder[f];
                List<TcpSegment> list = flows[key];
                list.Sort(CompareSegments);

                byte[] stream = Concatenate(list);

                List<int> endOffsets = new List<int>();
                IReadOnlyList<byte[]> frames = HdlcDeframer.SplitFrames(stream, endOffsets);

                for (int i = 0; i < frames.Count; i++)
                {
                    byte[] frameBytes = frames[i];
                    if (!Fcs16.IsValid(frameBytes))
                    {
                        continue;
                    }

                    int ordinal = FindSegmentOrdinal(list, endOffsets[i]);
                    ordered.Add(new OrderedFrame(ordinal, ordered.Count, new CapturedFrame(key, LapbFrame.FromCapture(frameBytes))));
                }
            }

            ordered.Sort(CompareOrderedFrames);

            List<CapturedFrame> result = new List<CapturedFrame>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                result.Add(ordered[i].Frame);
            }

            return result;
        }

        /// <summary>
        /// Finds the capture ordinal of the segment whose payload contains a stream offset.
        /// </summary>
        /// <param name="segments">
        /// The flow's segments, already ordered as they were concatenated.
        /// </param>
        /// <param name="streamOffset">
        /// An offset into the concatenated stream.
        /// </param>
        /// <returns>
        /// The capture ordinal of the containing segment, or the last segment's ordinal.
        /// </returns>
        private static int FindSegmentOrdinal(List<TcpSegment> segments, int streamOffset)
        {
            int cursor = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                int length = segments[i].Payload.Length;
                if (streamOffset < cursor + length)
                {
                    return segments[i].Ordinal;
                }

                cursor += length;
            }

            return segments.Count > 0 ? segments[segments.Count - 1].Ordinal : 0;
        }

        /// <summary>
        /// Orders two frames by capture ordinal, breaking ties by discovery order so that frames
        /// completed by the same TCP segment keep their stream order.
        /// </summary>
        /// <param name="a">
        /// The first frame.
        /// </param>
        /// <param name="b">
        /// The second frame.
        /// </param>
        /// <returns>
        /// A negative, zero, or positive value per <see cref="Comparison{T}"/> semantics.
        /// </returns>
        private static int CompareOrderedFrames(OrderedFrame a, OrderedFrame b)
        {
            if (a.Ordinal != b.Ordinal)
            {
                return a.Ordinal < b.Ordinal ? -1 : 1;
            }

            return a.Tiebreak.CompareTo(b.Tiebreak);
        }

        /// <summary>
        /// A frame together with the capture position at which it completed.
        /// </summary>
        private readonly struct OrderedFrame
        {
            /// <summary>
            /// Capture ordinal of the TCP segment that carried the frame's closing flag.
            /// </summary>
            public readonly int Ordinal;

            /// <summary>
            /// Discovery order, used only to break ordinal ties stably.
            /// </summary>
            public readonly int Tiebreak;

            /// <summary>
            /// The frame, with the flow it came from.
            /// </summary>
            public readonly CapturedFrame Frame;

            /// <summary>
            /// Initialises the pairing.
            /// </summary>
            /// <param name="ordinal">
            /// The completing segment's capture ordinal.
            /// </param>
            /// <param name="tiebreak">
            /// Discovery order.
            /// </param>
            /// <param name="frame">
            /// The frame.
            /// </param>
            public OrderedFrame(int ordinal, int tiebreak, CapturedFrame frame)
            {
                Ordinal = ordinal;
                Tiebreak = tiebreak;
                Frame = frame;
            }
        }

        /// <summary>
        /// Reads the information fields of every FCS-valid LAPB information frame.
        /// </summary>
        /// <param name="path">
        /// The path of the pcapng capture file.
        /// </param>
        /// <returns>
        /// One <see cref="ReadOnlyMemory{T}"/> per FCS-valid I-frame, each spanning that
        /// frame's information field (starting at the SINTRAN header for SINTRAN traffic).
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="path"/> is null.
        /// </exception>
        public static IReadOnlyList<ReadOnlyMemory<byte>> ReadInfoFields(string path)
        {
            IReadOnlyList<LapbFrame> frames = ReadFrames(path);
            List<ReadOnlyMemory<byte>> infos = new List<ReadOnlyMemory<byte>>();
            for (int i = 0; i < frames.Count; i++)
            {
                LapbFrame frame = frames[i];
                if (frame.Kind == LapbFrameKind.Information && frame.Info.Length > 0)
                {
                    infos.Add(frame.Info);
                }
            }

            return infos;
        }

        /// <summary>
        /// Orders two TCP segments by sequence number, then by capture order.
        /// </summary>
        /// <param name="a">
        /// The first segment.
        /// </param>
        /// <param name="b">
        /// The second segment.
        /// </param>
        /// <returns>
        /// A negative, zero, or positive value per <see cref="Comparison{T}"/> semantics.
        /// </returns>
        private static int CompareSegments(TcpSegment a, TcpSegment b)
        {
            if (a.Sequence != b.Sequence)
            {
                return a.Sequence < b.Sequence ? -1 : 1;
            }

            return a.Ordinal.CompareTo(b.Ordinal);
        }

        /// <summary>
        /// Concatenates the payloads of a flow's segments into one contiguous stream.
        /// </summary>
        /// <param name="segments">
        /// The flow's segments, already ordered.
        /// </param>
        /// <returns>
        /// A new array holding every segment payload joined end to end.
        /// </returns>
        private static byte[] Concatenate(List<TcpSegment> segments)
        {
            int total = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                total += segments[i].Payload.Length;
            }

            byte[] stream = new byte[total];
            int cursor = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                byte[] payload = segments[i].Payload;
                Array.Copy(payload, 0, stream, cursor, payload.Length);
                cursor += payload.Length;
            }

            return stream;
        }
    }
}
