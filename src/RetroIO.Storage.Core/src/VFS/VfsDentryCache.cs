//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace RetroIO.Storage.Core.VFS
{
    /// <summary>
    /// LRU cache for directory listings to avoid repeated filesystem access.
    /// Equivalent to Linux kernel dentry (directory entry) cache.
    /// </summary>
    /// <remarks>
    /// The dentry cache dramatically improves performance for directory navigation by caching
    /// directory listings in memory. When users navigate back and forth between directories,
    /// cached entries are returned instantly instead of re-reading from the filesystem.
    ///
    /// Cache entries expire based on configurable TTL (Time To Live):
    /// - Local filesystems: Long TTL (60 minutes default) - disk images rarely change
    /// - Remote filesystems: Short TTL (5 minutes default) - files may change on server
    ///
    /// Cache is automatically invalidated on write operations (WriteFile, DeleteFile, etc.)
    /// to ensure consistency.
    /// </remarks>
    public class VfsDentryCache
    {
        private readonly int _maxEntries;
        private readonly TimeSpan _defaultTtl;
        private readonly TimeSpan _remoteTtl;
        private readonly ConcurrentDictionary<string, CacheEntry> _cache;
        private readonly object _evictionLock = new object();

        // Statistics tracking
        private long _hits;
        private long _misses;
        private long _evictions;
        private long _invalidations;

        /// <summary>
        /// Initializes a new dentry cache.
        /// </summary>
        /// <param name="maxEntries">
        /// Maximum number of cached directory listings
        /// </param>
        /// <param name="defaultTtl">
        /// TTL for local filesystem entries
        /// </param>
        /// <param name="remoteTtl">
        /// TTL for remote filesystem entries (usually shorter)
        /// </param>
        public VfsDentryCache(int maxEntries = 1000, TimeSpan? defaultTtl = null, TimeSpan? remoteTtl = null)
        {
            if (maxEntries <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxEntries), "Must be positive");

            _maxEntries = maxEntries;
            _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(60);
            _remoteTtl = remoteTtl ?? TimeSpan.FromMinutes(5);
            _cache = new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Try to get a cached directory listing.
        /// </summary>
        /// <param name="urnPath">
        /// URN path to directory (e.g., "urn:retrofs:local:0:d64:/SYSTEM")
        /// </param>
        /// <param name="isRemote">
        /// Whether this is a remote filesystem (affects TTL)
        /// </param>
        /// <returns>
        /// Cached directory entries, or null if not in cache or expired
        /// </returns>
        public DirectoryEntry[]? TryGetDirectory(string urnPath, bool isRemote)
        {
            if (string.IsNullOrEmpty(urnPath))
                throw new ArgumentNullException(nameof(urnPath));

            if (!_cache.TryGetValue(urnPath, out var entry))
            {
                System.Threading.Interlocked.Increment(ref _misses);
                return null;
            }

            // Check if expired
            var ttl = isRemote ? _remoteTtl : _defaultTtl;
            if (DateTime.UtcNow - entry.CachedAt > ttl)
            {
                // Expired - remove from cache
                _cache.TryRemove(urnPath, out _);
                System.Threading.Interlocked.Increment(ref _misses);
                return null;
            }

            // Update last access time for LRU
            entry.LastAccessedAt = DateTime.UtcNow;
            System.Threading.Interlocked.Increment(ref _hits);
            return entry.Entries;
        }

        /// <summary>
        /// Cache a directory listing.
        /// </summary>
        /// <param name="urnPath">
        /// URN path to directory
        /// </param>
        /// <param name="entries">
        /// Directory entries to cache
        /// </param>
        /// <param name="isRemote">
        /// Whether this is a remote filesystem
        /// </param>
        public void CacheDirectory(string urnPath, DirectoryEntry[] entries, bool isRemote)
        {
            if (string.IsNullOrEmpty(urnPath))
                throw new ArgumentNullException(nameof(urnPath));
            if (entries == null)
                throw new ArgumentNullException(nameof(entries));

            // Check if we need to evict entries (LRU)
            if (_cache.Count >= _maxEntries)
            {
                EvictLru();
            }

            var entry = new CacheEntry
            {
                Entries = entries,
                CachedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow,
                IsRemote = isRemote
            };

            _cache[urnPath] = entry;
        }

        /// <summary>
        /// Invalidate a specific directory entry.
        /// Called when the directory is modified (file added/deleted/renamed).
        /// </summary>
        /// <param name="urnPath">
        /// URN path to directory to invalidate
        /// </param>
        public void Invalidate(string urnPath)
        {
            if (string.IsNullOrEmpty(urnPath))
                return;

            if (_cache.TryRemove(urnPath, out _))
            {
                System.Threading.Interlocked.Increment(ref _invalidations);
            }
        }

        /// <summary>
        /// Invalidate all directory entries with a specific path prefix.
        /// Used when unmounting a volume or invalidating an entire subtree.
        /// </summary>
        /// <param name="urnPathPrefix">
        /// URN path prefix (e.g., "urn:retrofs:local:0:d64:")
        /// </param>
        public void InvalidatePrefix(string urnPathPrefix)
        {
            if (string.IsNullOrEmpty(urnPathPrefix))
                return;

            var keysToRemove = new List<string>();

            // Find all keys with this prefix
            foreach (var kvp in _cache)
            {
                if (kvp.Key.StartsWith(urnPathPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            // Remove them
            for (int i = 0; i < keysToRemove.Count; i++)
            {
                if (_cache.TryRemove(keysToRemove[i], out _))
                {
                    System.Threading.Interlocked.Increment(ref _invalidations);
                }
            }
        }

        /// <summary>
        /// Clear all cached entries.
        /// </summary>
        public void Clear()
        {
            _cache.Clear();
            System.Threading.Interlocked.Exchange(ref _hits, 0);
            System.Threading.Interlocked.Exchange(ref _misses, 0);
            System.Threading.Interlocked.Exchange(ref _evictions, 0);
            System.Threading.Interlocked.Exchange(ref _invalidations, 0);
        }

        /// <summary>
        /// Get cache statistics.
        /// </summary>
        public CacheStatistics GetStatistics()
        {
            return new CacheStatistics
            {
                Hits = System.Threading.Interlocked.Read(ref _hits),
                Misses = System.Threading.Interlocked.Read(ref _misses),
                Evictions = System.Threading.Interlocked.Read(ref _evictions),
                Invalidations = System.Threading.Interlocked.Read(ref _invalidations),
                CurrentSize = _cache.Count,
                MaxSize = _maxEntries
            };
        }

        /// <summary>
        /// Evict least recently used entry.
        /// </summary>
        private void EvictLru()
        {
            lock (_evictionLock)
            {
                // Double-check after acquiring lock
                if (_cache.Count < _maxEntries)
                    return;

                // Find LRU entry
                string? lruKey = null;
                DateTime oldestAccess = DateTime.MaxValue;

                foreach (var kvp in _cache)
                {
                    if (kvp.Value.LastAccessedAt < oldestAccess)
                    {
                        oldestAccess = kvp.Value.LastAccessedAt;
                        lruKey = kvp.Key;
                    }
                }

                // Remove it
                if (lruKey != null && _cache.TryRemove(lruKey, out _))
                {
                    System.Threading.Interlocked.Increment(ref _evictions);
                }
            }
        }

        private class CacheEntry
        {
            public DirectoryEntry[] Entries { get; set; } = Array.Empty<DirectoryEntry>();
            public DateTime CachedAt { get; set; }
            public DateTime LastAccessedAt { get; set; }
            public bool IsRemote { get; set; }
        }
    }

    /// <summary>
    /// Statistics about cache performance.
    /// </summary>
    public class CacheStatistics
    {
        /// <summary>
        /// Number of cache hits (found in cache)
        /// </summary>
        public long Hits { get; set; }

        /// <summary>
        /// Number of cache misses (not found or expired)
        /// </summary>
        public long Misses { get; set; }

        /// <summary>
        /// Number of entries evicted due to size limit
        /// </summary>
        public long Evictions { get; set; }

        /// <summary>
        /// Number of entries explicitly invalidated
        /// </summary>
        public long Invalidations { get; set; }

        /// <summary>
        /// Current number of cached entries
        /// </summary>
        public int CurrentSize { get; set; }

        /// <summary>
        /// Maximum cache size
        /// </summary>
        public int MaxSize { get; set; }

        /// <summary>
        /// Cache hit rate (0.0 to 1.0)
        /// </summary>
        public double HitRate
        {
            get
            {
                long total = Hits + Misses;
                return total == 0 ? 0.0 : (double)Hits / total;
            }
        }
    }
}
