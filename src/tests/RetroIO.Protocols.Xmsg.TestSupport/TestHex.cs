//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.TestSupport
{
    /// <summary>
    /// Turns the hex text of a capture into bytes, and bytes back into hex for failure output.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is shared</b></para>
    /// <para>
    /// This conversion was hand-written eleven times across five test projects, and the copies did
    /// not agree. Six stripped nothing, four stripped spaces, and one stripped spaces and newlines.
    /// So the same pasted capture parsed in one file and threw in another, decided by nothing but
    /// which copy the test happened to sit beside - and a capture is exactly the kind of text that
    /// arrives with spaces and line breaks in it.
    /// </para>
    /// <para>
    /// <see cref="FromHex"/> ignores ALL whitespace, which is the superset of what the eleven
    /// copies did: text they accepted is accepted unchanged, and text that only the strict copies
    /// rejected now works everywhere. No test asserted that a badly-spaced string throws, so
    /// nothing depended on the strict behaviour.
    /// </para>
    /// <para><b>Not the same as HexBytes</b></para>
    /// <para>
    /// <c>RetroIO.Protocols.Xmsg.Diagnostics.HexBytes</c> is a production helper for the JSON round-trip, and it
    /// rejects whitespace on purpose - a stray space in a serialised blob is corruption, not
    /// formatting. This one is for hand-pasted capture text, where a space is just a space. They
    /// are deliberately separate rather than one lenient helper used by both.
    /// </para>
    /// </remarks>
    public static class TestHex
    {
        /// <summary>
        /// Decodes hex text into bytes, ignoring any whitespace between the digits.
        /// </summary>
        /// <param name="hex">
        /// The hex text. Spaces, tabs, carriage returns and newlines may appear anywhere and are
        /// skipped, so a capture can be pasted across several lines exactly as it was read.
        /// </param>
        /// <returns>
        /// The decoded bytes. A null or empty string yields an empty array.
        /// </returns>
        /// <exception cref="FormatException">
        /// Thrown when <paramref name="hex"/> holds an odd number of hex digits, or a character
        /// that is neither a hex digit nor whitespace.
        /// </exception>
        public static byte[] FromHex(string? hex)
        {
            if (string.IsNullOrEmpty(hex))
            {
                return Array.Empty<byte>();
            }

            // Count the real digits first so the result is allocated once at the right size. The
            // old copies called Substring per byte, which allocated a throwaway string for every
            // single byte of every capture.
            int digits = 0;
            for (int i = 0; i < hex.Length; i++)
            {
                if (!char.IsWhiteSpace(hex[i]))
                {
                    digits++;
                }
            }

            if ((digits & 1) != 0)
            {
                throw new FormatException(
                    "Hex text must hold an even number of digits; found " + digits + ".");
            }

            byte[] result = new byte[digits / 2];
            int at = 0;
            int high = -1;
            for (int i = 0; i < hex.Length; i++)
            {
                char c = hex[i];
                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                int value = HexDigit(c);
                if (high < 0)
                {
                    high = value;
                    continue;
                }

                result[at++] = (byte)((high << 4) | value);
                high = -1;
            }

            return result;
        }

        /// <summary>
        /// Encodes bytes as uppercase hex with no separators, for printing on a failure.
        /// </summary>
        /// <param name="data">
        /// The bytes to encode.
        /// </param>
        /// <returns>
        /// The hex text, for example <c>2113000E</c>. An empty input yields an empty string.
        /// </returns>
        public static string ToHex(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
            {
                return string.Empty;
            }

            return Convert.ToHexString(data);
        }

        /// <summary>
        /// Converts one hex character to its value.
        /// </summary>
        /// <param name="c">
        /// The character to convert.
        /// </param>
        /// <returns>
        /// The value 0-15.
        /// </returns>
        /// <exception cref="FormatException">
        /// Thrown when <paramref name="c"/> is not a hex digit.
        /// </exception>
        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') { return c - '0'; }
            if (c >= 'a' && c <= 'f') { return (c - 'a') + 10; }
            if (c >= 'A' && c <= 'F') { return (c - 'A') + 10; }

            throw new FormatException("'" + c + "' is not a hex digit.");
        }
    }
}
