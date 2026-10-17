//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Core.VFS;

namespace RetroIO.Tests.VFS
{
    [TestFixture]
    public class VfsDentryCacheTests
    {
        private VfsDentryCache _cache = null!;
        private DirectoryEntry[] _testEntries = null!;

        [SetUp]
        public void SetUp()
        {
            _cache = new VfsDentryCache(maxEntries: 100);
            _testEntries = new[]
            {
                new DirectoryEntry { Name = "FILE1.TXT", Metadata = new FileMetadata { Size = 1024, IsDirectory = false } },
                new DirectoryEntry { Name = "FILE2.TXT", Metadata = new FileMetadata { Size = 2048, IsDirectory = false } },
                new DirectoryEntry { Name = "SUBDIR", Metadata = new FileMetadata { Size = 0, IsDirectory = true } }
            };
        }

        [Test]
        public void CacheDirectory_ValidEntry_StoresSuccessfully()
        {
            // Arrange
            string path = "urn:retrofs:local:0:d64:/SYSTEM";

            // Act
            _cache.CacheDirectory(path, _testEntries, isRemote: false);
            var result = _cache.TryGetDirectory(path, isRemote: false);

            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Length, Is.EqualTo(3));
            Assert.That(result[0].Name, Is.EqualTo("FILE1.TXT"));
        }

        [Test]
        public void TryGetDirectory_NotCached_ReturnsNull()
        {
            // Arrange
            string path = "urn:retrofs:local:0:d64:/NOTCACHED";

            // Act
            var result = _cache.TryGetDirectory(path, isRemote: false);

            // Assert
            Assert.That(result, Is.Null);
        }

        [Test]
        public void TryGetDirectory_Expired_ReturnsNull()
        {
            // Arrange
            var shortTtlCache = new VfsDentryCache(
                maxEntries: 100,
                defaultTtl: TimeSpan.FromMilliseconds(50),
                remoteTtl: TimeSpan.FromMilliseconds(50)
            );
            string path = "urn:retrofs:local:0:d64:/SYSTEM";

            // Act
            shortTtlCache.CacheDirectory(path, _testEntries, isRemote: false);
            Thread.Sleep(100); // Wait for expiration
            var result = shortTtlCache.TryGetDirectory(path, isRemote: false);

            // Assert
            Assert.That(result, Is.Null);
        }

        [Test]
        public void CacheDirectory_RemoteFilesystem_UsesShorterTtl()
        {
            // Arrange
            var cache = new VfsDentryCache(
                maxEntries: 100,
                defaultTtl: TimeSpan.FromMinutes(60),
                remoteTtl: TimeSpan.FromMilliseconds(50)
            );
            string path = "urn:retrofs:tnfs:server:/DIR";

            // Act
            cache.CacheDirectory(path, _testEntries, isRemote: true);
            Thread.Sleep(100); // Wait for remote TTL expiration
            var result = cache.TryGetDirectory(path, isRemote: true);

            // Assert - should be expired for remote
            Assert.That(result, Is.Null);
        }

        [Test]
        public void CacheDirectory_ExceedsMaxSize_EvictsLru()
        {
            // Arrange
            var smallCache = new VfsDentryCache(maxEntries: 3);

            // Act - Add 4 entries
            smallCache.CacheDirectory("path1", _testEntries, false);
            Thread.Sleep(10); // Ensure different access times
            smallCache.CacheDirectory("path2", _testEntries, false);
            Thread.Sleep(10);
            smallCache.CacheDirectory("path3", _testEntries, false);
            Thread.Sleep(10);
            smallCache.CacheDirectory("path4", _testEntries, false); // Should evict path1

            // Assert - path1 should be evicted (LRU)
            Assert.That(smallCache.TryGetDirectory("path1", false), Is.Null);
            Assert.That(smallCache.TryGetDirectory("path2", false), Is.Not.Null);
            Assert.That(smallCache.TryGetDirectory("path3", false), Is.Not.Null);
            Assert.That(smallCache.TryGetDirectory("path4", false), Is.Not.Null);
        }

        [Test]
        public void TryGetDirectory_UpdatesLruAccess()
        {
            // Arrange
            var smallCache = new VfsDentryCache(maxEntries: 3);
            smallCache.CacheDirectory("path1", _testEntries, false);
            Thread.Sleep(10);
            smallCache.CacheDirectory("path2", _testEntries, false);
            Thread.Sleep(10);
            smallCache.CacheDirectory("path3", _testEntries, false);

            // Act - Access path1 to make it most recently used
            smallCache.TryGetDirectory("path1", false);
            Thread.Sleep(10);

            // Add path4 - should evict path2 (now LRU), not path1
            smallCache.CacheDirectory("path4", _testEntries, false);

            // Assert
            Assert.That(smallCache.TryGetDirectory("path1", false), Is.Not.Null); // Still cached
            Assert.That(smallCache.TryGetDirectory("path2", false), Is.Null);      // Evicted
            Assert.That(smallCache.TryGetDirectory("path3", false), Is.Not.Null);
            Assert.That(smallCache.TryGetDirectory("path4", false), Is.Not.Null);
        }

        [Test]
        public void Invalidate_RemovesEntry()
        {
            // Arrange
            string path = "urn:retrofs:local:0:d64:/SYSTEM";
            _cache.CacheDirectory(path, _testEntries, false);

            // Act
            _cache.Invalidate(path);
            var result = _cache.TryGetDirectory(path, false);

            // Assert
            Assert.That(result, Is.Null);
        }

        [Test]
        public void Invalidate_NonExistentEntry_DoesNotThrow()
        {
            // Act & Assert
            Assert.DoesNotThrow(() => _cache.Invalidate("non-existent-path"));
        }

