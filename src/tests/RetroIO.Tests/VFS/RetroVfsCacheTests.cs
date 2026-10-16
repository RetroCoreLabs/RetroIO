//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Core.VFS;

namespace RetroIO.Tests.VFS
{
    [TestFixture]
    public class RetroVfsCacheTests
    {
        [Test]
        public void Constructor_DefaultConfig_InitializesCaches()
        {
            // Act
            using var vfs = new RetroVFS(enablePersistence: false);

            // Assert
            Assert.That(vfs.GetDentryCache(), Is.Not.Null);
            Assert.That(vfs.GetMetadataCache(), Is.Not.Null);
            Assert.That(vfs.CacheConfiguration, Is.Not.Null);
        }

        [Test]
        public void Constructor_DisabledConfig_DoesNotInitializeCaches()
        {
            // Arrange
            var config = VfsCacheConfiguration.Disabled;

            // Act
            using var vfs = new RetroVFS(enablePersistence: false, cacheConfig: config);

            // Assert
            Assert.That(vfs.GetDentryCache(), Is.Null);
            Assert.That(vfs.GetMetadataCache(), Is.Null);
        }

        [Test]
        public void Constructor_CustomConfig_InitializesCachesWithCustomValues()
        {
            // Arrange
            var config = new VfsCacheConfiguration
            {
                EnableDentryCache = true,
                DentryCacheMaxEntries = 500,
                DentryCacheTtl = TimeSpan.FromMinutes(30),
                DentryCacheRemoteTtl = TimeSpan.FromMinutes(3),
                EnableMetadataCache = true,
                MetadataCacheMaxEntries = 1000,
                MetadataCacheTtl = TimeSpan.FromMinutes(45),
                MetadataCacheRemoteTtl = TimeSpan.FromMinutes(4)
            };

            // Act
            using var vfs = new RetroVFS(enablePersistence: false, cacheConfig: config);

            // Assert
            Assert.That(vfs.GetDentryCache(), Is.Not.Null);
            Assert.That(vfs.GetMetadataCache(), Is.Not.Null);
            Assert.That(vfs.CacheConfiguration.DentryCacheMaxEntries, Is.EqualTo(500));
            Assert.That(vfs.CacheConfiguration.MetadataCacheMaxEntries, Is.EqualTo(1000));
        }

        [Test]
        public void Constructor_OnlyDentryCacheEnabled_InitializesOnlyDentryCache()
        {
            // Arrange
            var config = new VfsCacheConfiguration
            {
                EnableDentryCache = true,
                EnableMetadataCache = false
            };

            // Act
            using var vfs = new RetroVFS(enablePersistence: false, cacheConfig: config);

            // Assert
            Assert.That(vfs.GetDentryCache(), Is.Not.Null);
            Assert.That(vfs.GetMetadataCache(), Is.Null);
        }

        [Test]
        public void Constructor_OnlyMetadataCacheEnabled_InitializesOnlyMetadataCache()
        {
            // Arrange
            var config = new VfsCacheConfiguration
            {
                EnableDentryCache = false,
                EnableMetadataCache = true
            };

            // Act
            using var vfs = new RetroVFS(enablePersistence: false, cacheConfig: config);

            // Assert
            Assert.That(vfs.GetDentryCache(), Is.Null);
            Assert.That(vfs.GetMetadataCache(), Is.Not.Null);
        }

        [Test]
        public void ClearCaches_ClearsAllCaches()
        {
            // Arrange
            using var vfs = new RetroVFS(enablePersistence: false);
            var dentryCache = vfs.GetDentryCache();
            var metadataCache = vfs.GetMetadataCache();

            // Add some entries
            var testEntries = new[] { new DirectoryEntry { Name = "TEST.TXT", Metadata = new FileMetadata { Size = 100 } } };
            var testMetadata = new FileMetadata { Name = "TEST.TXT", Size = 100 };
            dentryCache!.CacheDirectory("path1", testEntries, false);
            metadataCache!.CacheMetadata("path2", testMetadata, false);

            // Act
            vfs.ClearCaches();

            // Assert
            var dentryStats = dentryCache.GetStatistics();
            var metadataStats = metadataCache.GetStatistics();
            Assert.That(dentryStats.CurrentSize, Is.EqualTo(0));
            Assert.That(metadataStats.CurrentSize, Is.EqualTo(0));
        }

        [Test]
        public void ClearCaches_WithDisabledCaches_DoesNotThrow()
        {
            // Arrange
            var config = VfsCacheConfiguration.Disabled;
            using var vfs = new RetroVFS(enablePersistence: false, cacheConfig: config);

            // Act & Assert
            Assert.DoesNotThrow(() => vfs.ClearCaches());
        }

