//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Local.FFS.FileSystems;
using RetroIO.Storage.Local.FFS.Structures;

namespace RetroIO.Tests
{
    [TestFixture]
    public class FfsIntegrationTests
    {
        private const string TestImagePath = @"D:\ND\HDD\sun2-sunos-3.2.img";
        private DriverFactory? _factory;

        [SetUp]
        public void Setup()
        {
            _factory = new DriverFactory();
            _factory.Register(new FfsDriverProvider());
            _factory.Register(new V7DriverProvider());
        }

        [Test]
        [Category("FFS")]
        [Category("Integration")]
        public void FfsDriver_DetectSunOSImage_ReturnsHighConfidence()
        {
            // Arrange
            if (!File.Exists(TestImagePath))
            {
                Assert.Ignore($"Test skipped: SunOS image not found at {TestImagePath}");
                return;
            }

            // Act
            // Use the REGISTERED provider type name ("V7"), not the driver's display string
            // ("BSD-V7"). GetDetectionConfidences returns 0.0f for a type it does not know, so
            // asking for "BSD-V7" made the comparison below vacuous - it was measuring FFS
            // against a hardcoded zero rather than against the real V7 provider.
            var confidences = _factory!.GetDetectionConfidences(TestImagePath, "FFS", "V7");

            // Assert
            TestContext.Out.WriteLine($"FFS confidence: {confidences["FFS"]:P1}");
            TestContext.Out.WriteLine($"V7 confidence: {confidences["V7"]:P1}");

            Assert.That(confidences["FFS"], Is.GreaterThan(0.5f),
                "FFS confidence should be > 50% for SunOS disk image");
            Assert.That(confidences["FFS"], Is.GreaterThan(confidences["V7"]),
                "FFS confidence should be higher than V7 for a SunOS image");
        }

        [Test]
        [Category("FFS")]
        [Category("Integration")]
        public void FfsDriver_MountSunOSImage_Success()
        {
            // Arrange
            if (!File.Exists(TestImagePath))
            {
                Assert.Ignore($"Test skipped: SunOS image not found at {TestImagePath}");
                return;
            }

            var driver = _factory!.Detect(TestImagePath);
            Assert.That(driver, Is.Not.Null, "FFS driver should be detected");

            // Act
            using var imageStream = File.OpenRead(TestImagePath);
            driver!.Mount(imageStream, readOnly: true);

            // Assert
            Assert.That(driver.FileSystemType, Does.StartWith("FFS"));
            Assert.That(driver.CanRead, Is.True);
            Assert.That(driver.HasBlockAccess, Is.True);

            TestContext.Out.WriteLine($"Filesystem Type: {driver.FileSystemType}");
            TestContext.Out.WriteLine($"Volume Label: {driver.VolumeLabel}");
            TestContext.Out.WriteLine($"Capabilities: {driver.Capabilities}");
        }

        [Test]
        [Category("FFS")]
        [Category("Integration")]
        public void FfsSuperblock_ReadFromSunOSImage_ValidStructure()
        {
            // Arrange
            if (!File.Exists(TestImagePath))
            {
                Assert.Ignore($"Test skipped: SunOS image not found at {TestImagePath}");
                return;
            }

            // Act
            using var stream = File.OpenRead(TestImagePath);
            stream.Seek(FfsConstants.SBLOCK_OFFSET, SeekOrigin.Begin);

            byte[] buffer = new byte[FfsConstants.SBSIZE];
            stream.ReadExactly(buffer);

            var superblock = FfsSuperblock.Parse(buffer);

            // Assert
            Assert.That(superblock, Is.Not.Null);
            Assert.That(superblock.fs_magic, Is.EqualTo(FfsConstants.FS_MAGIC),
                "Magic number should match FFS magic");
            Assert.That(superblock.Validate(), Is.True,
                "Superblock should pass validation");

            // Check reasonable values
            Assert.That(superblock.fs_bsize, Is.InRange(FfsConstants.MIN_BSIZE, FfsConstants.MAX_BSIZE),
                "Block size should be within valid range");
            Assert.That(superblock.fs_fsize, Is.InRange(FfsConstants.MIN_FSIZE, FfsConstants.MAX_FSIZE),
                "Fragment size should be within valid range");
            Assert.That(superblock.fs_ncg, Is.GreaterThan(0),
                "Should have at least one cylinder group");
            Assert.That(superblock.fs_ipg, Is.GreaterThan(0),
                "Should have inodes per group");

            TestContext.Out.WriteLine($"Block Size: {superblock.fs_bsize} bytes");
            TestContext.Out.WriteLine($"Fragment Size: {superblock.fs_fsize} bytes");
            TestContext.Out.WriteLine($"Fragments per Block: {superblock.fs_frag}");
            TestContext.Out.WriteLine($"Cylinder Groups: {superblock.fs_ncg}");
            TestContext.Out.WriteLine($"Inodes per Group: {superblock.fs_ipg}");
            TestContext.Out.WriteLine($"Mount Name: {superblock.fs_fsmnt}");
            TestContext.Out.WriteLine($"Free Blocks: {superblock.fs_cstotal_cs_nbfree}");
            TestContext.Out.WriteLine($"Free Inodes: {superblock.fs_cstotal_cs_nifree}");
        }

