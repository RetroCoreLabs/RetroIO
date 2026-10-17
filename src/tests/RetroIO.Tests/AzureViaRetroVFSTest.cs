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
    /// Tests Azure Blob Storage through RetroVFS - EXACTLY like the demo program does.
    /// This will prove whether the issue is in the provider or in how RetroVFS wraps it.
    /// </summary>
    [TestFixture]
    public class AzureViaRetroVFSTest
    {
        private const string AzuriteConnectionString = "UseDevelopmentStorage=true";
        private const string TestContainerName = "retrofs-vfs-test";
        private RetroVFS? _vfs;
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
            // Setup EXACTLY like demo program does
            _vfs = new RetroVFS(enablePersistence: false); // Disable persistence for tests
            _vfs.RegisterProvider(new AzureBlobVolumeProvider());
            _testVolumeId = $"vfs-test-{Guid.NewGuid().ToString().Substring(0, 8)}";
        }

        [TearDown]
        public void TearDown()
        {
            if (_vfs != null)
            {
                try
                {
                    MountInfo[] mounts = _vfs.GetAllMounts();
                    for (int i = 0; i < mounts.Length; i++)
                    {
                        _vfs.Unmount(mounts[i].VolumeId);
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }

                _vfs.Dispose();
                _vfs = null;
            }
        }

        [Test]
        [Category("Azure")]
        [Category("RetroVFS")]
        public void Mount_ThroughRetroVFS_Succeeds()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";

            // Act - Mount EXACTLY like demo does
            MountInfo mount = _vfs!.Mount("azure", uri, _testVolumeId, readOnly: false);

            // Assert
            Assert.That(mount, Is.Not.Null);
            Assert.That(mount.VolumeId, Is.EqualTo(_testVolumeId));
            Assert.That(mount.Type, Is.EqualTo("azure"));
            Console.WriteLine($"✓ Mounted: {mount.VolumeId}");
        }

        [Test]
        [Category("Azure")]
        [Category("RetroVFS")]
        public void GetProvider_ReturnsAzureBlobVolumeProvider()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _vfs!.Mount("azure", uri, _testVolumeId, readOnly: false);

            // Act - Get provider EXACTLY like demo does
            IVolumeProvider? provider = _vfs.GetProvider(_testVolumeId);

            // Assert
            Assert.That(provider, Is.Not.Null);
            Assert.That(provider!.GetType().Name, Is.EqualTo("AzureBlobVolumeProvider"));
            Console.WriteLine($"✓ Provider type: {provider.GetType().Name}");
        }

        [Test]
        [Category("Azure")]
        [Category("RetroVFS")]
        public void WriteFile_ThroughRetroVFS_CreatesFile()
        {
            // Arrange - Mount through RetroVFS like demo does
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _vfs!.Mount("azure", uri, _testVolumeId, readOnly: false);

            // Get provider EXACTLY like demo does
            IVolumeProvider? provider = _vfs.GetProvider(_testVolumeId);
            Assert.That(provider, Is.Not.Null, "Provider should not be null");

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };
            string filePath = "/test-file.txt";
            byte[] data = Encoding.UTF8.GetBytes("Test content from RetroVFS");

            Console.WriteLine($"DEBUG: Provider type: {provider!.GetType().Name}");
            Console.WriteLine($"DEBUG: Volume ID: {context.VolumeId}");
            Console.WriteLine($"DEBUG: Writing file to: {filePath}");
            Console.WriteLine($"DEBUG: Data size: {data.Length} bytes");

            // Act - Write file EXACTLY like demo does
            provider.WriteFile(context, filePath, data);

            Console.WriteLine($"✓ File written successfully");

            // Assert - Read it back
            byte[] readData = provider.ReadFile(context, filePath);
            string content = Encoding.UTF8.GetString(readData);
            Assert.That(content, Is.EqualTo("Test content from RetroVFS"));
            Console.WriteLine($"✓ File read successfully: {content}");
        }

        [Test]
        [Category("Azure")]
        [Category("RetroVFS")]
        public void CreateDirectory_ThroughRetroVFS_CreatesPlaceholder()
        {
            // Arrange - Mount through RetroVFS
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _vfs!.Mount("azure", uri, _testVolumeId, readOnly: false);

            IVolumeProvider? provider = _vfs.GetProvider(_testVolumeId);
            Assert.That(provider, Is.Not.Null);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };
            string currentPath = "/";
            string dirName = "test-directory";

            // Act - Create directory EXACTLY like demo does
            string dirPath = currentPath == "/" ? "/" + dirName : currentPath + "/" + dirName;
            string placeholderPath = dirPath + "/.placeholder";
            byte[] emptyData = new byte[0];

            Console.WriteLine($"DEBUG: Creating directory: {dirPath}");
            Console.WriteLine($"DEBUG: Placeholder path: {placeholderPath}");
            Console.WriteLine($"DEBUG: Provider type: {provider!.GetType().Name}");

            provider!.WriteFile(context, placeholderPath, emptyData);

            Console.WriteLine($"✓ Directory created: {dirPath}");

            // Assert - Verify directory appears in listing
            DirectoryEntry[] entries = provider.ListDirectory(context, "/");

            bool foundDir = false;
            for (int i = 0; i < entries.Length; i++)
            {
                Console.WriteLine($"  Entry: {entries[i].Name} (IsDir: {entries[i].Metadata.IsDirectory})");
                if (entries[i].Name == "test-directory" && entries[i].Metadata.IsDirectory)
                {
                    foundDir = true;
                }
            }

            Assert.That(foundDir, Is.True, "Directory should appear in listing");
            Console.WriteLine($"✓ Directory verified in listing");
        }

        [Test]
        [Category("Azure")]
        [Category("RetroVFS")]
        public void CreateFile_WithRandomData_ThroughRetroVFS_Succeeds()
        {
            // Arrange - Mount through RetroVFS
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _vfs!.Mount("azure", uri, _testVolumeId, readOnly: false);

            IVolumeProvider? provider = _vfs.GetProvider(_testVolumeId);
            Assert.That(provider, Is.Not.Null);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };
            string currentPath = "/";
            string fileName = "random-file.dat";
            int size = 2048;

            // Act - Create file with random data EXACTLY like demo does
            Console.WriteLine($"Creating {size} bytes of random data...");

            byte[] data = new byte[size];
            Random random = new Random();
            random.NextBytes(data);

            string filePath = currentPath == "/" ? "/" + fileName : currentPath + "/" + fileName;

            Console.WriteLine($"DEBUG: Writing file to: {filePath}");
            Console.WriteLine($"DEBUG: Provider type: {provider!.GetType().Name}");
            Console.WriteLine($"DEBUG: Volume ID: {context.VolumeId}");
            Console.WriteLine($"DEBUG: Data size: {data.Length} bytes");

            provider!.WriteFile(context, filePath, data);

            Console.WriteLine($"✓ Created file: {filePath}");

            // Assert - Read back and verify
            byte[] readData = provider.ReadFile(context, filePath);
            Assert.That(readData.Length, Is.EqualTo(size));

            // Verify data matches
            bool matches = true;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != readData[i])
                {
                    matches = false;
                    break;
                }
            }
            Assert.That(matches, Is.True, "Data should match");
            Console.WriteLine($"✓ File content verified");
        }

        [Test]
        [Category("Azure")]
        [Category("RetroVFS")]
        public void UploadFile_ThroughRetroVFS_Succeeds()
        {
            // Arrange - Mount through RetroVFS
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _vfs!.Mount("azure", uri, _testVolumeId, readOnly: false);

            IVolumeProvider? provider = _vfs.GetProvider(_testVolumeId);
            Assert.That(provider, Is.Not.Null);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };
            string currentPath = "/";
            string fileName = "uploaded-file.txt";
            byte[] fileData = Encoding.UTF8.GetBytes("This is uploaded content");

            // Act - Upload file EXACTLY like demo does
            string remotePath = currentPath == "/" ? "/" + fileName : currentPath + "/" + fileName;

            Console.WriteLine($"DEBUG: Uploading to: {remotePath}");
            Console.WriteLine($"DEBUG: Provider type: {provider!.GetType().Name}");
            Console.WriteLine($"DEBUG: Data size: {fileData.Length} bytes");

            provider!.WriteFile(context, remotePath, fileData);

            Console.WriteLine($"✓ Uploaded to: {remotePath}");

            // Assert - Read back
            byte[] readData = provider.ReadFile(context, remotePath);
            string content = Encoding.UTF8.GetString(readData);
            Assert.That(content, Is.EqualTo("This is uploaded content"));
            Console.WriteLine($"✓ Upload verified");
        }

        [Test]
        [Category("Azure")]
        [Category("RetroVFS")]
        public void ListDirectory_ThroughRetroVFS_ShowsFiles()
        {
            // Arrange - Mount and create files
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _vfs!.Mount("azure", uri, _testVolumeId, readOnly: false);

            IVolumeProvider? provider = _vfs.GetProvider(_testVolumeId);
            Assert.That(provider, Is.Not.Null);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };

            // Create test files
            provider!.WriteFile(context, "/file1.txt", Encoding.UTF8.GetBytes("File 1"));
            provider.WriteFile(context, "/file2.txt", Encoding.UTF8.GetBytes("File 2"));
            provider.WriteFile(context, "/dir1/.placeholder", new byte[0]);

            // Act - List directory EXACTLY like demo does
            DirectoryEntry[] entries = provider.ListDirectory(context, "/");

            // Assert
            Console.WriteLine($"Found {entries.Length} entries:");
            for (int i = 0; i < entries.Length; i++)
            {
                string type = entries[i].Metadata.IsDirectory ? "[DIR]" : "[FILE]";
                Console.WriteLine($"  {i + 1}. {type} {entries[i].Name}");
            }

            Assert.That(entries.Length, Is.GreaterThanOrEqualTo(3));
        }
    }
}
