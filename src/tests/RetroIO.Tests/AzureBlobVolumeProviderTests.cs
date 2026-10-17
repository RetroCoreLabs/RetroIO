//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;
using NUnit.Framework;
using RetroIO.Storage.Azure;
using RetroIO.Storage.Core;

namespace RetroIO.Tests
{
    /// <summary>
    /// Tests for AzureBlobVolumeProvider - Azure Blob Storage integration with Azurite.
    ///
    /// PREREQUISITES:
    /// 1. Install Azurite: npm install -g azurite
    /// 2. Start Azurite before running tests: azurite --silent
    ///
    /// These tests verify:
    /// - Mount/Unmount operations
    /// - File operations (create, read, write, delete)
    /// - Directory operations (create, list)
    /// - Persistence support
    /// </summary>
    [TestFixture]
    public class AzureBlobVolumeProviderTests
    {
        private const string AzuriteConnectionString = "UseDevelopmentStorage=true";
        private const string TestContainerName = "retrofs-test";
        private AzureBlobVolumeProvider? _provider;
        private string _testVolumeId = string.Empty;

        /// <summary>
        /// Makes sure Azurite is running before any test in this fixture executes.
        /// </summary>
        /// <remarks>
        /// Starts the emulator if it is not already up, and skips the whole fixture if it cannot
        /// be started - a missing emulator is a missing prerequisite, not a defect in RetroFS.
        /// An emulator the developer started is used as-is and left running afterwards.
        /// </remarks>
        [OneTimeSetUp]
        public void EnsureAzuriteIsRunning()
        {
            AzuriteSupport.SkipIfUnavailable();
        }

        [SetUp]
        public void Setup()
        {
            _provider = new AzureBlobVolumeProvider();
            _testVolumeId = $"test-{Guid.NewGuid().ToString().Substring(0, 8)}";
        }

        [TearDown]
        public void TearDown()
        {
            if (_provider != null)
            {
                try
                {
                    // Unmount all test volumes
                    MountInfo[] mounts = _provider.ListMounts();
                    for (int i = 0; i < mounts.Length; i++)
                    {
                        _provider.Unmount(mounts[i].VolumeId);
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }

                _provider.Dispose();
                _provider = null;
            }
        }

        #region Mount/Unmount Tests

        [Test]
        [Category("Azure")]
        public void Mount_WithAzurite_Succeeds()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";

            // Act
            MountInfo mount = _provider!.Mount(uri, _testVolumeId, readOnly: false);

            // Assert
            Assert.That(mount, Is.Not.Null);
            Assert.That(mount.VolumeId, Is.EqualTo(_testVolumeId));
            Assert.That(mount.Type, Is.EqualTo("azure"));
            Assert.That(mount.FileSystemType, Is.EqualTo("Azure Blob"));
            Assert.That(mount.VolumeLabel, Is.EqualTo(TestContainerName));
            Assert.That(mount.IsReadOnly, Is.False);
            Assert.That(mount.HasBlockAccess, Is.False);
            Assert.That(mount.SourceUri, Is.EqualTo(uri));
        }

