//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Storage.Ndfs.Structures
{
    /// <summary>
    /// Represents a pointer to a block in the NDFS filesystem.
    /// Block pointers are 32-bit values with type in top 2 bits, block ID in bottom 30 bits.
    /// </summary>
    public class BlockPointer
    {
        /// <summary>
        /// The block/page ID (30-bit LBA address, bits 29-0).
        /// </summary>
        public uint BlockID { get; set; }

        /// <summary>
        /// Type of block pointer (2 bits, bits 31-30).
        /// </summary>
        public PointerType Type { get; set; }

        /// <summary>
        /// NDFS pointer types encoded in top 2 bits.
        /// </summary>
        public enum PointerType
        {
            Contiguous = 0,   // b00 - Sequential blocks
            Indexed = 1,      // b01 - Index block pointing to data blocks
            SubIndexed = 2,   // b10 - Index block pointing to index blocks
            Reserved = 3      // b11 - Invalid/Reserved
        }

        /// <summary>
        /// Default constructor.
        /// </summary>
        public BlockPointer()
        {
            BlockID = 0;
            Type = PointerType.Contiguous;
        }

        /// <summary>
        /// Creates a block pointer from a 32-bit native value.
        /// </summary>
        public BlockPointer(uint blockPointer)
        {
            InitializePointer(blockPointer);
        }

        /// <summary>
        /// Creates a block pointer with explicit block ID and type.
        /// </summary>
        public BlockPointer(uint blockID, PointerType type)
        {
            Type = type;
            BlockID = blockID & 0x3FFFFFFF; // Ensure only 30 bits
        }

        /// <summary>
        /// Initialize from 32-bit native value.
        /// </summary>
        internal void InitializePointer(uint blockPointer)
        {
            Type = (PointerType)(blockPointer >> 30);      // Top 2 bits
            BlockID = blockPointer & 0x3FFFFFFF;           // Bottom 30 bits
        }

        /// <summary>
        /// Return native 32-bit NDFS representation of pointer.
        /// </summary>
        public uint Native
        {
            get
            {
                return ((uint)Type << 30) | (BlockID & 0x3FFFFFFF);
            }
        }

        /// <summary>
        /// Checks if this is a valid block pointer.
        /// </summary>
        public bool IsValid()
        {
            return BlockID > 0 && Type != PointerType.Reserved;
        }

        /// <summary>
        /// Checks that this pointer could plausibly address a block on a device of
        /// <paramref name="devicePages"/> pages: the type must not be Reserved (11), and the block
        /// id must be a real, allocatable page — at or above the block-7 floor (blocks 0-6 are
        /// system-reserved: boot/label plus the object/user/bit-file roots) and strictly inside the
        /// device.
        /// </summary>
        /// <remarks>
        /// <see cref="IsValid"/> only rejects blockId 0 and the Reserved type, so a garbage word
        /// such as 0x2A3F1C55 (blockId ~708 million) passes it. Detection must bound the pointer
        /// against the actual device size, otherwise almost any 2048-byte-aligned file whose bytes
        /// at offset 2016 happen to look like a pointer scores as NDFS.
        /// </remarks>
        public bool IsValidWithin(uint devicePages)
        {
            const uint FIRST_ALLOC_BLOCK = 7;

            if (Type == PointerType.Reserved)
                return false;

            return BlockID >= FIRST_ALLOC_BLOCK && BlockID < devicePages;
        }

        /// <summary>
        /// Creates a block pointer from raw bytes (big-endian).
        /// NDFS uses 2 words (4 bytes) for a block pointer.
        /// </summary>
        public static BlockPointer FromBytes(byte[] data, int offset)
        {
            if (data == null || data.Length < offset + 4)
                throw new ArgumentException("Insufficient data for block pointer");

            // Read as big-endian 32-bit value
            uint value = (uint)((data[offset] << 24) |
                               (data[offset + 1] << 16) |
                               (data[offset + 2] << 8) |
                               data[offset + 3]);

            return new BlockPointer(value);
        }

        /// <summary>
        /// Converts the block pointer to raw bytes (big-endian).
        /// </summary>
        public byte[] ToBytes()
        {
            uint value = Native;

            return new byte[]
            {
                (byte)(value >> 24),
                (byte)(value >> 16),
                (byte)(value >> 8),
                (byte)value
            };
        }

        public override string ToString()
        {
            return $"{BlockID} ({Type})";
        }
    }
}