        [Test]
        [Category("FFS")]
        [Category("Integration")]
        public void FfsFileSystem_ListRootDirectory_ReturnsEntries()
        {
            // Arrange
            if (!File.Exists(TestImagePath))
            {
                Assert.Ignore($"Test skipped: SunOS image not found at {TestImagePath}");
                return;
            }

            var driver = _factory!.Detect(TestImagePath);
            Assert.That(driver, Is.Not.Null);

            using var imageStream = File.OpenRead(TestImagePath);
            driver!.Mount(imageStream, readOnly: true);

            // Act
            var entries = driver.ListDirectory("/");

            // Assert
            Assert.That(entries, Is.Not.Null);
            Assert.That(entries.Length, Is.GreaterThan(0),
                "Root directory should contain entries");

            TestContext.Out.WriteLine($"Root directory contains {entries.Length} entries:");
            for (int i = 0; i < Math.Min(entries.Length, 20); i++)
            {
                var entry = entries[i];
                string type = entry.Metadata.IsDirectory ? "DIR " : "FILE";
                TestContext.Out.WriteLine($"  [{type}] {entry.Name,-30} {entry.Metadata.Size,10} bytes");
            }

            if (entries.Length > 20)
            {
                TestContext.Out.WriteLine($"  ... and {entries.Length - 20} more entries");
            }

            // Check for common Unix directories
            bool hasEtc = false;
            bool hasBin = false;
            bool hasUsr = false;

            for (int i = 0; i < entries.Length; i++)
            {
                string name = entries[i].Name;
                if (name == "etc") hasEtc = true;
                if (name == "bin") hasBin = true;
                if (name == "usr") hasUsr = true;
            }

            TestContext.Out.WriteLine($"\nCommon directories found:");
            TestContext.Out.WriteLine($"  /etc: {(hasEtc ? "YES" : "NO")}");
            TestContext.Out.WriteLine($"  /bin: {(hasBin ? "YES" : "NO")}");
            TestContext.Out.WriteLine($"  /usr: {(hasUsr ? "YES" : "NO")}");
        }

        [Test]
        [Category("FFS")]
        [Category("Integration")]
        public void FfsFileSystem_ReadRootInode_Success()
        {
            // Arrange
            if (!File.Exists(TestImagePath))
            {
                Assert.Ignore($"Test skipped: SunOS image not found at {TestImagePath}");
                return;
            }

            using var stream = File.OpenRead(TestImagePath);
            var descriptor = new BlockDeviceDescriptor
            {
                Name = "FFS Test",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(stream.Length / 512)
            };

            var blockDevice = new GenericBlockDevice(descriptor);
            blockDevice.Mount(stream, readOnly: true);

            var filesystem = new FfsFileSystem();
            filesystem.Initialize(blockDevice);

            // Act
            var metadata = filesystem.GetMetadata("/");

            // Assert
            Assert.That(metadata, Is.Not.Null);
            Assert.That(metadata!.IsDirectory, Is.True,
                "Root inode should be a directory");
            Assert.That(metadata.Size, Is.GreaterThan(0),
                "Root directory should have non-zero size");

            TestContext.Out.WriteLine($"Root Inode:");
            TestContext.Out.WriteLine($"  Type: {(metadata.IsDirectory ? "Directory" : "File")}");
            TestContext.Out.WriteLine($"  Size: {metadata.Size} bytes");
            TestContext.Out.WriteLine($"  Modified: {metadata.ModifiedTime}");
            TestContext.Out.WriteLine($"  Accessed: {metadata.AccessedTime}");
        }

