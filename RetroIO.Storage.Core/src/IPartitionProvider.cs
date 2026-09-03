//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Collections.Generic;

namespace RetroIO.Storage.Core;

/// <summary>
/// Optional interface for filesystem drivers that support partitioned disk images.
/// Drivers implementing this interface can enumerate and select partitions within a disk image.
/// </summary>
public interface IPartitionProvider
{
    /// <summary>
    /// Indicates whether the current disk image contains a partition table.
    /// </summary>
    /// <returns>True if partition table is detected; false otherwise.</returns>
    bool SupportsPartitions();

    /// <summary>
    /// Lists all available partitions in the disk image.
    /// </summary>
    /// <returns>List of partition identifiers and metadata. Returns empty list if no partitions found.</returns>
    IReadOnlyList<PartitionMetadata> ListPartitions();

    /// <summary>
    /// Selects a specific partition to mount.
    /// </summary>
    /// <param name="partitionId">Partition identifier (e.g., "a", "b" for BSD; "0", "1" for MBR).</param>
    /// <returns>True if partition was successfully selected; false if partition not found.</returns>
    bool SelectPartition(string partitionId);

    /// <summary>
    /// Gets the currently selected partition identifier, or null if no partition selected or in raw mode.
    /// </summary>
    string? CurrentPartition { get; }

    /// <summary>
    /// Sets raw filesystem mode (no partition table).
    /// Must be called before mounting the filesystem.
    /// </summary>
    void SetRawMode();
}

/// <summary>
/// Metadata describing a single partition within a disk image.
/// </summary>
public class PartitionMetadata
{
    /// <summary>
    /// Unique identifier for this partition (e.g., "a", "0", "1").
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable display text (e.g., "a - V7-1K (2.11BSD) (7.8 MB)").
    /// </summary>
    public required string DisplayText { get; init; }

    /// <summary>
    /// Partition starting offset in 512-byte sectors.
    /// </summary>
    public required uint OffsetSectors { get; init; }

    /// <summary>
    /// Partition size in 512-byte sectors.
    /// </summary>
    public required uint SizeSectors { get; init; }

    /// <summary>
    /// Filesystem type code (BSD type or MBR type code).
    /// </summary>
    public required byte FileSystemTypeCode { get; init; }

    /// <summary>
    /// Recommended URN for mounting this partition (e.g., "bsd:///E:/disk.img#a").
    /// </summary>
    public required string RecommendedUrn { get; init; }

    /// <summary>
    /// Indicates if this partition is marked as bootable (MBR only).
    /// </summary>
    public bool IsBootable { get; init; }

    /// <summary>
    /// Type of partition table this partition belongs to.
    /// </summary>
    public required PartitionTableType TableType { get; init; }
}

/// <summary>
/// Type of partition table detected in disk image.
/// </summary>
public enum PartitionTableType
{
    /// <summary>
    /// No partition table or unknown format.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// BSD disk label (typically at sector 1).
    /// </summary>
    BsdDiskLabel = 1,

    /// <summary>
    /// Master Boot Record (MBR) partition table.
    /// </summary>
    Mbr = 2,

    /// <summary>
    /// GUID Partition Table (GPT).
    /// </summary>
    Gpt = 3,

    /// <summary>
    /// Sun disk label (Sun VTOC) partition table (partitions a-h or 0-7).
    /// Used by SunOS, Solaris, and other Sun Unix variants.
    /// </summary>
    SunLabel = 4
}