        [Test]
        public void InvalidatePrefix_RemovesMatchingEntries()
        {
            // Arrange
            _cache.CacheDirectory("urn:retrofs:local:0:d64:/SYSTEM", _testEntries, false);
            _cache.CacheDirectory("urn:retrofs:local:0:d64:/GAMES", _testEntries, false);
            _cache.CacheDirectory("urn:retrofs:local:1:d81:/OTHER", _testEntries, false);

            // Act - Invalidate all d64 entries
            _cache.InvalidatePrefix("urn:retrofs:local:0:d64:");

            // Assert
            Assert.That(_cache.TryGetDirectory("urn:retrofs:local:0:d64:/SYSTEM", false), Is.Null);
            Assert.That(_cache.TryGetDirectory("urn:retrofs:local:0:d64:/GAMES", false), Is.Null);
            Assert.That(_cache.TryGetDirectory("urn:retrofs:local:1:d81:/OTHER", false), Is.Not.Null); // Different volume
        }

        [Test]
        public void Clear_RemovesAllEntries()
        {
            // Arrange
            _cache.CacheDirectory("path1", _testEntries, false);
            _cache.CacheDirectory("path2", _testEntries, false);
            _cache.CacheDirectory("path3", _testEntries, false);

            // Act
            _cache.Clear();

            // Assert - Check entries are cleared
            Assert.That(_cache.TryGetDirectory("path1", false), Is.Null);
            Assert.That(_cache.TryGetDirectory("path2", false), Is.Null);
            Assert.That(_cache.TryGetDirectory("path3", false), Is.Null);

            // Get stats AFTER the TryGet calls (which increment misses)
            var stats = _cache.GetStatistics();
            Assert.That(stats.CurrentSize, Is.EqualTo(0));
            Assert.That(stats.Hits, Is.EqualTo(0));
            Assert.That(stats.Misses, Is.EqualTo(3)); // 3 misses from the TryGet calls above
        }

        [Test]
        public void GetStatistics_TracksHitsAndMisses()
        {
            // Arrange
            string path = "urn:retrofs:local:0:d64:/SYSTEM";
            _cache.CacheDirectory(path, _testEntries, false);

            // Act - 2 hits, 1 miss
            _cache.TryGetDirectory(path, false); // Hit
            _cache.TryGetDirectory(path, false); // Hit
            _cache.TryGetDirectory("non-existent", false); // Miss

            var stats = _cache.GetStatistics();

            // Assert
            Assert.That(stats.Hits, Is.EqualTo(2));
            Assert.That(stats.Misses, Is.EqualTo(1));
            Assert.That(stats.HitRate, Is.EqualTo(2.0 / 3.0).Within(0.001));
        }

        [Test]
        public void GetStatistics_TracksEvictions()
        {
            // Arrange
            var smallCache = new VfsDentryCache(maxEntries: 2);

            // Act - Add 3 entries to trigger 1 eviction
            smallCache.CacheDirectory("path1", _testEntries, false);
            smallCache.CacheDirectory("path2", _testEntries, false);
            smallCache.CacheDirectory("path3", _testEntries, false); // Evicts path1

            var stats = smallCache.GetStatistics();

            // Assert
            Assert.That(stats.Evictions, Is.EqualTo(1));
            Assert.That(stats.CurrentSize, Is.EqualTo(2));
            Assert.That(stats.MaxSize, Is.EqualTo(2));
        }

        [Test]
        public void GetStatistics_TracksInvalidations()
        {
            // Arrange
            _cache.CacheDirectory("path1", _testEntries, false);
            _cache.CacheDirectory("path2", _testEntries, false);

            // Act
            _cache.Invalidate("path1");
            _cache.Invalidate("path2");

            var stats = _cache.GetStatistics();

            // Assert
            Assert.That(stats.Invalidations, Is.EqualTo(2));
        }

        [Test]
        public void Constructor_InvalidMaxEntries_ThrowsException()
        {
            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => new VfsDentryCache(maxEntries: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VfsDentryCache(maxEntries: -1));
        }

        [Test]
        public void CacheDirectory_NullPath_ThrowsException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => _cache.CacheDirectory(null!, _testEntries, false));
            Assert.Throws<ArgumentNullException>(() => _cache.CacheDirectory("", _testEntries, false));
        }

        [Test]
        public void CacheDirectory_NullEntries_ThrowsException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => _cache.CacheDirectory("path", null!, false));
        }

        [Test]
        public void TryGetDirectory_NullPath_ThrowsException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => _cache.TryGetDirectory(null!, false));
            Assert.Throws<ArgumentNullException>(() => _cache.TryGetDirectory("", false));
        }

        [Test]
        public void ConcurrentAccess_ThreadSafe()
        {
            // Arrange
            var cache = new VfsDentryCache(maxEntries: 1000);
            var threads = new Thread[10];
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            // Act - Multiple threads reading/writing simultaneously
            for (int i = 0; i < threads.Length; i++)
            {
                int threadId = i;
                threads[i] = new Thread(() =>
                {
                    try
                    {
                        for (int j = 0; j < 100; j++)
                        {
                            string path = $"path{threadId}-{j}";
                            cache.CacheDirectory(path, _testEntries, false);
                            cache.TryGetDirectory(path, false);
                            if (j % 10 == 0)
                                cache.Invalidate(path);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });
                threads[i].Start();
            }

            for (int i = 0; i < threads.Length; i++)
            {
                threads[i].Join();
            }

            // Assert - No exceptions should occur
            Assert.That(exceptions, Is.Empty, $"Exceptions occurred: {string.Join(", ", exceptions)}");
        }
    }
}