        [Test]
        [Category("FFS")]
        [Category("Integration")]
        public void FfsFileSystem_ReadSmallFile_Success()
        {
            // Arrange
            if (!File.Exists(TestImagePath))
            {
                Assert.Ignore($"Test skipped: SunOS image not found at {TestImagePath}");
                return;
            }

            var driver = _factory!.Detect(TestImagePath);
            Assert.That(driver, Is.Not.Null);

            using var imageStream = File.OpenRead(TestImagePath);
            driver!.Mount(imageStream, readOnly: true);

            // Find a small file in root directory
            var entries = driver.ListDirectory("/");
            DirectoryEntry? smallFile = null;

            for (int i = 0; i < entries.Length; i++)
            {
                if (!entries[i].Metadata.IsDirectory &&
                    entries[i].Metadata.Size > 0 &&
                    entries[i].Metadata.Size < 10000)
                {
                    smallFile = entries[i];
                    break;
                }
            }

            if (smallFile == null)
            {
                Assert.Ignore("No small files found in root directory to test");
                return;
            }

            TestContext.Out.WriteLine($"Testing with file: {smallFile.Name} ({smallFile.Metadata.Size} bytes)");

            // Act
            var data = driver.ReadFile(smallFile.Name);

            // Assert
            Assert.That(data, Is.Not.Null);
            Assert.That(data.Length, Is.EqualTo((int)smallFile.Metadata.Size),
                "Read data length should match file size");

            TestContext.Out.WriteLine($"Successfully read {data.Length} bytes from {smallFile.Name}");

            // Show first 64 bytes as hex dump
            int displayBytes = Math.Min(64, data.Length);
            TestContext.Out.WriteLine($"\nFirst {displayBytes} bytes (hex):");
            for (int i = 0; i < displayBytes; i += 16)
            {
                string hex = "";
                string ascii = "";
                for (int j = 0; j < 16 && i + j < displayBytes; j++)
                {
                    byte b = data[i + j];
                    hex += $"{b:X2} ";
                    ascii += (b >= 32 && b < 127) ? (char)b : '.';
                }
                TestContext.Out.WriteLine($"  {i:X4}: {hex,-48} {ascii}");
            }
        }

        [Test]
        [Category("FFS")]
        [Category("Integration")]
        public void FfsFileSystem_NavigateSubdirectories_Success()
        {
            // Arrange
            if (!File.Exists(TestImagePath))
            {
                Assert.Ignore($"Test skipped: SunOS image not found at {TestImagePath}");
                return;
            }

            var driver = _factory!.Detect(TestImagePath);
            Assert.That(driver, Is.Not.Null);

            using var imageStream = File.OpenRead(TestImagePath);
            driver!.Mount(imageStream, readOnly: true);

            // Find first subdirectory
            var rootEntries = driver.ListDirectory("/");
            DirectoryEntry? subdir = null;

            for (int i = 0; i < rootEntries.Length; i++)
            {
                if (rootEntries[i].Metadata.IsDirectory)
                {
                    subdir = rootEntries[i];
                    break;
                }
            }

            if (subdir == null)
            {
                Assert.Ignore("No subdirectories found in root");
                return;
            }

            TestContext.Out.WriteLine($"Testing subdirectory: /{subdir.Name}");

            // Act
            var subdirEntries = driver.ListDirectory(subdir.Name);

            // Assert
            Assert.That(subdirEntries, Is.Not.Null);
            TestContext.Out.WriteLine($"Subdirectory /{subdir.Name} contains {subdirEntries.Length} entries:");

            for (int i = 0; i < Math.Min(subdirEntries.Length, 10); i++)
            {
                var entry = subdirEntries[i];
                string type = entry.Metadata.IsDirectory ? "DIR " : "FILE";
                TestContext.Out.WriteLine($"  [{type}] {entry.Name,-30} {entry.Metadata.Size,10} bytes");
            }

            if (subdirEntries.Length > 10)
            {
                TestContext.Out.WriteLine($"  ... and {subdirEntries.Length - 10} more entries");
            }
        }

