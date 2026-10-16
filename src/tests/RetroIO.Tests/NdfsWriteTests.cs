//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tests
{
    [TestFixture]
    public class NdfsWriteTests
    {
        private MemoryStream _imageStream;
        private GenericBlockDevice _blockDevice;
        private NdfsFileSystem _fileSystem;

        [SetUp]
        public void Setup()
        {
            // Create a test NDFS image (1MB)
            int totalPages = 512;
            int imageSize = totalPages * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;

            _imageStream = new MemoryStream(imageSize);
            _imageStream.SetLength(imageSize);

            // Create valid NDFS structure
            CreateTestNdfsImage(_imageStream, totalPages);

            // Initialize filesystem
            _imageStream.Position = 0;
            var descriptor = NdfsBlockDeviceDescriptor.Create(imageSize);
            _blockDevice = new GenericBlockDevice(descriptor);
            _blockDevice.Mount(_imageStream, readOnly: false);

            _fileSystem = new NdfsFileSystem();
            _fileSystem.Initialize(_blockDevice);
        }

        [TearDown]
        public void TearDown()
        {
            _fileSystem?.Dispose();
            _blockDevice?.Dispose();
            _imageStream?.Dispose();
        }

        [Test]
        public void TestWriteSmallFile_Success()
        {
            // Arrange
            string path = "SYSTEM/TEST.TXT";
            byte[] data = Encoding.ASCII.GetBytes("Hello NDFS World!");

            // Act - Write file (no exception = success)
            _fileSystem.WriteFile(path, data);

            // Assert - File should be in directory listing
            var entries = _fileSystem.ListDirectory("SYSTEM");

            // Debug: check what entries we have
            Assert.That(entries, Is.Not.Null);
            bool hasAnyEntries = entries!.Length > 0;
            Assert.That(hasAnyEntries, Is.True, "Should have at least one entry");

            DirectoryEntry? testFile = null;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Name.StartsWith("TEST"))
                {
                    testFile = entries[i];
                    break;
                }
            }

            // Check if we found it
            if (testFile == null)
            {
                // Debug: print what we got
                string foundNames = "";
                for (int i = 0; i < Math.Min(entries.Length, 5); i++)
                    foundNames += entries[i].Name + ", ";
                Assert.Fail($"TEST file not found. Found: {foundNames}");
            }

            Assert.That(testFile, Is.Not.Null);
            Assert.That(testFile!.Metadata.Size, Is.EqualTo(data.Length));
        }

        [Test]
        public void TestWriteLargeFile_Success()
        {
            // Arrange - Create 10KB file
            string path = "SYSTEM/LARGE.DAT";
            byte[] data = new byte[10 * 1024];
            for (int i = 0; i < data.Length; i++)
                data[i] = (byte)(i % 256);

            // Act - Write large file
            _fileSystem.WriteFile(path, data);

            // Assert - File should appear in listing
            var entries = _fileSystem.ListDirectory("SYSTEM");
            DirectoryEntry? largeFile = null;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Name.StartsWith("LARGE"))
                {
                    largeFile = entries[i];
                    break;
                }
            }
            Assert.That(largeFile, Is.Not.Null);
            Assert.That(largeFile!.Metadata.Size, Is.EqualTo(data.Length));
        }

        [Test]
        public void TestUpdateExistingFile_Success()
        {
            // Arrange
            string path = "SYSTEM/UPDATE.TXT";
            byte[] originalData = Encoding.ASCII.GetBytes("Original content");
            byte[] newData = Encoding.ASCII.GetBytes("Updated content is longer!");

            // Act
            _fileSystem.WriteFile(path, originalData);
            _fileSystem.WriteFile(path, newData); // Update

            // Assert
            byte[] readData = _fileSystem.ReadFile(path);
            Assert.That(Encoding.ASCII.GetString(readData), Is.EqualTo("Updated content is longer!"));
        }

        [Test]
        public void TestDeleteFile_Success()
        {
            // Arrange
            string path = "SYSTEM/DELETE.TXT";
            byte[] data = Encoding.ASCII.GetBytes("To be deleted");
            _fileSystem.WriteFile(path, data);

            // Verify file exists in listing
            var entries = _fileSystem.ListDirectory("SYSTEM");
            bool foundBefore = false;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Name.StartsWith("DELETE"))
                {
                    foundBefore = true;
                    break;
                }
            }
            Assert.That(foundBefore, Is.True);

            // Act
            _fileSystem.DeleteFile(path);

            // Assert - File should not be in listing
            entries = _fileSystem.ListDirectory("SYSTEM");
            bool foundAfter = false;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Name.StartsWith("DELETE"))
                {
                    foundAfter = true;
                    break;
                }
            }
            Assert.That(foundAfter, Is.False);
        }

        [Test]
        public void TestWriteMultipleFiles_Success()
        {
            // Arrange & Act
            for (int i = 0; i < 5; i++)
            {
                string path = $"SYSTEM/FILE{i}.DAT";
                byte[] data = Encoding.ASCII.GetBytes($"File number {i}");
                _fileSystem.WriteFile(path, data);
            }

            // Assert - All files should appear in listing
            var entries = _fileSystem.ListDirectory("SYSTEM");
            for (int i = 0; i < 5; i++)
            {
                string expectedName = $"FILE{i}";
                bool found = false;
                for (int j = 0; j < entries.Length; j++)
                {
                    if (entries[j].Name.StartsWith(expectedName))
                    {
                        found = true;
                        break;
                    }
                }
                Assert.That(found, Is.True, $"File {expectedName} not found in directory");
            }
        }

        [Test]
        public void TestWriteFile_AllocatesBlocks()
        {
            // Arrange
            string path = "SYSTEM/BLOCKS.DAT";
            int freeBlocksBefore = _fileSystem.GetBlocksFree();

            // Create file requiring 3 pages (2048 bytes each) with non-zero data
            byte[] data = new byte[5000];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(i % 256); // Non-zero data to prevent sparse optimization
            }

            // Act
            _fileSystem.WriteFile(path, data);

            // Assert - Should allocate 1 index block + 3 data blocks = 4 blocks
            int freeBlocksAfter = _fileSystem.GetBlocksFree();
            int blocksAllocated = freeBlocksBefore - freeBlocksAfter;
            Assert.That(blocksAllocated, Is.EqualTo(4)); // 1 index + 3 data
        }

        [Test]
        public void TestDeleteFile_FreesBlocks()
        {
            // Arrange
            string path = "SYSTEM/TOFREE.DAT";
            byte[] data = new byte[5000]; // 3 pages + 1 index
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(i % 256); // Non-zero data to prevent sparse optimization
            }
            _fileSystem.WriteFile(path, data);
            int freeBlocksAfter = _fileSystem.GetBlocksFree();

            // Act
            _fileSystem.DeleteFile(path);

            // Assert - Should free 4 blocks (1 index + 3 data)
            int freeBlocksFinal = _fileSystem.GetBlocksFree();
            int blocksFreed = freeBlocksFinal - freeBlocksAfter;
            Assert.That(blocksFreed, Is.EqualTo(4));
        }

        [Test]
        public void TestWriteFile_EmptyData()
        {
            // Arrange
            string path = "SYSTEM/EMPTY.TXT";
            byte[] data = Array.Empty<byte>();

            // Act
            _fileSystem.WriteFile(path, data);

            // Assert - File should appear in listing with 0 size
            var entries = _fileSystem.ListDirectory("SYSTEM");
            DirectoryEntry? emptyFile = null;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Name.StartsWith("EMPTY"))
                {
                    emptyFile = entries[i];
                    break;
                }
            }
            Assert.That(emptyFile, Is.Not.Null);
            Assert.That(emptyFile!.Metadata.Size, Is.EqualTo(0));
        }

        [Test]
        public void TestWriteFile_InvalidPath()
        {
            // Arrange
            byte[] data = Encoding.ASCII.GetBytes("test");

            // Act & Assert
            Assert.Throws<ArgumentException>(() => _fileSystem.WriteFile("", data));
            Assert.Throws<ArgumentException>(() => _fileSystem.WriteFile("/", data));
        }

        [Test]
        public void TestWriteFile_NullData()
        {
            // Arrange
            string path = "SYSTEM/NULL.TXT";

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => _fileSystem.WriteFile(path, null!));
        }

        [Test]
        public void TestWriteFile_UserNotFound()
        {
            // Arrange
            string path = "NONEXISTENT/FILE.TXT";
            byte[] data = Encoding.ASCII.GetBytes("test");

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => _fileSystem.WriteFile(path, data));
        }

        private void CreateTestNdfsImage(MemoryStream stream, int totalPages)
        {
            byte[] buffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];

            // Master block is in last 32 bytes of first page
            int masterOffset = NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET;

            // Directory name at master block offset
            string dirName = "TESTDISK";
            Encoding.ASCII.GetBytes(dirName).CopyTo(buffer, masterOffset);
            buffer[masterOffset + dirName.Length] = 0x27; // ' terminator

            // Total pages
            buffer[masterOffset + 0x0C] = (byte)(totalPages >> 24);
            buffer[masterOffset + 0x0D] = (byte)(totalPages >> 16);
            buffer[masterOffset + 0x0E] = (byte)(totalPages >> 8);
            buffer[masterOffset + 0x0F] = (byte)totalPages;

            // ObjectFile pointer (Indexed to page 2)
            WriteBlockPointer(buffer, masterOffset + 0x10, 0x40000002);

            // UserFile pointer (Indexed to page 3)
            WriteBlockPointer(buffer, masterOffset + 0x14, 0x40000003);

            // BitFile pointer (Contiguous to page 4)
            WriteBlockPointer(buffer, masterOffset + 0x18, 0x00000004);

            // Unreserved pages (total - system pages 0-6)
            uint unreserved = (uint)(totalPages - 7); // Subtract system pages 0-6
            buffer[masterOffset + 0x1C] = (byte)(unreserved >> 24);
            buffer[masterOffset + 0x1D] = (byte)(unreserved >> 16);
            buffer[masterOffset + 0x1E] = (byte)(unreserved >> 8);
            buffer[masterOffset + 0x1F] = (byte)unreserved;

            stream.Position = 0;
            stream.Write(buffer, 0, buffer.Length);

            // ObjectFile index block (page 2) with pointer to data block at page 6
            Array.Clear(buffer, 0, buffer.Length);
            WriteBlockPointer(buffer, 0, 0x00000006); // Contiguous pointer to page 6
            stream.Position = 2 * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            stream.Write(buffer, 0, buffer.Length);

            // ObjectFile data block (page 6) - initially empty (space for 32 entries)
            Array.Clear(buffer, 0, buffer.Length);
            stream.Position = 6 * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            stream.Write(buffer, 0, buffer.Length);

            // UserFile index block (page 3) with one user
            Array.Clear(buffer, 0, buffer.Length);
            WriteBlockPointer(buffer, 0, 0x00000005); // Pointer to user block at page 5
            stream.Position = 3 * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            stream.Write(buffer, 0, buffer.Length);

            // BitFile (page 4) - mark pages 0-6 as used
            Array.Clear(buffer, 0, buffer.Length);
            buffer[0] = 0x7F; // Binary 01111111 - pages 0-6 used
            stream.Position = 4 * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            stream.Write(buffer, 0, buffer.Length);

            // User block (page 5)
            Array.Clear(buffer, 0, buffer.Length);
            buffer[0] = 0x81; // Header: 0x81 for valid user entry
            Encoding.ASCII.GetBytes("SYSTEM").CopyTo(buffer, 2); // Username at offset 2
            buffer[2 + 6] = 0x27; // ' terminator after SYSTEM
            buffer[37] = 0x00; // User index at byte 37

            // Pages reserved (bytes 28-31): 1000 pages
            uint reserved = 1000;
            buffer[28] = (byte)(reserved >> 24);
            buffer[29] = (byte)(reserved >> 16);
            buffer[30] = (byte)(reserved >> 8);
            buffer[31] = (byte)reserved;

            // Pages used (bytes 32-35): 0 initially
            buffer[32] = 0;
            buffer[33] = 0;
            buffer[34] = 0;
            buffer[35] = 0;

            stream.Position = 5 * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            stream.Write(buffer, 0, buffer.Length);
        }

        private static void WriteBlockPointer(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }
    }
}
