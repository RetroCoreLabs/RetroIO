//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Core.VFS;

namespace RetroIO.Tests.VFS
{
    [TestFixture]
    public class VfsCoreTests
    {
        private VfsCore _vfsCore = null!;
        private MockVolumeProvider _mockProvider = null!;

        [SetUp]
        public void SetUp()
        {
            _vfsCore = new VfsCore(enablePersistence: false);
            _mockProvider = new MockVolumeProvider();
            _vfsCore.RegisterProvider(_mockProvider);
        }

        [TearDown]
        public void TearDown()
        {
            _vfsCore?.Dispose();
            _mockProvider?.Dispose();
        }

        [Test]
        public void Constructor_InitializesVfs()
        {
            // Arrange & Act
            using var vfsCore = new VfsCore(enablePersistence: false);

            // Assert
            Assert.That(vfsCore.Vfs, Is.Not.Null);
        }

        [Test]
        public void Constructor_WithCacheConfig_PassesConfigToVfs()
        {
            // Arrange
            var config = new VfsCacheConfiguration
            {
                DentryCacheMaxEntries = 123,
                MetadataCacheMaxEntries = 456
            };

            // Act
            using var vfsCore = new VfsCore(enablePersistence: false, cacheConfig: config);

            // Assert
            Assert.That(vfsCore.Vfs.CacheConfiguration.DentryCacheMaxEntries, Is.EqualTo(123));
            Assert.That(vfsCore.Vfs.CacheConfiguration.MetadataCacheMaxEntries, Is.EqualTo(456));
        }

        [Test]
        public void RegisterProvider_RegistersWithUnderlyingVfs()
        {
            // Arrange
            using var vfsCore = new VfsCore(enablePersistence: false);
            var provider = new MockVolumeProvider();

            // Act
            vfsCore.RegisterProvider(provider);

            // Assert - mounting via the newly-registered provider must actually succeed AND the
            // mount must be real/queryable afterward. The old DoesNotThrow-only assertion would
            // still pass even if RegisterProvider silently no-op'd and Mount fell through to some
            // other already-registered provider (or threw a different, unrelated exception type
            // that DoesNotThrow would still catch as "didn't throw" only for the wrong reason).
            Assert.DoesNotThrow(() => vfsCore.Mount("mock", "uri", "vol1", true));
            var mountInfo = vfsCore.GetMountInfo("vol1");
            Assert.That(mountInfo, Is.Not.Null, "Mount should be queryable after RegisterProvider+Mount succeeded");
        }

        [Test]
        public void Mount_DelegatesToVfs()
        {
            // Act
            var mountInfo = _vfsCore.Mount("mock", "test-uri", "vol1", readOnly: true);

            // Assert
            Assert.That(mountInfo, Is.Not.Null);
            Assert.That(mountInfo.VolumeId, Is.EqualTo("vol1"));
            Assert.That(mountInfo.SourceUri, Is.EqualTo("test-uri"));
        }

        [Test]
        public void Unmount_DelegatesToVfs()
        {
            // Arrange
            _vfsCore.Mount("mock", "test-uri", "vol1", readOnly: true);
            Assert.That(_vfsCore.GetMountInfo("vol1"), Is.Not.Null, "Precondition: mount should exist before Unmount");

            // Act & Assert
            Assert.DoesNotThrow(() => _vfsCore.Unmount("vol1"));

            // The old assertion only checked Unmount didn't throw — it would still pass even if
            // Unmount silently failed to actually remove the mount. Confirm it's really gone.
            Assert.That(_vfsCore.GetMountInfo("vol1"), Is.Null, "Mount should no longer be queryable after Unmount");
        }

        [Test]
        public void GetAllMounts_DelegatesToVfs()
        {
            // Arrange
            _vfsCore.Mount("mock", "uri1", "vol1", readOnly: true);
            _vfsCore.Mount("mock", "uri2", "vol2", readOnly: true);

            // Act
            var mounts = _vfsCore.GetAllMounts();

            // Assert
            Assert.That(mounts.Length, Is.EqualTo(2));
        }