        [Test]
        [Category("FFS")]
        [Category("Integration")]
        public void FfsFileSystem_DirectoryEntries_HavePathProperty()
        {
            // Arrange
            if (!File.Exists(TestImagePath))
            {
                Assert.Ignore($"Test skipped: SunOS image not found at {TestImagePath}");
                return;
            }

            var driver = _factory!.Detect(TestImagePath);
            Assert.That(driver, Is.Not.Null);

            using var imageStream = File.OpenRead(TestImagePath);
            driver!.Mount(imageStream, readOnly: true);

            // Act: List root directory
            var rootEntries = driver.ListDirectory("/");

            // Assert: All entries must have Path property set
            Assert.That(rootEntries, Is.Not.Null);
            Assert.That(rootEntries.Length, Is.GreaterThan(0));

            for (int i = 0; i < rootEntries.Length; i++)
            {
                var entry = rootEntries[i];
                Assert.That(entry.Metadata.Path, Is.Not.Null,
                    $"Entry '{entry.Name}' must have Path property set");
                Assert.That(entry.Metadata.Path, Is.Not.Empty,
                    $"Entry '{entry.Name}' Path property must not be empty");
                Assert.That(entry.Metadata.Path, Is.EqualTo($"/{entry.Name}"),
                    $"Entry '{entry.Name}' Path should be '/{entry.Name}' but was '{entry.Metadata.Path}'");
            }

            TestContext.Out.WriteLine($"✓ All {rootEntries.Length} root entries have correct Path property");
        }

        [Test]
        [Category("FFS")]
        [Category("Integration")]
        public void FfsFileSystem_SubdirectoryEntries_HaveCorrectPath()
        {
            // Arrange
            if (!File.Exists(TestImagePath))
            {
                Assert.Ignore($"Test skipped: SunOS image not found at {TestImagePath}");
                return;
            }

            var driver = _factory!.Detect(TestImagePath);
            Assert.That(driver, Is.Not.Null);

            using var imageStream = File.OpenRead(TestImagePath);
            driver!.Mount(imageStream, readOnly: true);

            // Find first subdirectory
            var rootEntries = driver.ListDirectory("/");
            DirectoryEntry? subdir = null;

            for (int i = 0; i < rootEntries.Length; i++)
            {
                if (rootEntries[i].Metadata.IsDirectory)
                {
                    subdir = rootEntries[i];
                    break;
                }
            }

            if (subdir == null)
            {
                Assert.Ignore("No subdirectories found in root");
                return;
            }

            TestContext.Out.WriteLine($"Testing subdirectory: /{subdir.Name}");

            // Act: List subdirectory using the Path from metadata
            var subdirEntries = driver.ListDirectory(subdir.Metadata.Path);

            // Assert: All entries must have Path property with correct parent path
            Assert.That(subdirEntries, Is.Not.Null);

            for (int i = 0; i < subdirEntries.Length; i++)
            {
                var entry = subdirEntries[i];
                string expectedPath = $"/{subdir.Name}/{entry.Name}";

                Assert.That(entry.Metadata.Path, Is.Not.Null,
                    $"Entry '{entry.Name}' must have Path property set");
                Assert.That(entry.Metadata.Path, Is.Not.Empty,
                    $"Entry '{entry.Name}' Path property must not be empty");
                Assert.That(entry.Metadata.Path, Is.EqualTo(expectedPath),
                    $"Entry '{entry.Name}' Path should be '{expectedPath}' but was '{entry.Metadata.Path}'");
            }

            TestContext.Out.WriteLine($"✓ All {subdirEntries.Length} entries in /{subdir.Name} have correct Path property");
        }

