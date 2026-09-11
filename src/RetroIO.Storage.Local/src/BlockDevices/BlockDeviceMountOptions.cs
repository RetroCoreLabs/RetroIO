//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Buffers;

namespace RetroIO.Storage.Local.BlockDevices
{
    /// <summary>
    /// Options for mounting block devices with various caching and write strategies.
    /// </summary>
    public class BlockDeviceMountOptions
    {
        /// <summary>
        /// Source access mode (actual capability of underlying source).
        /// </summary>
        public AccessMode SourceAccess { get; set; } = AccessMode.ReadWrite;

        /// <summary>
        /// Desired filesystem access mode.
        /// </summary>
        public AccessMode FileSystemAccess { get; set; } = AccessMode.ReadWrite;

        /// <summary>
        /// Cache strategy to use.
        /// </summary>
        public CacheStrategy CacheStrategy { get; set; } = CacheStrategy.WriteThrough;

        /// <summary>
        /// Write strategy when cache is enabled.
        /// </summary>
        public WriteStrategy WriteStrategy { get; set; } = WriteStrategy.WriteThrough;

        /// <summary>
        /// Size of the cache in blocks (0 = auto-calculate based on strategy).
        /// </summary>
        public int CacheSize { get; set; } = 0;

        /// <summary>
        /// Enable read-ahead caching for sequential access.
        /// </summary>
        public bool EnableReadAhead { get; set; } = false;

        /// <summary>
        /// Read-ahead size in blocks.
        /// </summary>
        public int ReadAheadBlocks { get; set; } = 8;

        /// <summary>
        /// Path to delta file for overlay writes (null = no delta).
        /// </summary>
        public string? DeltaFilePath { get; set; }

        /// <summary>
        /// Apply existing delta file on mount.
        /// </summary>
        public bool ApplyDeltaOnMount { get; set; } = true;

        /// <summary>
        /// Delta compression mode: FullBlock or DiffOnly.
        /// </summary>
        public DeltaCompressionMode DeltaCompression { get; set; } = DeltaCompressionMode.FullBlock;

        /// <summary>
        /// Memory pool to use for cache buffers.
        /// </summary>
        public ArrayPool<byte> MemoryPool { get; set; } = ArrayPool<byte>.Shared;

        /// <summary>
        /// Auto-flush dirty blocks after this many seconds of inactivity (0 = manual flush only).
        /// </summary>
        public int AutoFlushSeconds { get; set; } = 30;

        /// <summary>
        /// Validate options and set appropriate defaults.
        /// </summary>
        public void Validate()
        {
            // If source is read-only but FS wants write, must use delta overlay
            if (SourceAccess == AccessMode.ReadOnly && FileSystemAccess == AccessMode.ReadWrite)
            {
                if (WriteStrategy != WriteStrategy.Delta && string.IsNullOrEmpty(DeltaFilePath))
                {
                    throw new InvalidOperationException(
                        "Cannot write to read-only source without delta file. " +
                        "Set WriteStrategy=Delta and provide DeltaFilePath.");
                }
            }

            // Auto-size cache if not specified
            if (CacheSize == 0)
            {
                CacheSize = CacheStrategy switch
                {
                    CacheStrategy.WriteThrough => 256,  // 256 blocks default
                    CacheStrategy.WriteBack => 512,     // More space for deferred writes
                    CacheStrategy.ReadOnly => 128,      // Less space for read-only
                    CacheStrategy.None => 0,            // No cache
                    _ => 256
                };
            }

            // Validate delta file path if using delta strategy
            if (WriteStrategy == WriteStrategy.Delta && string.IsNullOrEmpty(DeltaFilePath))
            {
                throw new InvalidOperationException(
                    "WriteStrategy.Delta requires a valid DeltaFilePath.");
            }

            // Auto-flush only valid for WriteBack
            if (AutoFlushSeconds > 0 && WriteStrategy != WriteStrategy.WriteBack)
            {
                AutoFlushSeconds = 0; // Disable for non-WriteBack strategies
            }
        }
    }

    /// <summary>
    /// Access mode for block device source or filesystem.
    /// </summary>
    public enum AccessMode
    {
        /// <summary>Read-only access.</summary>
        ReadOnly,

        /// <summary>Read and write access.</summary>
        ReadWrite
    }

    /// <summary>
    /// Write strategy for cached block devices.
    /// </summary>
    public enum WriteStrategy
    {
        /// <summary>Write to source and cache immediately (synchronous).</summary>
        WriteThrough,

        /// <summary>Write to cache only, flush to source later (deferred).</summary>
        WriteBack,

        /// <summary>Write to delta file only, preserving original source.</summary>
        Delta
    }

    /// <summary>
    /// Delta file compression mode.
    /// </summary>
    public enum DeltaCompressionMode
    {
        /// <summary>
        /// Store full blocks (faster write, more space).
        /// Best for: Random access patterns, many small changes.
        /// </summary>
        FullBlock,

        /// <summary>
        /// Store only byte-level diffs within blocks (slower write, less space).
        /// Best for: Sequential access, few large changes, text editing.
        /// </summary>
        DiffOnly
    }
}
