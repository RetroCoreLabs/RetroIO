//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.BlockDevices
{
    /// <summary>
    /// Wraps an IBlockDevice with caching capabilities.
    /// Transparent caching layer for any block device.
    /// </summary>
    public class CachedBlockDevice : IBlockDevice
    {
        private readonly IBlockDevice _innerDevice;
        private readonly IBlockCache _cache;

        /// <summary>
        /// Creates a cached device over the given device using the given cache.
        /// </summary>
        /// <param name="innerDevice">
        /// The device that does the real reads and writes.
        /// </param>
        /// <param name="cache">
        /// The cache to put in front of the device.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="innerDevice"/> or <paramref name="cache"/> is null.
        /// </exception>
        public CachedBlockDevice(IBlockDevice innerDevice, IBlockCache cache)
        {
            _innerDevice = innerDevice ?? throw new ArgumentNullException(nameof(innerDevice));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        /// <summary>
        /// Creates a cached device and a new <see cref="LruBlockCache"/> sized to the block size of the inner device.
        /// </summary>
        /// <param name="innerDevice">
        /// The device that does the real reads and writes.
        /// </param>
        /// <param name="maxCachedBlocks">
        /// The most blocks the new cache keeps.
        /// </param>
        /// <param name="strategy">
        /// The write strategy of the new cache.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="innerDevice"/> is null.
        /// </exception>
        public CachedBlockDevice(IBlockDevice innerDevice, int maxCachedBlocks = 256,
                                 CacheStrategy strategy = CacheStrategy.WriteThrough)
        {
            _innerDevice = innerDevice ?? throw new ArgumentNullException(nameof(innerDevice));
            _cache = new LruBlockCache(maxCachedBlocks, innerDevice.BlockSize, strategy);
        }

        // Expose cache for management
        /// <summary>
        /// Gets the cache used by this device.
        /// </summary>
        public IBlockCache Cache => _cache;

        // IBlockDevice passthrough properties
        /// <summary>
        /// Gets the block size in bytes of the inner device.
        /// </summary>
        public int BlockSize => _innerDevice.BlockSize;
        /// <summary>
        /// Gets the total number of blocks on the inner device.
        /// </summary>
        public uint TotalBlocks => _innerDevice.TotalBlocks;
        /// <summary>
        /// Gets whether the inner device is mounted read-only.
        /// </summary>
        public bool IsReadOnly => _innerDevice.IsReadOnly;
        /// <summary>
        /// Gets whether the inner device is mounted and ready for reads and writes.
        /// </summary>
        public bool IsMounted => _innerDevice.IsMounted;
        /// <summary>
        /// Gets whether the inner device has a different number of sectors on some tracks.
        /// </summary>
        public bool SupportsVariableGeometry => _innerDevice.SupportsVariableGeometry;

        /// <summary>
        /// Clears the cache and mounts the inner device.
        /// </summary>
        /// <param name="imageStream">
        /// The stream holding the disk image.
        /// </param>
        /// <param name="readOnly">
        /// True to mount read-only.
        /// </param>
        public void Mount(Stream imageStream, bool readOnly)
        {
            _cache.InvalidateAll();
            _innerDevice.Mount(imageStream, readOnly);
        }

        /// <summary>
        /// Reads a block through the cache.
        /// </summary>
        /// <param name="address">
        /// The block to read.
        /// </param>
        /// <param name="buffer">
        /// Receives the block data.
        /// </param>
        /// <returns>
        /// The smaller of the buffer length and the block size.
        /// </returns>
        public int ReadBlock(BlockAddress address, Span<byte> buffer)
        {
            _cache.TryReadBlock(address, buffer, _innerDevice.ReadBlock);
            return Math.Min(buffer.Length, _innerDevice.BlockSize);
        }

        /// <summary>
        /// Writes a block through the cache, following the write strategy of the cache.
        /// </summary>
        /// <param name="address">
        /// The block to write.
        /// </param>
        /// <param name="data">
        /// The block data.
        /// </param>
        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            _cache.WriteBlock(address, data, _innerDevice.WriteBlock);
        }

        /// <summary>
        /// Gets the number of blocks on a track, as reported by the inner device.
        /// </summary>
        /// <param name="track">
        /// The track number, numbered the way the inner device numbers them.
        /// </param>
        /// <returns>
        /// The number of blocks (sectors) on <paramref name="track"/>.
        /// </returns>
        public int GetBlocksPerTrack(int track)
        {
            return _innerDevice.GetBlocksPerTrack(track);
        }

        /// <summary>
        /// Gets the block device information of the inner device.
        /// </summary>
        /// <returns>
        /// The information the inner device returns; the cache does not change it.
        /// </returns>
        public BlockDeviceInfo GetBlockDeviceInfo()
        {
            return _innerDevice.GetBlockDeviceInfo();
        }

        /// <summary>
        /// Writes the pending blocks of the cache to the inner device, then flushes the inner device.
        /// </summary>
        public void Flush()
        {
            _cache.Flush(_innerDevice.WriteBlock);
            _innerDevice.Flush();
        }

        /// <summary>
        /// Writes the pending blocks of the cache to the inner device, then disposes the cache and the inner device.
        /// </summary>
        public void Dispose()
        {
            _cache.Flush(_innerDevice.WriteBlock);
            _cache.Dispose();
            _innerDevice.Dispose();
        }
    }
}
