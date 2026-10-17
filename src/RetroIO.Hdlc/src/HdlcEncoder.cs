//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers;

namespace RetroIO.Link.Hdlc
{
    /// <summary>
    /// Builds HDLC frames: opening flag, body, FCS, transparency, closing flag.
    /// </summary>
    /// <remarks>
    /// Every member is pure and thread-safe. Encoding a frame is a function of the body and the
    /// options and holds no state between calls, so there is nothing to instantiate - making the
    /// encoder an object is the usual over-design in this area and buys nothing.
    /// </remarks>
    public static class HdlcEncoder
    {
        /// <summary>
        /// Upper bound on the octets <see cref="Encode(System.ReadOnlySpan{byte}, System.Span{byte}, HdlcFramerOptions)"/>
        /// will write for a body of this length.
        /// </summary>
        /// <param name="bodyLength">
        /// Octets of address, control and information, FCS excluded.
        /// </param>
        /// <returns>
        /// A size that is always sufficient.
        /// </returns>
        /// <remarks>
        /// Worst case is every octet of the body and both FCS octets needing an escape, plus the
        /// two flags.
        /// </remarks>
        public static int GetMaxEncodedLength(int bodyLength)
        {
            if (bodyLength < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bodyLength), bodyLength, "Negative length.");
            }

            return 2 + ((bodyLength + 2) * 2);
        }

        /// <summary>
        /// Encodes one frame into a caller-supplied buffer.
        /// </summary>
        /// <param name="body">
        /// Address, control and information octets. The FCS is computed here and must NOT already
        /// be present.
        /// </param>
        /// <param name="destination">
        /// At least <see cref="GetMaxEncodedLength"/> octets.
        /// </param>
        /// <param name="options">
        /// The link's framing policy.
        /// </param>
        /// <returns>
        /// How many octets were written.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="options"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="destination"/> is too small.
        /// </exception>
        public static int Encode(ReadOnlySpan<byte> body, Span<byte> destination, HdlcFramerOptions options)
        {
            if (!TryEncode(body, destination, options, out int written))
            {
                throw new ArgumentException(
                    "The destination is too small; size it with GetMaxEncodedLength.", nameof(destination));
            }

            return written;
        }

        /// <summary>
        /// Encodes one frame, reporting failure instead of throwing when the buffer is too small.
        /// </summary>
        /// <param name="body">
        /// Address, control and information octets.
        /// </param>
        /// <param name="destination">
        /// The buffer to write into.
        /// </param>
        /// <param name="options">
        /// The link's framing policy.
        /// </param>
        /// <param name="written">
        /// Octets written; zero when this returns <see langword="false"/>.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the frame was written in full.
        /// </returns>
        public static bool TryEncode(
            ReadOnlySpan<byte> body,
            Span<byte> destination,
            HdlcFramerOptions options,
            out int written)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            written = 0;

            // The FCS is computed over the body BEFORE any transparency is applied, and is then
            // escaped along with it - X.25 clause 2.2.2.2 puts the escaping "following completion
            // of the FCS calculation". Appending a raw, unescaped FCS is the classic bug here: it
            // works until an FCS octet lands on 0x7E or 0x7D, about one frame in 130.
            ushort fcs = options.UseFcs ? HdlcFcs.ComputeTransmitFcs(body) : (ushort)0;
            byte fcsLow = (byte)(fcs & 0xFF);
            byte fcsHigh = (byte)((fcs >> 8) & 0xFF);

            int pos = 0;
            if (!Write(destination, ref pos, HdlcFramerOptions.Flag))
            {
                return false;
            }

            for (int i = 0; i < body.Length; i++)
            {
                if (!WriteStuffed(destination, ref pos, body[i], options))
                {
                    return false;
                }
            }

            if (options.UseFcs &&
                (!WriteStuffed(destination, ref pos, fcsLow, options) ||
                 !WriteStuffed(destination, ref pos, fcsHigh, options)))
            {
                return false;
            }

            if (!Write(destination, ref pos, HdlcFramerOptions.Flag))
            {
                return false;
            }

            written = pos;
            return true;
        }

        /// <summary>
        /// Encodes one frame and returns it as a new array.
        /// </summary>
        /// <param name="body">
        /// Address, control and information octets.
        /// </param>
        /// <param name="options">
        /// The link's framing policy.
        /// </param>
        /// <returns>
        /// The framed octets.
        /// </returns>
        /// <remarks>
        /// Allocates, so prefer <see cref="Encode(ReadOnlySpan{byte}, Span{byte}, HdlcFramerOptions)"/>
        /// on a hot path. This overload exists because most existing callers already hand an array
        /// to a socket, and forcing every one of them to manage a scratch buffer would be churn for
        /// no measurable gain.
        /// </remarks>
        public static byte[] Encode(ReadOnlySpan<byte> body, HdlcFramerOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            int max = GetMaxEncodedLength(body.Length);
            byte[] rented = ArrayPool<byte>.Shared.Rent(max);
            try
            {
                int written = Encode(body, rented, options);
                byte[] result = new byte[written];
                Array.Copy(rented, result, written);
                return result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        /// <summary>
        /// Fills a span with inter-frame time fill: contiguous complete flag octets.
        /// </summary>
        /// <param name="destination">
        /// The span to fill.
        /// </param>
        /// <remarks>
        /// X.25 clause 2.2.4 - "interframe time fill is accomplished by transmitting contiguous
        /// flags between frames" - together with clause 2.2.1, which requires complete eight-bit
        /// flags rather than a shared zero bit.
        /// </remarks>
        public static void WriteTimeFill(Span<byte> destination)
        {
            destination.Fill(HdlcFramerOptions.Flag);
        }

        private static bool Write(Span<byte> destination, ref int pos, byte value)
        {
            if (pos >= destination.Length)
            {
                return false;
            }

            destination[pos] = value;
            pos++;
            return true;
        }

        private static bool WriteStuffed(Span<byte> destination, ref int pos, byte value, HdlcFramerOptions options)
        {
            if (!options.MustEscape(value))
            {
                return Write(destination, ref pos, value);
            }

            return Write(destination, ref pos, HdlcFramerOptions.ControlEscape)
                && Write(destination, ref pos, (byte)(value ^ HdlcFramerOptions.EscapeMask));
        }
    }
}