        [Test]
        [Category("FFS")]
        [Category("Integration")]
        public void FfsDriver_GetBlockDeviceInfo_ReturnsValidInfo()
        {
            // Arrange
            if (!File.Exists(TestImagePath))
            {
                Assert.Ignore($"Test skipped: SunOS image not found at {TestImagePath}");
                return;
            }

            var driver = _factory!.Detect(TestImagePath);
            Assert.That(driver, Is.Not.Null);

            using var imageStream = File.OpenRead(TestImagePath);
            driver!.Mount(imageStream, readOnly: true);

            // Act
            var blockInfo = driver.GetBlockDeviceInfo();

            // Assert
            Assert.That(blockInfo, Is.Not.Null);
            Assert.That(blockInfo!.BlockSize, Is.EqualTo(512),
                "Block size should be 512 bytes");
            Assert.That(blockInfo.TotalBlocks, Is.GreaterThan(0),
                "Should have blocks");

            TestContext.Out.WriteLine($"Block Device Info:");
            TestContext.Out.WriteLine($"  Block Size: {blockInfo.BlockSize} bytes");
            TestContext.Out.WriteLine($"  Total Blocks: {blockInfo.TotalBlocks:N0}");
            TestContext.Out.WriteLine($"  Total Size: {(blockInfo.TotalBlocks * blockInfo.BlockSize):N0} bytes");
            TestContext.Out.WriteLine($"  Address Mode: {blockInfo.AddressMode}");
        }

        [Test]
        [Category("FFS")]
        [Category("Integration")]
        public void FfsDriver_ReadBlock_Success()
        {
            // Arrange
            if (!File.Exists(TestImagePath))
            {
                Assert.Ignore($"Test skipped: SunOS image not found at {TestImagePath}");
                return;
            }

            var driver = _factory!.Detect(TestImagePath);
            Assert.That(driver, Is.Not.Null);

            using var imageStream = File.OpenRead(TestImagePath);
            driver!.Mount(imageStream, readOnly: true);

            // Act - Read superblock at sector 16
            byte[] buffer = new byte[512];
            int bytesRead = driver.ReadBlock(BlockAddress.FromLBA(16), buffer);

            // Assert
            Assert.That(bytesRead, Is.EqualTo(512));

            // Verify it's the start of the superblock
            TestContext.Out.WriteLine($"Read block 16 (superblock start): {bytesRead} bytes");
            TestContext.Out.WriteLine($"First 32 bytes (hex):");
            for (int i = 0; i < 32; i += 16)
            {
                string hex = "";
                for (int j = 0; j < 16; j++)
                {
                    hex += $"{buffer[i + j]:X2} ";
                }
                TestContext.Out.WriteLine($"  {i:X4}: {hex}");
            }
        }

        [Test]
        [Category("FFS")]
        [Category("Integration")]
        public void FfsFileSystem_GetMetadata_ValidatesFileProperties()
        {
            // Arrange
            if (!File.Exists(TestImagePath))
            {
                Assert.Ignore($"Test skipped: SunOS image not found at {TestImagePath}");
                return;
            }

            var driver = _factory!.Detect(TestImagePath);
            Assert.That(driver, Is.Not.Null);

            using var imageStream = File.OpenRead(TestImagePath);
            driver!.Mount(imageStream, readOnly: true);

            var entries = driver.ListDirectory("/");
            DirectoryEntry? testFile = null;

            for (int i = 0; i < entries.Length; i++)
            {
                if (!entries[i].Metadata.IsDirectory)
                {
                    testFile = entries[i];
                    break;
                }
            }

            if (testFile == null)
            {
                Assert.Ignore("No files found in root directory");
                return;
            }

            // Act
            var metadata = driver.GetMetadata(testFile.Name);

            // Assert
            Assert.That(metadata, Is.Not.Null);
            Assert.That(metadata!.Name, Is.EqualTo(testFile.Name));
            Assert.That(metadata.IsDirectory, Is.False);
            Assert.That(metadata.Size, Is.GreaterThanOrEqualTo(0));

            TestContext.Out.WriteLine($"File Metadata for: {metadata.Name}");
            TestContext.Out.WriteLine($"  Type: File");
            TestContext.Out.WriteLine($"  Size: {metadata.Size:N0} bytes");
            TestContext.Out.WriteLine($"  Modified: {metadata.ModifiedTime}");
            TestContext.Out.WriteLine($"  Accessed: {metadata.AccessedTime}");
            TestContext.Out.WriteLine($"  Created: {metadata.CreatedTime}");
        }

