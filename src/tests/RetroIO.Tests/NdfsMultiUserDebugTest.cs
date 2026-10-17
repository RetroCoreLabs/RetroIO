//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
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
    public class NdfsMultiUserDebugTest
    {
        [Test]
        public void Test_CreateMultipleUsersAndFiles()
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

                // Create 3 users and 3 files
                using (var stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create(imageSize);
                    var blockDevice = new GenericBlockDevice(descriptor);
                    blockDevice.Mount(stream, readOnly: false);

                    var fileSystem = new NdfsFileSystem();
                    fileSystem.Initialize(blockDevice);

                    // Create 3 users
                    Console.WriteLine("Creating users...");
                    fileSystem.AddUser("USER1", 100);
                    fileSystem.AddUser("USER2", 100);
                    fileSystem.AddUser("USER3", 100);

                    var users = fileSystem.GetUsers();
                    Console.WriteLine($"Created {users.Count} users");

                    // Get initial free space
                    int freeSpaceInitial = fileSystem.GetBlocksFree();
                    Console.WriteLine($"Initial free space: {freeSpaceInitial} blocks");

                    // Create 3 files
                    Console.WriteLine("\nCreating files...");
                    byte[] data1 = new byte[5000];
                    byte[] data2 = new byte[3000];
                    byte[] data3 = new byte[7000];

                    fileSystem.WriteFile("USER1/FILE1.DAT", data1);
                    Console.WriteLine($"Wrote FILE1.DAT ({data1.Length} bytes)");
                    int free1 = fileSystem.GetBlocksFree();
                    Console.WriteLine($"  Free space: {free1} (allocated: {freeSpaceInitial - free1})");

                    fileSystem.WriteFile("USER2/FILE2.DAT", data2);
                    Console.WriteLine($"Wrote FILE2.DAT ({data2.Length} bytes)");
                    int free2 = fileSystem.GetBlocksFree();
                    Console.WriteLine($"  Free space: {free2} (allocated: {free1 - free2})");

                    fileSystem.WriteFile("USER3/FILE3.DAT", data3);
                    Console.WriteLine($"Wrote FILE3.DAT ({data3.Length} bytes)");
                    int free3 = fileSystem.GetBlocksFree();
                    Console.WriteLine($"  Free space: {free3} (allocated: {free2 - free3})");

                    Console.WriteLine($"\nTotal blocks allocated: {freeSpaceInitial - free3}");

                    // Now delete files and check
                    Console.WriteLine("\nDeleting files...");

                    int freeBefore1 = fileSystem.GetBlocksFree();
                    fileSystem.DeleteFile("USER1/FILE1.DAT");
                    int freeAfter1 = fileSystem.GetBlocksFree();
                    Console.WriteLine($"Deleted FILE1.DAT: freed {freeAfter1 - freeBefore1} blocks");

                    int freeBefore2 = fileSystem.GetBlocksFree();
                    fileSystem.DeleteFile("USER2/FILE2.DAT");
                    int freeAfter2 = fileSystem.GetBlocksFree();
                    Console.WriteLine($"Deleted FILE2.DAT: freed {freeAfter2 - freeBefore2} blocks");

                    int freeBefore3 = fileSystem.GetBlocksFree();
                    fileSystem.DeleteFile("USER3/FILE3.DAT");
                    int freeAfter3 = fileSystem.GetBlocksFree();
                    Console.WriteLine($"Deleted FILE3.DAT: freed {freeAfter3 - freeBefore3} blocks");

                    Console.WriteLine($"\nFinal free space: {freeAfter3}");
                    Console.WriteLine($"Initial free space: {freeSpaceInitial}");

                    // Deleting a file reclaims its data + index blocks. Each
                    // user's object-file directory page is allocated lazily on
                    // that user's first write (so the file lands in the correct
                    // per-user region, as SINTRAN requires) and is region
                    // infrastructure that is NOT freed on file delete. So after
                    // deleting all files, only those per-user directory pages
                    // remain — at most one per user that received a file.
                    int retainedPages = freeSpaceInitial - freeAfter3;
                    Assert.That(retainedPages, Is.InRange(0, users.Count),
                        "Only per-user object-directory pages may remain; all file data/index blocks must be reclaimed");

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
        public void Test_FileLandsInOwningUserRegion()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                int totalPages = 2048;
                int imageSize = totalPages * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.ReadWrite))
                {
                    fs.SetLength(imageSize);
                    CreateTestNdfsImage(fs, totalPages);
                }

                int buildUserIndex;
                using (var stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create(imageSize);
                    var blockDevice = new GenericBlockDevice(descriptor);
                    blockDevice.Mount(stream, readOnly: false);
                    var fileSystem = new NdfsFileSystem();
                    fileSystem.Initialize(blockDevice);

                    fileSystem.AddUser("SYSTEM", 100); // first user => index 0
                    fileSystem.AddUser("BUILD", 100);  // => non-zero index
                    buildUserIndex = -1;
                    foreach (var u in fileSystem.GetUsers())
                        if (u.UserName == "BUILD") buildUserIndex = (int)u.UserIndex;
                    Assert.That(buildUserIndex, Is.GreaterThan(0), "BUILD should get a non-zero user index");

                    fileSystem.WriteFile("BUILD/HELLO.DAT", new byte[10]);
                    var e = fileSystem.GetObjectEntry("HELLO", "BUILD");
                    Assert.That(e, Is.Not.Null);
                    // Object index high byte = owning user (the per-user region).
                    Assert.That((int)((e!.ObjectIndex >> 8) & 0xFF), Is.EqualTo(buildUserIndex));
                    Assert.That(e.ObjectIndex, Is.InRange(buildUserIndex * 256, buildUserIndex * 256 + 255));
                    // Single-version file: self-referential version chain.
                    Assert.That(e.ObjectEntryNextVersion, Is.EqualTo((uint)e.ObjectIndex));
                    Assert.That(e.ObjectEntryPrevVersion, Is.EqualTo((uint)e.ObjectIndex));
                    Assert.That(e.AccessBits, Is.EqualTo((uint)0x03FF));

                    fileSystem.Dispose();
                    blockDevice.Dispose();
                }

                // Reopen: the file must still be under BUILD, in BUILD's region.
                using (var stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create(imageSize);
                    var blockDevice = new GenericBlockDevice(descriptor);
                    blockDevice.Mount(stream, readOnly: false);
                    var fileSystem = new NdfsFileSystem();
                    fileSystem.Initialize(blockDevice);
                    var e = fileSystem.GetObjectEntry("HELLO", "BUILD");
                    Assert.That(e, Is.Not.Null, "file must persist under BUILD after reload");
                    Assert.That((int)((e!.ObjectIndex >> 8) & 0xFF), Is.EqualTo(buildUserIndex));
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
            string dirName = "MULTITEST";
            Encoding.ASCII.GetBytes(dirName).CopyTo(buffer, masterOffset);
            buffer[masterOffset + dirName.Length] = 0x27;

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

            // ObjectFile index (page 2)
            Array.Clear(buffer, 0, buffer.Length);
            WriteBlockPointer(buffer, 0, 0x00000006);
            stream.Position = 2 * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            stream.Write(buffer, 0, buffer.Length);

            // UserFile index (page 3)
            Array.Clear(buffer, 0, buffer.Length);
            WriteBlockPointer(buffer, 0, 0x00000005);
            stream.Position = 3 * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            stream.Write(buffer, 0, buffer.Length);

            // BitFile (page 4)
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
