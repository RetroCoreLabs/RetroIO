using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Hdlc
{
    /// <summary>
    /// Splits a reassembled byte stream into unstuffed HDLC frames.
    /// </summary>
    /// <remarks>
    /// <para><b>Framing</b></para>
    /// Asynchronous HDLC framing (XMSG-PROTOCOL.md section 2):
    ///  - <c>0x7E</c> is the frame delimiter; runs of consecutive flags are interframe
    ///    fill and collapse to a single boundary.
    ///  - within a frame, <c>0x7D</c> escapes the following byte, which is recovered as
    ///    <c>(next XOR 0x20)</c>.
    /// This class only performs the flag split and byte unstuffing; FCS validation is a
    /// separate step (see <see cref="Fcs16"/>).
    /// </remarks>
    public static class HdlcDeframer
    {
        /// <summary>
        /// The HDLC flag / frame-delimiter byte.
        /// </summary>
        private const byte Flag = 0x7E;

        /// <summary>
        /// The HDLC escape byte.
        /// </summary>
        private const byte Escape = 0x7D;

        /// <summary>
        /// The XOR mask applied to an escaped byte.
        /// </summary>
        private const byte EscapeMask = 0x20;

        /// <summary>
        /// Splits a stream on <c>0x7E</c> flags and unstuffs each frame's bytes.
        /// </summary>
        /// <param name="stream">
        /// The reassembled byte stream (the concatenated TCP payloads of one flow).
        /// </param>
        /// <returns>
        /// The list of unstuffed frame byte arrays, in stream order. Empty runs between
        /// consecutive flags are skipped.
        /// </returns>
        public static IReadOnlyList<byte[]> SplitFrames(ReadOnlySpan<byte> stream)
        {
            return SplitFrames(stream, null);
        }

        /// <summary>
        /// Splits a byte stream into unstuffed LAPB frames, recording where each one ended.
        /// </summary>
        /// <param name="stream">The captured or received octets.</param>
        /// <param name="frameEndOffsets">
        /// When not null, receives the offset just past each frame's closing flag, in the same
        /// order as the returned frames. Used by the pcap tooling to keep frames in capture order.
        /// </param>
        /// <returns>The frames, each with its FCS still attached.</returns>
        /// <remarks>
        /// <para>
        /// Since 2026-09-06 this drives the shared streaming framer in <c>RetroIO.Link.Hdlc</c>
        /// rather than its own loop - one of five HDLC implementations that were merged. The
        /// whole-buffer shape is kept because the pcap tooling wants it, but there is only one
        /// engine underneath now.
        /// </para>
        /// <para>
        /// <b>One behaviour deliberately changed.</b> The old loop treated the end of the buffer as
        /// a frame terminator and returned a truncated tail as though it were a frame. A streaming
        /// decoder holds it instead, so a partial frame at the end of a read is simply not
        /// reported. That is the correct answer - ITU-T X.25 clause 2.3.5.3 a makes a frame invalid
        /// unless it is "properly bounded by two flags" - and it is what lets the byte-at-a-time
        /// accumulators that used to wrap this function go away.
        /// </para>
        /// <para>
        /// Frames are returned with the FCS attached and are NOT checked here; the caller decides,
        /// as it always has, with <see cref="Fcs16.IsValid"/>. That is why the framer is configured
        /// with <c>UseFcs</c> false.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<byte[]> SplitFrames(ReadOnlySpan<byte> stream, IList<int>? frameEndOffsets)
        {
            List<byte[]> frames = new List<byte[]>();

            // Minimum length one: without an FCS check there is no four-octet floor, and this
            // function has always returned whatever lay between two flags and left the judgement
            // to the caller.
            RetroIO.Link.Hdlc.HdlcFramerOptions options =
                RetroIO.Link.Hdlc.HdlcFramerOptions.Xmsg with
                {
                    UseFcs = false,
                    MinFrameLength = 1,
                };

            using RetroIO.Link.Hdlc.HdlcDecoder decoder = new RetroIO.Link.Hdlc.HdlcDecoder(options);

            int offset = 0;
            while (offset < stream.Length)
            {
                RetroIO.Link.Hdlc.HdlcDecodeStatus status =
                    decoder.Decode(stream.Slice(offset), out int consumed);
                offset += consumed;

                if (status == RetroIO.Link.Hdlc.HdlcDecodeStatus.NeedMoreData)
                {
                    break;
                }

                if (status == RetroIO.Link.Hdlc.HdlcDecodeStatus.FrameComplete)
                {
                    frames.Add(decoder.Frame.ToArray());
                    frameEndOffsets?.Add(offset);
                }
            }

            return frames;
        }
    }
}
