//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.FileSystems;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Tests
{
    [TestFixture]
    public class NdfsTests
    {
        private DriverFactory _factory;

        [SetUp]
        public void Setup()
        {
            _factory = new DriverFactory();
            _factory.Register(new NdfsDriverProvider());
        }

        [Test]
        public void TestNdfsDriverProviderRegistration()
        {
            // Arrange
            var provider = new NdfsDriverProvider();

            // Assert
            Assert.That(provider.FileSystemType, Is.EqualTo("NDFS"));
            Assert.That(provider.Description, Is.EqualTo("Nord Disk File System"));
        }

        [Test]
        public void TestNdfsBlockDeviceDescriptor()
        {
            // Arrange
            long imageSize = 2048 * 100; // 100 pages

            // Act
            var descriptor = NdfsBlockDeviceDescriptor.Create(imageSize);

            // Assert
            Assert.That(descriptor.Name, Is.EqualTo("NDFS"));
            Assert.That(descriptor.BlockSize, Is.EqualTo(2048));
            Assert.That(descriptor.TotalBlocks, Is.EqualTo(100));
            Assert.That(descriptor.AddressMode, Is.EqualTo(BlockAddressMode.LBA));
        }

        [Test]
        public void TestNdfsBlockDeviceDescriptor_InvalidSize()
        {
            // Arrange
            long tooSmall = 1024; // Less than one page

            // Act & Assert
            Assert.Throws<ArgumentException>(() => NdfsBlockDeviceDescriptor.Create(tooSmall));
        }

        [Test]
        public void TestNdfsDetection_WithoutFile()
        {
            // Arrange
            var provider = new NdfsDriverProvider();
            var hints = new DetectionHints
            {
                FileExtension = ".ndfs",
                FileSize = 2048 * 100,
                HeaderBytes = new byte[2048] // Empty header
            };

            // Act
            float confidence = provider.DetectConfidence(hints);

            // Assert
            Assert.That(confidence, Is.LessThan(0.5f)); // Low confidence without valid master block
        }

        [Test]
        public void TestNdfsDetection_WithExtension()
        {
            // Arrange
            var provider = new NdfsDriverProvider();
            var hints = new DetectionHints
            {
                FileExtension = ".nd",
                FileSize = 2048 * 100,
                HeaderBytes = CreateValidNdfsHeader()
            };

            // Act
            float confidence = provider.DetectConfidence(hints);

            // Assert. This bare header has the correct structural shape (Indexed object/user 2 apart,
            // Contiguous bit file, all above the block-7 floor) but carries NO boot code and NO
            // extended-info checksum, so it lands in the structure-only tier (0.85) plus the small
            // extension boost. That is the same tier the 20 real boot-less ND floppies rely on —
            // decisively above the 0.5 mount threshold, but below a checksum-backed hard disk (0.98).
            Assert.That(confidence, Is.GreaterThan(0.8f));
        }

        [Test]
        public void TestNdfsDriver_Mount()
        {
            // Arrange
            var driver = new NdfsDriver();
            using var stream = CreateMinimalNdfsImage();

            // Act & Assert - should not throw
            Assert.DoesNotThrow(() => driver.Mount(stream, readOnly: true));

            // Verify basic properties
            Assert.That(driver.FileSystemType, Is.EqualTo("NDFS"));
            Assert.That(driver.CanRead, Is.True);
            Assert.That(driver.HasBlockAccess, Is.True);

            // Cleanup
            driver.Dispose();
        }

        [Test]
        public void TestNdfsFileSystem_Initialize()
        {
            // Arrange
            using var stream = CreateMinimalNdfsImage();
            var descriptor = NdfsBlockDeviceDescriptor.Create(stream.Length);
            var blockDevice = new GenericBlockDevice(descriptor);
            blockDevice.Mount(stream, readOnly: true);
            var filesystem = new NdfsFileSystem();

            // Act
            filesystem.Initialize(blockDevice);

            // Assert
            Assert.That(filesystem.FileSystemType, Is.EqualTo("NDFS"));
            Assert.That(filesystem.VolumeLabel, Is.Not.Null);

            // Cleanup
            filesystem.Dispose();
            blockDevice.Dispose();
        }

        [Test]
        public void TestMasterBlock_Parsing()
        {
            // Arrange
            var pageData = CreateValidNdfsHeader();

            // Act
            var masterBlock = MasterBlock.FromBytes(pageData);

            // Assert
            Assert.That(masterBlock, Is.Not.Null);
            Assert.That(masterBlock.DirectoryName, Is.EqualTo("TEST"));
            Assert.That(masterBlock.IsValid(), Is.True);
        }

        [Test]
        public void TestBitFile_Operations()
        {
            // Arrange
            var bitFile = new BitFile();
            bitFile.Initialize(100); // 100 pages

            // Act & Assert - all blocks should be free initially
            Assert.That(bitFile.IsBlockUsed(0), Is.False);
            Assert.That(bitFile.GetFreePages(), Is.EqualTo(100));

            // Mark some blocks as used
            bitFile.MarkBlockUsed(0);
            bitFile.MarkBlockUsed(10);
            bitFile.MarkBlockUsed(99);

            // Verify
            Assert.That(bitFile.IsBlockUsed(0), Is.True);
            Assert.That(bitFile.IsBlockUsed(10), Is.True);
            Assert.That(bitFile.IsBlockUsed(99), Is.True);
            Assert.That(bitFile.IsBlockUsed(50), Is.False);
            Assert.That(bitFile.GetFreePages(), Is.EqualTo(97));

            // Free a block
            bitFile.MarkBlockFree(10);
            Assert.That(bitFile.IsBlockUsed(10), Is.False);
            Assert.That(bitFile.GetFreePages(), Is.EqualTo(98));
        }

        [Test]
        public void TestBitFile_FindFreeBlocks()
        {
            // Arrange: 100-page bitmap, mark blocks 0-9 used. AllocCeiling defaults to
            // TotalPages (100), so the highest allocatable page is 99.
            var bitFile = new BitFile();
            bitFile.Initialize(100);

            // Mark some blocks as used
            for (uint i = 0; i < 10; i++)
            {
                bitFile.MarkBlockUsed(i);
            }

            // Act
            var firstFree = bitFile.FindFirstFreeBlock();
            var freeRange = bitFile.FindFreeBlockRange(5);

            // Assert: allocation scans HIGH -> LOW to match genuine SINTRAN layout
            // (TESTP 51355B descends). So the first free page is the top of the window (99),
            // and a 5-block contiguous run starts at 95 (pages 95..99).
            Assert.That(firstFree, Is.EqualTo(99));
            Assert.That(freeRange, Is.EqualTo(95));
        }

        // Helper methods

        private byte[] CreateValidNdfsHeader()
        {
            var header = new byte[2048];

            // Create a simple master block at offset 2016
            int masterOffset = 2016;

            // Directory name "TEST"
            var nameBytes = Encoding.ASCII.GetBytes("TEST");
            Array.Copy(nameBytes, 0, header, masterOffset, nameBytes.Length);

            // Add valid block pointers (4 bytes each, big-endian). These mirror the shape every
            // REAL ND image has (verified across 53): object and user are INDEXED (type 01) and sit
            // exactly 2 blocks apart, the bit file is CONTIGUOUS (type 00), and all three are at or
            // above the block-7 floor (blocks 0-6 are system-reserved). The old fixture used blocks
            // 2/3/4 as Contiguous — below the floor and only 1 apart — a layout no real disk has.

            // Object file pointer at offset 0x10 -> block 8, Indexed (0x40000008)
            header[masterOffset + 0x10] = 0x40;
            header[masterOffset + 0x11] = 0x00;
            header[masterOffset + 0x12] = 0x00;
            header[masterOffset + 0x13] = 0x08;

            // User file pointer at offset 0x14 -> block 10, Indexed (0x4000000A) = object + 2
            header[masterOffset + 0x14] = 0x40;
            header[masterOffset + 0x15] = 0x00;
            header[masterOffset + 0x16] = 0x00;
            header[masterOffset + 0x17] = 0x0A;

            // Bit file pointer at offset 0x18 -> block 12, Contiguous (0x0000000C)
            header[masterOffset + 0x18] = 0x00;
            header[masterOffset + 0x19] = 0x00;
            header[masterOffset + 0x1A] = 0x00;
            header[masterOffset + 0x1B] = 0x0C;

            // Set unreserved pages
            header[masterOffset + 0x1C] = 0x00;
            header[masterOffset + 0x1D] = 0x00;
            header[masterOffset + 0x1E] = 0x00;
            header[masterOffset + 0x1F] = 0x05; // 5 unreserved pages

            return header;
        }

        private Stream CreateMinimalNdfsImage()
        {
            // Create a minimal NDFS image with just a master block. It must be large enough to
            // contain the master block's pointers (object=8, user=10, bit=12 — see
            // CreateValidNdfsHeader), which sit at/above the block-7 floor like every real image.
            var imageSize = 2048 * 32; // 32 pages
            var imageData = new byte[imageSize];

            // Copy valid header to first page
            var header = CreateValidNdfsHeader();
            Array.Copy(header, imageData, 2048);

            return new MemoryStream(imageData);
        }
    }
}
