//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
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

        /// <summary>
        /// Creates a device that keeps all changes in a delta file and leaves the base device unchanged.
        /// </summary>
        /// <param name="baseDevice">
        /// The device that supplies blocks that have not been changed.
        /// </param>
        /// <param name="deltaFilePath">
        /// Path of the delta file.
        /// </param>
        /// <param name="compressionMode">
        /// How changed blocks are stored in the delta file.
        /// </param>
        /// <param name="applyDeltaOnMount">
        /// True to load the blocks of a delta file that already exists at <paramref name="deltaFilePath"/>, so reads
        /// see the earlier changes. False to ignore the existing delta file: the device starts with no changes and the
        /// existing file is replaced by an empty one, so its blocks are lost.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="baseDevice"/> is null.
        /// </exception>
        public DeltaBlockDevice(
            IBlockDevice baseDevice,
            string deltaFilePath,
            DeltaCompressionMode compressionMode = DeltaCompressionMode.FullBlock,
            bool applyDeltaOnMount = true)
        {
            _baseDevice = baseDevice ?? throw new ArgumentNullException(nameof(baseDevice));
            _compressionMode = compressionMode;

            _deltaWriter = new DeltaFileWriter(
                deltaFilePath,
                baseDevice.BlockSize,
                compressionMode,
                applyDeltaOnMount);
        }

        // IBlockDevice properties
        /// <summary>
        /// Gets the block size in bytes of the base device.
        /// </summary>
        public int BlockSize => _baseDevice.BlockSize;
        /// <summary>
        /// Gets the total number of blocks on the base device.
        /// </summary>
        public uint TotalBlocks => _baseDevice.TotalBlocks;
        /// <summary>
        /// Always false, because writes go to the delta file even when the base device is read-only.
        /// </summary>
        public bool IsReadOnly => false; // Delta allows writes even if base is read-only
        /// <summary>
        /// Gets whether the base device is mounted and ready for reads and writes.
        /// </summary>
        public bool IsMounted => _baseDevice.IsMounted;
        /// <summary>
        /// Gets whether the base device has a different number of sectors on some tracks.
        /// </summary>
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

        /// <summary>
        /// Mounts the base device.
        /// </summary>
        /// <param name="imageStream">
        /// The stream holding the disk image.
        /// </param>
        /// <param name="readOnly">
        /// True to mount the base device read-only.
        /// </param>
        public void Mount(System.IO.Stream imageStream, bool readOnly)
        {
            _baseDevice.Mount(imageStream, readOnly);
        }

        /// <summary>
        /// Reads a block from the delta file if it was changed, otherwise from the base device.
        /// </summary>
        /// <param name="address">
        /// The block to read.
        /// </param>
        /// <param name="buffer">
        /// Receives the block data.
        /// </param>
        /// <returns>
        /// The number of bytes read.
        /// </returns>
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

        /// <summary>
        /// Writes a block to the delta file. The base device is not written.
        /// </summary>
        /// <param name="address">
        /// The block to write.
        /// </param>
        /// <param name="data">
        /// The block data.
        /// </param>
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

        /// <summary>
        /// Gets the number of blocks on a track, as reported by the base device.
        /// </summary>
        /// <param name="track">
        /// The track number, numbered the way the base device numbers them.
        /// </param>
        /// <returns>
        /// The number of blocks (sectors) on <paramref name="track"/>.
        /// </returns>
        public int GetBlocksPerTrack(int track)
        {
            return _baseDevice.GetBlocksPerTrack(track);
        }

        /// <summary>
        /// Gets the block device information of the base device.
        /// </summary>
        /// <returns>
        /// The information the base device returns, unchanged.
        /// </returns>
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

        /// <summary>
        /// Flushes the delta writer and the base device.
        /// </summary>
        public void Flush()
        {
            // Flush delta writer and base device
            _deltaWriter?.Flush();
            _baseDevice?.Flush();
        }

        /// <summary>
        /// Disposes the delta writer and the base device.
        /// </summary>
        public void Dispose()
        {
            _deltaWriter?.Dispose();
            _baseDevice?.Dispose();
        }
    }
}
