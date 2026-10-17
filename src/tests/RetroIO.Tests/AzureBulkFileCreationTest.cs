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
    /// Bulk file creation test for Azure Blob Storage.
    /// Creates 100 files to verify performance and functionality.
    /// </summary>
    [TestFixture]
    public class AzureBulkFileCreationTest
    {
        private const string AzuriteConnectionString = "UseDevelopmentStorage=true";
        private const string TestContainerName = "retrofs-bulk-test";
        private AzureBlobVolumeProvider? _provider;
        private string _testVolumeId = "bulk-test";

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
        }

        [TearDown]
        public void TearDown()
        {
            if (_provider != null)
            {
                try
                {
                    _provider.Unmount(_testVolumeId);
                }
                catch
                {
                    // Ignore cleanup errors
                }

                _provider.Dispose();
                _provider = null;
            }
        }

        [Test]
        [Category("Azure")]
        [Category("BulkTest")]
        public void CreateBulkFiles_Creates100Files_AllFilesCreated()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };
            Random random = new Random(42); // Fixed seed for reproducibility

            Console.WriteLine("Creating 100 test files in Azure Blob Storage...");
            Console.WriteLine();

            // Act - Create 100 files
            int filesCreated = 0;
            DateTime startTime = DateTime.UtcNow;

            for (int i = 1; i <= 100; i++)
            {
                string fileName = $"/test-file-{i:D3}.dat";
                int fileSize = random.Next(100, 10000); // Random size between 100 and 10000 bytes

                byte[] data = new byte[fileSize];
                random.NextBytes(data);

                _provider.WriteFile(context, fileName, data);
                filesCreated++;

                if (i % 10 == 0)
                {
                    Console.WriteLine($"Created {i} files...");
                }
            }

            DateTime endTime = DateTime.UtcNow;
            TimeSpan duration = endTime - startTime;

            Console.WriteLine();
            Console.WriteLine($"✓ Created {filesCreated} files in {duration.TotalSeconds:F2} seconds");
            Console.WriteLine($"  Average: {duration.TotalMilliseconds / filesCreated:F2} ms per file");

            // Assert - Verify all files were created
            Assert.That(filesCreated, Is.EqualTo(100));

            // Verify by listing directory
            DirectoryEntry[] entries = _provider.ListDirectory(context, "/");

            int fileCount = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                if (!entries[i].Metadata.IsDirectory)
                {
                    fileCount++;
                }
            }

            Console.WriteLine();
            Console.WriteLine($"✓ Verified: {fileCount} files listed in directory");

            Assert.That(fileCount, Is.GreaterThanOrEqualTo(100),
                "Should have at least 100 files in directory");

            // Verify we can read some files back
            Console.WriteLine();
            Console.WriteLine("Verifying file contents...");

            for (int i = 1; i <= 10; i++)
            {
                string fileName = $"/test-file-{i:D3}.dat";
                byte[] readData = _provider.ReadFile(context, fileName);
                Assert.That(readData.Length, Is.GreaterThan(0),
                    $"File {fileName} should have data");
            }

            Console.WriteLine("✓ Successfully verified 10 sample files");
        }

        [Test]
        [Category("Azure")]
        [Category("BulkTest")]
        public void CreateDirectoryStructure_WithMultipleLevels_CreatesHierarchy()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };

            Console.WriteLine("Creating directory structure with 100 files...");
            Console.WriteLine();

            // Act - Create directory structure
            string[] categories = { "commodore", "dragon", "pc", "amiga", "atari" };
            int totalFiles = 0;

            for (int catIdx = 0; catIdx < categories.Length; catIdx++)
            {
                string category = categories[catIdx];

                for (int i = 1; i <= 20; i++)
                {
                    string filePath = $"/{category}/file-{i:D2}.prg";
                    byte[] data = Encoding.UTF8.GetBytes($"Content for {filePath}");
                    _provider.WriteFile(context, filePath, data);
                    totalFiles++;
                }

                Console.WriteLine($"Created 20 files in /{category}");
            }

            Console.WriteLine();
            Console.WriteLine($"✓ Total files created: {totalFiles}");

            // Assert
            Assert.That(totalFiles, Is.EqualTo(100));

            // Verify directory structure
            DirectoryEntry[] rootEntries = _provider.ListDirectory(context, "/");

            int dirCount = 0;
            for (int i = 0; i < rootEntries.Length; i++)
            {
                if (rootEntries[i].Metadata.IsDirectory)
                {
                    dirCount++;
                }
            }

            Console.WriteLine($"✓ Found {dirCount} directories at root level");
            Assert.That(dirCount, Is.GreaterThanOrEqualTo(5),
                "Should have at least 5 directories");

            // Verify files in one directory
            DirectoryEntry[] commodoreEntries = _provider.ListDirectory(context, "/commodore");

            int commodoreFileCount = 0;
            for (int i = 0; i < commodoreEntries.Length; i++)
            {
                if (!commodoreEntries[i].Metadata.IsDirectory)
                {
                    commodoreFileCount++;
                }
            }

            Console.WriteLine($"✓ Found {commodoreFileCount} files in /commodore");
            Assert.That(commodoreFileCount, Is.EqualTo(20),
                "Should have 20 files in commodore directory");
        }

        [Test]
        [Category("Azure")]
        [Category("BulkTest")]
        public void CreateMixedSizeFiles_VariousSizes_AllCreatedSuccessfully()
        {
            // Arrange
            string uri = $"{AzuriteConnectionString}|{TestContainerName}";
            _provider!.Mount(uri, _testVolumeId, readOnly: false);

            UrnContext context = new UrnContext { VolumeId = _testVolumeId };
            Random random = new Random(123);

            Console.WriteLine("Creating files with various sizes...");
            Console.WriteLine();

            // Act - Create files with different size categories
            int[] sizeCategories = { 10, 100, 1024, 10240, 102400 }; // 10B, 100B, 1KB, 10KB, 100KB
            int totalFiles = 0;
            long totalBytes = 0;

            for (int catIdx = 0; catIdx < sizeCategories.Length; catIdx++)
            {
                int baseSize = sizeCategories[catIdx];

                for (int i = 0; i < 20; i++)
                {
                    string fileName = $"/size-{baseSize}-file-{i:D2}.bin";
                    int variance = random.Next(-baseSize / 10, baseSize / 10);
                    int fileSize = Math.Max(1, baseSize + variance);

                    byte[] data = new byte[fileSize];
                    random.NextBytes(data);

                    _provider.WriteFile(context, fileName, data);
                    totalFiles++;
                    totalBytes += fileSize;
                }

                string sizeStr = FormatBytes(baseSize);
                Console.WriteLine($"Created 20 files around {sizeStr}");
            }

            Console.WriteLine();
            Console.WriteLine($"✓ Total files created: {totalFiles}");
            Console.WriteLine($"✓ Total data written: {FormatBytes(totalBytes)}");

            // Assert
            Assert.That(totalFiles, Is.EqualTo(100));
            Assert.That(totalBytes, Is.GreaterThan(0));

            // Verify by reading back some files
            for (int i = 0; i < 5; i++)
            {
                string fileName = $"/size-1024-file-{i:D2}.bin";
                byte[] readData = _provider.ReadFile(context, fileName);
                Assert.That(readData.Length, Is.GreaterThan(900),
                    $"File {fileName} should be around 1KB");
                Assert.That(readData.Length, Is.LessThan(1200),
                    $"File {fileName} should be around 1KB");
            }

            Console.WriteLine("✓ Verified sample file sizes");
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return $"{bytes} B";
            }
            else if (bytes < 1024 * 1024)
            {
                return $"{bytes / 1024.0:F1} KB";
            }
            else
            {
                return $"{bytes / (1024.0 * 1024.0):F1} MB";
            }
        }
    }
}
