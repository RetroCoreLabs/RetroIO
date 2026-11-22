//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Tn3270
{
    /// <summary>
    /// Two-byte 3270 buffer addresses in 12-bit and 14-bit form (GA23-0059-4 p. 4-4 and
    /// Appendix D, p. D-1, D-2). 16-bit addressing needs a partition created by Create Partition and
    /// is not handled here.
    /// </summary>
    public static class BufferAddress
    {
        // Figure D-1 (p. D-2): the EBCDIC byte sent for each 6-bit value 0-63. Copied from the table
        // in RetroTerm docs/spec/IBM3270/3270-DATA-STREAM.md section 6.3, read from the page image.
        private static readonly byte[] SixBitToEbcdic =
        {
            0x40, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F,
            0x50, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F,
            0x60, 0x61, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F,
            0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
        };

        /// <summary>
        /// The EBCDIC byte Figure D-1 gives for a 6-bit value. Also used for the top two bits of a
        /// field attribute or WCC that the terminal sends inbound.
        /// </summary>
        /// <param name="sixBits">
        /// A value 0 to 63.
        /// </param>
        /// <returns>
        /// The byte to send.
        /// </returns>
        public static byte EncodeSixBits(int sixBits) => SixBitToEbcdic[sixBits & 0x3F];

        /// <summary>
        /// Decodes an outbound address.
        /// </summary>
        /// <param name="first">
        /// The first address byte.
        /// </param>
        /// <param name="second">
        /// The second address byte.
        /// </param>
        /// <param name="address">
        /// The decoded address.
        /// </param>
        /// <returns>
        /// False for flag bits B'10', which the manual reserves: "the data stream is rejected"
        /// (p. 4-4). x3270 instead reads it as 12-bit; tnz rejects it, as the manual says.
        /// </returns>
        public static bool TryDecode(byte first, byte second, out int address)
        {
            switch (first >> 6)
            {
                case 0b00:
                    // 14-bit binary: low 6 bits of byte 1, then all 8 bits of byte 2.
                    address = ((first & 0x3F) << 8) | second;
                    return true;

                case 0b01:
                case 0b11:
                    // 12-bit coded: low 6 bits of each byte.
                    address = ((first & 0x3F) << 6) | (second & 0x3F);
                    return true;

                default:
                    address = 0;
                    return false;
            }
        }

        /// <summary>
        /// Encodes an inbound address. 12-bit form when the buffer is no larger than 4096 cells,
        /// 14-bit otherwise.
        /// </summary>
        /// <remarks>
        /// The manual disagrees with itself at exactly 4096 cells: p. 4-4 says 12-bit only when the
        /// size is "less than 4096", p. 6-88 "equal to or less than". 12-bit form cannot hold 4096
        /// anyway (its largest address is 4095), and a 4096-cell buffer has no address 4096, so the
        /// two readings give the same bytes for every real address. x3270 follows p. 6-88.
        /// </remarks>
        /// <param name="address">
        /// The address.
        /// </param>
        /// <param name="bufferSize">
        /// Rows times columns.
        /// </param>
        /// <param name="output">
        /// Two bytes are written here.
        /// </param>
        public static void Encode(int address, int bufferSize, Span<byte> output)
        {
            if (address < 0 || address > 0x3FFF)
            {
                throw new ArgumentOutOfRangeException(nameof(address), address, "A 14-bit address is 0 to 16383.");
            }

            if (bufferSize <= 4096)
            {
                output[0] = SixBitToEbcdic[(address >> 6) & 0x3F];
                output[1] = SixBitToEbcdic[address & 0x3F];
            }
            else
            {
                output[0] = (byte)((address >> 8) & 0x3F);
                output[1] = (byte)(address & 0xFF);
            }
        }
    }
}
