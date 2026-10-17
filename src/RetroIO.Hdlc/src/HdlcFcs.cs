//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Runtime.CompilerServices;

namespace RetroIO.Link.Hdlc
{
    /// <summary>
    /// Frame Check Sequence arithmetic for HDLC-family links.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FCS-16 uses the generator polynomial x^16 + x^12 + x^5 + 1 (ITU-T X.25 clause 2.2.7.4,
    /// ITU-T Q.921 clause 2.7), evaluated in its reflected form - generator <c>0x8408</c>,
    /// RFC 1662 appendix C. The register is preset to all ones, and the ones complement of the
    /// remainder is what goes on the wire, low octet first (X.25 2.2.7.4, RFC 1662 section 3.1).
    /// </para>
    /// <para>
    /// <b>Why the method names are what they are.</b> This repository previously had TWO public
    /// classes called <c>Fcs16</c>, both with a method called <c>Compute</c>, whose results
    /// differed by a final <c>~</c>: <c>RetroIO.Link.Lapb.Fcs16.Compute</c> returned the
    /// complemented value ready to transmit, and <c>RetroIO.Protocols.Xmsg.Hdlc.Fcs16.Compute</c>
    /// returned the raw running register with every caller applying <c>^ 0xFFFF</c> itself.
    /// Merging those two by name would have compiled cleanly and silently produced wrong CRCs at
    /// every call site on one of the two sides. So neither name survives here:
    /// <see cref="ComputeTransmitFcs"/> and <see cref="Update(ushort, byte)"/> cannot be confused with each
    /// other or bound to by accident.
    /// </para>
    /// <para>
    /// All members are pure and thread-safe.
    /// </para>
    /// </remarks>
    public static class HdlcFcs
    {
        /// <summary>
        /// Initial register value: all ones (X.25 clause 2.2.7.4, RFC 1662 <c>PPPINITFCS16</c>).
        /// </summary>
        public const ushort Init = 0xFFFF;

        /// <summary>
        /// The register value a correct receiver holds after folding the frame content
        /// <em>and both received FCS octets</em> (RFC 1662 <c>PPPGOODFCS16</c>).
        /// </summary>
        /// <remarks>
        /// X.25 clause 2.2.7.4 states the same residue bit-reversed, as
        /// <c>0001110100001111</c> = <c>0x1D0F</c>. Those are the same value written in the two
        /// documents' opposite bit conventions - do not "correct" one to match the other.
        /// </remarks>
        public const ushort Good = 0xF0B8;

        /// <summary>
        /// Smallest frame that can carry an FCS and mean anything: address, control and the two
        /// FCS octets (X.25 clause 2.3.5.3 b, "fewer than 32 bits between flags"; RFC 1662
        /// section 4.3).
        /// </summary>
        public const int MinimumFrameLength = 4;

        // RFC 1662 appendix C FCS-16 lookup table (reflected CRC-16-CCITT, one entry per input
        // octet). Carried over verbatim from RetroIO.Link.Lapb.Fcs16, where it is verified both
        // against a bit-by-bit reference implementation and against a real captured SABM frame.
        private static readonly ushort[] Table = BuildTable();

