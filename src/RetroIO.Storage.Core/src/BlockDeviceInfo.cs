//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Information about block device characteristics.
    /// Describes the physical layout and capabilities of the disk image.
    /// </summary>
    public sealed class BlockDeviceInfo
    {
        /// <summary>
        /// Preferred addressing mode for this device.
        /// </summary>
        public BlockAddressMode AddressMode { get; set; }

        /// <summary>
        /// Block size in bytes (typically 256 or 512).
        /// </summary>
        public int BlockSize { get; set; }

        /// <summary>
        /// Total number of blocks (when using LBA mode).
        /// </summary>
        public uint TotalBlocks { get; set; }

        /// <summary>
        /// Number of tracks (when using Track/Sector mode).
        /// </summary>
        public byte TrackCount { get; set; }

        /// <summary>
        /// Number of cylinders (when using CHS mode).
        /// </summary>
        public ushort CylinderCount { get; set; }

        /// <summary>
        /// Number of heads (when using CHS mode).
        /// </summary>
        public byte HeadCount { get; set; }

        /// <summary>
        /// Sectors per track (may vary by track in some formats like D64).
        /// This represents a typical or maximum value.
        /// </summary>
        public byte SectorsPerTrack { get; set; }

        /// <summary>
        /// Total capacity in bytes.
        /// </summary>
        public long TotalSize { get; set; }

        /// <summary>
        /// Device geometry description (e.g., "35 tracks, variable sectors per track").
        /// </summary>
        public string? GeometryDescription { get; set; }
    }
}
