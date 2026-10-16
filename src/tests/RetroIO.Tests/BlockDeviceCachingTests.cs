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

namespace RetroIO.Tests
{
    [TestFixture]
    public class BlockDeviceCachingTests
    {
        private const int BLOCK_SIZE = 512;
        private const uint TOTAL_BLOCKS = 100;

        [Test]
        public void WriteThrough_CachesReadsAndWritesToDiskImmediately()
        {
            string tempFile = Path.GetTempFileName();

            try
            {
                // Create a test disk image
                CreateTestDiskImage(tempFile, TOTAL_BLOCKS, BLOCK_SIZE);

                using (var stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = new BlockDeviceDescriptor
                    {
                        Name = "TestDevice",
                        BlockSize = BLOCK_SIZE,
                        AddressMode = BlockAddressMode.LBA,
                        TotalBlocks = TOTAL_BLOCKS
                    };
                    var baseDevice = new GenericBlockDevice(descriptor);
                    baseDevice.Mount(stream, readOnly: false);

                    var cache = new LruBlockCache(10, BLOCK_SIZE, CacheStrategy.WriteThrough);
                    var cachedDevice = new CachedBlockDevice(baseDevice, cache);

                    // Write to block 5
                    byte[] writeData = CreateTestData(BLOCK_SIZE, 0xAA);
                    cachedDevice.WriteBlock(BlockAddress.FromLBA(5), writeData);

                    // WriteThrough writes to underlying device immediately.
                    // GenericBlockDevice uses memory-mapped I/O, so we need to flush to sync to disk file.
                    baseDevice.Flush();

                    // Verify write went to disk immediately (WriteThrough)
                    stream.Position = 5 * BLOCK_SIZE;
                    byte[] diskData = new byte[BLOCK_SIZE];
                    stream.ReadExactly(diskData, 0, BLOCK_SIZE);
                    Assert.That(diskData, Is.EqualTo(writeData), "WriteThrough should write to disk immediately");

                    // Read from block 5 (should hit cache)
                    byte[] readData = new byte[BLOCK_SIZE];
                    cachedDevice.ReadBlock(BlockAddress.FromLBA(5), readData);
                    Assert.That(readData, Is.EqualTo(writeData), "Read should return cached data");

                    // Verify cache statistics
                    Assert.That(cache.Statistics.Hits, Is.EqualTo(1), "Should have 1 cache hit");
                    Assert.That(cache.Statistics.Writes, Is.EqualTo(1), "Should have 1 write");
                }
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        [Test]
        public void WriteBack_DelaysWritesToDiskUntilFlush()
        {
            string tempFile = Path.GetTempFileName();

            try
            {
                CreateTestDiskImage(tempFile, TOTAL_BLOCKS, BLOCK_SIZE);

                using (var stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = new BlockDeviceDescriptor
                    {
                        Name = "TestDevice",
                        BlockSize = BLOCK_SIZE,
                        AddressMode = BlockAddressMode.LBA,
                        TotalBlocks = TOTAL_BLOCKS
                    };
                    var baseDevice = new GenericBlockDevice(descriptor);
                    baseDevice.Mount(stream, readOnly: false);

                    var cache = new LruBlockCache(10, BLOCK_SIZE, CacheStrategy.WriteBack);
                    var cachedDevice = new CachedBlockDevice(baseDevice, cache);

                    // Write to block 5
                    byte[] writeData = CreateTestData(BLOCK_SIZE, 0xBB);
                    cachedDevice.WriteBlock(BlockAddress.FromLBA(5), writeData);

                    // Verify write did NOT go to disk yet (WriteBack)
                    stream.Position = 5 * BLOCK_SIZE;
                    byte[] diskData = new byte[BLOCK_SIZE];
                    stream.ReadExactly(diskData, 0, BLOCK_SIZE);
                    Assert.That(diskData, Is.Not.EqualTo(writeData), "WriteBack should NOT write to disk immediately");

                    // Flush cache
                    cachedDevice.Flush();

                    // Now verify write went to disk
                    stream.Position = 5 * BLOCK_SIZE;
                    stream.ReadExactly(diskData, 0, BLOCK_SIZE);
                    Assert.That(diskData, Is.EqualTo(writeData), "WriteBack should write to disk after Flush()");

                    // Verify flush statistics
                    Assert.That(cache.Statistics.FlushCount, Is.EqualTo(1), "Should have 1 flush");
                }
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        [Test]
        public void LRU_EvictsOldestBlockWhenCacheFull()
        {
            string tempFile = Path.GetTempFileName();

            try
            {
                CreateTestDiskImage(tempFile, TOTAL_BLOCKS, BLOCK_SIZE);

                using (var stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = new BlockDeviceDescriptor
                    {
                        Name = "TestDevice",
                        BlockSize = BLOCK_SIZE,
                        AddressMode = BlockAddressMode.LBA,
                        TotalBlocks = TOTAL_BLOCKS
                    };
                    var baseDevice = new GenericBlockDevice(descriptor);
                    baseDevice.Mount(stream, readOnly: false);

                    // Small cache: only 3 blocks
                    var cache = new LruBlockCache(3, BLOCK_SIZE, CacheStrategy.WriteThrough);
                    var cachedDevice = new CachedBlockDevice(baseDevice, cache);

                    byte[] buffer = new byte[BLOCK_SIZE];

                    // Fill cache with blocks 0, 1, 2
                    cachedDevice.ReadBlock(BlockAddress.FromLBA(0), buffer);
                    cachedDevice.ReadBlock(BlockAddress.FromLBA(1), buffer);
                    cachedDevice.ReadBlock(BlockAddress.FromLBA(2), buffer);

                    Assert.That(cache.CachedBlocks, Is.EqualTo(3), "Cache should be full");
                    Assert.That(cache.Statistics.Misses, Is.EqualTo(3), "Should have 3 misses");

                    // Read block 3 - should evict block 0 (oldest)
                    cachedDevice.ReadBlock(BlockAddress.FromLBA(3), buffer);
                    Assert.That(cache.Statistics.Evictions, Is.EqualTo(1), "Should have 1 eviction");

                    // Read block 0 again - should be a miss (was evicted)
                    cachedDevice.ReadBlock(BlockAddress.FromLBA(0), buffer);
                    Assert.That(cache.Statistics.Misses, Is.EqualTo(5), "Block 0 should have been evicted");
                }
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        [Test]
        public void CacheHitRate_ImprovesWithRepeatedReads()
        {
            string tempFile = Path.GetTempFileName();

            try
            {
                CreateTestDiskImage(tempFile, TOTAL_BLOCKS, BLOCK_SIZE);

                using (var stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = new BlockDeviceDescriptor
                    {
                        Name = "TestDevice",
                        BlockSize = BLOCK_SIZE,
                        AddressMode = BlockAddressMode.LBA,
                        TotalBlocks = TOTAL_BLOCKS
                    };
                    var baseDevice = new GenericBlockDevice(descriptor);
                    baseDevice.Mount(stream, readOnly: false);

                    var cache = new LruBlockCache(20, BLOCK_SIZE, CacheStrategy.WriteThrough);
                    var cachedDevice = new CachedBlockDevice(baseDevice, cache);

                    byte[] buffer = new byte[BLOCK_SIZE];

                    // Read blocks 0-9 twice each
                    for (int iteration = 0; iteration < 2; iteration++)
                    {
                        for (uint block = 0; block < 10; block++)
                        {
                            cachedDevice.ReadBlock(BlockAddress.FromLBA(block), buffer);
                        }
                    }

                    // First 10 reads = misses, second 10 reads = hits
                    Assert.That(cache.Statistics.Hits, Is.EqualTo(10), "Second iteration should all be cache hits");
                    Assert.That(cache.Statistics.Misses, Is.EqualTo(10), "First iteration should all be cache misses");
                    Assert.That(cache.Statistics.HitRate, Is.EqualTo(0.5).Within(0.01), "Hit rate should be 50%");
                }
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        [Test]
        public void Prefetch_LoadsSequentialBlocksIntoCache()
        {
            string tempFile = Path.GetTempFileName();

            try
            {
                CreateTestDiskImage(tempFile, TOTAL_BLOCKS, BLOCK_SIZE);

                using (var stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = new BlockDeviceDescriptor
                    {
                        Name = "TestDevice",
                        BlockSize = BLOCK_SIZE,
                        AddressMode = BlockAddressMode.LBA,
                        TotalBlocks = TOTAL_BLOCKS
                    };
                    var baseDevice = new GenericBlockDevice(descriptor);
                    baseDevice.Mount(stream, readOnly: false);

                    var cache = new LruBlockCache(20, BLOCK_SIZE, CacheStrategy.WriteThrough);
                    var cachedDevice = new CachedBlockDevice(baseDevice, cache);

                    // Prefetch blocks 10-19
                    cache.Prefetch(BlockAddress.FromLBA(10), 10, baseDevice.ReadBlock);

                    // Reading blocks 10-19 should all be cache hits
                    byte[] buffer = new byte[BLOCK_SIZE];
                    for (uint block = 10; block < 20; block++)
                    {
                        cachedDevice.ReadBlock(BlockAddress.FromLBA(block), buffer);
                    }

                    Assert.That(cache.Statistics.Hits, Is.EqualTo(10), "All prefetched blocks should be cache hits");
                }
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        // Helper methods

        private void CreateTestDiskImage(string path, uint totalBlocks, int blockSize)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                fs.SetLength(totalBlocks * blockSize);

                // Write test pattern
                byte[] block = CreateTestData(blockSize, 0x00);
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
