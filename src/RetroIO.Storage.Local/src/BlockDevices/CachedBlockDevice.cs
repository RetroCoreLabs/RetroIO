//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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

        public CachedBlockDevice(IBlockDevice innerDevice, IBlockCache cache)
        {
            _innerDevice = innerDevice ?? throw new ArgumentNullException(nameof(innerDevice));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        public CachedBlockDevice(IBlockDevice innerDevice, int maxCachedBlocks = 256,
                                 CacheStrategy strategy = CacheStrategy.WriteThrough)
        {
            _innerDevice = innerDevice ?? throw new ArgumentNullException(nameof(innerDevice));
            _cache = new LruBlockCache(maxCachedBlocks, innerDevice.BlockSize, strategy);
        }

        // Expose cache for management
        public IBlockCache Cache => _cache;

        // IBlockDevice passthrough properties
        public int BlockSize => _innerDevice.BlockSize;
        public uint TotalBlocks => _innerDevice.TotalBlocks;
        public bool IsReadOnly => _innerDevice.IsReadOnly;
        public bool IsMounted => _innerDevice.IsMounted;
        public bool SupportsVariableGeometry => _innerDevice.SupportsVariableGeometry;

        public void Mount(Stream imageStream, bool readOnly)
        {
            _cache.InvalidateAll();
            _innerDevice.Mount(imageStream, readOnly);
        }

        public int ReadBlock(BlockAddress address, Span<byte> buffer)
        {
            _cache.TryReadBlock(address, buffer, _innerDevice.ReadBlock);
            return Math.Min(buffer.Length, _innerDevice.BlockSize);
        }

        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            _cache.WriteBlock(address, data, _innerDevice.WriteBlock);
        }

        public int GetBlocksPerTrack(int track)
        {
            return _innerDevice.GetBlocksPerTrack(track);
        }

        public BlockDeviceInfo GetBlockDeviceInfo()
        {
            return _innerDevice.GetBlockDeviceInfo();
        }

        public void Flush()
        {
            _cache.Flush(_innerDevice.WriteBlock);
            _innerDevice.Flush();
        }

        public void Dispose()
        {
            _cache.Flush(_innerDevice.WriteBlock);
            _cache.Dispose();
            _innerDevice.Dispose();
        }
    }
}