        [Test]
        [Category("Azure")]
        public void Mount_DuplicateVolumeId_ThrowsException()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
            {
                _provider.Mount(uri, _testVolumeId, readOnly: false);
            });
        }

        [Test]
        [Category("Azure")]
        public void Unmount_ExistingVolume_Succeeds()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            // Act
            _provider.Unmount(_testVolumeId);

            // Assert
            MountInfo? info = _provider.GetMountInfo(_testVolumeId);
            Assert.That(info, Is.Null);
        }

        [Test]
        [Category("Azure")]
        public void Unmount_NonExistentVolume_ThrowsException()
        {
            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
            {
                _provider!.Unmount("non-existent-volume");
            });
        }

        [Test]
        [Category("Azure")]
        public void GetMountInfo_ExistingVolume_ReturnsCorrectInfo()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            // Act
            MountInfo? info = _provider.GetMountInfo(_testVolumeId);

            // Assert
            Assert.That(info, Is.Not.Null);
            Assert.That(info!.VolumeId, Is.EqualTo(_testVolumeId));
            Assert.That(info.SourceUri, Is.EqualTo(uri));
        }

        [Test]
        [Category("Azure")]
        public void ListMounts_ReturnsAllMounts()
        {
            // Arrange
            string uri1 = $"{AzuriteConnectionString}|{TestContainerName}";
            string uri2 = $"{AzuriteConnectionString}|{TestContainerName}-2";
            string volumeId2 = _testVolumeId + "-2";

            _provider!.Mount(uri1, _testVolumeId, readOnly: false);
            _provider.Mount(uri2, volumeId2, readOnly: false);

            // Act
            MountInfo[] mounts = _provider.ListMounts();

            // Assert
            Assert.That(mounts.Length, Is.GreaterThanOrEqualTo(2));

            bool found1 = false;
            bool found2 = false;
            for (int i = 0; i < mounts.Length; i++)
            {
                if (mounts[i].VolumeId == _testVolumeId) found1 = true;
                if (mounts[i].VolumeId == volumeId2) found2 = true;
            }

            Assert.That(found1, Is.True);
            Assert.That(found2, Is.True);
        }

        #endregion

        #region File Operations Tests

        [Test]
        [Category("Azure")]
        public void WriteFile_CreatesFile()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };
            string filePath = "/test-file.txt";
            byte[] data = Encoding.UTF8.GetBytes("Hello, Azure Blob Storage!");

            // Act
            _provider.WriteFile(context, filePath, data);

            // Assert
            byte[] readData = _provider.ReadFile(context, filePath);
            Assert.That(readData, Is.Not.Null);
            Assert.That(readData.Length, Is.EqualTo(data.Length));

            string content = Encoding.UTF8.GetString(readData);
            Assert.That(content, Is.EqualTo("Hello, Azure Blob Storage!"));
        }

        [Test]
        [Category("Azure")]
        public void WriteFile_OverwritesExistingFile()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };
            string filePath = "/overwrite-test.txt";
            byte[] data1 = Encoding.UTF8.GetBytes("First content");
            byte[] data2 = Encoding.UTF8.GetBytes("Second content - this is longer");

            // Act
            _provider.WriteFile(context, filePath, data1);
            _provider.WriteFile(context, filePath, data2);

            // Assert
            byte[] readData = _provider.ReadFile(context, filePath);
            string content = Encoding.UTF8.GetString(readData);
            Assert.That(content, Is.EqualTo("Second content - this is longer"));
        }

        [Test]
        [Category("Azure")]
        public void ReadFile_NonExistentFile_ThrowsException()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };

            // Act & Assert
            Assert.Throws<System.IO.FileNotFoundException>(() =>
            {
                _provider.ReadFile(context, "/non-existent.txt");
            });
        }

        [Test]
        [Category("Azure")]
        public void DeleteFile_RemovesFile()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };
            string filePath = "/delete-test.txt";
            byte[] data = Encoding.UTF8.GetBytes("To be deleted");
            _provider.WriteFile(context, filePath, data);

            // Act
            _provider.DeleteFile(context, filePath);

            // Assert
            Assert.Throws<System.IO.FileNotFoundException>(() =>
            {
                _provider.ReadFile(context, filePath);
            });
        }

        [Test]
        [Category("Azure")]
        public void WriteFile_InSubdirectory_CreatesVirtualDirectory()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };
            string filePath = "/subdir/nested/file.txt";
            byte[] data = Encoding.UTF8.GetBytes("Nested file content");

            // Act
            _provider.WriteFile(context, filePath, data);

            // Assert
            byte[] readData = _provider.ReadFile(context, filePath);
            string content = Encoding.UTF8.GetString(readData);
            Assert.That(content, Is.EqualTo("Nested file content"));
        }

        [Test]
        [Category("Azure")]
        public void CreateFile_WithRandomData_Succeeds()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };
            string filePath = "/random-data.bin";

            byte[] randomData = new byte[2048];
            Random random = new Random(12345);
            random.NextBytes(randomData);

            // Act
            _provider.WriteFile(context, filePath, randomData);

            // Assert
            byte[] readData = _provider.ReadFile(context, filePath);
            Assert.That(readData.Length, Is.EqualTo(2048));

            // Verify data matches
            bool matches = true;
            for (int i = 0; i < randomData.Length; i++)
            {
                if (randomData[i] != readData[i])
                {
                    matches = false;
                    break;
                }
            }
            Assert.That(matches, Is.True);
        }

        #endregion

        #region Directory Operations Tests

        [Test]
        [Category("Azure")]
        public void CreateDirectory_UsingPlaceholderFile_CreatesDirectory()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };
            string dirPath = "/test-directory";
            string placeholderPath = dirPath + "/.placeholder";
            byte[] emptyData = new byte[0];

            // Act
            _provider.WriteFile(context, placeholderPath, emptyData);

            // Assert
            DirectoryEntry[] entries = _provider.ListDirectory(context, "/");

            bool foundDir = false;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Name == "test-directory" && entries[i].Metadata.IsDirectory)
                {
                    foundDir = true;
                    break;
                }
            }

            Assert.That(foundDir, Is.True);
        }

        [Test]
        [Category("Azure")]
        public void ListDirectory_RootPath_ReturnsEntries()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };

            // Create some test files
            _provider.WriteFile(context, "/file1.txt", Encoding.UTF8.GetBytes("File 1"));
            _provider.WriteFile(context, "/file2.txt", Encoding.UTF8.GetBytes("File 2"));
            _provider.WriteFile(context, "/dir1/.placeholder", new byte[0]);

            // Act
            DirectoryEntry[] entries = _provider.ListDirectory(context, "/");

            // Assert
            Assert.That(entries.Length, Is.GreaterThanOrEqualTo(3));

            bool foundFile1 = false;
            bool foundFile2 = false;
            bool foundDir1 = false;

            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Name == "file1.txt" && !entries[i].Metadata.IsDirectory)
                    foundFile1 = true;
                if (entries[i].Name == "file2.txt" && !entries[i].Metadata.IsDirectory)
                    foundFile2 = true;
                if (entries[i].Name == "dir1" && entries[i].Metadata.IsDirectory)
                    foundDir1 = true;
            }

            Assert.That(foundFile1, Is.True);
            Assert.That(foundFile2, Is.True);
            Assert.That(foundDir1, Is.True);
        }

        [Test]
        [Category("Azure")]
        public void ListDirectory_Subdirectory_ReturnsCorrectEntries()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };

            // Create hierarchical structure
            _provider.WriteFile(context, "/parent/child1.txt", Encoding.UTF8.GetBytes("Child 1"));
            _provider.WriteFile(context, "/parent/child2.txt", Encoding.UTF8.GetBytes("Child 2"));
            _provider.WriteFile(context, "/parent/subdir/.placeholder", new byte[0]);

            // Act
            DirectoryEntry[] entries = _provider.ListDirectory(context, "/parent");

            // Assert
            Assert.That(entries.Length, Is.GreaterThanOrEqualTo(3));

            bool foundChild1 = false;
            bool foundChild2 = false;
            bool foundSubdir = false;

            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Name == "child1.txt") foundChild1 = true;
                if (entries[i].Name == "child2.txt") foundChild2 = true;
                if (entries[i].Name == "subdir" && entries[i].Metadata.IsDirectory) foundSubdir = true;
            }

            Assert.That(foundChild1, Is.True);
            Assert.That(foundChild2, Is.True);
            Assert.That(foundSubdir, Is.True);
        }

        [Test]
        [Category("Azure")]
        public void ListDirectory_EmptyDirectory_ReturnsEmpty()
        {
            // Arrange
            string uniqueContainer = TestContainerName + "-empty-" + _testVolumeId;
            string uri = $"{AzuriteConnectionString}|{uniqueContainer}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };

            // Act
            DirectoryEntry[] entries = _provider.ListDirectory(context, "/");

            // Assert - new container should be empty
            Assert.That(entries.Length, Is.EqualTo(0));
        }

        #endregion

        #region Metadata Tests

        [Test]
        [Category("Azure")]
        public void GetMetadata_ExistingFile_ReturnsMetadata()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };
            string filePath = "/metadata-test.txt";
            byte[] data = Encoding.UTF8.GetBytes("Test content for metadata");
            _provider.WriteFile(context, filePath, data);

            // Act
            FileMetadata? metadata = _provider.GetMetadata(context, filePath);

            // Assert
            Assert.That(metadata, Is.Not.Null);
            Assert.That(metadata!.IsDirectory, Is.False);
            Assert.That(metadata.Size, Is.EqualTo(data.Length));
            Assert.That(metadata.FileType, Is.EqualTo("[BLB]"));
        }

        [Test]
        [Category("Azure")]
        public void GetMetadata_VirtualDirectory_ReturnsDirectoryMetadata()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };
            string dirPath = "/metadata-dir";
            _provider.WriteFile(context, dirPath + "/file.txt", Encoding.UTF8.GetBytes("File in dir"));

            // Act
            FileMetadata? metadata = _provider.GetMetadata(context, dirPath);

            // Assert
            Assert.That(metadata, Is.Not.Null);
            Assert.That(metadata!.IsDirectory, Is.True);
            Assert.That(metadata.Size, Is.EqualTo(0));
        }

        [Test]
        [Category("Azure")]
        public void GetMetadata_NonExistent_ReturnsNull()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };

            // Act
            FileMetadata? metadata = _provider.GetMetadata(context, "/does-not-exist.txt");

            // Assert
            Assert.That(metadata, Is.Null);
        }

        #endregion

        #region Persistence Tests

        [Test]
        [Category("Azure")]
        public void CanRestore_ValidEntry_ReturnsTrue()
        {
            // Arrange
            UnifiedMountEntry entry = new UnifiedMountEntry
            {
                VolumeId = "test-volume",
                ProviderType = "azure",
                SourceUri = $"{AzuriteConnectionString}|{TestContainerName}",
                IsReadOnly = false
            };

            // Act
            bool canRestore = _provider!.CanRestore(entry, out string? errorMessage);

            // Assert
            Assert.That(canRestore, Is.True);
            Assert.That(errorMessage, Is.Null);
        }

        [Test]
        [Category("Azure")]
        public void CanRestore_WrongProviderType_ReturnsFalse()
        {
            // Arrange
            UnifiedMountEntry entry = new UnifiedMountEntry
            {
                VolumeId = "test-volume",
                ProviderType = "tnfs",
                SourceUri = "some-uri",
                IsReadOnly = false
            };

            // Act
            bool canRestore = _provider!.CanRestore(entry, out string? errorMessage);

            // Assert
            Assert.That(canRestore, Is.False);
            Assert.That(errorMessage, Is.Not.Null);
        }

        [Test]
        [Category("Azure")]
        public void CanRestore_MissingSourceUri_ReturnsFalse()
        {
            // Arrange
            UnifiedMountEntry entry = new UnifiedMountEntry
            {
                VolumeId = "test-volume",
                ProviderType = "azure",
                SourceUri = "",
                IsReadOnly = false
            };

            // Act
            bool canRestore = _provider!.CanRestore(entry, out string? errorMessage);

            // Assert
            Assert.That(canRestore, Is.False);
            Assert.That(errorMessage, Is.Not.Null);
        }

        [Test]
        [Category("Azure")]
        public void MountFromConfig_ValidEntry_Succeeds()
        {
            // Arrange
            UnifiedMountEntry entry = new UnifiedMountEntry
            {
                VolumeId = _testVolumeId,
                ProviderType = "azure",
                SourceUri = $"{AzuriteConnectionString}|{TestContainerName}",
                IsReadOnly = false
            };

            // Act
            MountInfo mount = _provider!.MountFromConfig(entry);

            // Assert
            Assert.That(mount, Is.Not.Null);
            Assert.That(mount.VolumeId, Is.EqualTo(_testVolumeId));
            Assert.That(mount.Type, Is.EqualTo("azure"));
            Assert.That(mount.SourceUri, Is.EqualTo(entry.SourceUri));
        }

        #endregion

        #region Read-Only Tests

        [Test]
        [Category("Azure")]
        public void WriteFile_ReadOnlyMount_ThrowsException()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: true);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };
            byte[] data = Encoding.UTF8.GetBytes("Should not write");

            // Act & Assert
            Assert.Throws<UnauthorizedAccessException>(() =>
            {
                _provider.WriteFile(context, "/readonly-test.txt", data);
            });
        }

        [Test]
        [Category("Azure")]
        public void DeleteFile_ReadOnlyMount_ThrowsException()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";

            // First mount as writable, create file
            _provider!.Mount(uri, _testVolumeId, readOnly: false);
            UrnContext context = new UrnContext { VolumeId = _testVolumeId };
            _provider.WriteFile(context, "/readonly-delete-test.txt", Encoding.UTF8.GetBytes("Data"));
            _provider.Unmount(_testVolumeId);

            // Remount as read-only
            _provider.Mount(uri, _testVolumeId, readOnly: true);

            // Act & Assert
            Assert.Throws<UnauthorizedAccessException>(() =>
            {
                _provider.DeleteFile(context, "/readonly-delete-test.txt");
            });
        }

        #endregion

        #region Block Access Tests

        [Test]
        [Category("Azure")]
        public void ReadBlock_ThrowsNotSupportedException()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };
            byte[] buffer = new byte[256];
            BlockAddress address = BlockAddress.FromTrackSector(1, 0);

            // Act & Assert
            Assert.Throws<NotSupportedException>(() =>
            {
                _provider.ReadBlock(context, address, buffer);
            });
        }

        [Test]
        [Category("Azure")]
        public void WriteBlock_ThrowsNotSupportedException()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };
            byte[] data = new byte[256];
            BlockAddress address = BlockAddress.FromTrackSector(1, 0);

            // Act & Assert
            Assert.Throws<NotSupportedException>(() =>
            {
                _provider.WriteBlock(context, address, data);
            });
        }

        [Test]
        [Category("Azure")]
        public void GetBlockDeviceInfo_ReturnsNull()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };

            // Act
            BlockDeviceInfo? info = _provider.GetBlockDeviceInfo(context);

            // Assert
            Assert.That(info, Is.Null);
        }

        #endregion
    }
}