        [Test]
        public void GetMountInfo_DelegatesToVfs()
        {
            // Arrange
            _vfsCore.Mount("mock", "test-uri", "vol1", readOnly: true);

            // Act
            var mountInfo = _vfsCore.GetMountInfo("vol1");

            // Assert
            Assert.That(mountInfo, Is.Not.Null);
            Assert.That(mountInfo!.VolumeId, Is.EqualTo("vol1"));
        }

        [Test]
        public async Task ReadDirectoryAsync_CachesMissOnFirstCall()
        {
            // Arrange
            _vfsCore.Mount("mock", "test-uri", "vol1", readOnly: true);
            var urnPath = "urn:retrofs:mock:vol1:/";
            var context = new UrnContext { VolumeId = "vol1", ReadOnly = true };

            // Act
            var entries = await _vfsCore.ReadDirectoryAsync(urnPath, context);

            // Assert
            Assert.That(entries, Is.Not.Null);
            Assert.That(_mockProvider.ListDirectoryCallCount, Is.EqualTo(1));
        }

        [Test]
        public async Task ReadDirectoryAsync_CachesHitOnSecondCall()
        {
            // Arrange
            _vfsCore.Mount("mock", "test-uri", "vol1", readOnly: true);
            var urnPath = "urn:retrofs:mock:vol1:/";
            var context = new UrnContext { VolumeId = "vol1", ReadOnly = true };

            // Act - First call (cache miss)
            await _vfsCore.ReadDirectoryAsync(urnPath, context);

            // Act - Second call (should hit cache)
            await _vfsCore.ReadDirectoryAsync(urnPath, context);

            // Assert - Provider should only be called once
            Assert.That(_mockProvider.ListDirectoryCallCount, Is.EqualTo(1));

            // Assert - Cache hit
            var (dentryStats, _) = _vfsCore.GetCacheStatistics();
            Assert.That(dentryStats!.Hits, Is.EqualTo(1));
            Assert.That(dentryStats.Misses, Is.EqualTo(1));
        }

        [Test]
        public async Task GetMetadataAsync_CachesMissOnFirstCall()
        {
            // Arrange
            _vfsCore.Mount("mock", "test-uri", "vol1", readOnly: true);
            var urnPath = "urn:retrofs:mock:vol1:/FILE.TXT";
            var context = new UrnContext { VolumeId = "vol1", ReadOnly = true };

            // Act
            var metadata = await _vfsCore.GetMetadataAsync(urnPath, context);

            // Assert
            Assert.That(metadata, Is.Not.Null);
            Assert.That(_mockProvider.GetMetadataCallCount, Is.EqualTo(1));
        }

        [Test]
        public async Task GetMetadataAsync_CachesHitOnSecondCall()
        {
            // Arrange
            _vfsCore.Mount("mock", "test-uri", "vol1", readOnly: true);
            var urnPath = "urn:retrofs:mock:vol1:/FILE.TXT";
            var context = new UrnContext { VolumeId = "vol1", ReadOnly = true };

            // Act - First call (cache miss)
            await _vfsCore.GetMetadataAsync(urnPath, context);

            // Act - Second call (should hit cache)
            await _vfsCore.GetMetadataAsync(urnPath, context);

            // Assert - Provider should only be called once
            Assert.That(_mockProvider.GetMetadataCallCount, Is.EqualTo(1));

            // Assert - Cache hit
            var (_, metadataStats) = _vfsCore.GetCacheStatistics();
            Assert.That(metadataStats!.Hits, Is.EqualTo(1));
            Assert.That(metadataStats.Misses, Is.EqualTo(1));
        }

