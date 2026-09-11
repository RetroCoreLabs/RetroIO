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
    public class NdfsWritePersistenceTest
    {
        [Test]
        public void TestWritePersistsToDisk()
        {
            // Arrange - Create test image
            int totalPages = 512;
            int imageSize = totalPages * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            string tempFile = Path.GetTempFileName();

            try
            {
                // Create initial NDFS image
                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.ReadWrite))
                {
                    fs.SetLength(imageSize);
                    CreateTestNdfsImage(fs, totalPages);
                }

                // Verify initial image
                byte[] initialData = File.ReadAllBytes(tempFile);
                int masterOffset = NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET;
                string initialDirName = System.Text.Encoding.ASCII.GetString(initialData, masterOffset, 16).TrimEnd('\0', '\'');
                Console.WriteLine($"Initial master block directory name: '{initialDirName}'");

                // Phase 1: Write a file
                using (var writeStream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create(imageSize);
                    var blockDevice = new GenericBlockDevice(descriptor);
                    blockDevice.Mount(writeStream, readOnly: false);

                    var fileSystem = new NdfsFileSystem();
                    fileSystem.Initialize(blockDevice);

                    // Write file
                    string path = "SYSTEM/PERSIST.TXT";
                    byte[] data = Encoding.ASCII.GetBytes("This data should persist!");
                    fileSystem.WriteFile(path, data);

                    // Dispose to ensure flush
                    fileSystem.Dispose();
                    blockDevice.Dispose();
                }

                // Phase 2: Check what was written to disk
                byte[] diskData = File.ReadAllBytes(tempFile);
                Console.WriteLine($"Disk file size: {diskData.Length} bytes");

                int masterOffsetAfter = NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET;
                Console.WriteLine($"Master offset: {masterOffsetAfter}");

                // Check first few bytes
                Console.WriteLine($"First 8 bytes: {BitConverter.ToString(diskData, 0, Math.Min(8, diskData.Length))}");
                Console.WriteLine($"Master block first 8: {BitConverter.ToString(diskData, masterOffsetAfter, 8)}");

                // Read master block directory name
                string dirName = System.Text.Encoding.ASCII.GetString(diskData, masterOffsetAfter, 16).TrimEnd('\0', '\'');
                Console.WriteLine($"Master block directory name: '{dirName}'");

                // Read ObjectFile pointer
                uint objPtr = (uint)((diskData[masterOffsetAfter + 0x10] << 24) |
                                    (diskData[masterOffsetAfter + 0x11] << 16) |
                                    (diskData[masterOffsetAfter + 0x12] << 8) |
                                    diskData[masterOffsetAfter + 0x13]);
                Console.WriteLine($"ObjectFile pointer: 0x{objPtr:X8}");

                // Phase 3: Remount and verify
                using (var readStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create(imageSize);
                    var blockDevice = new GenericBlockDevice(descriptor);
                    blockDevice.Mount(readStream, readOnly: true);

                    var fileSystem = new NdfsFileSystem();
                    fileSystem.Initialize(blockDevice);

                    // Read file back
                    string path = "SYSTEM/PERSIST.TXT";
                    byte[] readData = fileSystem.ReadFile(path);

                    Assert.That(readData, Is.Not.Null, "File data should exist");
                    string readText = Encoding.ASCII.GetString(readData);
                    Assert.That(readText, Is.EqualTo("This data should persist!"));

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
            string dirName = "TESTDISK";
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

            // User block (page 5)
            Array.Clear(buffer, 0, buffer.Length);
            buffer[0] = 0x81; // Valid user entry
            Encoding.ASCII.GetBytes("SYSTEM").CopyTo(buffer, 2);
            buffer[2 + 6] = 0x27; // ' terminator
            buffer[37] = 0x00; // User index

            // Pages reserved
            uint reserved = 1000;
            buffer[28] = (byte)(reserved >> 24);
            buffer[29] = (byte)(reserved >> 16);
            buffer[30] = (byte)(reserved >> 8);
            buffer[31] = (byte)reserved;

            stream.Position = 5 * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            stream.Write(buffer, 0, buffer.Length);

            // ObjectFile data block (page 6) - empty initially
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
