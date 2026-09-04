//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Buffers;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.BlockDevices
{
    /// <summary>
    /// Block device wrapper that overlays a delta file on top of a base device.
    /// Reads from delta if block is modified, otherwise from base device.
    /// All writes go to delta file, preserving original base device.
    /// </summary>
    public class DeltaBlockDevice : IBlockDevice
    {
        private readonly IBlockDevice _baseDevice;
        private readonly DeltaFileWriter _deltaWriter;
        private readonly DeltaCompressionMode _compressionMode;
        private readonly bool _applyDeltaOnMount;

        public DeltaBlockDevice(
            IBlockDevice baseDevice,
            string deltaFilePath,
            DeltaCompressionMode compressionMode = DeltaCompressionMode.FullBlock,
            bool applyDeltaOnMount = true)
        {
            _baseDevice = baseDevice ?? throw new ArgumentNullException(nameof(baseDevice));
            _compressionMode = compressionMode;
            _applyDeltaOnMount = applyDeltaOnMount;

            _deltaWriter = new DeltaFileWriter(
                deltaFilePath,
                baseDevice.BlockSize,
                compressionMode);
        }

        // IBlockDevice properties
        public int BlockSize => _baseDevice.BlockSize;
        public uint TotalBlocks => _baseDevice.TotalBlocks;
        public bool IsReadOnly => false; // Delta allows writes even if base is read-only
        public bool IsMounted => _baseDevice.IsMounted;
        public bool SupportsVariableGeometry => _baseDevice.SupportsVariableGeometry;

        /// <summary>
        /// Gets the underlying delta writer for advanced operations.
        /// </summary>
        public DeltaFileWriter DeltaWriter => _deltaWriter;

        /// <summary>
        /// Number of blocks modified in delta.
        /// </summary>
        public int ModifiedBlockCount => _deltaWriter.BlockCount;

        /// <summary>
        /// Compression ratio (original size / delta size).
        /// </summary>
        public double CompressionRatio => _deltaWriter.CompressionRatio;

        public void Mount(System.IO.Stream imageStream, bool readOnly)
        {
            _baseDevice.Mount(imageStream, readOnly);
        }

        public int ReadBlock(BlockAddress address, Span<byte> buffer)
        {
            uint blockId = address.LBA;

            if (_deltaWriter.HasBlock(blockId))
            {
                // Block modified - read from delta
                ReadOnlySpan<byte> originalData = default;

                if (_compressionMode == DeltaCompressionMode.DiffOnly)
                {
                    // Need original data for diff reconstruction
                    byte[] original = ArrayPool<byte>.Shared.Rent(BlockSize);
                    try
                    {
                        _baseDevice.ReadBlock(address, original);
                        originalData = original.AsSpan(0, BlockSize);
                        return _deltaWriter.ReadBlock(blockId, buffer, originalData);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(original);
                    }
                }
                else
                {
                    // Full block mode - no original data needed
                    return _deltaWriter.ReadBlock(blockId, buffer);
                }
            }
            else
            {
                // Block unchanged - read from base device
                return _baseDevice.ReadBlock(address, buffer);
            }
        }

        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            uint blockId = address.LBA;

            if (_compressionMode == DeltaCompressionMode.DiffOnly)
            {
                // Need original data for diff compression
                byte[] original = ArrayPool<byte>.Shared.Rent(BlockSize);
                try
                {
                    _baseDevice.ReadBlock(address, original);
                    _deltaWriter.WriteBlock(blockId, data, original.AsSpan(0, BlockSize));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(original);
                }
            }
            else
            {
                // Full block mode - no original data needed
                _deltaWriter.WriteBlock(blockId, data);
            }
        }

        public int GetBlocksPerTrack(int track)
        {
            return _baseDevice.GetBlocksPerTrack(track);
        }

        public BlockDeviceInfo GetBlockDeviceInfo()
        {
            return _baseDevice.GetBlockDeviceInfo();
        }

        /// <summary>
        /// Merges delta changes back to the base device.
        /// WARNING: This modifies the original base device!
        /// </summary>
        public void MergeToBase()
        {
            _deltaWriter.MergeToBase(_baseDevice, blockId =>
            {
                // Read original block for diff reconstruction
                byte[] buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
                try
                {
                    var address = BlockAddress.FromLBA(blockId);
                    _baseDevice.ReadBlock(address, buffer);
                    return buffer;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            });
        }

        /// <summary>
        /// Creates a new merged image file combining base + delta.
        /// </summary>
        public void CreateMergedImage(string outputPath)
        {
            _deltaWriter.CreateMergedImage(_baseDevice, outputPath, blockId =>
            {
                // Read original block for diff reconstruction
                byte[] buffer = new byte[BlockSize];
                var address = BlockAddress.FromLBA(blockId);
                _baseDevice.ReadBlock(address, buffer);
                return buffer;
            });
        }

        public void Flush()
        {
            // Flush delta writer and base device
            _deltaWriter?.Flush();
            _baseDevice?.Flush();
        }

        public void Dispose()
        {
            _deltaWriter?.Dispose();
            _baseDevice?.Dispose();
        }
    }
}