        [Test]
        public async Task WriteFileAsync_InvalidatesCache()
        {
            // Arrange
            _vfsCore.Mount("mock", "test-uri", "vol1", readOnly: false);
            var urnPath = "urn:retrofs:mock:vol1:/FILE.TXT";
            var context = new UrnContext { VolumeId = "vol1", ReadOnly = false };

            // Cache metadata first
            await _vfsCore.GetMetadataAsync(urnPath, context);

            // Act - Write file (should invalidate cache)
            await _vfsCore.WriteFileAsync(urnPath, new byte[] { 1, 2, 3 }, context);

            // Act - Read metadata again (should be cache miss)
            await _vfsCore.GetMetadataAsync(urnPath, context);

            // Assert - Provider should be called twice (once before write, once after)
            Assert.That(_mockProvider.GetMetadataCallCount, Is.EqualTo(2));
        }

        [Test]
        public async Task DeleteFileAsync_InvalidatesCache()
        {
            // Arrange
            _vfsCore.Mount("mock", "test-uri", "vol1", readOnly: false);
            var urnPath = "urn:retrofs:mock:vol1:/FILE.TXT";
            var context = new UrnContext { VolumeId = "vol1", ReadOnly = false };

            // Cache metadata first
            await _vfsCore.GetMetadataAsync(urnPath, context);

            // Act - Delete file (should invalidate cache)
            await _vfsCore.DeleteFileAsync(urnPath, context);

            // Assert - Write operation called
            Assert.That(_mockProvider.DeleteFileCallCount, Is.EqualTo(1));
        }

        [Test]
        public async Task OpenReadAsync_DelegatesToProvider()
        {
            // Arrange
            _vfsCore.Mount("mock", "test-uri", "vol1", readOnly: true);
            var urnPath = "urn:retrofs:mock:vol1:/FILE.TXT";
            var context = new UrnContext { VolumeId = "vol1", ReadOnly = true };

            // Act
            using var stream = await _vfsCore.OpenReadAsync(urnPath, context);

            // Assert
            Assert.That(stream, Is.Not.Null);
            Assert.That(_mockProvider.OpenReadCallCount, Is.EqualTo(1));
        }

        [Test]
        public void ClearCaches_ClearsUnderlyingVfsCaches()
        {
            // Arrange
            _vfsCore.Mount("mock", "test-uri", "vol1", readOnly: true);

            // Act
            _vfsCore.ClearCaches();

            // Assert
            var (dentryStats, metadataStats) = _vfsCore.GetCacheStatistics();
            Assert.That(dentryStats!.CurrentSize, Is.EqualTo(0));
            Assert.That(metadataStats!.CurrentSize, Is.EqualTo(0));
        }

        [Test]
        public void GetCacheStatistics_ReturnsStatistics()
        {
            // Act
            var (dentryStats, metadataStats) = _vfsCore.GetCacheStatistics();

            // Assert
            Assert.That(dentryStats, Is.Not.Null);
            Assert.That(metadataStats, Is.Not.Null);
        }

        [Test]
        public void Dispose_DisposesUnderlyingVfs()
        {
            // Arrange
            var vfsCore = new VfsCore(enablePersistence: false);

            // Act
            vfsCore.Dispose();

            // Assert - Should throw ObjectDisposedException
            Assert.Throws<ObjectDisposedException>(() => vfsCore.GetAllMounts());
        }

