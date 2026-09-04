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
        /// Splits a stream on <c>0x7E</c> flags and unstuffs each frame's bytes, optionally
        /// reporting where in the stream each frame ended.
        /// </summary>
        /// <param name="stream">
        /// The reassembled byte stream (the concatenated TCP payloads of one flow).
        /// </param>
        /// <param name="frameEndOffsets">
        /// When not null, receives one entry per returned frame: the offset of that frame's closing
        /// flag within <paramref name="stream"/>. Callers use it to map a frame back to the TCP
        /// segment that completed it, and so to recover capture order across flows - which plain
        /// per-flow reassembly throws away.
        /// </param>
        /// <returns>
        /// The list of unstuffed frame byte arrays, in stream order. Empty runs between
        /// consecutive flags are skipped.
        /// </returns>
        public static IReadOnlyList<byte[]> SplitFrames(ReadOnlySpan<byte> stream, IList<int>? frameEndOffsets)
        {
            List<byte[]> frames = new List<byte[]>();

            int length = stream.Length;
            int pos = 0;
            while (pos < length)
            {
                // Seek an opening flag.
                while (pos < length && stream[pos] != Flag)
                {
                    pos++;
                }

                // Skip the run of consecutive flags (interframe fill).
                while (pos < length && stream[pos] == Flag)
                {
                    pos++;
                }

                if (pos >= length)
                {
                    break;
                }

                int contentStart = pos;

                // Seek the closing flag.
                while (pos < length && stream[pos] != Flag)
                {
                    pos++;
                }

                if (pos > contentStart)
                {
                    byte[] unstuffed = Unstuff(stream.Slice(contentStart, pos - contentStart));
                    if (unstuffed.Length > 0)
                    {
                        frames.Add(unstuffed);
                        if (frameEndOffsets != null) { frameEndOffsets.Add(pos); }
                    }
                }

                // Leave pos on the closing flag; the outer loop treats it as the next
                // opening flag, which also correctly handles shared-flag framing.
            }

            return frames;
        }

        /// <summary>
        /// Reverses HDLC byte stuffing on a single frame's content bytes.
        /// </summary>
        /// <param name="content">
        /// The stuffed frame content between two flags (excluding the flags themselves).
        /// </param>
        /// <returns>
        /// A new array holding the unstuffed bytes.
        /// </returns>
        private static byte[] Unstuff(ReadOnlySpan<byte> content)
        {
            // The unstuffed output can never be longer than the input.
            byte[] output = new byte[content.Length];
            int n = 0;
            int i = 0;
            while (i < content.Length)
            {
                byte b = content[i];
                if (b == Escape)
                {
                    i++;
                    if (i < content.Length)
                    {
                        output[n++] = (byte)(content[i] ^ EscapeMask);
                    }
                }
                else
                {
                    output[n++] = b;
                }

                i++;
            }

            if (n == output.Length)
            {
                return output;
            }

            byte[] trimmed = new byte[n];
            Array.Copy(output, trimmed, n);
            return trimmed;
        }
    }
}
