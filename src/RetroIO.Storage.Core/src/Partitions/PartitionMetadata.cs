//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Storage.Core.Partitions
{
    /// <summary>
    /// Type of partition table detected.
    /// </summary>
    public enum PartitionTableType
    {
        /// <summary>
        /// No partition table detected (raw filesystem).
        /// </summary>
        None = 0,

        /// <summary>
        /// BSD disk label (partitions a-h).
        /// </summary>
        BsdDiskLabel = 1,

        /// <summary>
        /// Master Boot Record (MBR) partition table (partitions 0-3).
        /// </summary>
        Mbr = 2,

        /// <summary>
        /// GUID Partition Table (GPT) - future support.
        /// </summary>
        Gpt = 3,

        /// <summary>
        /// Sun disk label (Sun VTOC) partition table (partitions a-h or 0-7).
        /// Used by SunOS, Solaris, and other Sun Unix variants.
        /// </summary>
        SunLabel = 4
    }

    /// <summary>
    /// Metadata describing a detected partition.
    /// Unified structure used for BSD, MBR, and GPT partitions.
    /// </summary>
    public class PartitionMetadata
    {
        /// <summary>
        /// Partition identifier (e.g., "a", "0", "1", or GUID for GPT).
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Display text for UI presentation (e.g., "a - V7-1K (2.11BSD) (7.8 MB)").
        /// </summary>
        public string DisplayText { get; set; } = string.Empty;

        /// <summary>
        /// Partition offset in 512-byte sectors.
        /// </summary>
        public uint OffsetSectors { get; set; }

        /// <summary>
        /// Partition size in 512-byte sectors.
        /// </summary>
        public uint SizeSectors { get; set; }

        /// <summary>
        /// Filesystem type code (BSD type code or MBR type code).
        /// </summary>
        public byte FileSystemTypeCode { get; set; }

        /// <summary>
        /// Type of partition table this partition belongs to.
        /// </summary>
        public PartitionTableType TableType { get; set; }

        /// <summary>
        /// Recommended URN for mounting this partition (e.g., "bsd:///E:/disk.img#a").
        /// </summary>
        public string RecommendedUrn { get; set; } = string.Empty;

        /// <summary>
        /// Indicates if this partition is marked as bootable (MBR only, always false for BSD).
        /// </summary>
        public bool IsBootable { get; set; }

        // Helper properties

        /// <summary>
        /// Partition size in bytes.
        /// </summary>
        public long SizeBytes => (long)SizeSectors * 512;

        /// <summary>
        /// Partition size in megabytes.
        /// </summary>
        public double SizeMB => SizeBytes / (1024.0 * 1024.0);

        /// <summary>
        /// Partition offset in bytes.
        /// </summary>
        public long OffsetBytes => (long)OffsetSectors * 512;
    }
}
