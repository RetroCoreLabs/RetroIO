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
    public class NdfsFragmentationTest
    {
        [Test]
        public void Test_FileEditFragmentation_ShowsBlockReuse()
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

                Console.WriteLine("=== NDFS Fragmentation Analysis ===\n");
                Console.WriteLine("Simulating text editor behavior: repeated edits to same file\n");

                // Phase 1: Create initial file
                uint initialIndexBlock;
                uint[] initialDataBlocks;

                using (var stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create(imageSize);
                    var blockDevice = new GenericBlockDevice(descriptor);
                    blockDevice.Mount(stream, readOnly: false);

                    var fileSystem = new NdfsFileSystem();
                    fileSystem.Initialize(blockDevice);

                    // Create user
                    fileSystem.AddUser("TESTUSER", 100);

                    // Create file with 3 pages of data (6KB)
                    byte[] initialData = new byte[6000];
                    for (int i = 0; i < initialData.Length; i++)
                        initialData[i] = (byte)'A';

                    fileSystem.WriteFile("TESTUSER/DOCUMENT.TXT", initialData);

                    // Get allocated blocks
                    var file = fileSystem.GetMetadata("TESTUSER/DOCUMENT.TXT");
                    var objFile = ((NdfsFileSystem)fileSystem).GetObjectFile();
                    var entry = objFile.FindObject("DOCUMENT", "TESTUSER");
                    Assert.That(entry, Is.Not.Null);

                    initialIndexBlock = entry!.FilePointer!.BlockID;
                    Console.WriteLine($"Initial write (6000 bytes):");
                    Console.WriteLine($"  Index block: {initialIndexBlock}");
                    Console.WriteLine($"  Expected data blocks: 3");
                    Console.WriteLine($"  Total blocks allocated: 4 (1 index + 3 data)\n");

                    // Track initial data blocks
                    initialDataBlocks = GetDataBlocks(fileSystem, blockDevice, entry);
                    Console.WriteLine($"  Data blocks: [{string.Join(", ", initialDataBlocks)}]\n");

                    fileSystem.Dispose();
                    blockDevice.Dispose();
                }

                // Phase 2: Edit file multiple times (simulating user edits)
                int editCount = 10;
                for (int edit = 1; edit <= editCount; edit++)
                {
                    using (var stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite))
                    {
                        var descriptor = NdfsBlockDeviceDescriptor.Create(imageSize);
                        var blockDevice = new GenericBlockDevice(descriptor);
                        blockDevice.Mount(stream, readOnly: false);

                        var fileSystem = new NdfsFileSystem();
                        fileSystem.Initialize(blockDevice);

                        // Read file
                        byte[] data = fileSystem.ReadFile("TESTUSER/DOCUMENT.TXT");

                        // Modify file (add/remove text randomly)
                        int sizeChange = (edit % 3 == 0) ? 500 : -200; // Grow or shrink
                        Array.Resize(ref data, Math.Max(1000, data.Length + sizeChange));
                        for (int i = 0; i < data.Length; i++)
                            data[i] = (byte)('A' + (edit % 26));

                        // Write back
                        fileSystem.WriteFile("TESTUSER/DOCUMENT.TXT", data);

                        // Check if blocks were reused
                        var objFile = ((NdfsFileSystem)fileSystem).GetObjectFile();
                        var entry = objFile.FindObject("DOCUMENT", "TESTUSER");
                        Assert.That(entry, Is.Not.Null);
                        uint newIndexBlock = entry!.FilePointer!.BlockID;
                        uint[] newDataBlocks = GetDataBlocks(fileSystem, blockDevice, entry);

                        bool indexReused = (newIndexBlock == initialIndexBlock);
                        int dataBlocksReused = CountReusedBlocks(initialDataBlocks, newDataBlocks);

                        Console.WriteLine($"Edit #{edit} (size: {data.Length} bytes):");
                        Console.WriteLine($"  Index block: {newIndexBlock} (reused: {indexReused})");
                        Console.WriteLine($"  Data blocks: [{string.Join(", ", newDataBlocks)}]");
                        Console.WriteLine($"  Data blocks reused: {dataBlocksReused}/{initialDataBlocks.Length}");
                        Console.WriteLine($"  Fragmentation: {(indexReused && dataBlocksReused > 0 ? "LOW" : "HIGH")}\n");

                        // Updated 2026-07-05: UpdateExistingFile used to never free the
                        // overwritten file's old top-level index block at all (a real,
                        // independent leak found while adding SubIndexed write support and fixed
                        // alongside it — DeleteFile always freed it, UpdateExistingFile never
                        // did). That leak was the reason every edit got a genuinely fresh index
                        // block AND never re-used data blocks: since the old index block was
                        // never returned to the free pool, FindFirstFreeBlock's lowest-free-block-
                        // first policy handed each new file's index block the position the old
                        // file's *data* would otherwise have reused, shifting every subsequent
                        // allocation by one slot. Now that the old index block is freed correctly,
                        // a same-or-smaller-page-count overwrite naturally gets the exact same
                        // physical blocks back for both index and data (empirically verified: this
                        // edit shrinks 6000->5800 bytes, still 3 pages, so all 3 data blocks and
                        // the index block are all reused — genuinely low fragmentation now, not
                        // just relabeled).
                        Assert.That(newIndexBlock, Is.EqualTo(initialIndexBlock),
                            $"Edit #{edit}: expected the index block to be reused now that " +
                            "UpdateExistingFile correctly frees the old one before allocating a new file.");
                        Assert.That(dataBlocksReused, Is.EqualTo(initialDataBlocks.Length),
                            $"Edit #{edit}: expected all {initialDataBlocks.Length} data blocks to be " +
                            $"reused (same page count as the original write), but only {dataBlocksReused} were.");

                        fileSystem.Dispose();
                        blockDevice.Dispose();
                    }
                }

                Console.WriteLine("=== Analysis Complete ===");
                Console.WriteLine("CURRENT BEHAVIOR: Index block reused, data blocks freshly allocated (LOW-ish fragmentation)");
                Console.WriteLine("OPTIMAL BEHAVIOR: Reuse index block + reuse data blocks when possible");
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }

        private uint[] GetDataBlocks(NdfsFileSystem fs, GenericBlockDevice device, dynamic entry)
        {
            // Use reflection to call GetAllBlockPointersForFile
            var method = typeof(NdfsFileSystem).GetMethod("GetAllBlockPointersForFile",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (method == null)
                return new uint[0];

            var result = method.Invoke(fs, new object[] { entry.FilePointer, entry.BytesInFile });

            // Cannot cast List<BlockPointer> to List<dynamic> due to generic invariance
            // Use IList interface instead
            if (result is System.Collections.IList blockList)
            {
                var blockIds = new uint[blockList.Count];
                for (int i = 0; i < blockList.Count; i++)
                {
                    if (blockList[i] is RetroIO.Storage.Ndfs.Structures.BlockPointer blockPointer)
                    {
                        blockIds[i] = blockPointer.BlockID;
                    }
                }
                return blockIds;
            }

            return new uint[0];
        }

        private int CountReusedBlocks(uint[] oldBlocks, uint[] newBlocks)
        {
            int count = 0;
            for (int i = 0; i < oldBlocks.Length && i < newBlocks.Length; i++)
            {
                if (oldBlocks[i] == newBlocks[i])
                    count++;
            }
            return count;
        }

        private void CreateTestNdfsImage(FileStream stream, int totalPages)
        {
            byte[] buffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            int masterOffset = NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET;

            // Master block
            string dirName = "FRAGTEST";
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

            // User block (page 5)
            Array.Clear(buffer, 0, buffer.Length);
            stream.Position = 5 * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            stream.Write(buffer, 0, buffer.Length);

            // ObjectFile data block (page 6)
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