        /// <summary>
        /// Builds the reflected CRC-16 table from the polynomial rather than pasting 256 literals.
        /// </summary>
        /// <returns>One entry per possible input octet.</returns>
        /// <remarks>
        /// Generated rather than transcribed on purpose: a table typed out by hand is 256 chances
        /// to introduce a digit nobody will ever notice, and the generator is three lines. The
        /// table this produces is asserted byte-for-byte against the existing verified one in
        /// <c>HdlcFcsTests</c>.
        /// </remarks>
        private static ushort[] BuildTable()
        {
            const ushort ReflectedPolynomial = 0x8408;

            ushort[] table = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                ushort value = (ushort)i;
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((value & 0x0001) != 0)
                    {
                        value = (ushort)((value >> 1) ^ ReflectedPolynomial);
                    }
                    else
                    {
                        value = (ushort)(value >> 1);
                    }
                }
                table[i] = value;
            }
            return table;
        }

        /// <summary>
        /// Folds one octet into a running register.
        /// </summary>
        /// <param name="fcs">The running register; seed it with <see cref="Init"/>.</param>
        /// <param name="value">The octet, <em>after</em> transparency has been removed.</param>
        /// <returns>The updated register.</returns>
        /// <remarks>
        /// Transparency first, then the FCS - X.25 clause 2.2.2.2 puts the unescaping "prior to FCS
        /// calculation" on receive, and the escaping "following completion of the FCS calculation"
        /// on send. Folding stuffed octets is one of the classic ways to get this wrong.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Update(ushort fcs, byte value)
        {
            return (ushort)((fcs >> 8) ^ Table[(fcs ^ value) & 0xFF]);
        }

        /// <summary>
        /// Folds a run of octets into a running register.
        /// </summary>
        /// <param name="fcs">The running register; seed it with <see cref="Init"/>.</param>
        /// <param name="data">Octets with transparency already removed.</param>
        /// <returns>The updated register.</returns>
        public static ushort Update(ushort fcs, ReadOnlySpan<byte> data)
        {
            ushort running = fcs;
            for (int i = 0; i < data.Length; i++)
            {
                running = (ushort)((running >> 8) ^ Table[(running ^ data[i]) & 0xFF]);
            }
            return running;
        }

        /// <summary>
        /// Computes the FCS to transmit after a body: the ones complement of the register.
        /// </summary>
        /// <param name="body">Address, control and information octets, transparency excluded.</param>
        /// <returns>The value to place on the wire, low octet first.</returns>
        public static ushort ComputeTransmitFcs(ReadOnlySpan<byte> body)
        {
            return (ushort)(Update(Init, body) ^ 0xFFFF);
        }

        /// <summary>
        /// Writes the transmit FCS for a body into a destination, in wire order.
        /// </summary>
        /// <param name="body">Address, control and information octets.</param>
        /// <param name="destination">Receives two octets: <b>low order first</b>.</param>
        /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than two octets.</exception>
        /// <remarks>
        /// Low octet first per RFC 1662 section 3.1. X.25 clause 2.2.3.1 words it the other way
        /// round - "commencing with the coefficient of the highest term" - because octets go out
        /// least significant bit first there. Both sentences describe the identical wire pattern.
        /// </remarks>
        public static void WriteTransmitFcs(ReadOnlySpan<byte> body, Span<byte> destination)
        {
            if (destination.Length < 2)
            {
                throw new ArgumentException("An FCS-16 needs two octets.", nameof(destination));
            }

            ushort fcs = ComputeTransmitFcs(body);
            destination[0] = (byte)(fcs & 0xFF);
            destination[1] = (byte)((fcs >> 8) & 0xFF);
        }

        /// <summary>
        /// Tests a running register that has already consumed the content <em>and</em> the received
        /// FCS octets.
        /// </summary>
        /// <param name="fcs">The register after the final received octet.</param>
        /// <returns><see langword="true"/> when it equals <see cref="Good"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsGoodResidue(ushort fcs)
        {
            return fcs == Good;
        }

        /// <summary>
        /// Checks a whole unstuffed frame, FCS included.
        /// </summary>
        /// <param name="frameWithFcs">Address, control, information and the two FCS octets.</param>
        /// <returns>
        /// <see langword="true"/> when the frame is at least <see cref="MinimumFrameLength"/>
        /// octets and the residue is correct.
        /// </returns>
        /// <remarks>
        /// The residue form is used rather than recomputing over the body and comparing, because it
        /// needs no advance knowledge of where the body ends - which is exactly what a streaming
        /// decoder does not have. It also has no awkward edge case on a very short frame.
        /// </remarks>
        public static bool IsValid(ReadOnlySpan<byte> frameWithFcs)
        {
            if (frameWithFcs.Length < MinimumFrameLength)
            {
                return false;
            }

            return IsGoodResidue(Update(Init, frameWithFcs));
        }
    }
}
