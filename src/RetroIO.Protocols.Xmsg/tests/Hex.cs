//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Turns the hex in a capture into bytes, for tests that assert against real wire traffic.
    /// </summary>
    /// <remarks>
    /// The captures under <c>DOC\captures\</c> record each frame as one unbroken hex string, so
    /// pasting a body straight out of one is the least error-prone way to write a test. Spaces are
    /// allowed so a long body can be grouped for reading.
    /// </remarks>
    public static class Hex
    {
        /// <summary>
        /// Parses a hex string into bytes.
        /// </summary>
        /// <param name="hex">
        /// The hex, optionally with spaces between groups.
        /// </param>
        /// <returns>
        /// The bytes.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="hex"/> has an odd number of digits.
        /// </exception>
        public static byte[] ToBytes(string hex)
        {
            if (hex == null)
            {
                throw new ArgumentNullException(nameof(hex));
            }

            string packed = hex.Replace(" ", string.Empty);

            if ((packed.Length & 1) != 0)
            {
                throw new ArgumentException(
                    "A hex string needs an even number of digits, got " + packed.Length + ".",
                    nameof(hex));
            }

            byte[] result = new byte[packed.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = Convert.ToByte(packed.Substring(i * 2, 2), 16);
            }

            return result;
        }
    }
}
