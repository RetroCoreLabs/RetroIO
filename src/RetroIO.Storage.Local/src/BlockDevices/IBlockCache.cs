//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.BlockDevices
{
    /// <summary>
    /// Block-level cache interface for IBlockDevice implementations.
    /// Provides read/write caching with configurable strategies.
    /// </summary>
    public interface IBlockCache : IDisposable
    {
        /// <summary>
        /// Cache strategy type.
        /// </summary>
        CacheStrategy Strategy { get; }

        /// <summary>
        /// Maximum number of blocks to cache.
        /// </summary>
        int MaxBlocks { get; }

        /// <summary>
        /// Current number of cached blocks.
        /// </summary>
        int CachedBlocks { get; }

        /// <summary>
        /// Cache statistics.
        /// </summary>
        CacheStatistics Statistics { get; }

        /// <summary>
        /// Reads a block from cache or underlying device.
        /// </summary>
        bool TryReadBlock(BlockAddress address, Span<byte> buffer, Func<BlockAddress, Span<byte>, int> readFunc);

        /// <summary>
        /// Writes a block to cache and optionally to underlying device.
        /// </summary>
        void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data, Action<BlockAddress, ReadOnlySpan<byte>> writeFunc);

        /// <summary>
        /// Invalidates a specific block in cache.
        /// </summary>
        void Invalidate(BlockAddress address);

        /// <summary>
        /// Invalidates all blocks in cache.
        /// </summary>
        void InvalidateAll();

        /// <summary>
        /// Flushes all dirty blocks to disk (write-back mode).
        /// </summary>
        /// <param name="writeFunc">
        /// Function to write blocks to underlying device
        /// </param>
        void Flush(Action<BlockAddress, ReadOnlySpan<byte>> writeFunc);

        /// <summary>
        /// Prefetches sequential blocks into cache.
        /// </summary>
        void Prefetch(BlockAddress startAddress, int count, Func<BlockAddress, Span<byte>, int> readFunc);
    }

    /// <summary>
    /// Cache strategy types.
    /// </summary>
    public enum CacheStrategy
    {
        /// <summary>
        /// No caching.
        /// </summary>
        None,

        /// <summary>
        /// Write immediately to disk, cache the result.
        /// </summary>
        WriteThrough,

        /// <summary>
        /// Write to cache, flush to disk later.
        /// </summary>
        WriteBack,

        /// <summary>
        /// Read-only cache (no write caching).
        /// </summary>
        ReadOnly
    }

    /// <summary>
    /// Cache performance statistics.
    /// </summary>
    public class CacheStatistics
    {
        public long Hits { get; set; }
        public long Misses { get; set; }
        public long Writes { get; set; }
        public long Evictions { get; set; }
        public long FlushCount { get; set; }

        public double HitRate => (Hits + Misses) > 0 ? (double)Hits / (Hits + Misses) : 0.0;

        public void Reset()
        {
            Hits = 0;
            Misses = 0;
            Writes = 0;
            Evictions = 0;
            FlushCount = 0;
        }
    }
}
