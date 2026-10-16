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
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tests
{
    [TestFixture]
    public class NdfsUserManagementTest
    {
        [Test]
        public void Test_CreateAndDeleteUser_SingleIteration()
        {
            string tempFile = Path.GetTempFileName();

            try
            {
                // Create NDFS image
                int totalPages = 2048;
                int imageSize = totalPages * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;

                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.ReadWrite))
                {
                    fs.SetLength(imageSize);
                    CreateTestNdfsImage(fs, totalPages);
                }

                // Phase 1: Create user and verify
                using (var stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create(imageSize);
                    var blockDevice = new GenericBlockDevice(descriptor);
                    blockDevice.Mount(stream, readOnly: false);

                    var fileSystem = new NdfsFileSystem();
                    fileSystem.Initialize(blockDevice);

                    // Verify no users initially
                    var users = fileSystem.GetUsers();
                    Assert.That(users.Count, Is.EqualTo(0), "Should have no users initially");

                    // Create user
                    bool added = fileSystem.AddUser("TESTUSER", 100);
                    Assert.That(added, Is.True, "Should successfully add user");

                    // Verify user exists
                    users = fileSystem.GetUsers();
                    Assert.That(users.Count, Is.EqualTo(1), "Should have 1 user after adding");
                    Assert.That(users[0].UserName, Is.EqualTo("TESTUSER"));

                    fileSystem.Dispose();
                    blockDevice.Dispose();
                }

                // Phase 2: Reload and verify user persisted
                using (var stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create(imageSize);
                    var blockDevice = new GenericBlockDevice(descriptor);
                    blockDevice.Mount(stream, readOnly: false);

                    var fileSystem = new NdfsFileSystem();
                    fileSystem.Initialize(blockDevice);

                    // Verify user persisted
                    var users = fileSystem.GetUsers();
                    Assert.That(users.Count, Is.EqualTo(1), "User should persist after remount");
                    Assert.That(users[0].UserName, Is.EqualTo("TESTUSER"));

                    // Delete user
                    bool deleted = fileSystem.RemoveUser(users[0].UserIndex);
                    Assert.That(deleted, Is.True, "Should successfully delete user");

                    // Verify user deleted
                    users = fileSystem.GetUsers();
                    Assert.That(users.Count, Is.EqualTo(0), "Should have no users after deletion");

                    fileSystem.Dispose();
                    blockDevice.Dispose();
                }

                // Phase 3: Reload and verify user deletion persisted
                using (var stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create(imageSize);
                    var blockDevice = new GenericBlockDevice(descriptor);
                    blockDevice.Mount(stream, readOnly: false);

                    var fileSystem = new NdfsFileSystem();
                    fileSystem.Initialize(blockDevice);

                    // Verify no users
                    var users = fileSystem.GetUsers();
                    Assert.That(users.Count, Is.EqualTo(0), "User deletion should persist after remount");

                    fileSystem.Dispose();
                    blockDevice.Dispose();
                }
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }

        [Test]
        public void Test_CreateFileAndDelete_VerifyBlocksFreed()
        {
            string tempFile = Path.GetTempFileName();

            try
            {
                // Create NDFS image
                int totalPages = 2048;
                int imageSize = totalPages * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;

                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.ReadWrite))
                {
                    fs.SetLength(imageSize);
                    CreateTestNdfsImage(fs, totalPages);
                }

                // Create user and file
                using (var stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create(imageSize);
                    var blockDevice = new GenericBlockDevice(descriptor);
                    blockDevice.Mount(stream, readOnly: false);

                    var fileSystem = new NdfsFileSystem();
                    fileSystem.Initialize(blockDevice);

                    // Create user
                    fileSystem.AddUser("TESTUSER", 100);

                    // Get initial free space
                    int freeSpaceInitial = fileSystem.GetBlocksFree();
                    Console.WriteLine($"Initial free space: {freeSpaceInitial} blocks");

                    // Create file (5000 bytes = 3 pages: 1 index + 2 data + 1 partial)
                    byte[] fileData = new byte[5000];
                    for (int i = 0; i < fileData.Length; i++)
                        fileData[i] = (byte)(i % 256);

                    fileSystem.WriteFile("TESTUSER/TEST.DAT", fileData);

                    int freeSpaceAfterWrite = fileSystem.GetBlocksFree();
                    int blocksAllocated = freeSpaceInitial - freeSpaceAfterWrite;
                    Console.WriteLine($"Free space after write: {freeSpaceAfterWrite} blocks");
                    Console.WriteLine($"Blocks allocated: {blocksAllocated}");

                    // Expected: 1 index block + 3 data blocks = 4 blocks
                    int expectedBlocks = 1 + ((5000 + 2047) / 2048);
                    Assert.That(blocksAllocated, Is.EqualTo(expectedBlocks),
                        $"Expected {expectedBlocks} blocks allocated, got {blocksAllocated}");

                    // Delete file
                    fileSystem.DeleteFile("TESTUSER/TEST.DAT");

                    int freeSpaceAfterDelete = fileSystem.GetBlocksFree();
                    int blocksFreed = freeSpaceAfterDelete - freeSpaceAfterWrite;
                    Console.WriteLine($"Free space after delete: {freeSpaceAfterDelete} blocks");
                    Console.WriteLine($"Blocks freed: {blocksFreed}");

                    Assert.That(blocksFreed, Is.EqualTo(expectedBlocks),
                        $"Expected {expectedBlocks} blocks freed, got {blocksFreed}");

                    fileSystem.Dispose();
                    blockDevice.Dispose();
                }
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }

        private void CreateTestNdfsImage(FileStream stream, int totalPages)
        {
            byte[] buffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            int masterOffset = NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET;

            // Master block
            string dirName = "TESTIMAGE";
            Encoding.ASCII.GetBytes(dirName).CopyTo(buffer, masterOffset);
            buffer[masterOffset + dirName.Length] = 0x27; // ' terminator

            // Total pages
            buffer[masterOffset + 0x0C] = (byte)(totalPages >> 24);
            buffer[masterOffset + 0x0D] = (byte)(totalPages >> 16);
            buffer[masterOffset + 0x0E] = (byte)(totalPages >> 8);
            buffer[masterOffset + 0x0F] = (byte)totalPages;

            // Pointers
            WriteBlockPointer(buffer, masterOffset + 0x10, 0x40000002); // ObjectFile
            WriteBlockPointer(buffer, masterOffset + 0x14, 0x40000003); // UserFile
            WriteBlockPointer(buffer, masterOffset + 0x18, 0x00000004); // BitFile

            // Unreserved pages
            uint unreserved = (uint)(totalPages - 7);
            buffer[masterOffset + 0x1C] = (byte)(unreserved >> 24);
            buffer[masterOffset + 0x1D] = (byte)(unreserved >> 16);
            buffer[masterOffset + 0x1E] = (byte)(unreserved >> 8);
            buffer[masterOffset + 0x1F] = (byte)unreserved;

            stream.Position = 0;
            stream.Write(buffer, 0, buffer.Length);

            // ObjectFile index (page 2) with data block pointer
            Array.Clear(buffer, 0, buffer.Length);
            WriteBlockPointer(buffer, 0, 0x00000006); // Data block at page 6
            stream.Position = 2 * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            stream.Write(buffer, 0, buffer.Length);

            // UserFile index (page 3) with user pointer
            Array.Clear(buffer, 0, buffer.Length);
            WriteBlockPointer(buffer, 0, 0x00000005); // User block at page 5
            stream.Position = 3 * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            stream.Write(buffer, 0, buffer.Length);

            // BitFile (page 4) - mark system pages as used
            Array.Clear(buffer, 0, buffer.Length);
            buffer[0] = 0x7F; // Pages 0-6 used
            stream.Position = 4 * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            stream.Write(buffer, 0, buffer.Length);

            // User block (page 5) - empty
            Array.Clear(buffer, 0, buffer.Length);
            stream.Position = 5 * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            stream.Write(buffer, 0, buffer.Length);

            // ObjectFile data block (page 6) - empty
            Array.Clear(buffer, 0, buffer.Length);
            stream.Position = 6 * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
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
    }
}