        [Test]
        [Category("FFS")]
        [Category("Diagnostic")]
        public void FFS_DIAGNOSTIC_FindRootInode()
        {
            // This is a diagnostic test to find where the root inode actually is on disk
            if (!File.Exists(TestImagePath))
            {
                Assert.Ignore($"Test skipped: SunOS image not found at {TestImagePath}");
                return;
            }

            using var fs = File.OpenRead(TestImagePath);

            // Read superblock at offset 8192
            fs.Seek(8192, SeekOrigin.Begin);
            byte[] sb = new byte[2048];
            fs.ReadExactly(sb);

            // Read superblock fields
            uint fs_bsize = ReadBE32(sb, 48);
            uint fs_fsize = ReadBE32(sb, 52);
            uint fs_frag = ReadBE32(sb, 56);
            uint fs_ipg = ReadBE32(sb, 184);
            uint fs_fpg = ReadBE32(sb, 188);
            uint fs_iblkno = ReadBE32(sb, 16);
            uint fs_fsbtodb = ReadBE32(sb, 100);

            TestContext.Out.WriteLine("=== SUPERBLOCK VALUES ===");
            TestContext.Out.WriteLine($"fs_bsize: {fs_bsize} bytes");
            TestContext.Out.WriteLine($"fs_fsize: {fs_fsize} bytes");
            TestContext.Out.WriteLine($"fs_frag: {fs_frag}");
            TestContext.Out.WriteLine($"fs_ipg: {fs_ipg}");
            TestContext.Out.WriteLine($"fs_fpg: {fs_fpg}");
            TestContext.Out.WriteLine($"fs_iblkno: {fs_iblkno} (raw value from offset 16)");
            TestContext.Out.WriteLine($"fs_fsbtodb: {fs_fsbtodb}");

            uint inodesPerBlock = fs_bsize / 128;
            TestContext.Out.WriteLine($"\nInodes per block: {inodesPerBlock}");

            // Search for root inode by looking for directory inodes
            TestContext.Out.WriteLine("\n=== SEARCHING FOR ROOT INODE ===");
            TestContext.Out.WriteLine("Searching for ANY directory inodes in first 200 sectors...");

            int found = 0;
            // Search first 200 sectors
            for (uint sector = 0; sector < 200; sector++)
            {
                fs.Seek(sector * 512, SeekOrigin.Begin);
                byte[] sectorBuf = new byte[512];
                fs.ReadExactly(sectorBuf, 0, 512);

                // Check every 128-byte aligned position
                for (int offset = 0; offset <= 512 - 128; offset += 128)
                {
                    ushort mode = ReadBE16(sectorBuf, offset);
                    ushort nlink = ReadBE16(sectorBuf, offset + 2);

                    // Look for ANY directory (0x4000 = directory bit)
                    if ((mode & 0xF000) == 0x4000 && nlink > 0 && nlink < 1000)
                    {
                        ulong size = ReadBE64(sectorBuf, offset + 8);
                        found++;
                        TestContext.Out.WriteLine($"\n#{found} Directory inode at sector {sector}, offset {offset}:");
                        TestContext.Out.WriteLine($"  Absolute byte: {sector * 512 + offset}");
                        TestContext.Out.WriteLine($"  mode: 0x{mode:X4} (octal {Convert.ToString(mode & 0xFFF, 8)})");
                        TestContext.Out.WriteLine($"  nlink: {nlink}");
                        TestContext.Out.WriteLine($"  size: {size}");

                        // If this is inode #2 (second inode in inode area)
                        // It would be at offset 128 in its sector
                        if (offset == 128)
                        {
                            TestContext.Out.WriteLine($"  *** This is at offset 128 - likely inode #2 (root)! ***");
                            TestContext.Out.WriteLine($"  *** Inode area starts at sector {sector} ***");
                            TestContext.Out.WriteLine($"  *** fs_iblkno should be {sector} (if in sectors) ***");
                            TestContext.Out.WriteLine($"  *** fs_iblkno should be {sector / 2} (if in fragments with {fs_fsize}-byte frags) ***");
                        }

                        // Show first 32 bytes for debugging
                        TestContext.Out.Write("  First 32 bytes: ");
                        for (int i = 0; i < 32; i++)
                        {
                            TestContext.Out.Write($"{sectorBuf[offset + i]:X2} ");
                            if ((i + 1) % 16 == 0 && i < 31) TestContext.Out.Write("\n                  ");
                        }
                        TestContext.Out.WriteLine();

                        if (found >= 10)
                        {
                            TestContext.Out.WriteLine("\n(Stopping after 10 matches)");
                            goto done;
                        }
                    }
                }
            }
        done:
            if (found == 0)
            {
                TestContext.Out.WriteLine("\nNO directory inodes found in first 200 sectors!");
                TestContext.Out.WriteLine("This might mean:");
                TestContext.Out.WriteLine("  1. The inode area is beyond sector 200");
                TestContext.Out.WriteLine("  2. The disk image is corrupt or in wrong format");
                TestContext.Out.WriteLine("  3. We're looking for the wrong signature");
            }
            else
            {
                TestContext.Out.WriteLine($"\nFound {found} directory inode(s) total");
            }
        }

