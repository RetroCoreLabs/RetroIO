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

namespace RetroIO.Tests
{
    [TestFixture]
    public class DeltaFileTests
    {
        private const int BLOCK_SIZE = 512;
        private const uint TOTAL_BLOCKS = 100;

        /// <summary>
        /// Helper method to delete files with retry logic for Windows file handle release delays.
        /// </summary>
        private static void SafeDeleteFile(string path)
        {
            if (!File.Exists(path))
                return;

            // Force GC to release any pending file handles
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Retry deletion with exponential backoff
            int maxRetries = 5;
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    File.Delete(path);
                    return;
                }
                catch (IOException) when (i < maxRetries - 1)
                {
                    System.Threading.Thread.Sleep(50 * (i + 1));
                }
            }
        }

        [Test]
        public void DeltaFile_FullBlock_StoresCompleteBlockData()
        {
            string deltaPath = Path.GetTempFileName();

            try
            {
                using (var deltaWriter = new DeltaFileWriter(deltaPath, BLOCK_SIZE, DeltaCompressionMode.FullBlock))
                {
                    // Write block 5
                    byte[] originalData = CreateTestData(BLOCK_SIZE, 0x00);
                    byte[] modifiedData = CreateTestData(BLOCK_SIZE, 0xAA);

                    deltaWriter.WriteBlock(5, modifiedData);

                    // Read it back
                    byte[] readData = new byte[BLOCK_SIZE];
                    int bytesRead = deltaWriter.ReadBlock(5, readData);

                    Assert.That(bytesRead, Is.EqualTo(BLOCK_SIZE), "Should read full block");
                    Assert.That(readData, Is.EqualTo(modifiedData), "Data should match");
                }

                // Verify delta file exists and has expected size
                var fileInfo = new FileInfo(deltaPath);
                Assert.That(fileInfo.Exists, Is.True, "Delta file should exist");

                // Header (15 bytes) + Index entry (16 bytes) + Block data (512 bytes)
                long expectedSize = 15 + 16 + BLOCK_SIZE;
                Assert.That(fileInfo.Length, Is.GreaterThanOrEqualTo(expectedSize),
                    $"Delta file should be at least {expectedSize} bytes");
            }
            finally
            {
                SafeDeleteFile(deltaPath);
            }
        }

        [Test]
        public void DeltaFile_DiffOnly_CompressesSmallChanges()
        {
            string deltaPath = Path.GetTempFileName();

            try
            {
                using (var deltaWriter = new DeltaFileWriter(deltaPath, BLOCK_SIZE, DeltaCompressionMode.DiffOnly))
                {
                    // Original block: all zeros
                    byte[] originalData = CreateTestData(BLOCK_SIZE, 0x00);

                    // Modified block: change only 10 bytes
                    byte[] modifiedData = (byte[])originalData.Clone();
                    for (int i = 0; i < 10; i++)
                    {
                        modifiedData[i] = 0xFF;
                    }

                    // Write with diff compression
                    deltaWriter.WriteBlock(5, modifiedData, originalData);

                    // Read it back
                    byte[] readData = new byte[BLOCK_SIZE];
                    int bytesRead = deltaWriter.ReadBlock(5, readData, originalData);

                    Assert.That(bytesRead, Is.EqualTo(BLOCK_SIZE), "Should reconstruct full block");
                    Assert.That(readData, Is.EqualTo(modifiedData), "Reconstructed data should match");
                }

                // Verify diff compression saved space
                var fileInfo = new FileInfo(deltaPath);
                long fullBlockSize = 15 + 16 + BLOCK_SIZE; // Header + Index + Full block
                Assert.That(fileInfo.Length, Is.LessThan(fullBlockSize),
                    "DiffOnly mode should use less space than FullBlock for small changes");

                Console.WriteLine($"FullBlock would be: {fullBlockSize} bytes");
                Console.WriteLine($"DiffOnly actual: {fileInfo.Length} bytes");
                Console.WriteLine($"Savings: {fullBlockSize - fileInfo.Length} bytes ({100.0 * (fullBlockSize - fileInfo.Length) / fullBlockSize:F1}%)");
            }
            finally
            {
                SafeDeleteFile(deltaPath);
            }
        }

        [Test]
        public void DeltaFile_DiffOnly_HandlesManySmallChanges()
        {
            string deltaPath = Path.GetTempFileName();

            try
            {
                using (var deltaWriter = new DeltaFileWriter(deltaPath, BLOCK_SIZE, DeltaCompressionMode.DiffOnly))
                {
                    // Original: alternating pattern
                    byte[] originalData = new byte[BLOCK_SIZE];
                    for (int i = 0; i < BLOCK_SIZE; i++)
                    {
                        originalData[i] = (byte)(i % 2 == 0 ? 0xAA : 0x55);
                    }

                    // Modified: change every 50th byte
                    byte[] modifiedData = (byte[])originalData.Clone();
                    for (int i = 0; i < BLOCK_SIZE; i += 50)
                    {
                        modifiedData[i] = 0xFF;
                    }

                    deltaWriter.WriteBlock(10, modifiedData, originalData);

                    // Read back
                    byte[] readData = new byte[BLOCK_SIZE];
                    deltaWriter.ReadBlock(10, readData, originalData);

                    Assert.That(readData, Is.EqualTo(modifiedData), "Should correctly reconstruct scattered changes");
                }
            }
            finally
            {
                SafeDeleteFile(deltaPath);
            }
        }

        [Test]
        public void DeltaFile_FullBlock_BetterForLargeChanges()
        {
            string deltaPathFull = Path.GetTempFileName();
            string deltaPathDiff = Path.GetTempFileName();

            try
            {
                byte[] originalData = CreateTestData(BLOCK_SIZE, 0x00);
                byte[] modifiedData = CreateTestData(BLOCK_SIZE, 0xFF); // Complete change

                // FullBlock mode
                using (var deltaWriterFull = new DeltaFileWriter(deltaPathFull, BLOCK_SIZE, DeltaCompressionMode.FullBlock))
                {
                    deltaWriterFull.WriteBlock(5, modifiedData);
                }

                // DiffOnly mode
                using (var deltaWriterDiff = new DeltaFileWriter(deltaPathDiff, BLOCK_SIZE, DeltaCompressionMode.DiffOnly))
                {
                    deltaWriterDiff.WriteBlock(5, modifiedData, originalData);
                }

                long fullBlockSize = new FileInfo(deltaPathFull).Length;
                long diffOnlySize = new FileInfo(deltaPathDiff).Length;

                Console.WriteLine($"FullBlock: {fullBlockSize} bytes");
                Console.WriteLine($"DiffOnly: {diffOnlySize} bytes");

                // For complete changes, FullBlock should be more efficient
                Assert.That(fullBlockSize, Is.LessThanOrEqualTo(diffOnlySize),
                    "FullBlock should be better for complete block changes");
            }
            finally
            {
                SafeDeleteFile(deltaPathFull);
                SafeDeleteFile(deltaPathDiff);
            }
        }

        [Test]
        public void DeltaBlockDevice_PreservesOriginalDisk()
        {
            string baseDiskPath = Path.GetTempFileName();
            string deltaPath = Path.GetTempFileName();

            try
            {
                // Create original disk
                CreateTestDiskImage(baseDiskPath, TOTAL_BLOCKS, BLOCK_SIZE, 0x00);

                byte[] originalBlock = CreateTestData(BLOCK_SIZE, 0x00);
                byte[] modifiedBlock = CreateTestData(BLOCK_SIZE, 0xBB);

                using (var baseStream = new FileStream(baseDiskPath, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = new BlockDeviceDescriptor
                    {
                        Name = "TestDevice",
                        BlockSize = BLOCK_SIZE,
                        AddressMode = BlockAddressMode.LBA,
                        TotalBlocks = TOTAL_BLOCKS
                    };
                    var baseDevice = new GenericBlockDevice(descriptor);
                    baseDevice.Mount(baseStream, readOnly: true); // Mount as read-only

                    using (var deltaDevice = new DeltaBlockDevice(baseDevice, deltaPath, DeltaCompressionMode.FullBlock))
                    {
                        // Write to block 5 via delta device
                        deltaDevice.WriteBlock(BlockAddress.FromLBA(5), modifiedBlock);

                        // Read via delta device - should get modified data
                        byte[] readData = new byte[BLOCK_SIZE];
                        deltaDevice.ReadBlock(BlockAddress.FromLBA(5), readData);
                        Assert.That(readData, Is.EqualTo(modifiedBlock), "Delta device should return modified data");
                    }
                }

                // Verify original disk unchanged
                using (var verifyStream = new FileStream(baseDiskPath, FileMode.Open, FileAccess.Read))
                {
                    verifyStream.Position = 5 * BLOCK_SIZE;
                    byte[] diskData = new byte[BLOCK_SIZE];
                    verifyStream.ReadExactly(diskData, 0, BLOCK_SIZE);
                    Assert.That(diskData, Is.EqualTo(originalBlock), "Original disk should be unchanged");
                }
            }
            finally
            {
                SafeDeleteFile(baseDiskPath);
                SafeDeleteFile(deltaPath);
            }
        }

        [Test]
        public void DeltaBlockDevice_MergeCreatesNewImage()
        {
            string baseDiskPath = Path.GetTempFileName();
            string deltaPath = Path.GetTempFileName();
            string mergedPath = Path.GetTempFileName();

            try
            {
                // Create original disk with pattern 0x00
                CreateTestDiskImage(baseDiskPath, TOTAL_BLOCKS, BLOCK_SIZE, 0x00);

                // Modify blocks 5, 10, 15 via delta
                using (var baseStream = new FileStream(baseDiskPath, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = new BlockDeviceDescriptor
                    {
                        Name = "TestDevice",
                        BlockSize = BLOCK_SIZE,
                        AddressMode = BlockAddressMode.LBA,
                        TotalBlocks = TOTAL_BLOCKS
                    };
                    var baseDevice = new GenericBlockDevice(descriptor);
                    baseDevice.Mount(baseStream, readOnly: true);

                    using (var deltaDevice = new DeltaBlockDevice(baseDevice, deltaPath, DeltaCompressionMode.FullBlock))
                    {
                        deltaDevice.WriteBlock(BlockAddress.FromLBA(5), CreateTestData(BLOCK_SIZE, 0xAA));
                        deltaDevice.WriteBlock(BlockAddress.FromLBA(10), CreateTestData(BLOCK_SIZE, 0xBB));
                        deltaDevice.WriteBlock(BlockAddress.FromLBA(15), CreateTestData(BLOCK_SIZE, 0xCC));

                        // Create merged image
                        deltaDevice.CreateMergedImage(mergedPath);
                    }
                }

                // Verify merged image
                using (var mergedStream = new FileStream(mergedPath, FileMode.Open, FileAccess.Read))
                {
                    byte[] buffer = new byte[BLOCK_SIZE];

                    // Block 5 should be 0xAA
                    mergedStream.Position = 5 * BLOCK_SIZE;
                    mergedStream.ReadExactly(buffer, 0, BLOCK_SIZE);
                    Assert.That(buffer[0], Is.EqualTo(0xAA), "Block 5 should be modified");

                    // Block 10 should be 0xBB
                    mergedStream.Position = 10 * BLOCK_SIZE;
                    mergedStream.ReadExactly(buffer, 0, BLOCK_SIZE);
                    Assert.That(buffer[0], Is.EqualTo(0xBB), "Block 10 should be modified");

                    // Block 0 should still be 0x00 (unchanged)
                    mergedStream.Position = 0;
                    mergedStream.ReadExactly(buffer, 0, BLOCK_SIZE);
                    Assert.That(buffer[0], Is.EqualTo(0x00), "Block 0 should be unchanged");
                }
            }
            finally
            {
                SafeDeleteFile(baseDiskPath);
                SafeDeleteFile(deltaPath);
                SafeDeleteFile(mergedPath);
            }
        }

        [Test]
        public void DeltaFile_MultipleBlocks_TrackedCorrectly()
        {
            string deltaPath = Path.GetTempFileName();

            try
            {
                using (var deltaWriter = new DeltaFileWriter(deltaPath, BLOCK_SIZE, DeltaCompressionMode.FullBlock))
                {
                    // Write 10 different blocks
                    for (uint i = 0; i < 10; i++)
                    {
                        byte[] data = CreateTestData(BLOCK_SIZE, (byte)(0xA0 + i));
                        deltaWriter.WriteBlock(i * 10, data); // Blocks 0, 10, 20, 30, ...
                    }

                    Assert.That(deltaWriter.BlockCount, Is.EqualTo(10), "Should track 10 blocks");

                    // Verify all blocks
                    for (uint i = 0; i < 10; i++)
                    {
                        uint blockId = i * 10;
                        Assert.That(deltaWriter.HasBlock(blockId), Is.True, $"Should have block {blockId}");

                        byte[] readData = new byte[BLOCK_SIZE];
                        deltaWriter.ReadBlock(blockId, readData);
                        Assert.That(readData[0], Is.EqualTo(0xA0 + i), $"Block {blockId} data should match");
                    }

                    // Get list of modified blocks
                    uint[] modifiedBlocks = deltaWriter.GetModifiedBlocks();
                    Assert.That(modifiedBlocks.Length, Is.EqualTo(10), "Should return 10 modified blocks");
                    Assert.That(modifiedBlocks, Is.Ordered, "Modified blocks should be sorted");
                }
            }
            finally
            {
                SafeDeleteFile(deltaPath);
            }
        }

        // Helper methods

        private void CreateTestDiskImage(string path, uint totalBlocks, int blockSize, byte fillValue)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                byte[] block = CreateTestData(blockSize, fillValue);
                for (uint i = 0; i < totalBlocks; i++)
                {
                    fs.Write(block, 0, blockSize);
                }
            }
        }

        private byte[] CreateTestData(int size, byte fillValue)
        {
            byte[] data = new byte[size];
            for (int i = 0; i < size; i++)
            {
                data[i] = fillValue;
            }
            return data;
        }
    }
}
