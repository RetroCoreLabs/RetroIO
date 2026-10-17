//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Test helper for turning whitespace-separated hex strings into byte arrays.
    /// </summary>
    internal static class LiveTestHex
    {
        /// <summary>
        /// Parses a string of hex byte pairs (whitespace separated) into a byte array.
        /// </summary>
        /// <param name="hex">
        /// The hex text, for example <c>"21 13 00 19"</c>.
        /// </param>
        /// <returns>
        /// The decoded bytes.
        /// </returns>
        public static byte[] Parse(string hex)
        {
            string[] parts = hex.Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            byte[] result = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                result[i] = Convert.ToByte(parts[i], 16);
            }

            return result;
        }
    }
}
