//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.BSD.Partitions;
using RetroIO.Storage.Local.BSD.Structures;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Local.V7.Boot;
using RetroIO.Storage.Local.V7.FileSystems;
using RetroIO.Storage.Local.V7.Structures;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Unit tests for BSD data structures (disk label, partition, superblock, inode, directory entry).
    /// </summary>
    [TestFixture]
    public class BsdStructureTests
    {
        [Test]
        public void BsdDiskLabel_ParseValid_Success()
        {
            // Arrange: Create a valid disk label
            byte[] buffer = new byte[512];

            // Magic numbers (PDP-endian for 32-bit values)
            BsdEndianHelper.WriteUInt32PDP(buffer, 0, 0x82564557u);        // Magic1
            BsdEndianHelper.WriteUInt32PDP(buffer, 108, 0x82564557u);      // Magic2

            // Geometry
            BitConverter.TryWriteBytes(buffer.AsSpan(38), (ushort)512);   // BytesPerSector (16-bit)
            BitConverter.TryWriteBytes(buffer.AsSpan(40), (ushort)32);    // SectorsPerTrack (16-bit)
            BitConverter.TryWriteBytes(buffer.AsSpan(42), (ushort)8);     // TracksPerCylinder (16-bit)
            BitConverter.TryWriteBytes(buffer.AsSpan(44), (ushort)1024);  // CylindersPerUnit (16-bit)
            BsdEndianHelper.WriteUInt32PDP(buffer, 48, 262144u);          // SectorsPerUnit (32-bit, PDP-endian)

            // Partitions
            BitConverter.TryWriteBytes(buffer.AsSpan(114), (ushort)2);    // NumPartitions (16-bit)

            // Act
            var label = BsdDiskLabel.Parse(buffer);

            // Assert
            Assert.That(label.Magic, Is.EqualTo(0x82564557u));
            Assert.That(label.Magic2, Is.EqualTo(0x82564557u));
            Assert.That(label.IsValid(), Is.True);
            Assert.That(label.BytesPerSector, Is.EqualTo(512));
            Assert.That(label.SectorsPerTrack, Is.EqualTo(32));
            Assert.That(label.NumPartitions, Is.EqualTo(2));
            Assert.That(label.SectorsPerUnit, Is.EqualTo(262144u));
        }

        [Test]
        public void BsdPartition_ParseValid_Success()
        {
            // Arrange: Create valid partition entry
            byte[] buffer = new byte[12];
            BsdEndianHelper.WriteUInt32PDP(buffer, 0, 204800u);        // Size: 100MB in sectors (PDP-endian)
            BsdEndianHelper.WriteUInt32PDP(buffer, 4, 0u);             // Offset: sector 0 (PDP-endian)
            BitConverter.TryWriteBytes(buffer.AsSpan(8), (ushort)1024); // FragmentSize (16-bit)
            buffer[10] = V7Constants.FS_V71K;                         // FsType: V7-1K
            buffer[11] = 1;                                             // FragmentsPerBlock

            // Act
            var partition = BsdPartition.Parse(buffer, 0);

            // Assert
            Assert.That(partition.Size, Is.EqualTo(204800u));
            Assert.That(partition.Offset, Is.EqualTo(0u));
            Assert.That(partition.IsV7Filesystem, Is.True);
            Assert.That(partition.IsUnused, Is.False);
            Assert.That(partition.FileSystemTypeName, Is.EqualTo("V7-1K (2.11BSD)"));
        }

        [Test]
        public void BsdPartition_GetPartitionLetter_CorrectMapping()
        {
            // Test partition letter mapping
            Assert.That(BsdPartition.GetPartitionLetter(0), Is.EqualTo('a'));
            Assert.That(BsdPartition.GetPartitionLetter(1), Is.EqualTo('b'));
            Assert.That(BsdPartition.GetPartitionLetter(7), Is.EqualTo('h'));

            // Test reverse mapping
            Assert.That(BsdPartition.GetPartitionIndex('a'), Is.EqualTo(0));
            Assert.That(BsdPartition.GetPartitionIndex('h'), Is.EqualTo(7));
        }

        [Test]
        public void BsdSuperblock_ParseValid_Success()
        {
            // Arrange: Create minimal valid superblock
            byte[] buffer = new byte[1024];

            BitConverter.TryWriteBytes(buffer.AsSpan(0), (ushort)802);     // ISize

            // FSize is PDP-endian 32-bit (daddr_t) at offset 2
            // Write using PDP-endian helper
            BsdEndianHelper.WriteInt32PDP(buffer, 2, 50000);

            BitConverter.TryWriteBytes(buffer.AsSpan(6), (short)10);       // NFree
            BitConverter.TryWriteBytes(buffer.AsSpan(0x0D0), (short)20);   // NInode

            // Act
            var sb = BsdSuperblock.Parse(buffer);

            // Assert
            Assert.That(sb.ISize, Is.EqualTo(802));
            Assert.That(sb.FSize, Is.EqualTo(50000));
            Assert.That(sb.NFree, Is.EqualTo(10));
            Assert.That(sb.NInode, Is.EqualTo(20));
            Assert.That(sb.Validate(), Is.True);
        }

        [Test]
        public void V7Inode_FileTypeDetection_Correct()
        {
            // Arrange: Create inodes with different file types
            byte[] buffer = new byte[64];

            // Test regular file
            BitConverter.TryWriteBytes(buffer.AsSpan(0), V7Constants.IFREG);
            var regFile = V7Inode.Parse(buffer, 0);
            Assert.That(regFile.IsRegularFile, Is.True);
            Assert.That(regFile.IsDirectory, Is.False);

            // Test directory
            Array.Clear(buffer);
            BitConverter.TryWriteBytes(buffer.AsSpan(0), V7Constants.IFDIR);
            var dir = V7Inode.Parse(buffer, 0);
            Assert.That(dir.IsDirectory, Is.True);
            Assert.That(dir.IsRegularFile, Is.False);

            // Test symlink
            Array.Clear(buffer);
            BitConverter.TryWriteBytes(buffer.AsSpan(0), V7Constants.IFLNK);
            var symlink = V7Inode.Parse(buffer, 0);
            Assert.That(symlink.IsSymLink, Is.True);
        }

        [Test]
        public void V7Inode_PermissionsParsing_Correct()
        {
            // Arrange: Create inode with 0755 permissions
            byte[] buffer = new byte[64];
            ushort mode = (ushort)(V7Constants.IFREG | 0x01ED); // Regular file, rwxr-xr-x
            BitConverter.TryWriteBytes(buffer.AsSpan(0), mode);

            // Act
            var inode = V7Inode.Parse(buffer, 0);

            // Assert
            Assert.That(inode.Permissions, Is.EqualTo(0x01ED));
            Assert.That(inode.PermissionsString, Is.EqualTo("rwxr-xr-x"));
        }

        [Test]
        public void BsdDirectoryEntry_ParseValid_Success()
        {
            // Arrange: Create directory entry for "test.txt"
            byte[] buffer = new byte[32];
            BitConverter.TryWriteBytes(buffer.AsSpan(0), (ushort)42);      // Inode
            BitConverter.TryWriteBytes(buffer.AsSpan(2), (ushort)16);      // RecLen (aligned to 4)
            BitConverter.TryWriteBytes(buffer.AsSpan(4), (ushort)8);       // NameLen
            System.Text.Encoding.ASCII.GetBytes("test.txt").CopyTo(buffer, 6);

            // Act
            var entry = BsdDirectoryEntry.Parse(buffer, 0);

            // Assert
            Assert.That(entry, Is.Not.Null);
            Assert.That(entry!.Inode, Is.EqualTo(42));
            Assert.That(entry.Name, Is.EqualTo("test.txt"));
            Assert.That(entry.IsDeleted, Is.False);
            Assert.That(entry.Validate(), Is.True);
        }

        [Test]
        public void BsdDirectoryEntry_CalculateEntrySize_CorrectAlignment()
        {
            // Test 4-byte alignment: header(6) + name + null(1) rounded up to 4-byte boundary
            Assert.That(BsdDirectoryEntry.CalculateEntrySize("a"), Is.EqualTo(8));        // 6+1+1 = 8 (already aligned)
            Assert.That(BsdDirectoryEntry.CalculateEntrySize("ab"), Is.EqualTo(12));      // 6+2+1 = 9 → 12
            Assert.That(BsdDirectoryEntry.CalculateEntrySize("abc"), Is.EqualTo(12));     // 6+3+1 = 10 → 12
            Assert.That(BsdDirectoryEntry.CalculateEntrySize("abcd"), Is.EqualTo(12));    // 6+4+1 = 11 → 12
            Assert.That(BsdDirectoryEntry.CalculateEntrySize("abcde"), Is.EqualTo(12));   // 6+5+1 = 12 (already aligned)
        }

        [Test]
        public void BsdEndianHelper_PdpEndian_CorrectConversion()
        {
            // PDP-11 middle-endian stores 32-bit values as two 16-bit little-endian words in swapped order
            // For value 0x12345678:
            //   Word0 (offset 0): 0x5678 stored as: 78 56
            //   Word1 (offset 2): 0x1234 stored as: 34 12
            //   Result: (0x5678 << 16) | 0x1234 = 0x56781234

            // Test simple value
            byte[] buffer = new byte[4];
            uint testValue = 12345678u;  // Use a simple test value

            // Write using helper
            BsdEndianHelper.WriteUInt32PDP(buffer, 0, testValue);

            // Read back
            uint readValue = BsdEndianHelper.ReadUInt32PDP(buffer, 0);

            // Assert round-trip works
            Assert.That(readValue, Is.EqualTo(testValue), "Round-trip conversion should preserve value");

            // Test specific known values
            Array.Clear(buffer);
            BsdEndianHelper.WriteUInt32PDP(buffer, 0, 50000);
            uint result = BsdEndianHelper.ReadUInt32PDP(buffer, 0);
            Assert.That(result, Is.EqualTo(50000u));
        }
    }

    /// <summary>
    /// Tests for BSD block mapping and indirect block handling.
    /// </summary>
    [TestFixture]
    public class V7BlockMapperTests
    {
        private MemoryBlockDevice CreateTestDevice(int blockSize, int totalBlocks)
        {
            var descriptor = new BlockDeviceDescriptor
            {
                Name = "Test Device",
                BlockSize = blockSize,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)totalBlocks,
                ExpectedImageSize = blockSize * totalBlocks
            };

            var device = new MemoryBlockDevice(descriptor);
            var stream = new MemoryStream(new byte[blockSize * totalBlocks]);
            device.Mount(stream, false);

            return device;
        }

        [Test]
        public void V7BlockMapper_512ByteSectors_CorrectMapping()
        {
            // Arrange: Device with 512-byte sectors
            var device = CreateTestDevice(512, 2048);
            var mapper = new RetroIO.Storage.Local.V7.Core.V7BlockMapper(device);

            // Act & Assert
            Assert.That(mapper.PointersPerBlock, Is.EqualTo(256)); // 1024 / 4
        }

        [Test]
        public void V7BlockMapper_1024ByteSectors_CorrectMapping()
        {
            // Arrange: Device with 1024-byte sectors (ND hardware)
            var device = CreateTestDevice(1024, 1024);
            var mapper = new RetroIO.Storage.Local.V7.Core.V7BlockMapper(device);

            // Act & Assert
            Assert.That(mapper.PointersPerBlock, Is.EqualTo(256));
        }

        [Test]
        public void V7BlockMapper_DirectBlockAccess_Works()
        {
            // Arrange
            var device = CreateTestDevice(512, 2048);
            var mapper = new RetroIO.Storage.Local.V7.Core.V7BlockMapper(device);

            var inode = new V7Inode();
            inode.Addr[0] = 100;  // Direct block 0
            inode.Addr[1] = 101;  // Direct block 1
            inode.Addr[2] = 102;  // Direct block 2
            inode.Addr[3] = 103;  // Direct block 3

            // Act & Assert: Map direct blocks
            Assert.That(mapper.MapFileBlock(inode, 0), Is.EqualTo(100));
            Assert.That(mapper.MapFileBlock(inode, 1), Is.EqualTo(101));
            Assert.That(mapper.MapFileBlock(inode, 2), Is.EqualTo(102));
            Assert.That(mapper.MapFileBlock(inode, 3), Is.EqualTo(103));
        }

        [Test]
        public void V7BlockMapper_SparseBlock_ReturnsZero()
        {
            // Arrange
            var device = CreateTestDevice(512, 2048);
            var mapper = new RetroIO.Storage.Local.V7.Core.V7BlockMapper(device);

            var inode = new V7Inode();
            inode.Addr[0] = 0;  // Sparse block

            // Act
            uint blockNum = mapper.MapFileBlock(inode, 0);

            // Assert
            Assert.That(blockNum, Is.EqualTo(0), "Sparse blocks should return 0");
        }

        [Test]
        public void V7Inode_InodeToBlock_CorrectCalculation()
        {
            // Root inode (#2) calculation:
            // itod(2) = ((2 + 2*16 - 1) / 16) = 33 / 16 = 2
            Assert.That(V7Inode.InodeToBlock(2), Is.EqualTo(2));

            // Inode 1 (bad blocks)
            // itod(1) = ((1 + 2*16 - 1) / 16) = 32 / 16 = 2
            Assert.That(V7Inode.InodeToBlock(1), Is.EqualTo(2));

            // Inode 16
            // itod(16) = ((16 + 31) / 16) = 47 / 16 = 2
            Assert.That(V7Inode.InodeToBlock(16), Is.EqualTo(2));

            // Inode 17
            // itod(17) = ((17 + 31) / 16) = 48 / 16 = 3
            Assert.That(V7Inode.InodeToBlock(17), Is.EqualTo(3));
        }

        [Test]
        public void V7Inode_InodeToOffset_CorrectCalculation()
        {
            // Root inode (#2): offset 1 * 64 = 64 bytes
            // itoo(2) = ((2 + 31) % 16) = 33 % 16 = 1
            Assert.That(V7Inode.InodeToOffset(2), Is.EqualTo(64));

            // Inode 1: offset 0 * 64 = 0 bytes
            Assert.That(V7Inode.InodeToOffset(1), Is.EqualTo(0));
        }
    }

    /// <summary>
    /// Tests for BSD partition management.
    /// </summary>
    [TestFixture]
    public class BsdPartitionTests
    {
        [Test]
        public void BsdPartition_AllPartitionLetters_Valid()
        {
            // Test all valid partition letters (a-h)
            for (char c = 'a'; c <= 'h'; c++)
            {
                int index = BsdPartition.GetPartitionIndex(c);
                Assert.That(index, Is.GreaterThanOrEqualTo(0));
                Assert.That(index, Is.LessThan(8));

                char reverse = BsdPartition.GetPartitionLetter(index);
                Assert.That(reverse, Is.EqualTo(c));
            }
        }

        [Test]
        public void BsdPartition_InvalidLetter_ThrowsException()
        {
            Assert.Throws<ArgumentException>(() => BsdPartition.GetPartitionIndex('z'));
            Assert.Throws<ArgumentException>(() => BsdPartition.GetPartitionIndex('0'));
        }

        [Test]
        public void BsdPartition_Validate_ChecksBounds()
        {
            // Valid partition within disk bounds
            var validPartition = new BsdPartition
            {
                Size = 1000,
                Offset = 0,
                FsType = V7Constants.FS_V71K
            };
            Assert.That(validPartition.Validate(2000), Is.True);

            // Invalid: exceeds disk size
            var invalidPartition = new BsdPartition
            {
                Size = 1000,
                Offset = 1500,
                FsType = V7Constants.FS_V71K
            };
            Assert.That(invalidPartition.Validate(2000), Is.False);
        }
    }

    /// <summary>
    /// Tests for BSD driver integration with partition support.
    /// </summary>
    [TestFixture]
    public class V7DriverTests
    {
        [Test]
        public void V7Driver_SetPartition_ValidLetters_Success()
        {
            // Arrange
            var driver = new V7Driver();

            // Act & Assert: All valid partition letters
            for (char c = 'a'; c <= 'h'; c++)
            {
                Assert.DoesNotThrow(() => driver.SetPartition(c));

                // The old assertion only checked SetPartition didn't throw — it would still pass
                // even if the letter were silently ignored. Read back CurrentPartition (which
                // SetPartition wires to _partitionLetter + _useDiskLabel) to confirm it actually
                // took effect for each letter, not just the last one.
                Assert.That(driver.CurrentPartition, Is.EqualTo(c.ToString()),
                    $"SetPartition('{c}') should make CurrentPartition report '{c}'");
            }
        }

        [Test]
        public void V7Driver_SetPartition_InvalidLetter_ThrowsException()
        {
            var driver = new V7Driver();

            Assert.Throws<ArgumentException>(() => driver.SetPartition('i'));
            Assert.Throws<ArgumentException>(() => driver.SetPartition('z'));
            Assert.Throws<ArgumentException>(() => driver.SetPartition('0'));
        }

        [Test]
        public void V7Driver_SetRawMode_NoException()
        {
            // Arrange
            var driver = new V7Driver();
            driver.SetPartition('c'); // establish a non-null CurrentPartition first

            // Act & Assert
            Assert.DoesNotThrow(() => driver.SetRawMode());

            // The old assertion only checked SetRawMode didn't throw — it would still pass even
            // if it silently did nothing. SetRawMode flips _useDiskLabel to false, which
            // CurrentPartition surfaces as null — confirm that state change actually happened.
            Assert.That(driver.CurrentPartition, Is.Null, "SetRawMode should clear disk-label partition mode");
        }

        [Test]
        public void V7Driver_Capabilities_IncludesRequiredFeatures()
        {
            // Arrange
            var driver = new V7Driver();

            // Act
            var caps = driver.Capabilities;

            // Assert: Verify required capabilities
            Assert.That((caps & DriverCapabilities.Read) != 0, Is.True);
            Assert.That((caps & DriverCapabilities.ListDirectory) != 0, Is.True);
            Assert.That((caps & DriverCapabilities.Subdirectories) != 0, Is.True);
            Assert.That((caps & DriverCapabilities.Permissions) != 0, Is.True);
            Assert.That((caps & DriverCapabilities.Timestamps) != 0, Is.True);
            Assert.That((caps & DriverCapabilities.Partitions) != 0, Is.True);
            Assert.That((caps & DriverCapabilities.CaseSensitive) != 0, Is.True);
            Assert.That((caps & DriverCapabilities.SymbolicLinks) != 0, Is.True);
            Assert.That((caps & DriverCapabilities.HardLinks) != 0, Is.True);
        }

        [Test]
        public void V7Driver_ReadOnlyMode_CorrectFlags()
        {
            // Arrange
            var driver = new V7Driver();

            // Act & Assert
            Assert.That(driver.CanRead, Is.True);
            Assert.That(driver.CanWrite, Is.False, "Write operations not yet implemented");
            Assert.That(driver.HasBlockAccess, Is.True);
        }
    }

    /// <summary>
    /// Memory-based block device for testing without file I/O.
    /// </summary>
    internal class MemoryBlockDevice : IBlockDevice
    {
        private readonly BlockDeviceDescriptor _descriptor;
        private byte[]? _data;
        private bool _mounted;

        public MemoryBlockDevice(BlockDeviceDescriptor descriptor)
        {
            _descriptor = descriptor;
        }

        public int BlockSize => _descriptor.BlockSize;
        public uint TotalBlocks => _descriptor.TotalBlocks;
        public bool IsReadOnly => false;
        public bool IsMounted => _mounted;
        public bool SupportsVariableGeometry => false;

        public void Mount(Stream imageStream, bool readOnly)
        {
            _data = new byte[imageStream.Length];
            imageStream.ReadExactly(_data);
            _mounted = true;
        }

        public int ReadBlock(BlockAddress address, Span<byte> buffer)
        {
            if (!_mounted || _data == null)
                throw new InvalidOperationException("Device not mounted");

            uint lba = address.LBA;
            int offset = (int)(lba * BlockSize);

            if (offset + buffer.Length > _data.Length)
                return 0;

            _data.AsSpan(offset, buffer.Length).CopyTo(buffer);
            return buffer.Length;
        }

        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            if (!_mounted || _data == null)
                throw new InvalidOperationException("Device not mounted");

            uint lba = address.LBA;
            int offset = (int)(lba * BlockSize);
            data.CopyTo(_data.AsSpan(offset));
        }

        public int GetBlocksPerTrack(int track) => 32;

        public BlockDeviceInfo GetBlockDeviceInfo() => new BlockDeviceInfo
        {
            AddressMode = BlockAddressMode.LBA,
            BlockSize = BlockSize,
            TotalBlocks = TotalBlocks
        };

        public void Flush() { }
        public void Dispose() { _data = null; _mounted = false; }
    }

    /// <summary>
    /// Integration tests using a real BSD disk image.
    ///
    /// Runs in the normal suite: [SetUp] checks File.Exists and calls Assert.Ignore when the image
    /// is absent, so a machine without it reports skips rather than failures. Previously [Explicit],
    /// which meant it never ran and never guarded anything.
    /// </summary>
    [TestFixture]
    public class BsdIntegrationTests
    {
        private const string TestImagePath = @"E:\Dev\Ronny\BSD\Boot\1b.FromTape\2.11BSD";

        [SetUp]
        public void Setup()
        {
            if (!File.Exists(TestImagePath))
            {
                Assert.Ignore($"Test image not found: {TestImagePath}");
            }
        }

        [Test]
        public void BsdRealImage_MountRawMode_Success()
        {
            // Arrange
            var driver = new V7Driver();
            driver.SetRawMode();  // Test raw filesystem mode

            // Act
            using var stream = new FileStream(TestImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            driver.Mount(stream, readOnly: true);

            // Assert
            // A MOUNTED driver reports its filesystem's own type, which is "V7"
            // (BsdV7FileSystem.FileSystemType). "BSD-V7" is only BsdDriver's fallback string for
            // when nothing is mounted, so expecting it here was stale - the same stale-name issue
            // as FileSystemScheme_Bsd_CorrectMapping and the note in BsdRealImageTests.cs:317.
            // "V7" is also what V7DriverProvider.FileSystemType registers, so this is the value
            // DriverFactory keys on.
            Assert.That(driver.FileSystemType, Is.EqualTo("V7"));
            Assert.DoesNotThrow(() => driver.ListDirectory("/"));
        }

        [Test]
        public void BsdRealImage_ListRootDirectory_ReturnsEntries()
        {
            // Arrange
            var driver = new V7Driver();
            driver.SetRawMode();

            using var stream = new FileStream(TestImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            driver.Mount(stream, readOnly: true);

            // Act
            var entries = driver.ListDirectory("/");

            // Assert
            Assert.That(entries, Is.Not.Null);
            Assert.That(entries.Length, Is.GreaterThan(0), "Root directory should contain entries");

            // Verify common Unix directories might exist
            Console.WriteLine($"Root directory contains {entries.Length} entries:");
            for (int i = 0; i < Math.Min(entries.Length, 20); i++)
            {
                var entry = entries[i];
                string type = entry.Metadata.IsDirectory ? "DIR " : "FILE";
                Console.WriteLine($"  {type}  {entry.Name}");
            }
        }

        [Test]
        public void BsdRealImage_ReadFileMetadata_HasTimestamps()
        {
            // Arrange
            var driver = new V7Driver();
            driver.SetRawMode();

            using var stream = new FileStream(TestImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            driver.Mount(stream, readOnly: true);

            var entries = driver.ListDirectory("/");
            if (entries.Length == 0)
            {
                Assert.Ignore("No files in root directory");
                return;
            }

            // Act: Get metadata for first entry
            var firstEntry = entries[0];
            var metadata = driver.GetMetadata("/" + firstEntry.Name);

            // Assert
            Assert.That(metadata, Is.Not.Null);
            Assert.That(metadata!.Name, Is.EqualTo(firstEntry.Name));
            Assert.That(metadata.ModifiedTime, Is.Not.EqualTo(DateTime.MinValue));
        }

        [Test]
        public void BsdRealImage_PartitionDetection_FindsPartitions()
        {
            // Arrange: Create block device
            var descriptor = new BlockDeviceDescriptor
            {
                Name = "BSD Test Disk",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(new FileInfo(TestImagePath).Length / 512)
            };

            var device = new GenericBlockDevice(descriptor);
            using var stream = new FileStream(TestImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            device.Mount(stream, readOnly: true);

            // Act
            var partMgr = new BsdPartitionManager(device);
            var diskLabel = partMgr.GetDiskLabel();

            // Assert
            if (diskLabel != null)
            {
                Assert.That(diskLabel.IsValid(), Is.True);
                Assert.That(diskLabel.NumPartitions, Is.GreaterThan(0));

                var partitions = partMgr.ListPartitions();
                Console.WriteLine($"Found {partitions.Count} partitions:");

                for (int i = 0; i < partitions.Count; i++)
                {
                    var (letter, part) = partitions[i];
                    Console.WriteLine($"  [{letter}] {part.FileSystemTypeName}, {part.Size} sectors");
                }
            }
            else
            {
                Console.WriteLine("No disk label found - testing raw filesystem mode");
            }
        }

        [Test]
        public void BsdRealImage_MountAllPartitions_NoErrors()
        {
            // Arrange: Detect all partitions
            var descriptor = new BlockDeviceDescriptor
            {
                Name = "BSD Test Disk",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(new FileInfo(TestImagePath).Length / 512)
            };

            var device = new GenericBlockDevice(descriptor);
            using var stream = new FileStream(TestImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            device.Mount(stream, readOnly: true);

            var partMgr = new BsdPartitionManager(device);
            var partitions = partMgr.ListPartitions();

            if (partitions.Count == 0)
            {
                Assert.Ignore("No partitions found in disk label");
                return;
            }

            // Act & Assert: Try to mount each V7 partition
            foreach (var (letter, part) in partitions)
            {
                if (!part.IsV7Filesystem)
                {
                    Console.WriteLine($"Skipping partition {letter}: {part.FileSystemTypeName}");
                    continue;
                }

                Console.WriteLine($"\nTesting partition {letter}:");

                // Create new device and stream for each partition test
                var testDevice = new GenericBlockDevice(descriptor);
                using var testStream = new FileStream(TestImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                testDevice.Mount(testStream, readOnly: true);

                var fs = new V7FileSystem
                {
                    PartitionLetter = letter,
                    UseDiskLabel = true
                };

                Assert.DoesNotThrow(() => fs.Initialize(testDevice), $"Failed to mount partition {letter}");

                // Try to list root directory
                var entries = fs.ListDirectory("/");
                Console.WriteLine($"  Partition {letter} root: {entries.Length} entries");
            }
        }

        [Test]
        public void BsdRealImage_BootBlock_CanRead()
        {
            // Arrange
            var descriptor = new BlockDeviceDescriptor
            {
                Name = "BSD Test Disk",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(new FileInfo(TestImagePath).Length / 512)
            };

            var device = new GenericBlockDevice(descriptor);
            using var stream = new FileStream(TestImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            device.Mount(stream, readOnly: true);

            // Act
            var bootBlock = V7BootBlock.Read(device);

            // Assert
            Assert.That(bootBlock, Is.Not.Null);
            Assert.That(bootBlock.Data.Length, Is.EqualTo(512));

            Console.WriteLine($"Boot block: {bootBlock}");
        }

        [Test]
        public void BsdRealImage_ReadFile_Success()
        {
            // Arrange
            var driver = new V7Driver();
            driver.SetRawMode();

            using var stream = new FileStream(TestImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            driver.Mount(stream, readOnly: true);

            var rootEntries = driver.ListDirectory("/");

            // Find a regular file (not a directory)
            DirectoryEntry? fileEntry = null;
            for (int i = 0; i < rootEntries.Length; i++)
            {
                if (!rootEntries[i].Metadata.IsDirectory && rootEntries[i].Metadata.Size > 0)
                {
                    fileEntry = rootEntries[i];
                    break;
                }
            }

            if (fileEntry == null)
            {
                Assert.Ignore("No regular files found in root directory");
                return;
            }

            // Act
            string filePath = "/" + fileEntry.Name;
            byte[] fileData = driver.ReadFile(filePath);

            // Assert
            Assert.That(fileData, Is.Not.Null);
            Assert.That(fileData.Length, Is.EqualTo(fileEntry.Metadata.Size));

            Console.WriteLine($"Successfully read file: {filePath} ({fileData.Length} bytes)");
        }

        [Test]
        public void V7FileSystem_DirectoryEntries_HavePathProperty()
        {
            // Arrange
            var driver = new V7Driver();
            driver.SetRawMode();

            using var stream = new FileStream(TestImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            driver.Mount(stream, readOnly: true);

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

            Console.WriteLine($"✓ All {rootEntries.Length} root entries have correct Path property");
        }

        [Test]
        public void V7FileSystem_SubdirectoryEntries_HaveCorrectPath()
        {
            // Arrange
            var driver = new V7Driver();
            driver.SetRawMode();

            using var stream = new FileStream(TestImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            driver.Mount(stream, readOnly: true);

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

            Console.WriteLine($"Testing subdirectory: /{subdir.Name}");

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

            Console.WriteLine($"✓ All {subdirEntries.Length} entries in /{subdir.Name} have correct Path property");
        }
    }

    /// <summary>
    /// Tests for BSD filesystem scheme enumeration.
    /// </summary>
    [TestFixture]
    public class BsdSchemeTests
    {
        [Test]
        public void FileSystemScheme_Bsd_CorrectMapping()
        {
            // Canonical scheme string is "v7", not "bsd". The enum was renamed BSD -> V7 to keep
            // the V7 *filesystem* distinct from the BSD *disk label* (a partition table) - see
            // ai-docs/BSD-NAMING-CONVENTIONS.md. "bsd" is still accepted when PARSING, for
            // backward compatibility with existing URNs, but it is no longer what we emit.
            Assert.That(FileSystemScheme.V7.ToSchemeString(), Is.EqualTo("v7"));

            // The backward-compatible input must still resolve, and must round-trip to canonical.
            Assert.That(FileSystemSchemeExtensions.ParseScheme("bsd"), Is.EqualTo(FileSystemScheme.V7));
            Assert.That(FileSystemSchemeExtensions.ParseScheme("v7"), Is.EqualTo(FileSystemScheme.V7));
            Assert.That(FileSystemSchemeExtensions.ParseScheme("bsd").ToSchemeString(), Is.EqualTo("v7"));
            // Driver type name must match V7DriverProvider.FileSystemType exactly ("V7"), because
            // DiskImageVolumeProvider looks the provider up by this string. "BSD-V7" would find
            // no provider at all.
            Assert.That(FileSystemScheme.V7.ToDriverTypeName(), Is.EqualTo("V7"));
            Assert.That(FileSystemScheme.V7.IsLocalDiskImage(), Is.True);
            Assert.That(FileSystemScheme.V7.IsNetworkFilesystem(), Is.False);
        }

        [Test]
        public void FileSystemScheme_ParseBsd_ReturnsCorrectEnum()
        {
            // Test parsing scheme name (not full URI - that's handled by ExtractScheme in DiskImageVolumeProvider)
            var scheme = FileSystemSchemeExtensions.ParseScheme("bsd");
            Assert.That(scheme, Is.EqualTo(FileSystemScheme.V7));

            scheme = FileSystemSchemeExtensions.ParseScheme("BSD");  // Case insensitive
            Assert.That(scheme, Is.EqualTo(FileSystemScheme.V7));

            // Test other schemes
            scheme = FileSystemSchemeExtensions.ParseScheme("d64");
            Assert.That(scheme, Is.EqualTo(FileSystemScheme.D64));

            scheme = FileSystemSchemeExtensions.ParseScheme("unknown-scheme");
            Assert.That(scheme, Is.EqualTo(FileSystemScheme.Unknown));
        }
    }
}