        private static ushort ReadBE16(byte[] buf, int offset)
        {
            return (ushort)((buf[offset] << 8) | buf[offset + 1]);
        }

        private static uint ReadBE32(byte[] buf, int offset)
        {
            return ((uint)buf[offset] << 24) |
                   ((uint)buf[offset + 1] << 16) |
                   ((uint)buf[offset + 2] << 8) |
                   buf[offset + 3];
        }

        private static ulong ReadBE64(byte[] buf, int offset)
        {
            return ((ulong)ReadBE32(buf, offset) << 32) | ReadBE32(buf, offset + 4);
        }

        [Test]
        [Category("FFS")]
        [Category("Integration")]
        [Category("PartitionG")]
        public void FfsFileSystem_PartitionG_NavigateBinDirectory()
        {
            // Arrange
            const string testImage = TestImagePath;
            if (!File.Exists(testImage))
            {
                Assert.Ignore($"Test skipped: SunOS image not found at {testImage}");
                return;
            }

            using var stream = File.OpenRead(testImage);
            var descriptor = new BlockDeviceDescriptor
            {
                Name = "SunOS 3.2 Partition G",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(stream.Length / 512)
            };

            var device = new GenericBlockDevice(descriptor);
            device.Mount(stream, readOnly: true);

            var fs = new FfsFileSystem
            {
                PartitionLetter = 'g',
                UseDiskLabel = true
            };

            TestContext.Out.WriteLine("Initializing FFS for partition 'g'...");
            fs.Initialize(device);

            // Act & Assert - List root directory
            TestContext.Out.WriteLine("\n1. Listing root directory (/):");
            var root = fs.ListDirectory("/");
            Assert.That(root, Is.Not.Null);
            Assert.That(root.Length, Is.GreaterThan(0));

            TestContext.Out.WriteLine($"Root directory: {root.Length} entries");
            for (int i = 0; i < Math.Min(10, root.Length); i++)
            {
                var e = root[i];
                string type = e.Metadata.IsDirectory ? "DIR " : "FILE";
                TestContext.Out.WriteLine($"  [{type}] {e.Name}");
            }

            // Check if /bin exists
            DirectoryEntry? binEntry = null;
            for (int i = 0; i < root.Length; i++)
            {
                if (root[i].Name == "bin" && root[i].Metadata.IsDirectory)
                {
                    binEntry = root[i];
                    break;
                }
            }

            if (binEntry == null)
            {
                Assert.Fail("/bin directory not found in root");
                return;
            }

            TestContext.Out.WriteLine($"\n2. Found /bin directory (path={binEntry.Metadata.Path}, size={binEntry.Metadata.Size})");

            // Act & Assert - List /bin directory
            TestContext.Out.WriteLine("\n3. Listing /bin directory:");
            var bin = fs.ListDirectory("/bin");
            Assert.That(bin, Is.Not.Null);
            Assert.That(bin.Length, Is.GreaterThan(0), "/bin should contain executables");

            TestContext.Out.WriteLine($"/bin directory: {bin.Length} entries");
            for (int i = 0; i < Math.Min(20, bin.Length); i++)
            {
                var e = bin[i];
                string type = e.Metadata.IsDirectory ? "DIR " : "FILE";
                TestContext.Out.WriteLine($"  [{i,2}] {type} size={e.Metadata.Size,8} name=\"{e.Name}\"");
            }

            if (bin.Length > 20)
            {
                TestContext.Out.WriteLine($"  ... and {bin.Length - 20} more entries");
            }

            TestContext.Out.WriteLine("\n✓ SUCCESS - No crash when navigating to /bin");
        }
    }
}
