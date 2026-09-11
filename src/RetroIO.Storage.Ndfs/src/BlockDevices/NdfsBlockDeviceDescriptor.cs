//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;

namespace RetroIO.Storage.Ndfs.BlockDevices
{
    /// <summary>
    /// Block device descriptor for NDFS volumes.
    /// Used with GenericBlockDevice - no custom block device needed.
    /// </summary>
    public static class NdfsBlockDeviceDescriptor
    {
        /// <summary>
        /// NDFS page size in bytes (2048 bytes = 1024 words).
        /// This is the standard block size for Nord Disk systems.
        /// </summary>
        public const int NDFS_PAGE_SIZE = 2048;

        /// <summary>
        /// NDFS page size in 16-bit words.
        /// </summary>
        public const int NDFS_WORD_SIZE = 1024;

        /// <summary>
        /// Master block offset within the first page.
        /// The master block occupies the last 32 bytes of the first 2KB page.
        /// </summary>
        public const int MASTER_BLOCK_OFFSET = 2016; // 2048 - 32

        /// <summary>
        /// Directory entry size within master block.
        /// </summary>
        public const int DIRECTORY_ENTRY_SIZE = 32;

        /// <summary>
        /// Creates a block device descriptor for an NDFS volume.
        /// </summary>
        /// <param name="imageSize">Total size of the image file in bytes</param>
        /// <returns>Configured block device descriptor for NDFS</returns>
        public static BlockDeviceDescriptor Create(long imageSize)
        {
            if (imageSize < NDFS_PAGE_SIZE)
            {
                throw new ArgumentException($"Image too small for NDFS. Minimum size is {NDFS_PAGE_SIZE} bytes.", nameof(imageSize));
            }

            if (imageSize % NDFS_PAGE_SIZE != 0)
            {
                throw new ArgumentException($"NDFS image size must be a multiple of {NDFS_PAGE_SIZE} bytes.", nameof(imageSize));
            }

            uint totalPages = (uint)(imageSize / NDFS_PAGE_SIZE);

            return new BlockDeviceDescriptor
            {
                Name = "NDFS",
                BlockSize = NDFS_PAGE_SIZE,
                TotalBlocks = totalPages,
                AddressMode = BlockAddressMode.LBA,
                SupportsVariableGeometry = false,
                ExpectedImageSize = imageSize,
                UniformSectorsPerTrack = 1,  // Not track-based, using LBA
                TotalTracks = 0,              // LBA only, no tracks
                Cylinders = 0,                // LBA only, no cylinders
                Heads = 0                     // LBA only, no heads
            };
        }
    }
}
