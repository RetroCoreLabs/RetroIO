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
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.FileSystems;
using GenericBlockDevice = RetroIO.Storage.Local.BlockDevices.GenericBlockDevice;

namespace RetroIO.Tests
{
    [TestFixture]
    public class NdfsDirectoryStructureTest
    {
        private string _testImagePath = null!;
        private FileStream _imageStream = null!;
        private GenericBlockDevice _blockDevice = null!;
        private NdfsFileSystem _fileSystem = null!;

        [SetUp]
        public void Setup()
        {
            // Create test NDFS image
            _testImagePath = Path.GetTempFileName();

            // Create a 10MB NDFS image (5000 pages * 2048 bytes)
            long imageSize = 5000 * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            CreateTestNdfsImage(_testImagePath, imageSize);

            // Open image stream
            _imageStream = new FileStream(_testImagePath, FileMode.Open, FileAccess.ReadWrite);

            // Create block device
            var descriptor = NdfsBlockDeviceDescriptor.Create(imageSize);
            _blockDevice = new GenericBlockDevice(descriptor);
            _blockDevice.Mount(_imageStream, readOnly: false);

            // Initialize filesystem
            _fileSystem = new NdfsFileSystem();
            _fileSystem.Initialize(_blockDevice);

            // Add some test users
            _fileSystem.AddUser("SYSTEM", 500);
            _fileSystem.AddUser("USER1", 300);
            _fileSystem.AddUser("USER2", 200);

            // Add some test files
            _fileSystem.WriteFile("/SYSTEM/BOOT.SYS", new byte[] { 1, 2, 3, 4, 5 });
            _fileSystem.WriteFile("/SYSTEM/CONFIG.TXT", new byte[] { 10, 20, 30 });
            _fileSystem.WriteFile("/USER1/DOCUMENT.TXT", new byte[] { 100, 101, 102 });
            _fileSystem.WriteFile("/USER2/README.MD", new byte[] { 200, 201 });
        }

        [TearDown]
        public void TearDown()
        {
            _fileSystem?.Dispose();
            _blockDevice?.Dispose();
            _imageStream?.Dispose();

            if (File.Exists(_testImagePath))
                File.Delete(_testImagePath);
        }

        private static void CreateTestNdfsImage(string path, long imageSize)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            int totalPages = (int)(imageSize / NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);

            byte[] buffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];

            // Master block is in last 32 bytes of first page
            int masterOffset = NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET;

            // Directory name at master block offset
            string dirName = "TESTDISK";
            System.Text.Encoding.ASCII.GetBytes(dirName).CopyTo(buffer, masterOffset);
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

            // Write rest of pages as empty
            Array.Clear(buffer, 0, buffer.Length);
            for (int i = 1; i < totalPages; i++)
            {
                stream.Write(buffer, 0, buffer.Length);
            }

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
            System.Text.Encoding.ASCII.GetBytes("SYSTEM").CopyTo(buffer, 2); // Username at offset 2
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