        [Test]
        public void GetCacheStatistics_ReturnsStatistics()
        {
            // Arrange
            using var vfs = new RetroVFS(enablePersistence: false);
            var dentryCache = vfs.GetDentryCache();
            var metadataCache = vfs.GetMetadataCache();

            // Add some entries
            var testEntries = new[] { new DirectoryEntry { Name = "TEST.TXT", Metadata = new FileMetadata { Size = 100 } } };
            var testMetadata = new FileMetadata { Name = "TEST.TXT", Size = 100 };
            dentryCache!.CacheDirectory("path1", testEntries, false);
            metadataCache!.CacheMetadata("path2", testMetadata, false);

            // Act
            var (dentryStats, metadataStats) = vfs.GetCacheStatistics();

            // Assert
            Assert.That(dentryStats, Is.Not.Null);
            Assert.That(metadataStats, Is.Not.Null);
            Assert.That(dentryStats!.CurrentSize, Is.EqualTo(1));
            Assert.That(metadataStats!.CurrentSize, Is.EqualTo(1));
        }

        [Test]
        public void GetCacheStatistics_WithDisabledCaches_ReturnsNull()
        {
            // Arrange
            var config = VfsCacheConfiguration.Disabled;
            using var vfs = new RetroVFS(enablePersistence: false, cacheConfig: config);

            // Act
            var (dentryStats, metadataStats) = vfs.GetCacheStatistics();

            // Assert
            Assert.That(dentryStats, Is.Null);
            Assert.That(metadataStats, Is.Null);
        }

        [Test]
        public void CacheConfiguration_ReturnsConfiguration()
        {
            // Arrange
            var config = new VfsCacheConfiguration
            {
                DentryCacheMaxEntries = 777,
                MetadataCacheMaxEntries = 888
            };

            using var vfs = new RetroVFS(enablePersistence: false, cacheConfig: config);

            // Act
            var returnedConfig = vfs.CacheConfiguration;

            // Assert
            Assert.That(returnedConfig.DentryCacheMaxEntries, Is.EqualTo(777));
            Assert.That(returnedConfig.MetadataCacheMaxEntries, Is.EqualTo(888));
        }

        [Test]
        public void Dispose_AccessingCachesAfterDispose_ThrowsObjectDisposedException()
        {
            // Arrange
            var vfs = new RetroVFS(enablePersistence: false);
            vfs.Dispose();

            // Act & Assert
            Assert.Throws<ObjectDisposedException>(() => vfs.GetDentryCache());
            Assert.Throws<ObjectDisposedException>(() => vfs.GetMetadataCache());
            Assert.Throws<ObjectDisposedException>(() => vfs.ClearCaches());
            Assert.Throws<ObjectDisposedException>(() => vfs.GetCacheStatistics());
        }

        [Test]
        public void RemoteOptimizedConfig_InitializesCachesWithSmallerSizes()
        {
            // Arrange
            var config = VfsCacheConfiguration.RemoteOptimized;

            // Act
            using var vfs = new RetroVFS(enablePersistence: false, cacheConfig: config);

            // Assert
            Assert.That(vfs.CacheConfiguration.DentryCacheMaxEntries, Is.EqualTo(500));
            Assert.That(vfs.CacheConfiguration.MetadataCacheMaxEntries, Is.EqualTo(1000));
            Assert.That(vfs.CacheConfiguration.DentryCacheTtl, Is.EqualTo(TimeSpan.FromMinutes(5)));
            Assert.That(vfs.CacheConfiguration.MetadataCacheTtl, Is.EqualTo(TimeSpan.FromMinutes(5)));
        }

        [Test]
        public void LocalOptimizedConfig_InitializesCachesWithLargerSizes()
        {
            // Arrange
            var config = VfsCacheConfiguration.LocalOptimized;

            // Act
            using var vfs = new RetroVFS(enablePersistence: false, cacheConfig: config);

            // Assert
            Assert.That(vfs.CacheConfiguration.DentryCacheMaxEntries, Is.EqualTo(2000));
            Assert.That(vfs.CacheConfiguration.MetadataCacheMaxEntries, Is.EqualTo(5000));
            Assert.That(vfs.CacheConfiguration.DentryCacheTtl, Is.EqualTo(TimeSpan.FromHours(2)));
            Assert.That(vfs.CacheConfiguration.MetadataCacheTtl, Is.EqualTo(TimeSpan.FromHours(2)));
        }
    }
}
