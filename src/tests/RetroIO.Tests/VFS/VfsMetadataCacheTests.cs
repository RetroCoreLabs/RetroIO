//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Threading;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Core.VFS;

namespace RetroIO.Tests.VFS
{
    [TestFixture]
    public class VfsMetadataCacheTests
    {
        private VfsMetadataCache _cache = null!;
        private FileMetadata _testMetadata = null!;

        [SetUp]
        public void SetUp()
        {
            _cache = new VfsMetadataCache(maxEntries: 100);
            _testMetadata = new FileMetadata
            {
                Name = "TEST.TXT",
                Size = 1024,
                IsDirectory = false,
                CreatedTime = DateTime.UtcNow.AddDays(-7),
                ModifiedTime = DateTime.UtcNow.AddDays(-1)
            };
        }

        [Test]
        public void CacheMetadata_ValidEntry_StoresSuccessfully()
        {
            // Arrange
            string path = "urn:retrofs:local:0:d64:/SYSTEM/FILE.TXT";

            // Act
            _cache.CacheMetadata(path, _testMetadata, isRemote: false);
            var result = _cache.TryGetMetadata(path, isRemote: false);

            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Name, Is.EqualTo("TEST.TXT"));
            Assert.That(result.Size, Is.EqualTo(1024));
        }

        [Test]
        public void TryGetMetadata_NotCached_ReturnsNull()
        {
            // Arrange
            string path = "urn:retrofs:local:0:d64:/NOTCACHED.TXT";

            // Act
            var result = _cache.TryGetMetadata(path, isRemote: false);

            // Assert
            Assert.That(result, Is.Null);
        }

        [Test]
        public void TryGetMetadata_Expired_ReturnsNull()
        {
            // Arrange
            var shortTtlCache = new VfsMetadataCache(
                maxEntries: 100,
                defaultTtl: TimeSpan.FromMilliseconds(50),
                remoteTtl: TimeSpan.FromMilliseconds(50)
            );
            string path = "urn:retrofs:local:0:d64:/FILE.TXT";

            // Act
            shortTtlCache.CacheMetadata(path, _testMetadata, isRemote: false);
            Thread.Sleep(100); // Wait for expiration
            var result = shortTtlCache.TryGetMetadata(path, isRemote: false);

            // Assert
            Assert.That(result, Is.Null);
        }

        [Test]
        public void CacheMetadata_RemoteFilesystem_UsesShorterTtl()
        {
            // Arrange
            var cache = new VfsMetadataCache(
                maxEntries: 100,
                defaultTtl: TimeSpan.FromMinutes(60),
                remoteTtl: TimeSpan.FromMilliseconds(50)
            );
            string path = "urn:retrofs:tnfs:server:/FILE.TXT";

            // Act
            cache.CacheMetadata(path, _testMetadata, isRemote: true);
            Thread.Sleep(100); // Wait for remote TTL expiration
            var result = cache.TryGetMetadata(path, isRemote: true);

            // Assert - should be expired for remote
            Assert.That(result, Is.Null);
        }

        [Test]
        public void CacheMetadata_ExceedsMaxSize_EvictsLru()
        {
            // Arrange
            var smallCache = new VfsMetadataCache(maxEntries: 3);
            var meta1 = new FileMetadata { Name = "File1", Size = 100 };
            var meta2 = new FileMetadata { Name = "File2", Size = 200 };
            var meta3 = new FileMetadata { Name = "File3", Size = 300 };
            var meta4 = new FileMetadata { Name = "File4", Size = 400 };

            // Act - Add 4 entries
            smallCache.CacheMetadata("path1", meta1, false);
            Thread.Sleep(10);
            smallCache.CacheMetadata("path2", meta2, false);
            Thread.Sleep(10);
            smallCache.CacheMetadata("path3", meta3, false);
            Thread.Sleep(10);
            smallCache.CacheMetadata("path4", meta4, false); // Should evict path1

            // Assert - path1 should be evicted (LRU)
            Assert.That(smallCache.TryGetMetadata("path1", false), Is.Null);
            Assert.That(smallCache.TryGetMetadata("path2", false), Is.Not.Null);
            Assert.That(smallCache.TryGetMetadata("path3", false), Is.Not.Null);
            Assert.That(smallCache.TryGetMetadata("path4", false), Is.Not.Null);
        }

        [Test]
        public void TryGetMetadata_UpdatesLruAccess()
        {
            // Arrange
            var smallCache = new VfsMetadataCache(maxEntries: 3);
            var meta1 = new FileMetadata { Name = "File1" };
            var meta2 = new FileMetadata { Name = "File2" };
            var meta3 = new FileMetadata { Name = "File3" };
            var meta4 = new FileMetadata { Name = "File4" };

            smallCache.CacheMetadata("path1", meta1, false);
            Thread.Sleep(10);
            smallCache.CacheMetadata("path2", meta2, false);
            Thread.Sleep(10);
            smallCache.CacheMetadata("path3", meta3, false);

            // Act - Access path1 to make it most recently used
            smallCache.TryGetMetadata("path1", false);
            Thread.Sleep(10);

            // Add path4 - should evict path2 (now LRU), not path1
            smallCache.CacheMetadata("path4", meta4, false);

            // Assert
            Assert.That(smallCache.TryGetMetadata("path1", false), Is.Not.Null); // Still cached
            Assert.That(smallCache.TryGetMetadata("path2", false), Is.Null);      // Evicted
            Assert.That(smallCache.TryGetMetadata("path3", false), Is.Not.Null);
            Assert.That(smallCache.TryGetMetadata("path4", false), Is.Not.Null);
        }

