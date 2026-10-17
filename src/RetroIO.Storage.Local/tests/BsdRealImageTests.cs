//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.BSD.Partitions;
using RetroIO.Storage.Local.BSD.Structures;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Local.V7.FileSystems;
using RetroIO.Storage.Local.V7.Structures;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Tests against real BSD disk images to validate partition detection and filesystem mounting.
    /// These tests verify that the implementation correctly handles actual BSD 2.11 disk images.
    ///
    /// Runs in the normal suite: every test checks File.Exists on its image and calls Assert.Ignore
    /// when it is absent, so a machine without the images reports skips rather than failures.
    /// Previously [Explicit], which meant it never ran and never guarded anything.
    /// </summary>
    [TestFixture]
    public class BsdRealImageValidationTests
    {
        private const string CleanInstallImage = @"E:\Dev\Ronny\BSD\Boot\1.FromTape\backup\clean_install";
        private const string TapeImage = @"E:\Dev\Ronny\BSD\Boot\1b.FromTape\2.11BSD";

        [Test]
        [Description("Validates clean_install image has disk label with 3 partitions (2 BSD + 1 swap)")]
        public void CleanInstall_DiskLabel_Has3Partitions()
        {
            // Arrange
            if (!File.Exists(CleanInstallImage))
            {
                Assert.Ignore($"Test image not found: {CleanInstallImage}");
                return;
            }

            TestContext.Out.WriteLine($"Testing BSD image: {CleanInstallImage}");
            TestContext.Out.WriteLine($"File size: {new FileInfo(CleanInstallImage).Length:N0} bytes");
            TestContext.Out.WriteLine("");

            var descriptor = new BlockDeviceDescriptor
            {
                Name = "BSD Clean Install",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(new FileInfo(CleanInstallImage).Length / 512)
            };

            var device = new GenericBlockDevice(descriptor);
            using var stream = new FileStream(CleanInstallImage, FileMode.Open, FileAccess.Read, FileShare.Read);
            device.Mount(stream, readOnly: true);

            // Act: Read disk label
            var partMgr = new BsdPartitionManager(device);
            var diskLabel = partMgr.GetDiskLabel();

            // Assert: Disk label exists
            Assert.That(diskLabel, Is.Not.Null, "Disk label should be present");
            Assert.That(diskLabel!.IsValid(), Is.True, "Disk label should be valid");

            TestContext.Out.WriteLine("=== DISK LABEL ===");
            TestContext.Out.WriteLine($"Magic: 0x{diskLabel.Magic:X8} (valid={diskLabel.Magic == V7Constants.DISK_MAGIC})");
            TestContext.Out.WriteLine($"Type: {diskLabel.TypeName}");
            TestContext.Out.WriteLine($"Pack: {diskLabel.PackName}");
            TestContext.Out.WriteLine($"Geometry: {diskLabel.CylindersPerUnit}C / {diskLabel.TracksPerCylinder}H / {diskLabel.SectorsPerTrack}S");
            TestContext.Out.WriteLine($"Total sectors: {diskLabel.SectorsPerUnit:N0}");
            TestContext.Out.WriteLine($"Bytes per sector: {diskLabel.BytesPerSector}");
            TestContext.Out.WriteLine($"Number of partitions: {diskLabel.NumPartitions}");
            TestContext.Out.WriteLine($"Checksum valid: {diskLabel.ValidateChecksum()}");
            TestContext.Out.WriteLine("");

            // Assert: 3 partitions
            Assert.That(diskLabel.NumPartitions, Is.EqualTo(3),
                "clean_install should have exactly 3 partitions");

            // Act: List all partitions
            var partitions = partMgr.ListPartitions();

            TestContext.Out.WriteLine("=== PARTITIONS ===");
            int bsdFilesystemCount = 0;
            int swapCount = 0;

            for (int i = 0; i < partitions.Count; i++)
            {
                var (letter, part) = partitions[i];
                double sizeMB = (part.Size * 512.0) / (1024.0 * 1024.0);

                TestContext.Out.WriteLine($"Partition [{letter}]:");
                TestContext.Out.WriteLine($"  Type: {part.FileSystemTypeName} (code={part.FsType})");
                TestContext.Out.WriteLine($"  Offset: {part.Offset:N0} sectors");
                TestContext.Out.WriteLine($"  Size: {part.Size:N0} sectors ({sizeMB:F2} MB)");
                TestContext.Out.WriteLine($"  Fragment size: {part.FragmentSize} bytes");
                TestContext.Out.WriteLine($"  Frags per block: {part.FragmentsPerBlock}");
                TestContext.Out.WriteLine($"  Is V7 filesystem: {part.IsV7Filesystem}");
                TestContext.Out.WriteLine($"  Is swap: {part.IsSwap}");
                TestContext.Out.WriteLine("");

                if (part.IsV7Filesystem)
                    bsdFilesystemCount++;
                if (part.IsSwap)
                    swapCount++;
            }

            // Assert: Partition counts
            Assert.That(partitions.Count, Is.EqualTo(3),
                "Should detect all 3 partitions from disk label");
            Assert.That(bsdFilesystemCount, Is.EqualTo(2),
                "Should have exactly 2 BSD V7 filesystems");
            Assert.That(swapCount, Is.EqualTo(1),
                "Should have exactly 1 swap partition");

            TestContext.Out.WriteLine("=== SUMMARY ===");
            TestContext.Out.WriteLine($"✓ Found {partitions.Count} partitions");
            TestContext.Out.WriteLine($"✓ Found {bsdFilesystemCount} BSD filesystems");
            TestContext.Out.WriteLine($"✓ Found {swapCount} swap partition");
        }

        [Test]
        [Description("Mounts each BSD filesystem partition and lists root directory")]
        public void CleanInstall_MountAllBsdPartitions_CanListRoot()
        {
            // Arrange
            if (!File.Exists(CleanInstallImage))
            {
                Assert.Ignore($"Test image not found: {CleanInstallImage}");
                return;
            }

            TestContext.Out.WriteLine($"Testing filesystem mounting: {CleanInstallImage}");
            TestContext.Out.WriteLine("");

            // First, list all partition names
            var previewDescriptor = new BlockDeviceDescriptor
            {
                Name = "BSD Clean Install",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(new FileInfo(CleanInstallImage).Length / 512)
            };

            var previewDevice = new GenericBlockDevice(previewDescriptor);
            using (var previewStream = new FileStream(CleanInstallImage, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                previewDevice.Mount(previewStream, readOnly: true);
                var previewPartMgr = new BsdPartitionManager(previewDevice);
                var partList = previewPartMgr.ListPartitions();

                TestContext.Out.WriteLine("=== PARTITION NAMES ===");
                for (int i = 0; i < partList.Count; i++)
                {
                    var (letter, part) = partList[i];
                    TestContext.Out.WriteLine($"Partition '{letter}': {part.FileSystemTypeName}");
                }
                TestContext.Out.WriteLine("");
            }

            var descriptor = new BlockDeviceDescriptor
            {
                Name = "BSD Clean Install",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(new FileInfo(CleanInstallImage).Length / 512)
            };

            var device = new GenericBlockDevice(descriptor);
            using var stream = new FileStream(CleanInstallImage, FileMode.Open, FileAccess.Read, FileShare.Read);
            device.Mount(stream, readOnly: true);

            // Find all BSD partitions
            var partMgr = new BsdPartitionManager(device);
            var partitions = partMgr.ListPartitions();

            int mountedCount = 0;

            foreach (var (letter, part) in partitions)
            {
                if (!part.IsV7Filesystem)
                {
                    TestContext.Out.WriteLine($"Skipping partition [{letter}]: {part.FileSystemTypeName}");
                    continue;
                }

                TestContext.Out.WriteLine($"=== PARTITION [{letter}] ===");

                // Create fresh device for each test
                var testDevice = new GenericBlockDevice(descriptor);
                using var testStream = new FileStream(CleanInstallImage, FileMode.Open, FileAccess.Read, FileShare.Read);
                testDevice.Mount(testStream, readOnly: true);

                // Act: Mount filesystem
                var fs = new V7FileSystem
                {
                    PartitionLetter = letter,
                    UseDiskLabel = true
                };

                Assert.DoesNotThrow(() => fs.Initialize(testDevice),
                    $"Should be able to mount partition [{letter}]");

                TestContext.Out.WriteLine($"Type: {fs.FileSystemType}");
                TestContext.Out.WriteLine($"Label: '{fs.VolumeLabel}'");
                TestContext.Out.WriteLine($"Free blocks: {fs.GetBlocksFree():N0}");

                // List root directory
                var rootEntries = fs.ListDirectory("/");
                TestContext.Out.WriteLine($"Root directory entries: {rootEntries.Length}");
                TestContext.Out.WriteLine("");

                Assert.That(rootEntries, Is.Not.Null);
                Assert.That(rootEntries.Length, Is.GreaterThan(0),
                    $"Partition [{letter}] root directory should have entries");

                // Show ALL entries
                for (int i = 0; i < rootEntries.Length; i++)
                {
                    var entry = rootEntries[i];
                    string type = entry.Metadata.IsDirectory ? "[DIR] " : "[FILE]";
                    string size = entry.Metadata.IsDirectory ? "         " : $"{entry.Metadata.Size,9}";
                    string perms = entry.Metadata.IsDirectory ? "drwxr-xr-x" : "-rw-r--r--";
                    TestContext.Out.WriteLine($"  {type} {perms} {size}  {entry.Name}");
                }

                TestContext.Out.WriteLine("");
                mountedCount++;
            }

            // Assert: Successfully mounted both BSD partitions
            Assert.That(mountedCount, Is.EqualTo(2),
                "Should successfully mount both BSD filesystem partitions");
        }

        [Test]
        [Description("Validates partition 'a' is bootable root filesystem")]
        public void CleanInstall_PartitionA_IsRootFilesystem()
        {
            // Arrange
            if (!File.Exists(CleanInstallImage))
            {
                Assert.Ignore($"Test image not found: {CleanInstallImage}");
                return;
            }

            var descriptor = new BlockDeviceDescriptor
            {
                Name = "BSD Clean Install",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(new FileInfo(CleanInstallImage).Length / 512)
            };

            var device = new GenericBlockDevice(descriptor);
            using var stream = new FileStream(CleanInstallImage, FileMode.Open, FileAccess.Read, FileShare.Read);
            device.Mount(stream, readOnly: true);

            // Act: Mount partition 'a' (traditionally the root partition)
            var fs = new V7FileSystem
            {
                PartitionLetter = 'a',
                UseDiskLabel = true
            };

            fs.Initialize(device);
            var rootEntries = fs.ListDirectory("/");

            // Assert: Common Unix root directories should exist
            bool hasEtc = false, hasBin = false, hasUsr = false, hasDev = false;

            for (int i = 0; i < rootEntries.Length; i++)
            {
                string name = rootEntries[i].Name.ToLowerInvariant();
                if (name == "etc") hasEtc = true;
                if (name == "bin") hasBin = true;
                if (name == "usr") hasUsr = true;
                if (name == "dev") hasDev = true;
            }

            TestContext.Out.WriteLine($"Root filesystem (partition 'a') contains:");
            TestContext.Out.WriteLine($"  /etc: {hasEtc}");
            TestContext.Out.WriteLine($"  /bin: {hasBin}");
            TestContext.Out.WriteLine($"  /usr: {hasUsr}");
            TestContext.Out.WriteLine($"  /dev: {hasDev}");

            // At least some standard Unix directories should exist
            Assert.That(hasEtc || hasBin || hasUsr, Is.True,
                "Root partition should contain standard Unix directories");
        }

        [Test]
        [Description("Tests V7Driver with partition selection via URN")]
        public void CleanInstall_V7Driver_MountsSpecificPartition()
        {
            // Arrange
            if (!File.Exists(CleanInstallImage))
            {
                Assert.Ignore($"Test image not found: {CleanInstallImage}");
                return;
            }

            var driver = new V7Driver();
            driver.SetPartition('a');  // Mount partition 'a'

            // Act
            using var stream = new FileStream(CleanInstallImage, FileMode.Open, FileAccess.Read, FileShare.Read);
            driver.Mount(stream, readOnly: true);

            // Assert
            // This test is [Explicit] (needs a real disk image) and had never actually been run
            // before this QA pass — the assertion below was stale: V7Driver.FileSystemType
            // (BsdDriver.cs) appends "-{partitionLetter}" whenever disk-label mode is active
            // (the default, and explicitly set here via SetPartition('a')), so a mounted driver
            // with a selected partition reports "V7-a", not "BSD-V7". Verified by actually
            // running this test against the real fixture, not assumed.
            Assert.That(driver.FileSystemType, Is.EqualTo("V7-a"));

            // The redundant Assert.DoesNotThrow(() => driver.ListDirectory("/")) that used to
            // sit here called ListDirectory a second, throwaway time and only checked "didn't
            // crash" — the very next lines already call it for real and assert entries.Length,
            // which is the actual, meaningful check. Removed the dead duplicate call.
            var entries = driver.ListDirectory("/");
            TestContext.Out.WriteLine($"Mounted partition 'a' via V7Driver:");
            TestContext.Out.WriteLine($"  Root entries: {entries.Length}");

            Assert.That(entries.Length, Is.GreaterThan(0));
        }

        [Test]
        [Description("Validates TapeImage (2.11BSD) structure - may have no disk label")]
        public void TapeImage_Structure_Analysis()
        {
            // Arrange
            if (!File.Exists(TapeImage))
            {
                Assert.Ignore($"Test image not found: {TapeImage}");
                return;
            }

            TestContext.Out.WriteLine($"Testing BSD tape image: {TapeImage}");
            TestContext.Out.WriteLine($"File size: {new FileInfo(TapeImage).Length:N0} bytes");
            TestContext.Out.WriteLine("");

            var descriptor = new BlockDeviceDescriptor
            {
                Name = "BSD Tape Image",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(new FileInfo(TapeImage).Length / 512)
            };

            var device = new GenericBlockDevice(descriptor);
            using var stream = new FileStream(TapeImage, FileMode.Open, FileAccess.Read, FileShare.Read);
            device.Mount(stream, readOnly: true);

            // Act: Try to read disk label
            var partMgr = new BsdPartitionManager(device);
            var diskLabel = partMgr.GetDiskLabel();

            if (diskLabel != null && diskLabel.IsValid())
            {
                TestContext.Out.WriteLine("=== DISK LABEL FOUND ===");
                TestContext.Out.WriteLine($"Partitions: {diskLabel.NumPartitions}");

                var partitions = partMgr.ListPartitions();
                for (int i = 0; i < partitions.Count; i++)
                {
                    var (letter, part) = partitions[i];
                    TestContext.Out.WriteLine($"[{letter}] {part.FileSystemTypeName}: {part.Size} sectors");
                }
            }
            else
            {
                TestContext.Out.WriteLine("=== NO DISK LABEL (RAW FILESYSTEM) ===");
                TestContext.Out.WriteLine("This appears to be a raw filesystem image without partition table.");
                TestContext.Out.WriteLine("Testing raw filesystem mount...");

                // Try mounting as raw filesystem
                var fs = new V7FileSystem
                {
                    UseDiskLabel = false  // Raw mode
                };

                Assert.DoesNotThrow(() => fs.Initialize(device),
                    "Should be able to mount as raw filesystem");

                var rootEntries = fs.ListDirectory("/");
                TestContext.Out.WriteLine($"Root directory entries: {rootEntries.Length}");

                Assert.That(rootEntries.Length, Is.GreaterThan(0),
                    "Raw filesystem should have root directory entries");
            }
        }

        [Test]
        [Description("Comprehensive test: detects partitions and validates each filesystem")]
        public void CleanInstall_ComprehensiveValidation()
        {
            // Arrange
            if (!File.Exists(CleanInstallImage))
            {
                Assert.Ignore($"Test image not found: {CleanInstallImage}");
                return;
            }

            TestContext.Out.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            TestContext.Out.WriteLine("║  BSD 2.11 Clean Install Image - Comprehensive Validation    ║");
            TestContext.Out.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            TestContext.Out.WriteLine("");

            var descriptor = new BlockDeviceDescriptor
            {
                Name = "BSD Clean Install",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(new FileInfo(CleanInstallImage).Length / 512)
            };

            var device = new GenericBlockDevice(descriptor);
            using var stream = new FileStream(CleanInstallImage, FileMode.Open, FileAccess.Read, FileShare.Read);
            device.Mount(stream, readOnly: true);

            // Step 1: Validate disk label
            TestContext.Out.WriteLine("STEP 1: Disk Label Validation");
            TestContext.Out.WriteLine("─────────────────────────────");

            var partMgr = new BsdPartitionManager(device);
            var diskLabel = partMgr.GetDiskLabel();

            Assert.That(diskLabel, Is.Not.Null, "✗ FAIL: No disk label found");
            TestContext.Out.WriteLine("✓ Disk label found");

            Assert.That(diskLabel!.IsValid(), Is.True, "✗ FAIL: Disk label validation failed");
            TestContext.Out.WriteLine("✓ Disk label is valid");

            Assert.That(diskLabel.ValidateChecksum(), Is.True, "✗ FAIL: Checksum mismatch");
            TestContext.Out.WriteLine("✓ Checksum is correct");

            TestContext.Out.WriteLine($"✓ Type: {diskLabel.TypeName}");
            TestContext.Out.WriteLine($"✓ Pack: {diskLabel.PackName}");
            TestContext.Out.WriteLine("");

            // Step 2: Validate partition count
            TestContext.Out.WriteLine("STEP 2: Partition Count Validation");
            TestContext.Out.WriteLine("───────────────────────────────────");

            var partitions = partMgr.ListPartitions();

            Assert.That(partitions.Count, Is.EqualTo(3),
                $"✗ FAIL: Expected 3 partitions, found {partitions.Count}");
            TestContext.Out.WriteLine($"✓ Found 3 partitions");

            // List partition names
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("Partition Names:");
            for (int i = 0; i < partitions.Count; i++)
            {
                var (letter, part) = partitions[i];
                TestContext.Out.WriteLine($"  • Partition '{letter}': {part.FileSystemTypeName}");
            }
            TestContext.Out.WriteLine("");

            // Step 3: Validate partition types
            TestContext.Out.WriteLine("STEP 3: Partition Type Validation");
            TestContext.Out.WriteLine("──────────────────────────────────");

            int v7Count = 0, swapCount = 0;

            for (int i = 0; i < partitions.Count; i++)
            {
                var (letter, part) = partitions[i];

                // Validate partition structure
                Assert.That(part.Validate(diskLabel.SectorsPerUnit), Is.True,
                    $"✗ FAIL: Partition [{letter}] validation failed");

                if (part.IsV7Filesystem) v7Count++;
                if (part.IsSwap) swapCount++;

                TestContext.Out.WriteLine($"✓ Partition [{letter}]: {part.FileSystemTypeName}");
            }

            Assert.That(v7Count, Is.EqualTo(2),
                $"✗ FAIL: Expected 2 V7 filesystems, found {v7Count}");
            TestContext.Out.WriteLine($"✓ Found 2 V7 filesystems");

            Assert.That(swapCount, Is.EqualTo(1),
                $"✗ FAIL: Expected 1 swap partition, found {swapCount}");
            TestContext.Out.WriteLine($"✓ Found 1 swap partition");
            TestContext.Out.WriteLine("");

            // Step 4: Mount and validate each BSD filesystem
            TestContext.Out.WriteLine("STEP 4: Filesystem Mount Validation");
            TestContext.Out.WriteLine("────────────────────────────────────");

            foreach (var (letter, part) in partitions)
            {
                if (!part.IsV7Filesystem)
                    continue;

                // Fresh device for each mount
                var testDevice = new GenericBlockDevice(descriptor);
                using var testStream = new FileStream(CleanInstallImage, FileMode.Open, FileAccess.Read, FileShare.Read);
                testDevice.Mount(testStream, readOnly: true);

                var fs = new V7FileSystem
                {
                    PartitionLetter = letter,
                    UseDiskLabel = true
                };

                fs.Initialize(testDevice);

                var rootEntries = fs.ListDirectory("/");

                Assert.That(rootEntries.Length, Is.GreaterThan(0),
                    $"✗ FAIL: Partition [{letter}] has empty root directory");

                TestContext.Out.WriteLine($"✓ Partition [{letter}]: {rootEntries.Length} root entries, {fs.GetBlocksFree():N0} free blocks");
            }

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            TestContext.Out.WriteLine("║  ✓ ALL VALIDATIONS PASSED                                    ║");
            TestContext.Out.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        }

        [Test]
        [Description("Verifies BsdPartitionManager correctly parses disk label from real image")]
        public void CleanInstall_PartitionManager_CorrectParsing()
        {
            // Arrange
            if (!File.Exists(CleanInstallImage))
            {
                Assert.Ignore($"Test image not found: {CleanInstallImage}");
                return;
            }

            var descriptor = new BlockDeviceDescriptor
            {
                Name = "BSD Clean Install",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(new FileInfo(CleanInstallImage).Length / 512)
            };

            var device = new GenericBlockDevice(descriptor);
            using var stream = new FileStream(CleanInstallImage, FileMode.Open, FileAccess.Read, FileShare.Read);
            device.Mount(stream, readOnly: true);

            // Act
            var partMgr = new BsdPartitionManager(device);
            var diskLabel = partMgr.GetDiskLabel();

            // Assert: Disk label structure
            Assert.That(diskLabel, Is.Not.Null);
            Assert.That(diskLabel!.Magic, Is.EqualTo(V7Constants.DISK_MAGIC));
            Assert.That(diskLabel.Magic2, Is.EqualTo(V7Constants.DISK_MAGIC));
            Assert.That(diskLabel.NumPartitions, Is.GreaterThanOrEqualTo(1));
            Assert.That(diskLabel.NumPartitions, Is.LessThanOrEqualTo(V7Constants.MAX_PARTITIONS));

            // Test partition accessor
            var partA = partMgr.GetPartition('a');
            Assert.That(partA, Is.Not.Null, "Partition 'a' should exist");

            var partB = partMgr.GetPartition('b');
            // Partition 'b' may or may not exist depending on layout

            // Test geometry info
            string geometryInfo = partMgr.GetGeometryInfo();
            TestContext.Out.WriteLine($"Geometry: {geometryInfo}");
            Assert.That(geometryInfo, Is.Not.Empty);

            // Validate all partitions
            bool allValid = partMgr.ValidatePartitions();
            Assert.That(allValid, Is.True, "All partitions should pass validation");
            TestContext.Out.WriteLine("✓ All partitions validated successfully");
        }

        [Test]
        [Description("Tests reading actual files from BSD filesystem")]
        public void CleanInstall_ReadFiles_Success()
        {
            // Arrange
            if (!File.Exists(CleanInstallImage))
            {
                Assert.Ignore($"Test image not found: {CleanInstallImage}");
                return;
            }

            var driver = new V7Driver();
            driver.SetPartition('a');

            using var stream = new FileStream(CleanInstallImage, FileMode.Open, FileAccess.Read, FileShare.Read);
            driver.Mount(stream, readOnly: true);

            var rootEntries = driver.ListDirectory("/");

            // Find a regular file
            DirectoryEntry? testFile = null;
            for (int i = 0; i < rootEntries.Length; i++)
            {
                if (!rootEntries[i].Metadata.IsDirectory &&
                    rootEntries[i].Metadata.Size > 0 &&
                    rootEntries[i].Metadata.Size < 100000) // Not too large
                {
                    testFile = rootEntries[i];
                    break;
                }
            }

            if (testFile == null)
            {
                TestContext.Out.WriteLine("No suitable test files found in root, trying /etc...");

                try
                {
                    var etcEntries = driver.ListDirectory("/etc");
                    for (int i = 0; i < etcEntries.Length; i++)
                    {
                        if (!etcEntries[i].Metadata.IsDirectory &&
                            etcEntries[i].Metadata.Size > 0 &&
                            etcEntries[i].Metadata.Size < 10000)
                        {
                            testFile = etcEntries[i];
                            break;
                        }
                    }
                }
                catch
                {
                    // /etc doesn't exist or can't be read
                }
            }

            if (testFile == null)
            {
                Assert.Ignore("No suitable test files found");
                return;
            }

            // Act: Read the file
            string filePath = testFile.Metadata.IsDirectory ? "/" + testFile.Name : "/" + testFile.Name;
            if (filePath.Contains("/etc/"))
                filePath = "/etc/" + testFile.Name;

            byte[] fileData = driver.ReadFile(filePath);

            // Assert
            Assert.That(fileData, Is.Not.Null);
            Assert.That(fileData.Length, Is.EqualTo(testFile.Metadata.Size));

            TestContext.Out.WriteLine($"✓ Successfully read file: {filePath}");
            TestContext.Out.WriteLine($"  Size: {fileData.Length} bytes");
            TestContext.Out.WriteLine($"  Modified: {testFile.Metadata.ModifiedTime}");

            // Show first few bytes as hex
            int displayBytes = Math.Min(fileData.Length, 64);
            string hex = BitConverter.ToString(fileData, 0, displayBytes).Replace("-", " ");
            TestContext.Out.WriteLine($"  First {displayBytes} bytes: {hex}");
        }

        [Test]
        [Description("Tests partition detection from file path (like RetroCommander UI does)")]
        public void CleanInstall_DetectPartitionsFromFile_FindsAllPartitions()
        {
            // Arrange
            if (!File.Exists(CleanInstallImage))
            {
                Assert.Ignore($"Test image not found: {CleanInstallImage}");
                return;
            }

            TestContext.Out.WriteLine("=== TESTING PARTITION DETECTION (RetroCommander-style) ===");
            TestContext.Out.WriteLine($"File: {CleanInstallImage}");
            TestContext.Out.WriteLine("");

            // Act: Detect partitions using file path (like UI does)
            using var fs = new FileStream(CleanInstallImage, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Read BSD disk label at sector 1
            if (fs.Length < 1024)
            {
                Assert.Fail("File too small for BSD disk label");
                return;
            }

            fs.Seek(512, SeekOrigin.Begin);
            byte[] labelBytes = new byte[512];
            int bytesRead = fs.Read(labelBytes, 0, 512);

            Assert.That(bytesRead, Is.EqualTo(512), "Should read full disk label sector");

            // Check for BSD disk label magic number (0x82564557 in PDP-endian)
            uint magic1 = BsdEndianHelper.ReadUInt32PDP(labelBytes, 0);
            uint magic2 = BsdEndianHelper.ReadUInt32PDP(labelBytes, 108);

            TestContext.Out.WriteLine($"Magic1: 0x{magic1:X8} (expected: 0x82564557)");
            TestContext.Out.WriteLine($"Magic2: 0x{magic2:X8} (expected: 0x82564557)");
            TestContext.Out.WriteLine("");

            Assert.That(magic1, Is.EqualTo(0x82564557u), "Magic1 should be valid BSD disk label");
            Assert.That(magic2, Is.EqualTo(0x82564557u), "Magic2 should be valid BSD disk label");

            // Read number of partitions (at offset 114, 2 bytes)
            ushort numPartitions = BitConverter.ToUInt16(labelBytes, 114);
            TestContext.Out.WriteLine($"Number of partitions: {numPartitions}");

            Assert.That(numPartitions, Is.EqualTo(3), "Should detect 3 partitions");

            // Parse partitions (table starts at offset 120)
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("=== DETECTED PARTITIONS ===");

            int v7Count = 0;
            int swapCount = 0;

            for (int i = 0; i < numPartitions && i < 8; i++)
            {
                int offset = 120 + (i * 12);

                // Use PDP-endian for 32-bit size and offset fields
                uint p_size = BsdEndianHelper.ReadUInt32PDP(labelBytes, offset + 0);
                uint p_offset = BsdEndianHelper.ReadUInt32PDP(labelBytes, offset + 4);
                ushort p_fsize = BitConverter.ToUInt16(labelBytes, offset + 8);
                byte p_fstype = labelBytes[offset + 10];
                byte p_frag = labelBytes[offset + 11];

                if (p_size > 0)
                {
                    char partitionLetter = (char)('a' + i);
                    string fsType = GetBsdFilesystemType(p_fstype);
                    double sizeMB = (p_size * 512.0) / (1024.0 * 1024.0);

                    TestContext.Out.WriteLine($"[{partitionLetter}] {fsType}: {p_size} sectors ({sizeMB:F2} MB) at offset {p_offset}");

                    // Count filesystem types
                    if (p_fstype == 5) v7Count++;      // FS_V71K
                    if (p_fstype == 1) swapCount++;    // FS_SWAP

                    // Validate expected values
                    if (partitionLetter == 'a')
                    {
                        Assert.That(p_size, Is.EqualTo(15884u), "Partition 'a' should be 15,884 sectors");
                        Assert.That(p_offset, Is.EqualTo(0u), "Partition 'a' should start at sector 0");
                        Assert.That(p_fstype, Is.EqualTo(5), "Partition 'a' should be V7-1K filesystem");
                    }
                    else if (partitionLetter == 'b')
                    {
                        Assert.That(p_size, Is.EqualTo(16720u), "Partition 'b' should be 16,720 sectors");
                        Assert.That(p_offset, Is.EqualTo(15884u), "Partition 'b' should start at sector 15,884");
                        Assert.That(p_fstype, Is.EqualTo(1), "Partition 'b' should be swap");
                    }
                    else if (partitionLetter == 'c')
                    {
                        Assert.That(p_size, Is.EqualTo(308066u), "Partition 'c' should be 308,066 sectors");
                        Assert.That(p_offset, Is.EqualTo(32604u), "Partition 'c' should start at sector 32,604");
                        Assert.That(p_fstype, Is.EqualTo(5), "Partition 'c' should be V7-1K filesystem");
                    }
                }
            }

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine($"✓ Found {v7Count} V7 filesystems");
            TestContext.Out.WriteLine($"✓ Found {swapCount} swap partition");

            Assert.That(v7Count, Is.EqualTo(2), "Should find 2 V7 filesystems");
            Assert.That(swapCount, Is.EqualTo(1), "Should find 1 swap partition");
        }

        private string GetBsdFilesystemType(byte fstype)
        {
            return fstype switch
            {
                0 => "Unused",
                1 => "Swap",
                2 => "V6",
                3 => "V7/1K",
                4 => "SystemV",
                5 => "V7/1K",
                6 => "V7/2K",
                7 => "4.2BSD",
                8 => "MSDOS",
                9 => "4.4LFS",
                10 => "Unknown",
                11 => "HPFS",
                12 => "ISO9660",
                13 => "Boot",
                _ => $"Type {fstype}"
            };
        }

        [Test]
        [Description("Validates partition detection matches expected layout")]
        public void CleanInstall_PartitionLayout_MatchesExpected()
        {
            // Arrange
            if (!File.Exists(CleanInstallImage))
            {
                Assert.Ignore($"Test image not found: {CleanInstallImage}");
                return;
            }

            var descriptor = new BlockDeviceDescriptor
            {
                Name = "BSD Clean Install",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(new FileInfo(CleanInstallImage).Length / 512)
            };

            var device = new GenericBlockDevice(descriptor);
            using var stream = new FileStream(CleanInstallImage, FileMode.Open, FileAccess.Read, FileShare.Read);
            device.Mount(stream, readOnly: true);

            var partMgr = new BsdPartitionManager(device);
            var partitions = partMgr.ListPartitions();

            // Expected layout based on user's description:
            // - 3 partitions total
            // - 2 BSD filesystems
            // - 1 swap

            TestContext.Out.WriteLine("=== DETECTED PARTITION LAYOUT ===");
            TestContext.Out.WriteLine($"Total partitions: {partitions.Count}");
            TestContext.Out.WriteLine("");

            int[] partitionTypes = new int[256]; // Count by filesystem type

            for (int i = 0; i < partitions.Count; i++)
            {
                var (letter, part) = partitions[i];
                partitionTypes[part.FsType]++;

                double sizeMB = (part.Size * 512.0) / (1024.0 * 1024.0);
                double offsetMB = (part.Offset * 512.0) / (1024.0 * 1024.0);

                TestContext.Out.WriteLine($"Partition [{letter}]:");
                TestContext.Out.WriteLine($"  Type Code: {part.FsType} ({part.FileSystemTypeName})");
                TestContext.Out.WriteLine($"  Offset: {part.Offset:N0} sectors ({offsetMB:F2} MB)");
                TestContext.Out.WriteLine($"  Size: {part.Size:N0} sectors ({sizeMB:F2} MB)");
                TestContext.Out.WriteLine($"  Fragment Size: {part.FragmentSize} bytes");
                TestContext.Out.WriteLine($"  End Sector: {part.EndSector:N0}");
                TestContext.Out.WriteLine("");
            }

            TestContext.Out.WriteLine("=== TYPE SUMMARY ===");
            TestContext.Out.WriteLine($"V7-1K (2.11BSD) filesystems: {partitionTypes[V7Constants.FS_V71K]}");
            TestContext.Out.WriteLine($"Swap partitions: {partitionTypes[V7Constants.FS_SWAP]}");
            TestContext.Out.WriteLine($"Other: {partitions.Count - partitionTypes[V7Constants.FS_V71K] - partitionTypes[V7Constants.FS_SWAP]}");
            TestContext.Out.WriteLine("");

            // Assert: Matches expected layout
            Assert.That(partitions.Count, Is.EqualTo(3),
                "Expected 3 total partitions");
            Assert.That(partitionTypes[V7Constants.FS_V71K], Is.EqualTo(2),
                "Expected 2 V7-1K filesystems");
            Assert.That(partitionTypes[V7Constants.FS_SWAP], Is.EqualTo(1),
                "Expected 1 swap partition");

            TestContext.Out.WriteLine("✓ Partition layout matches expected configuration");
        }

        [Test]
        [Description("Tests that partitions don't overlap and fit within disk bounds")]
        public void CleanInstall_PartitionGeometry_NoOverlaps()
        {
            // Arrange
            if (!File.Exists(CleanInstallImage))
            {
                Assert.Ignore($"Test image not found: {CleanInstallImage}");
                return;
            }

            var descriptor = new BlockDeviceDescriptor
            {
                Name = "BSD Clean Install",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(new FileInfo(CleanInstallImage).Length / 512)
            };

            var device = new GenericBlockDevice(descriptor);
            using var stream = new FileStream(CleanInstallImage, FileMode.Open, FileAccess.Read, FileShare.Read);
            device.Mount(stream, readOnly: true);

            var partMgr = new BsdPartitionManager(device);
            var diskLabel = partMgr.GetDiskLabel();
            var partitions = partMgr.ListPartitions();

            TestContext.Out.WriteLine("=== PARTITION GEOMETRY VALIDATION ===");

            // Check each partition fits within disk
            for (int i = 0; i < partitions.Count; i++)
            {
                var (letter, part) = partitions[i];

                Assert.That(part.Offset, Is.LessThan(diskLabel!.SectorsPerUnit),
                    $"Partition [{letter}] starts beyond disk end");
                Assert.That(part.EndSector, Is.LessThanOrEqualTo(diskLabel.SectorsPerUnit),
                    $"Partition [{letter}] extends beyond disk end");

                TestContext.Out.WriteLine($"✓ Partition [{letter}]: sectors {part.Offset:N0} - {part.EndSector:N0}");
            }

            // Check for overlaps
            for (int i = 0; i < partitions.Count; i++)
            {
                for (int j = i + 1; j < partitions.Count; j++)
                {
                    var (letter1, part1) = partitions[i];
                    var (letter2, part2) = partitions[j];

                    bool overlaps = !(part1.EndSector <= part2.Offset || part2.EndSector <= part1.Offset);

                    Assert.That(overlaps, Is.False,
                        $"Partitions [{letter1}] and [{letter2}] overlap!");

                    TestContext.Out.WriteLine($"✓ Partitions [{letter1}] and [{letter2}] don't overlap");
                }
            }

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("✓ All partitions have valid geometry with no overlaps");
        }
    }
}