            stream.Flush();
        }

        private static void WriteBlockPointer(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        [Test]
        public void ListDirectory_Root_ShouldShowOnlyUsers()
        {
            // Act
            var entries = _fileSystem.ListDirectory("/");

            // Assert
            Assert.That(entries.Length, Is.EqualTo(3), "Root should show exactly 3 users");

            // All entries should be directories
            for (int i = 0; i < entries.Length; i++)
            {
                Assert.That(entries[i].Metadata.IsDirectory, Is.True,
                    $"Entry '{entries[i].Name}' should be a directory");
            }

            // Check user names
            var userNames = new string[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                userNames[i] = entries[i].Name;
            }
            Assert.That(userNames, Does.Contain("SYSTEM"));
            Assert.That(userNames, Does.Contain("USER1"));
            Assert.That(userNames, Does.Contain("USER2"));
        }

        [Test]
        public void ListDirectory_UserPath_ShouldShowOnlyFiles()
        {
            // Act - list SYSTEM user directory
            var systemEntries = _fileSystem.ListDirectory("/SYSTEM");

            // Assert
            Assert.That(systemEntries.Length, Is.EqualTo(2),
                "SYSTEM user should have exactly 2 files");

            // All entries should be files (not directories)
            for (int i = 0; i < systemEntries.Length; i++)
            {
                Assert.That(systemEntries[i].Metadata.IsDirectory, Is.False,
                    $"Entry '{systemEntries[i].Name}' should be a file, not a directory");
            }

            // Check file names
            var fileNames = new string[systemEntries.Length];
            for (int i = 0; i < systemEntries.Length; i++)
            {
                fileNames[i] = systemEntries[i].Name;
            }
            // NDFS uses : as extension separator (not .)
            Assert.That(fileNames, Does.Contain("BOOT:SYS"));
            Assert.That(fileNames, Does.Contain("CONFIG:TXT"));
        }

        [Test]
        public void ListDirectory_UserPathWithTrailingSlash_ShouldShowOnlyFiles()
        {
            // Act - list USER1 directory with trailing slash
            var user1Entries = _fileSystem.ListDirectory("/USER1/");

            // Assert
            Assert.That(user1Entries.Length, Is.EqualTo(1),
                "USER1 should have exactly 1 file");

            Assert.That(user1Entries[0].Metadata.IsDirectory, Is.False,
                "Entry should be a file, not a directory");
            // NDFS uses : as extension separator (not .)
            Assert.That(user1Entries[0].Name, Is.EqualTo("DOCUMENT:TXT"));
        }

        [Test]
        public void ListDirectory_EmptyUser_ShouldReturnEmptyList()
        {
            // Arrange - add user with no files
            _fileSystem.AddUser("EMPTYUSER", 100);

            // Act
            var entries = _fileSystem.ListDirectory("/EMPTYUSER");

            // Assert
            Assert.That(entries.Length, Is.EqualTo(0),
                "Empty user should have no files");
        }

        [Test]
        public void ListDirectory_RootEmpty_ShouldShowUsersAsEmptyString()
        {
            // Act - test empty path
            var entries = _fileSystem.ListDirectory("");

            // Assert - should behave same as root
            Assert.That(entries.Length, Is.EqualTo(3),
                "Empty path should show users like root does");

            for (int i = 0; i < entries.Length; i++)
            {
                Assert.That(entries[i].Metadata.IsDirectory, Is.True);
            }
        }

        [Test]
        public void CreateDirectory_AtRoot_CreatesNewUser()
        {
            // Act
            _fileSystem.CreateDirectory("/NEWUSER");

            // Assert
            var rootEntries = _fileSystem.ListDirectory("/");

            var userNames = new string[rootEntries.Length];
            for (int i = 0; i < rootEntries.Length; i++)
            {
                userNames[i] = rootEntries[i].Name;
            }

            Assert.That(userNames, Does.Contain("NEWUSER"));

            // Should be able to list the new user
            var newUserEntries = _fileSystem.ListDirectory("/NEWUSER");
            Assert.That(newUserEntries.Length, Is.EqualTo(0), "New user should be empty");
        }

        [Test]
        public void DeleteDirectory_EmptyUser_RemovesUser()
        {
            // Arrange - create empty user
            _fileSystem.AddUser("TEMPUSER", 100);

            // Act
            _fileSystem.DeleteDirectory("/TEMPUSER");

            // Assert
            var rootEntries = _fileSystem.ListDirectory("/");
            var userNames = new string[rootEntries.Length];
            for (int i = 0; i < rootEntries.Length; i++)
            {
                userNames[i] = rootEntries[i].Name;
            }

            Assert.That(userNames, Does.Not.Contain("TEMPUSER"));
        }

        [Test]
        public void DeleteDirectory_NonEmptyUser_ThrowsException()
        {
            // Act & Assert
            Assert.Throws<InvalidOperationException>(() =>
                _fileSystem.DeleteDirectory("/SYSTEM"),
                "Should not allow deleting user with files");
        }
    }
}
