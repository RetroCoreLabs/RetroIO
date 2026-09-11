//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Concurrent;

namespace RetroIO.Storage.Core.VFS
{
    /// <summary>
    /// LRU cache for file metadata to avoid repeated GetMetadata() calls.
    /// Caches file size, timestamps, and other metadata for quick access.
    /// </summary>
    /// <remarks>
    /// The metadata cache improves performance when:
    /// - File properties dialogs are opened repeatedly
    /// - Directory listings are sorted by size/date
    /// - File validation checks metadata multiple times
    ///
    /// Cache entries expire based on configurable TTL:
    /// - Local filesystems: Long TTL (60 minutes default)
    /// - Remote filesystems: Short TTL (5 minutes default)
    ///
    /// Cache is automatically invalidated when files are modified.
    /// </remarks>
    public class VfsMetadataCache
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
        /// Initializes a new metadata cache.
        /// </summary>
        /// <param name="maxEntries">Maximum number of cached metadata entries</param>
        /// <param name="defaultTtl">TTL for local filesystem metadata</param>
        /// <param name="remoteTtl">TTL for remote filesystem metadata (usually shorter)</param>
        public VfsMetadataCache(int maxEntries = 2000, TimeSpan? defaultTtl = null, TimeSpan? remoteTtl = null)
        {
            if (maxEntries <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxEntries), "Must be positive");

            _maxEntries = maxEntries;
            _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(60);
            _remoteTtl = remoteTtl ?? TimeSpan.FromMinutes(5);
            _cache = new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Try to get cached file metadata.
        /// </summary>
        /// <param name="urnPath">URN path to file (e.g., "urn:retrofs:local:0:d64:/SYSTEM/README.TXT")</param>
        /// <param name="isRemote">Whether this is a remote filesystem (affects TTL)</param>
        /// <returns>Cached metadata, or null if not in cache or expired</returns>
        public FileMetadata? TryGetMetadata(string urnPath, bool isRemote)
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
            return entry.Metadata;
        }

        /// <summary>
        /// Cache file metadata.
        /// </summary>
        /// <param name="urnPath">URN path to file</param>
        /// <param name="metadata">File metadata to cache</param>
        /// <param name="isRemote">Whether this is a remote filesystem</param>
        public void CacheMetadata(string urnPath, FileMetadata metadata, bool isRemote)
        {
            if (string.IsNullOrEmpty(urnPath))
                throw new ArgumentNullException(nameof(urnPath));
            if (metadata == null)
                throw new ArgumentNullException(nameof(metadata));

            // Check if we need to evict entries (LRU)
            if (_cache.Count >= _maxEntries)
            {
                EvictLru();
            }

            var entry = new CacheEntry
            {
                Metadata = metadata,
                CachedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow,
                IsRemote = isRemote
            };

            _cache[urnPath] = entry;
        }

        /// <summary>
        /// Invalidate a specific file's metadata.
        /// Called when the file is modified.
        /// </summary>
        /// <param name="urnPath">URN path to file to invalidate</param>
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
        /// Invalidate all metadata entries with a specific path prefix.
        /// Used when unmounting a volume or invalidating an entire directory.
        /// </summary>
        /// <param name="urnPathPrefix">URN path prefix</param>
        public void InvalidatePrefix(string urnPathPrefix)
        {
            if (string.IsNullOrEmpty(urnPathPrefix))
                return;

            var keysToRemove = new System.Collections.Generic.List<string>();

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
        /// Clear all cached metadata.
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
            public FileMetadata Metadata { get; set; } = null!;
            public DateTime CachedAt { get; set; }
            public DateTime LastAccessedAt { get; set; }
            public bool IsRemote { get; set; }
        }
    }
}
