//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;

namespace RetroIO.Protocols.Xmsg.Diagnostics
{
    /// <summary>
    /// Helpers for converting byte blobs to and from compact uppercase hex strings.
    /// </summary>
    /// <remarks>
    /// Hex is used for every byte blob carried in the JSON representation so that binary
    /// payloads survive a round-trip through <see cref="System.Text.Json"/> unchanged.
    /// </remarks>
    public static class HexBytes
    {
        /// <summary>
        /// Encodes bytes as an uppercase hex string with no separators.
        /// </summary>
        /// <param name="data">
        /// The bytes to encode. A null or empty input yields an empty string.
        /// </param>
        /// <returns>
        /// The hex string, for example <c>2113000E</c>.
        /// </returns>
        public static string ToHex(byte[]? data)
        {
            if (data == null || data.Length == 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++)
            {
                builder.Append(data[i].ToString("X2"));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Encodes the contents of a span as an uppercase hex string with no separators.
        /// </summary>
        /// <param name="data">
        /// The bytes to encode.
        /// </param>
        /// <returns>
        /// The hex string.
        /// </returns>
        public static string ToHex(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++)
            {
                builder.Append(data[i].ToString("X2"));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Decodes an uppercase or lowercase hex string into a byte array.
        /// </summary>
        /// <param name="hex">
        /// The hex text, with no separators. A null or empty string yields an empty array.
        /// </param>
        /// <returns>
        /// The decoded bytes.
        /// </returns>
        /// <exception cref="FormatException">
        /// Thrown when <paramref name="hex"/> has an odd length or a non-hex character.
        /// </exception>
        public static byte[] FromHex(string? hex)
        {
            if (string.IsNullOrEmpty(hex))
            {
                return Array.Empty<byte>();
            }

            if ((hex.Length & 1) != 0)
            {
                throw new FormatException("Hex string must have an even number of characters.");
            }

            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                int high = HexDigit(hex[i * 2]);
                int low = HexDigit(hex[i * 2 + 1]);
                result[i] = (byte)((high << 4) | low);
            }

            return result;
        }

        /// <summary>
        /// Converts a single hex character to its 0-15 value.
        /// </summary>
        /// <param name="c">
        /// The hex character.
        /// </param>
        /// <returns>
        /// The nibble value 0-15.
        /// </returns>
        /// <exception cref="FormatException">
        /// Thrown when <paramref name="c"/> is not a hex digit.
        /// </exception>
        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return c - '0';
            }

            if (c >= 'A' && c <= 'F')
            {
                return c - 'A' + 10;
            }

            if (c >= 'a' && c <= 'f')
            {
                return c - 'a' + 10;
            }

            throw new FormatException("Invalid hex character '" + c + "'.");
        }
    }
}
