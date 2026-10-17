//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// Reads and writes multi-byte values with the byte order named at every call site.
    /// </summary>
    /// <remarks>
    /// <para><b>Written to be ported to C, not just to work in C#</b></para>
    /// <para>
    /// Each method is a plain function over <c>buffer, offset, value</c> - no spans, no
    /// extension methods, no fluent chains - so the C version is a direct transcription:
    /// </para>
    /// <code>
    /// NdEndian.PutBe16(buffer, at, value);      ->   put_be16(buf, at, val);
    /// ushort v = NdEndian.GetBe16(buffer, at);  ->   uint16_t v = get_be16(buf, at);
    /// </code>
    /// <para>
    /// A <c>Span</c> signature has no C equivalent and would force a rewrite at port time, which
    /// is why it is deliberately not used here.
    /// </para>
    /// <para><b>Be is in the name on purpose</b></para>
    /// <para>
    /// A reader should not have to know which class they are looking at to know the byte order.
    /// Everything ND is big-endian - the ND-100, the ND-500 and every wire structure in this
    /// protocol put the high byte first - but saying so at the call site is what stops the order
    /// drifting between two places.
    /// </para>
    /// <para>
    /// The shifts and masks live INSIDE these methods and nowhere else. Open-coding them is also
    /// how a constant like <c>0x6400</c> becomes a compile error: a cast to <c>byte</c> folds
    /// before it truncates.
    /// </para>
    /// <para><b>This arithmetic has been duplicated twice already - keep it in one place</b></para>
    /// <para>
    /// Two codecs once carried the same private reader, which is why a shared class was made at
    /// all. It then happened AGAIN at a larger scale: there were two <c>BigEndian</c> classes in
    /// this assembly, one taking an offset and an internal one under <c>Wire</c> that did not.
    /// Which one a file got was decided by nothing but how deeply its namespace was nested, since
    /// neither was ever named in a <c>using</c>. The arithmetic agreed, so nothing was wrong on
    /// the wire; the hazard was that a method added to one was simply absent from the other, and
    /// a file moved between namespaces would silently bind to a different class with a different
    /// call convention.
    /// </para>
    /// <para>
    /// This class therefore sits in the OUTER <c>RetroIO.Protocols.Xmsg</c> namespace so every
    /// file in the assembly resolves the same one whatever its own nesting. Do not add a second
    /// endian helper - extend this one.
    /// </para>
    /// <para>
    /// One copy is still outside this class: <c>PcapngReader</c> in <c>RetroIO.Protocols.Xmsg.Hdlc</c> has private
    /// <c>ReadU16BigEndian</c>/<c>ReadU32BigEndian</c> methods. That project does not reference
    /// this assembly, so it stays separate until it does.
    /// </para>
    /// <para><b>The span overloads are migration debt, not a second API</b></para>
    /// <para>
    /// This class replaced an older <c>BigEndian</c> whose only shape was a span. The wire
    /// parsers underneath it - <c>SintranHeader</c>, <c>XmsgSubHeader</c>, <c>XroutMessage</c>,
    /// <c>XroutParameter</c>, <c>XsgsyWire</c>, the FA codecs - all take a
    /// <c>ReadOnlySpan</c>, and de-spanning that layer touches hundreds of call sites in other
    /// assemblies. Rather than leave TWO endian classes standing while that waits, the span
    /// shapes were folded in HERE, so there is exactly one name for a big-endian word.
    /// </para>
    /// <para>
    /// A span overload does NOT port: there is no C equivalent of a span, so a transcription has
    /// to reach the array form first. Prefer the array form in new code; the span form exists
    /// only to serve callers that already hold a span.
    /// </para>
    /// </remarks>
    public static class NdEndian
    {
        /// <summary>
        /// Writes a 16-bit value, high byte first.
        /// </summary>
        /// <param name="buffer">
        /// The buffer to write into.
        /// </param>
        /// <param name="offset">
        /// Where the high byte goes.
        /// </param>
        /// <param name="value">
        /// The value to store.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="buffer"/> is null.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the two bytes would not fit at <paramref name="offset"/>.
        /// </exception>
        public static void PutBe16(byte[] buffer, int offset, ushort value)
        {
            Check(buffer, offset, 2);

            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)(value & 0xFF);
        }

        /// <summary>
        /// Reads a 16-bit value, high byte first.
        /// </summary>
        /// <param name="buffer">
        /// The buffer to read from.
        /// </param>
        /// <param name="offset">
        /// Where the high byte sits.
        /// </param>
        /// <returns>
        /// The value.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="buffer"/> is null.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when two bytes are not available at <paramref name="offset"/>.
        /// </exception>
        public static ushort GetBe16(byte[] buffer, int offset)
        {
            Check(buffer, offset, 2);

            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        /// <summary>
        /// Writes a 32-bit value, most significant byte first.
        /// </summary>
        /// <param name="buffer">
        /// The buffer to write into.
        /// </param>
        /// <param name="offset">
        /// Where the most significant byte goes.
        /// </param>
        /// <param name="value">
        /// The value to store.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="buffer"/> is null.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the four bytes would not fit at <paramref name="offset"/>.
        /// </exception>
        public static void PutBe32(byte[] buffer, int offset, uint value)
        {
            Check(buffer, offset, 4);

            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 3] = (byte)(value & 0xFF);
        }

        /// <summary>
        /// Reads a 32-bit value, most significant byte first.
        /// </summary>
        /// <param name="buffer">
        /// The buffer to read from.
        /// </param>
        /// <param name="offset">
        /// Where the most significant byte sits.
        /// </param>
        /// <returns>
        /// The value.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="buffer"/> is null.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when four bytes are not available at <paramref name="offset"/>.
        /// </exception>
        public static uint GetBe32(byte[] buffer, int offset)
        {
            Check(buffer, offset, 4);

            return ((uint)buffer[offset] << 24)
                 | ((uint)buffer[offset + 1] << 16)
                 | ((uint)buffer[offset + 2] << 8)
                 | buffer[offset + 3];
        }

        /// <summary>
        /// Writes a 16-bit value into a span, high byte first.
        /// </summary>
        /// <param name="buffer">
        /// The span to write into.
        /// </param>
        /// <param name="offset">
        /// Where the high byte goes.
        /// </param>
        /// <param name="value">
        /// The value to store.
        /// </param>
        /// <remarks>
        /// The span form does NOT port to C. It exists for callers that already hold a span; new
        /// code should take an array and an offset.
        /// </remarks>
        public static void PutBe16(Span<byte> buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)(value & 0xFF);
        }

        /// <summary>
        /// Reads a 16-bit value from a span, high byte first.
        /// </summary>
        /// <param name="buffer">
        /// The span to read from.
        /// </param>
        /// <param name="offset">
        /// Where the high byte sits.
        /// </param>
        /// <returns>
        /// The value.
        /// </returns>
        /// <remarks>
        /// The span form does NOT port to C. It exists for callers that already hold a span; new
        /// code should take an array and an offset.
        /// </remarks>
        public static ushort GetBe16(ReadOnlySpan<byte> buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        /// <summary>
        /// Writes a 32-bit value into a span, most significant byte first.
        /// </summary>
        /// <param name="buffer">
        /// The span to write into.
        /// </param>
        /// <param name="offset">
        /// Where the most significant byte goes.
        /// </param>
        /// <param name="value">
        /// The value to store.
        /// </param>
        /// <remarks>
        /// The span form does NOT port to C. It exists for callers that already hold a span; new
        /// code should take an array and an offset.
        /// </remarks>
        public static void PutBe32(Span<byte> buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 3] = (byte)(value & 0xFF);
        }

        /// <summary>
        /// Reads a 32-bit value from a span, most significant byte first.
        /// </summary>
        /// <param name="buffer">
        /// The span to read from.
        /// </param>
        /// <param name="offset">
        /// Where the most significant byte sits.
        /// </param>
        /// <returns>
        /// The value.
        /// </returns>
        /// <remarks>
        /// The span form does NOT port to C. It exists for callers that already hold a span; new
        /// code should take an array and an offset.
        /// </remarks>
        public static uint GetBe32(ReadOnlySpan<byte> buffer, int offset)
        {
            return ((uint)buffer[offset] << 24)
                 | ((uint)buffer[offset + 1] << 16)
                 | ((uint)buffer[offset + 2] << 8)
                 | buffer[offset + 3];
        }

        /// <summary>
        /// Refuses a read or write that would run off the end of the buffer.
        /// </summary>
        /// <param name="buffer">
        /// The buffer.
        /// </param>
        /// <param name="offset">
        /// Where the access starts.
        /// </param>
        /// <param name="width">
        /// How many bytes it needs.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="buffer"/> is null.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the access does not fit.
        /// </exception>
        /// <remarks>
        /// The C version drops this - a C caller owns its bounds - which is why it is a separate
        /// method rather than inline conditions cluttering the transcription.
        /// </remarks>
        private static void Check(byte[] buffer, int offset, int width)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0 || offset + width > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(offset),
                    "Need " + width + " bytes at " + offset + " but the buffer holds "
                        + buffer.Length + ".");
            }
        }
    }
}
