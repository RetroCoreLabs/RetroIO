//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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

        public CacheStrategy Strategy => _strategy;
        public int MaxBlocks => _maxBlocks;
        public int CachedBlocks { get { lock (_lock) return _cache.Count; } }
        public CacheStatistics Statistics => _statistics;

        public LruBlockCache(int maxBlocks, int blockSize, CacheStrategy strategy = CacheStrategy.WriteThrough)
        {
            _maxBlocks = maxBlocks;
            _blockSize = blockSize;
            _strategy = strategy;
            _cache = new Dictionary<long, CacheEntry>(maxBlocks);
            _lruList = new LinkedList<long>();
            _statistics = new CacheStatistics();
        }

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
