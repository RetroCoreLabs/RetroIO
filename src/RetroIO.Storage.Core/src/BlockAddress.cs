//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Addressing mode for block-level access to disk images.
    /// Different retro systems use different addressing schemes.
    /// </summary>
    public enum BlockAddressMode
    {
        /// <summary>
        /// Logical Block Address (modern, sequential block number).
        /// </summary>
        LBA,

        /// <summary>
        /// Track and Sector addressing (e.g., Commodore 1541, Apple II).
        /// </summary>
        TrackSector,

        /// <summary>
        /// Cylinder, Head, Sector addressing (e.g., hard drives, some floppies).
        /// </summary>
        CylinderHeadSector
    }

    /// <summary>
    /// Block address for low-level disk access.
    /// Supports multiple addressing modes used by different retro systems.
    /// </summary>
    public struct BlockAddress
    {
        /// <summary>
        /// Addressing mode for this block address.
        /// </summary>
        public BlockAddressMode Mode { get; set; }

        /// <summary>
        /// Logical Block Address (used when Mode = LBA).
        /// </summary>
        public uint LBA { get; set; }

        /// <summary>
        /// Track number (used when Mode = TrackSector).
        /// </summary>
        public byte Track { get; set; }

        /// <summary>
        /// Sector number (used when Mode = TrackSector or CylinderHeadSector).
        /// </summary>
        public byte Sector { get; set; }

        /// <summary>
        /// Cylinder number (used when Mode = CylinderHeadSector).
        /// </summary>
        public ushort Cylinder { get; set; }

        /// <summary>
        /// Head number (used when Mode = CylinderHeadSector).
        /// </summary>
        public byte Head { get; set; }

        /// <summary>
        /// Creates a block address using LBA mode.
        /// </summary>
        public static BlockAddress FromLBA(uint lba)
        {
            return new BlockAddress
            {
                Mode = BlockAddressMode.LBA,
                LBA = lba
            };
        }

        /// <summary>
        /// Creates a block address using Track/Sector mode.
        /// </summary>
        public static BlockAddress FromTrackSector(byte track, byte sector)
        {
            return new BlockAddress
            {
                Mode = BlockAddressMode.TrackSector,
                Track = track,
                Sector = sector
            };
        }

        /// <summary>
        /// Creates a block address using Cylinder/Head/Sector mode.
        /// </summary>
        public static BlockAddress FromCHS(ushort cylinder, byte head, byte sector)
        {
            return new BlockAddress
            {
                Mode = BlockAddressMode.CylinderHeadSector,
                Cylinder = cylinder,
                Head = head,
                Sector = sector
            };
        }

        /// <summary>
        /// Returns string representation of the block address.
        /// </summary>
        public override string ToString()
        {
            return Mode switch
            {
                BlockAddressMode.LBA => $"LBA:{LBA}",
                BlockAddressMode.TrackSector => $"T:{Track},S:{Sector}",
                BlockAddressMode.CylinderHeadSector => $"C:{Cylinder},H:{Head},S:{Sector}",
                _ => "Unknown"
            };
        }
    }
}
