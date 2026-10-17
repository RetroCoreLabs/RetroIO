//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.BlockDevices
{
    /// <summary>
    /// Manages delta/overlay files that track only changed blocks.
    /// Supports both full-block and diff-only compression modes.
    /// </summary>
    /// <remarks>
    /// Delta file format:
    /// Header: [Magic:4][Version:2][BlockSize:4][CompressionMode:1][BlockCount:4] (15 bytes)
    /// Data: [Block Data or Diff Data] * BlockCount (starts at offset 15)
    /// Index: [BlockId:4][FileOffset:8][CompressedSize:4] * BlockCount (at end of file)
    ///
    /// Index is stored at end of file to allow efficient appending of new blocks.
    /// Index position = file_length - (BlockCount * 16)
    /// </remarks>
    public class DeltaFileWriter : IDisposable
    {
        private const uint DELTA_MAGIC = 0x44454C54; // "DELT"
        private const ushort DELTA_VERSION = 2; // Version 2 adds diff support

        private readonly string _deltaPath;
        private readonly int _blockSize;
        private readonly DeltaCompressionMode _compressionMode;
        private readonly Dictionary<uint, DeltaBlockEntry> _blockIndex;
        private FileStream? _deltaStream;
        private readonly object _lock = new object();
        private long _dataEndOffset; // Tracks where data region ends (index starts here)

        /// <summary>
        /// Number of blocks in delta file.
        /// </summary>
        public int BlockCount
        {
            get
            {
                lock (_lock) return _blockIndex.Count;
            }
        }

        /// <summary>
        /// Total size of delta file in bytes.
        /// </summary>
        public long DeltaFileSize => _deltaStream?.Length ?? 0;

        /// <summary>
        /// Compression ratio (original size / delta size).
        /// </summary>
        public double CompressionRatio
        {
            get
            {
                lock (_lock)
                {
                    if (_blockIndex.Count == 0) return 1.0;
                    long originalSize = (long)_blockIndex.Count * _blockSize;
                    long deltaSize = DeltaFileSize;
                    return deltaSize > 0 ? (double)originalSize / deltaSize : 1.0;
                }
            }
        }

        public DeltaFileWriter(string deltaPath, int blockSize, DeltaCompressionMode compressionMode = DeltaCompressionMode.FullBlock)
        {
            _deltaPath = deltaPath ?? throw new ArgumentNullException(nameof(deltaPath));
            _blockSize = blockSize;
            _compressionMode = compressionMode;
            _blockIndex = new Dictionary<uint, DeltaBlockEntry>();

            OpenOrCreateDeltaFile();
        }

        private void OpenOrCreateDeltaFile()
        {
            // Check if file exists AND has content (is a valid delta file)
            bool isExistingDelta = false;
            if (File.Exists(_deltaPath))
            {
                var fileInfo = new FileInfo(_deltaPath);
                // A valid delta file must have at least the header (15 bytes)
                isExistingDelta = fileInfo.Length >= 15;
            }

            if (isExistingDelta)
            {
                // Load existing delta file
                _deltaStream = new FileStream(_deltaPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                LoadIndex();
            }
            else
            {
                // Create new delta file (or overwrite empty placeholder file from GetTempFileName)
                _deltaStream = new FileStream(_deltaPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
                WriteHeader();
            }
        }

        private void WriteHeader()
        {
            if (_deltaStream == null) return;

            _deltaStream.Position = 0;
            using (var writer = new BinaryWriter(_deltaStream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(DELTA_MAGIC);
                writer.Write(DELTA_VERSION);
                writer.Write(_blockSize);
                writer.Write((byte)_compressionMode);
                writer.Write(0); // Initial block count
            }
            _dataEndOffset = 15; // Header size - data starts immediately after
        }

        private void LoadIndex()
        {
            if (_deltaStream == null) return;

            _deltaStream.Position = 0;
            using (var reader = new BinaryReader(_deltaStream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                uint magic = reader.ReadUInt32();
                if (magic != DELTA_MAGIC)
                    throw new InvalidDataException("Invalid delta file format");

                ushort version = reader.ReadUInt16();
                if (version < 1 || version > DELTA_VERSION)
                    throw new InvalidDataException($"Unsupported delta version: {version}");

                int blockSize = reader.ReadInt32();
                if (blockSize != _blockSize)
                    throw new InvalidDataException($"Block size mismatch: expected {_blockSize}, got {blockSize}");

                byte compressionByte = reader.ReadByte();
                var fileCompressionMode = (DeltaCompressionMode)compressionByte;
                if (fileCompressionMode != _compressionMode)
                    throw new InvalidDataException($"Compression mode mismatch: expected {_compressionMode}, got {fileCompressionMode}");

                int blockCount = reader.ReadInt32();

                // Index is stored at end of file: file_length - (blockCount * 16)
                long indexSize = blockCount * 16L;
                long indexOffset = _deltaStream.Length - indexSize;
                _dataEndOffset = indexOffset; // Data region ends where index starts

                if (blockCount > 0 && indexOffset >= 15)
                {
                    _deltaStream.Position = indexOffset;

                    for (int i = 0; i < blockCount; i++)
                    {
                        uint blockId = reader.ReadUInt32();
                        long offset = reader.ReadInt64();
                        int compressedSize = reader.ReadInt32();

                        _blockIndex[blockId] = new DeltaBlockEntry
                        {
                            BlockId = blockId,
                            FileOffset = offset,
                            CompressedSize = compressedSize
                        };
                    }
                }
                else
                {
                    // No blocks yet, data starts after header
                    _dataEndOffset = 15;
                }
            }
        }

        /// <summary>
        /// Writes a modified block to the delta file.
        /// </summary>
        /// <param name="blockId">Block ID (LBA)</param>
        /// <param name="newData">New block data</param>
        /// <param name="originalData">Original block data (required for DiffOnly mode)</param>
        public void WriteBlock(uint blockId, ReadOnlySpan<byte> newData, ReadOnlySpan<byte> originalData = default)
        {
            if (newData.Length != _blockSize)
                throw new ArgumentException($"Data size must be {_blockSize} bytes", nameof(newData));

            if (_compressionMode == DeltaCompressionMode.DiffOnly && originalData.IsEmpty)
                throw new ArgumentException("Original data required for DiffOnly compression mode", nameof(originalData));

            lock (_lock)
            {
                if (_deltaStream == null)
                    throw new ObjectDisposedException(nameof(DeltaFileWriter));

                byte[] compressedData;
                int compressedSize;

                if (_compressionMode == DeltaCompressionMode.FullBlock)
                {
                    // Full block mode: just store the entire block
                    compressedData = newData.ToArray();
                    compressedSize = _blockSize;
                }
                else
                {
                    // Diff-only mode: compute and store only differences
                    compressedData = ComputeDiff(originalData, newData, out compressedSize);
                }

                // Truncate file to data region only (removes any previously written index)
                _deltaStream.SetLength(_dataEndOffset);

                long fileOffset;

                if (_blockIndex.TryGetValue(blockId, out var existing))
                {
                    // Overwrite existing block at same location
                    fileOffset = existing.FileOffset;
                    _deltaStream.Position = fileOffset;
                }
                else
                {
                    // Append new block at end of data region
                    fileOffset = _dataEndOffset;
                    _deltaStream.Position = fileOffset;
                }

                // Write compressed data
                _deltaStream.Write(compressedData, 0, compressedSize);

                // Update data end offset if we extended the data region
                if (_deltaStream.Position > _dataEndOffset)
                {
                    _dataEndOffset = _deltaStream.Position;
                }

                // Update index in memory
                _blockIndex[blockId] = new DeltaBlockEntry
                {
                    BlockId = blockId,
                    FileOffset = fileOffset,
                    CompressedSize = compressedSize
                };

                // Update block count in header
                UpdateBlockCount();
            }
        }

        /// <summary>
        /// Reads a block from the delta file.
        /// </summary>
        /// <param name="blockId">Block ID (LBA)</param>
        /// <param name="buffer">Buffer to receive block data</param>
        /// <param name="originalData">Original block data (required for DiffOnly mode)</param>
        /// <returns>Number of bytes read, or 0 if block not in delta</returns>
        public int ReadBlock(uint blockId, Span<byte> buffer, ReadOnlySpan<byte> originalData = default)
        {
            if (buffer.Length < _blockSize)
                throw new ArgumentException($"Buffer must be at least {_blockSize} bytes", nameof(buffer));

            lock (_lock)
            {
                if (_deltaStream == null)
                    throw new ObjectDisposedException(nameof(DeltaFileWriter));

                if (!_blockIndex.TryGetValue(blockId, out var entry))
                    return 0; // Block not in delta

                _deltaStream.Position = entry.FileOffset;

                if (_compressionMode == DeltaCompressionMode.FullBlock)
                {
                    // Full block mode: read entire block
                    return _deltaStream.Read(buffer.Slice(0, _blockSize));
                }
                else
                {
                    // Diff-only mode: read diff and apply to original
                    if (originalData.IsEmpty)
                        throw new ArgumentException("Original data required for DiffOnly compression mode", nameof(originalData));

                    byte[] compressedData = ArrayPool<byte>.Shared.Rent(entry.CompressedSize);
                    try
                    {
                        _deltaStream.ReadExactly(compressedData, 0, entry.CompressedSize);
                        ApplyDiff(originalData, compressedData.AsSpan(0, entry.CompressedSize), buffer);
                        return _blockSize;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(compressedData);
                    }
                }
            }
        }

        /// <summary>
        /// Checks if a block exists in the delta file.
        /// </summary>
        public bool HasBlock(uint blockId)
        {
            lock (_lock)
            {
                return _blockIndex.ContainsKey(blockId);
            }
        }

        /// <summary>
        /// Gets all block IDs in the delta file.
        /// </summary>
        public uint[] GetModifiedBlocks()
        {
            lock (_lock)
            {
                var blocks = new uint[_blockIndex.Count];
                int i = 0;
                foreach (var blockId in _blockIndex.Keys)
                {
                    blocks[i++] = blockId;
                }
                Array.Sort(blocks);
                return blocks;
            }
        }

        /// <summary>
        /// Merges delta file back to a base device.
        /// </summary>
        /// <param name="baseDevice">Base block device to merge changes into</param>
        /// <param name="readOriginalBlock">Function to read original block data (for DiffOnly mode)</param>
        public void MergeToBase(IBlockDevice baseDevice, Func<uint, byte[]>? readOriginalBlock = null)
        {
            if (baseDevice == null)
                throw new ArgumentNullException(nameof(baseDevice));

            if (_compressionMode == DeltaCompressionMode.DiffOnly && readOriginalBlock == null)
                throw new ArgumentNullException(nameof(readOriginalBlock), "Required for DiffOnly compression mode");

            lock (_lock)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
                try
                {
                    foreach (var blockId in GetModifiedBlocks())
                    {
                        ReadOnlySpan<byte> originalData = default;
                        if (_compressionMode == DeltaCompressionMode.DiffOnly)
                        {
                            originalData = readOriginalBlock!(blockId);
                        }

                        int bytesRead = ReadBlock(blockId, buffer, originalData);
                        if (bytesRead > 0)
                        {
                            var address = BlockAddress.FromLBA(blockId);
                            baseDevice.WriteBlock(address, buffer.AsSpan(0, bytesRead));
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        /// <summary>
        /// Creates a new merged image file combining base + delta.
        /// </summary>
        public void CreateMergedImage(IBlockDevice baseDevice, string outputPath, Func<uint, byte[]>? readOriginalBlock = null)
        {
            if (baseDevice == null)
                throw new ArgumentNullException(nameof(baseDevice));

            using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(_blockSize);

            try
            {
                for (uint blockId = 0; blockId < baseDevice.TotalBlocks; blockId++)
                {
                    if (HasBlock(blockId))
                    {
                        // Read from delta
                        ReadOnlySpan<byte> originalData = default;
                        if (_compressionMode == DeltaCompressionMode.DiffOnly)
                        {
                            originalData = readOriginalBlock!(blockId);
                        }

                        ReadBlock(blockId, buffer, originalData);
                    }
                    else
                    {
                        // Read from base
                        var address = BlockAddress.FromLBA(blockId);
                        baseDevice.ReadBlock(address, buffer);
                    }

                    output.Write(buffer, 0, _blockSize);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private void UpdateBlockCount()
        {
            if (_deltaStream == null) return;

            long currentPos = _deltaStream.Position;
            _deltaStream.Position = 11; // Offset of BlockCount field
            using (var writer = new BinaryWriter(_deltaStream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(_blockIndex.Count);
            }
            _deltaStream.Position = currentPos;
            _deltaStream.Flush();
        }

        /// <summary>
        /// Computes byte-level diff between original and new data.
        /// Format: [RunLength encoding] + [Changed byte positions]
        /// </summary>
        private byte[] ComputeDiff(ReadOnlySpan<byte> original, ReadOnlySpan<byte> modified, out int compressedSize)
        {
            var diffBuffer = new List<byte>();

            // Simple run-length encoding of changes
            int i = 0;
            while (i < _blockSize)
            {
                if (original[i] != modified[i])
                {
                    // Found a difference - record position and changed bytes
                    int startPos = i;
                    int changeCount = 0;

                    // Count consecutive changes
                    while (i < _blockSize && original[i] != modified[i] && changeCount < 255)
                    {
                        changeCount++;
                        i++;
                    }

                    // Write: [Position:2][Count:1][Data:Count]
                    diffBuffer.Add((byte)(startPos >> 8));
                    diffBuffer.Add((byte)(startPos & 0xFF));
                    diffBuffer.Add((byte)changeCount);
                    for (int j = 0; j < changeCount; j++)
                    {
                        diffBuffer.Add(modified[startPos + j]);
                    }
                }
                else
                {
                    i++;
                }
            }

            // End marker
            diffBuffer.Add(0xFF);
            diffBuffer.Add(0xFF);

            compressedSize = diffBuffer.Count;
            return diffBuffer.ToArray();
        }

        /// <summary>
        /// Applies byte-level diff to original data.
        /// </summary>
        private void ApplyDiff(ReadOnlySpan<byte> original, ReadOnlySpan<byte> diff, Span<byte> output)
        {
            // Copy original to output first
            original.CopyTo(output);

            int diffPos = 0;
            while (diffPos < diff.Length - 1)
            {
                ushort position = (ushort)((diff[diffPos] << 8) | diff[diffPos + 1]);
                diffPos += 2;

                // Check for end marker
                if (position == 0xFFFF)
                    break;

                byte count = diff[diffPos++];

                // Apply changes
                for (int i = 0; i < count; i++)
                {
                    output[position + i] = diff[diffPos++];
                }
            }
        }

        /// <summary>
        /// Writes the in-memory block index to the delta file at the end of the data region.
        /// </summary>
        private void WriteIndex()
        {
            if (_deltaStream == null) return;
            if (_blockIndex.Count == 0) return;

            // Position at end of data region
            _deltaStream.Position = _dataEndOffset;

            using (var writer = new BinaryWriter(_deltaStream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                // Write each index entry: BlockId(4) + FileOffset(8) + CompressedSize(4) = 16 bytes each
                foreach (var entry in _blockIndex.Values)
                {
                    writer.Write(entry.BlockId);
                    writer.Write(entry.FileOffset);
                    writer.Write(entry.CompressedSize);
                }
            }
        }

        /// <summary>
        /// Flushes any pending changes to the delta file.
        /// </summary>
        public void Flush()
        {
            lock (_lock)
            {
                if (_deltaStream == null) return;

                // Write index to file (at end of data region)
                WriteIndex();
                _deltaStream.Flush();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_deltaStream != null)
                {
                    // Write index to file before closing
                    WriteIndex();
                    _deltaStream.Flush();
                    _deltaStream.Dispose();
                    _deltaStream = null;
                }
            }
        }

        private class DeltaBlockEntry
        {
            public uint BlockId { get; set; }
            public long FileOffset { get; set; }
            public int CompressedSize { get; set; }
        }
    }
}
