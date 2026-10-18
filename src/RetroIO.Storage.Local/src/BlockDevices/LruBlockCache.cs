//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers;
using System.Collections.Generic;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.BlockDevices
{
    /// <summary>
    /// LRU (Least Recently Used) block cache implementation.
    /// Uses ArrayPool for zero-copy performance.
    /// </summary>
    public class LruBlockCache : IBlockCache
    {
        private readonly int _maxBlocks;
        private readonly int _blockSize;
        private readonly CacheStrategy _strategy;
        private readonly Dictionary<long, CacheEntry> _cache;
        private readonly LinkedList<long> _lruList;
        private readonly CacheStatistics _statistics;
        private readonly object _lock = new object();

        /// <summary>
        /// Gets the write strategy given to the constructor.
        /// </summary>
        public CacheStrategy Strategy => _strategy;
        /// <summary>
        /// Gets the most blocks the cache keeps before it removes the least recently used one.
        /// </summary>
        public int MaxBlocks => _maxBlocks;
        /// <summary>
        /// Gets the number of blocks in the cache now.
        /// </summary>
        public int CachedBlocks { get { lock (_lock) return _cache.Count; } }
        /// <summary>
        /// Gets the hit, miss, write, eviction and flush counters of this cache.
        /// </summary>
        public CacheStatistics Statistics => _statistics;

        /// <summary>
        /// Creates an empty cache.
        /// </summary>
        /// <param name="maxBlocks">
        /// The most blocks the cache keeps.
        /// </param>
        /// <param name="blockSize">
        /// Size of one block in bytes.
        /// </param>
        /// <param name="strategy">
        /// The write strategy.
        /// </param>
        public LruBlockCache(int maxBlocks, int blockSize, CacheStrategy strategy = CacheStrategy.WriteThrough)
        {
            _maxBlocks = maxBlocks;
            _blockSize = blockSize;
            _strategy = strategy;
            _cache = new Dictionary<long, CacheEntry>(maxBlocks);
            _lruList = new LinkedList<long>();
            _statistics = new CacheStatistics();
        }

        /// <summary>
        /// Reads a block from the cache, or on a miss from the device through <paramref name="readFunc"/>,
        /// and adds the block read from the device to the cache.
        /// </summary>
        /// <remarks>
        /// Blocks read from the device are cached whatever the <see cref="Strategy"/> is,
        /// including <see cref="CacheStrategy.None"/>.
        /// </remarks>
        /// <param name="address">
        /// The block to read. Only its LBA is used as the cache key.
        /// </param>
        /// <param name="buffer">
        /// Receives the block data.
        /// </param>
        /// <param name="readFunc">
        /// Reads a block from the underlying device and returns the number of bytes read.
        /// </param>
        /// <returns>
        /// <c>true</c> on a cache hit or when <paramref name="readFunc"/> read more than zero bytes;
        /// <c>false</c> when <paramref name="readFunc"/> returned zero or less.
        /// </returns>
        public bool TryReadBlock(BlockAddress address, Span<byte> buffer, Func<BlockAddress, Span<byte>, int> readFunc)
        {
            long key = GetCacheKey(address);

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    // Cache hit
                    _statistics.Hits++;
                    entry.Data.AsSpan(0, Math.Min(_blockSize, buffer.Length)).CopyTo(buffer);

                    // Move to front of LRU
                    _lruList.Remove(entry.LruNode);
                    entry.LruNode = _lruList.AddFirst(key);

                    return true;
                }

                // Cache miss
                _statistics.Misses++;
            }

            // Read from device
            int bytesRead = readFunc(address, buffer);
            if (bytesRead <= 0)
                return false;

            // Add to cache
            AddToCache(key, buffer.Slice(0, bytesRead));
            return true;
        }

        /// <summary>
        /// Writes a block, following the <see cref="Strategy"/> of this cache.
        /// </summary>
        /// <remarks>
        /// What each strategy does:
        ///  - <see cref="CacheStrategy.WriteThrough"/>: writes to the device at once and keeps a clean copy in the cache.
        ///  - <see cref="CacheStrategy.WriteBack"/>: writes to the cache only and marks the block dirty until <see cref="Flush"/>.
        ///  - <see cref="CacheStrategy.ReadOnly"/> and <see cref="CacheStrategy.None"/>: write to the device only and do not cache the block.
        /// </remarks>
        /// <param name="address">
        /// The block to write. Only its LBA is used as the cache key.
        /// </param>
        /// <param name="data">
        /// The block data.
        /// </param>
        /// <param name="writeFunc">
        /// Writes a block to the underlying device.
        /// </param>
        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data, Action<BlockAddress, ReadOnlySpan<byte>> writeFunc)
        {
            long key = GetCacheKey(address);

            lock (_lock)
            {
                _statistics.Writes++;

                switch (_strategy)
                {
                    case CacheStrategy.WriteThrough:
                        // Write immediately to disk
                        writeFunc(address, data);

                        // Update cache
                        if (_cache.TryGetValue(key, out var entry))
                        {
                            data.CopyTo(entry.Data);
                            entry.IsDirty = false;

                            // Move to front
                            _lruList.Remove(entry.LruNode);
                            entry.LruNode = _lruList.AddFirst(key);
                        }
                        else
                        {
                            AddToCache(key, data);
                        }
                        break;

                    case CacheStrategy.WriteBack:
                        // Write to cache only, mark dirty
                        if (_cache.TryGetValue(key, out var backEntry))
                        {
                            data.CopyTo(backEntry.Data);
                            backEntry.IsDirty = true;

                            // Move to front
                            _lruList.Remove(backEntry.LruNode);
                            backEntry.LruNode = _lruList.AddFirst(key);
                        }
                        else
                        {
                            var newEntry = AddToCache(key, data);
                            newEntry.IsDirty = true;
                        }
                        break;

                    case CacheStrategy.ReadOnly:
                        // Just write through, don't cache writes
                        writeFunc(address, data);
                        break;

                    case CacheStrategy.None:
                        writeFunc(address, data);
                        break;
                }
            }
        }

        /// <summary>
        /// Removes one block from the cache.
        /// </summary>
        /// <remarks>
        /// A dirty block is dropped without being written to the device.
        /// </remarks>
        /// <param name="address">
        /// The block to remove. Only its LBA is used as the cache key.
        /// </param>
        public void Invalidate(BlockAddress address)
        {
            long key = GetCacheKey(address);

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    _lruList.Remove(entry.LruNode);
                    ArrayPool<byte>.Shared.Return(entry.Data);
                    _cache.Remove(key);
                }
            }
        }

        /// <summary>
        /// Removes every block from the cache.
        /// </summary>
        /// <remarks>
        /// Dirty blocks are dropped without being written to the device.
        /// </remarks>
        public void InvalidateAll()
        {
            lock (_lock)
            {
                foreach (var entry in _cache.Values)
                {
                    ArrayPool<byte>.Shared.Return(entry.Data);
                }
                _cache.Clear();
                _lruList.Clear();
            }
        }

        /// <summary>
        /// Writes all dirty blocks to the device and marks them clean.
        /// </summary>
        /// <remarks>
        /// The flush counter in <see cref="Statistics"/> goes up on every call, but blocks are only
        /// written when the <see cref="Strategy"/> is <see cref="CacheStrategy.WriteBack"/>.
        /// </remarks>
        /// <param name="writeFunc">
        /// Writes a block to the underlying device.
        /// </param>
        public void Flush(Action<BlockAddress, ReadOnlySpan<byte>> writeFunc)
        {
            lock (_lock)
            {
                _statistics.FlushCount++;

                // Only relevant for write-back mode
                if (_strategy != CacheStrategy.WriteBack)
                    return;

                // Write all dirty blocks to disk
                foreach (var kvp in _cache)
                {
                    if (kvp.Value.IsDirty)
                    {
                        var address = BlockAddress.FromLBA((uint)kvp.Key);
                        writeFunc(address, kvp.Value.Data.AsSpan(0, _blockSize));
                        kvp.Value.IsDirty = false;
                    }
                }
            }
        }

        /// <summary>
        /// Reads a run of blocks into the cache, skipping blocks already cached.
        /// </summary>
        /// <param name="startAddress">
        /// The first block.
        /// </param>
        /// <param name="count">
        /// The number of blocks to read.
        /// </param>
        /// <param name="readFunc">
        /// Reads a block from the underlying device.
        /// </param>
        public void Prefetch(BlockAddress startAddress, int count, Func<BlockAddress, Span<byte>, int> readFunc)
        {
            for (int i = 0; i < count; i++)
            {
                var addr = BlockAddress.FromLBA((uint)(startAddress.LBA + i));
                long key = GetCacheKey(addr);

                lock (_lock)
                {
                    if (_cache.ContainsKey(key))
                        continue; // Already cached
                }

                // Read and cache
                byte[] buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
                try
                {
                    int bytesRead = readFunc(addr, buffer);
                    if (bytesRead > 0)
                    {
                        AddToCache(key, buffer.AsSpan(0, bytesRead));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        /// <summary>
        /// Returns the cached buffers to the pool and empties the cache.
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var entry in _cache.Values)
                {
                    ArrayPool<byte>.Shared.Return(entry.Data);
                }
                _cache.Clear();
                _lruList.Clear();
            }
        }

        private CacheEntry AddToCache(long key, ReadOnlySpan<byte> data)
        {
            // Evict if necessary
            while (_cache.Count >= _maxBlocks && _lruList.Last != null)
            {
                long evictKey = _lruList.Last.Value;
                if (_cache.TryGetValue(evictKey, out var evictEntry))
                {
                    _lruList.Remove(evictEntry.LruNode);
                    ArrayPool<byte>.Shared.Return(evictEntry.Data);
                    _cache.Remove(evictKey);
                    _statistics.Evictions++;
                }
            }

            // Create new entry
            byte[] buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
            data.CopyTo(buffer);

            var entry = new CacheEntry
            {
                Data = buffer,
                IsDirty = false,
                LruNode = _lruList.AddFirst(key)
            };

            _cache[key] = entry;
            return entry;
        }

        private static long GetCacheKey(BlockAddress address)
        {
            // Simple key: just use LBA
            return address.LBA;
        }

        private class CacheEntry
        {
            public byte[] Data { get; set; } = Array.Empty<byte>();
            public bool IsDirty { get; set; }
            public LinkedListNode<long> LruNode { get; set; } = null!;
        }
    }
}