        [Test]
        public async Task ReadDirectoryAsync_NullPath_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await _vfsCore.ReadDirectoryAsync(null!));
        }

        [Test]
        public async Task GetMetadataAsync_NullPath_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await _vfsCore.GetMetadataAsync(null!));
        }

        [Test]
        public async Task WriteFileAsync_NullPath_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await _vfsCore.WriteFileAsync(null!, new byte[0]));
        }

        [Test]
        public async Task WriteFileAsync_NullData_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await _vfsCore.WriteFileAsync("urn:retrofs:mock:vol1:/FILE.TXT", null!));
        }

        [Test]
        public async Task DeleteFileAsync_NullPath_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await _vfsCore.DeleteFileAsync(null!));
        }

        [Test]
        public async Task OpenReadAsync_NullPath_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await _vfsCore.OpenReadAsync(null!));
        }

        [Test]
        public async Task ReadDirectoryAsync_VolumeNotMounted_ThrowsArgumentException()
        {
            // Arrange
            var urnPath = "urn:retrofs:mock:nonexistent:/";

            // Act & Assert
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await _vfsCore.ReadDirectoryAsync(urnPath));
        }

        [Test]
        public async Task ReadDirectoryAsync_WithoutContext_CreatesDefaultContext()
        {
            // Arrange
            _vfsCore.Mount("mock", "test-uri", "vol1", readOnly: true);
            var urnPath = "urn:retrofs:mock:vol1:/";

            // Act - No context provided
            var entries = await _vfsCore.ReadDirectoryAsync(urnPath);

            // Assert - Should succeed
            Assert.That(entries, Is.Not.Null);
            Assert.That(_mockProvider.ListDirectoryCallCount, Is.EqualTo(1));
        }

        // Mock provider for testing
        private class MockVolumeProvider : IVolumeProvider
        {
            public int ListDirectoryCallCount { get; private set; }
            public int GetMetadataCallCount { get; private set; }
            public int WriteAsyncCallCount { get; private set; }
            public int DeleteFileCallCount { get; private set; }
            public int OpenReadCallCount { get; private set; }

            public string Type => "mock";
            public string[] SupportedSchemes => new[] { "mock" };
            public bool IsRemote => false;

            public MountInfo Mount(string uri, string volumeId, bool readOnly)
            {
                return new MountInfo
                {
                    VolumeId = volumeId,
                    Type = Type,
                    SourceUri = uri,
                    IsReadOnly = readOnly,
                    FileSystemType = "mock"
                };
            }

            public void Unmount(string volumeId) { }

            public MountInfo[] ListMounts() => Array.Empty<MountInfo>();

            public MountInfo? GetMountInfo(string volumeId) => null;

            public bool CanRestore(UnifiedMountEntry entry, out string? errorMessage)
            {
                errorMessage = null;
                return true;
            }

            public MountInfo MountFromConfig(UnifiedMountEntry entry)
            {
                return Mount(entry.SourceUri, entry.VolumeId, entry.IsReadOnly);
            }

            public byte[] ReadFile(UrnContext context, string path) => Array.Empty<byte>();

            public void WriteFile(UrnContext context, string path, byte[] data) { }

            public void DeleteFile(UrnContext context, string path)
            {
                DeleteFileCallCount++;
            }

            public DirectoryEntry[] ListDirectory(UrnContext context, string path) => Array.Empty<DirectoryEntry>();

            public FileMetadata? GetMetadata(UrnContext context, string path) => null;

            public Task<DirectoryEntry[]> ListDirectoryAsync(UrnContext context, string path, CancellationToken cancellationToken = default)
            {
                ListDirectoryCallCount++;
                var entries = new[]
                {
                    new DirectoryEntry
                    {
                        Name = "FILE.TXT",
                        Metadata = new FileMetadata { Name = "FILE.TXT", Size = 100, IsDirectory = false }
                    }
                };
                return Task.FromResult(entries);
            }

            public Task<FileMetadata?> GetMetadataAsync(UrnContext context, string path, CancellationToken cancellationToken = default)
            {
                GetMetadataCallCount++;
                FileMetadata? metadata = new FileMetadata
                {
                    Name = "FILE.TXT",
                    Size = 100,
                    IsDirectory = false
                };
                return Task.FromResult<FileMetadata?>(metadata);
            }

            public Task WriteAsync(UrnContext context, string path, Stream data, CancellationToken cancellationToken = default)
            {
                WriteAsyncCallCount++;
                return Task.CompletedTask;
            }

            public Task<Stream> OpenReadAsync(UrnContext context, string path, CancellationToken cancellationToken = default)
            {
                OpenReadCallCount++;
                return Task.FromResult<Stream>(new MemoryStream());
            }

            public int ReadBlock(UrnContext context, BlockAddress address, Span<byte> buffer) => 0;

            public void WriteBlock(UrnContext context, BlockAddress address, ReadOnlySpan<byte> data) { }

            public BlockDeviceInfo? GetBlockDeviceInfo(UrnContext context) => null;

            public void Dispose() { }
        }
    }
}