        [Test]
        public void Invalidate_RemovesEntry()
        {
            // Arrange
            string path = "urn:retrofs:local:0:d64:/FILE.TXT";
            _cache.CacheMetadata(path, _testMetadata, false);

            // Act
            _cache.Invalidate(path);
            var result = _cache.TryGetMetadata(path, false);

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
            _cache.CacheMetadata("urn:retrofs:local:0:d64:/SYSTEM/FILE1.TXT", _testMetadata, false);
            _cache.CacheMetadata("urn:retrofs:local:0:d64:/SYSTEM/FILE2.TXT", _testMetadata, false);
            _cache.CacheMetadata("urn:retrofs:local:1:d81:/OTHER.TXT", _testMetadata, false);

            // Act - Invalidate all d64 entries
            _cache.InvalidatePrefix("urn:retrofs:local:0:d64:");

            // Assert
            Assert.That(_cache.TryGetMetadata("urn:retrofs:local:0:d64:/SYSTEM/FILE1.TXT", false), Is.Null);
            Assert.That(_cache.TryGetMetadata("urn:retrofs:local:0:d64:/SYSTEM/FILE2.TXT", false), Is.Null);
            Assert.That(_cache.TryGetMetadata("urn:retrofs:local:1:d81:/OTHER.TXT", false), Is.Not.Null); // Different volume
        }

        [Test]
        public void Clear_RemovesAllEntries()
        {
            // Arrange
            _cache.CacheMetadata("path1", _testMetadata, false);
            _cache.CacheMetadata("path2", _testMetadata, false);
            _cache.CacheMetadata("path3", _testMetadata, false);

            // Act
            _cache.Clear();

            // Assert - Check entries are cleared
            Assert.That(_cache.TryGetMetadata("path1", false), Is.Null);
            Assert.That(_cache.TryGetMetadata("path2", false), Is.Null);
            Assert.That(_cache.TryGetMetadata("path3", false), Is.Null);

            // Get stats AFTER the TryGet calls (which increment misses)
            var stats = _cache.GetStatistics();
            Assert.That(stats.CurrentSize, Is.EqualTo(0));
            Assert.That(stats.Hits, Is.EqualTo(0));
            Assert.That(stats.Misses, Is.EqualTo(3)); // 3 misses from TryGet calls above
        }

        [Test]
        public void GetStatistics_TracksHitsAndMisses()
        {
            // Arrange
            string path = "urn:retrofs:local:0:d64:/FILE.TXT";
            _cache.CacheMetadata(path, _testMetadata, false);

            // Act - 2 hits, 1 miss
            _cache.TryGetMetadata(path, false); // Hit
            _cache.TryGetMetadata(path, false); // Hit
            _cache.TryGetMetadata("non-existent", false); // Miss

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
            var smallCache = new VfsMetadataCache(maxEntries: 2);
            var meta = new FileMetadata { Name = "Test" };

            // Act - Add 3 entries to trigger 1 eviction
            smallCache.CacheMetadata("path1", meta, false);
            smallCache.CacheMetadata("path2", meta, false);
            smallCache.CacheMetadata("path3", meta, false); // Evicts path1

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
            _cache.CacheMetadata("path1", _testMetadata, false);
            _cache.CacheMetadata("path2", _testMetadata, false);

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
            Assert.Throws<ArgumentOutOfRangeException>(() => new VfsMetadataCache(maxEntries: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VfsMetadataCache(maxEntries: -1));
        }

        [Test]
        public void CacheMetadata_NullPath_ThrowsException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => _cache.CacheMetadata(null!, _testMetadata, false));
            Assert.Throws<ArgumentNullException>(() => _cache.CacheMetadata("", _testMetadata, false));
        }

        [Test]
        public void CacheMetadata_NullMetadata_ThrowsException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => _cache.CacheMetadata("path", null!, false));
        }

        [Test]
        public void TryGetMetadata_NullPath_ThrowsException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => _cache.TryGetMetadata(null!, false));
            Assert.Throws<ArgumentNullException>(() => _cache.TryGetMetadata("", false));
        }

        [Test]
        public void ConcurrentAccess_ThreadSafe()
        {
            // Arrange
            var cache = new VfsMetadataCache(maxEntries: 1000);
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
                            var meta = new FileMetadata { Name = $"File{j}", Size = j * 100 };
                            cache.CacheMetadata(path, meta, false);
                            cache.TryGetMetadata(path, false);
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
