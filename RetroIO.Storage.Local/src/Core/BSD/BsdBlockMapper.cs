//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BSD.Structures;
using RetroIO.Storage.Local.V7.Structures;

namespace RetroIO.Storage.Local.V7.Core
{
    /// <summary>
    /// Maps filesystem blocks to physical sectors and handles indirect block traversal.
    /// Supports both 512-byte and 1024-byte physical sectors.
    /// </summary>
    public class V7BlockMapper
    {
        private readonly IBlockDevice _device;
        private readonly int _physicalSectorSize;
        private readonly int _filesystemBlockSize = V7Constants.FS_BLOCK_SIZE;
        private readonly int _sectorsPerFsBlock;

        /// <summary>
        /// Number of block pointers per indirect block (1024 / 4 = 256).
        /// </summary>
        public int PointersPerBlock => V7Constants.NINDIR;

        public V7BlockMapper(IBlockDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _physicalSectorSize = device.BlockSize;
            _sectorsPerFsBlock = _filesystemBlockSize / _physicalSectorSize;

            ValidateBlockSize();
        }

        private void ValidateBlockSize()
        {
            if (_physicalSectorSize != 512 && _physicalSectorSize != 1024)
            {
                throw new InvalidOperationException(
                    $"BSD filesystem requires 512 or 1024 byte sectors. Device has {_physicalSectorSize} byte sectors.");
            }

            if (_filesystemBlockSize % _physicalSectorSize != 0)
            {
                throw new InvalidOperationException(
                    $"Filesystem block size ({_filesystemBlockSize}) must be " +
                    $"divisible by physical sector size ({_physicalSectorSize})");
            }
        }

        /// <summary>
        /// Filesystem block at which this partition begins, in 1024-byte filesystem blocks.
        ///
        /// Every block number this mapper is handed is PARTITION-RELATIVE — inode block numbers,
        /// the superblock number, and the data-block pointers held in an inode's Addr[] array are
        /// all relative to the start of the filesystem, not the start of the disk. This mapper is
        /// the single place that turns them into absolute device blocks.
        ///
        /// It exists because the offset used to be applied by hand at only SOME call sites:
        /// V7InodeReader and ReadSuperblock added it, but the data-block reads behind
        /// ReadFile/ReadFileData did not. The result was that any partition at a non-zero offset
        /// mounted and reported a correct superblock (label, free counts) yet read its file and
        /// directory data from the wrong place. On the BSD 2.11 clean_install image, partition 'c'
        /// (/usr, offset 32604 sectors) listed 0 root entries while its on-disk root actually holds
        /// 28 subdirectories; partition 'a' was unaffected only because its offset is 0.
        /// </summary>
        public uint PartitionStartBlock { get; set; }

        /// <summary>
        /// Reads one filesystem block (1024 bytes) from the specified partition-relative block.
        /// </summary>
        public void ReadFilesystemBlock(uint fsBlockNum, Span<byte> buffer)
        {
            if (buffer.Length < _filesystemBlockSize)
                throw new ArgumentException($"Buffer must be at least {_filesystemBlockSize} bytes");

            uint absoluteBlock = PartitionStartBlock + fsBlockNum;

            if (_sectorsPerFsBlock == 1)
            {
                // 1024-byte sectors: direct 1:1 mapping
                _device.ReadBlock(BlockAddress.FromLBA(absoluteBlock), buffer);
            }
            else if (_sectorsPerFsBlock == 2)
            {
                // 512-byte sectors: read 2 consecutive sectors
                uint firstSector = absoluteBlock * 2;

                Span<byte> sector1 = buffer.Slice(0, 512);
                Span<byte> sector2 = buffer.Slice(512, 512);

                _device.ReadBlock(BlockAddress.FromLBA(firstSector), sector1);
                _device.ReadBlock(BlockAddress.FromLBA(firstSector + 1), sector2);
            }
        }

        /// <summary>
        /// Writes one filesystem block (1024 bytes) to the specified partition-relative block.
        /// </summary>
        public void WriteFilesystemBlock(uint fsBlockNum, ReadOnlySpan<byte> data)
        {
            if (data.Length != _filesystemBlockSize)
                throw new ArgumentException($"Data must be {_filesystemBlockSize} bytes");

            uint absoluteBlock = PartitionStartBlock + fsBlockNum;

            if (_sectorsPerFsBlock == 1)
            {
                // 1024-byte sectors: direct 1:1 mapping
                _device.WriteBlock(BlockAddress.FromLBA(absoluteBlock), data);
            }
            else if (_sectorsPerFsBlock == 2)
            {
                // 512-byte sectors: write 2 consecutive sectors
                uint firstSector = absoluteBlock * 2;

                ReadOnlySpan<byte> sector1 = data.Slice(0, 512);
                ReadOnlySpan<byte> sector2 = data.Slice(512, 512);

                _device.WriteBlock(BlockAddress.FromLBA(firstSector), sector1);
                _device.WriteBlock(BlockAddress.FromLBA(firstSector + 1), sector2);
            }
        }

        /// <summary>
        /// Maps a file block number to physical block number using inode's addressing scheme.
        /// Handles direct, single indirect, double indirect, and triple indirect blocks.
        /// </summary>
        /// <param name="inode">The inode containing block addresses.</param>
        /// <param name="fileBlockNum">Block number within the file (0-based).</param>
        /// <returns>Physical block number, or 0 if sparse/unallocated.</returns>
        public uint MapFileBlock(V7Inode inode, uint fileBlockNum)
        {
            // Direct blocks (0-3): 4 KB
            if (fileBlockNum < V7Constants.DIRECT_BLOCKS)
            {
                return inode.Addr[fileBlockNum];
            }

            fileBlockNum -= V7Constants.DIRECT_BLOCKS;

            // Single indirect (4-259): 256 KB
            if (fileBlockNum < V7Constants.NINDIR)
            {
                return ReadIndirectBlock(inode.Addr[4], fileBlockNum);
            }

            fileBlockNum -= V7Constants.NINDIR;

            // Double indirect (260-65795): 64 MB
            if (fileBlockNum < (uint)(V7Constants.NINDIR * V7Constants.NINDIR))
            {
                uint level1Index = fileBlockNum / (uint)V7Constants.NINDIR;
                uint level2Index = fileBlockNum % (uint)V7Constants.NINDIR;

                uint level1Block = ReadIndirectBlock(inode.Addr[5], level1Index);
                if (level1Block == 0)
                    return 0;

                return ReadIndirectBlock(level1Block, level2Index);
            }

            fileBlockNum -= (uint)(V7Constants.NINDIR * V7Constants.NINDIR);

            // Triple indirect (65796+): 16 GB
            if (fileBlockNum < (uint)(V7Constants.NINDIR * V7Constants.NINDIR * V7Constants.NINDIR))
            {
                uint level1Index = fileBlockNum / (uint)(V7Constants.NINDIR * V7Constants.NINDIR);
                uint remainder = fileBlockNum % (uint)(V7Constants.NINDIR * V7Constants.NINDIR);
                uint level2Index = remainder / (uint)V7Constants.NINDIR;
                uint level3Index = remainder % (uint)V7Constants.NINDIR;

                uint level1Block = ReadIndirectBlock(inode.Addr[6], level1Index);
                if (level1Block == 0)
                    return 0;

                uint level2Block = ReadIndirectBlock(level1Block, level2Index);
                if (level2Block == 0)
                    return 0;

                return ReadIndirectBlock(level2Block, level3Index);
            }

            // File too large
            throw new InvalidOperationException($"File block number {fileBlockNum} exceeds maximum addressable range");
        }

        /// <summary>
        /// Reads a pointer from an indirect block.
        /// </summary>
        /// <param name="indirectBlockNum">Block number of the indirect block.</param>
        /// <param name="index">Index within the indirect block (0-255).</param>
        /// <returns>Block number stored at that index, or 0 if sparse.</returns>
        private uint ReadIndirectBlock(uint indirectBlockNum, uint index)
        {
            if (indirectBlockNum == 0)
                return 0;

            if (index >= V7Constants.NINDIR)
                throw new ArgumentOutOfRangeException(nameof(index));

            // Read the indirect block
            byte[] indirectData = new byte[_filesystemBlockSize];
            ReadFilesystemBlock(indirectBlockNum, indirectData);

            // Extract the pointer at the specified index (4 bytes per pointer).
            //
            // These are daddr_t values written by a PDP-11, so they use the SAME mixed-endian
            // ("PDP-endian") layout as the direct addresses in the inode: the 32-bit value is
            // stored high-word-first, with each 16-bit word little-endian. BsdInode.Parse already
            // reads i_addr[] through V7EndianHelper.ReadUInt32PDP; reading the pointers INSIDE an
            // indirect block with a plain little-endian BitConverter instead produced a
            // word-swapped address.
            //
            // Observed on the BSD 2.11 clean_install image: listing /dev asked for block
            // 18481152 (0x011A0000) on a 340671-block device. The true value is 0x0000011A = 282
            // - the very same bytes with the two halves swapped. Small directories were unaffected
            // because they fit in the 4 direct blocks and never reach this path.
            int offset = (int)index * 4;
            return V7EndianHelper.ReadUInt32PDP(indirectData, offset);
        }

        /// <summary>
        /// Reads file data starting from specified block and offset.
        /// </summary>
        /// <param name="inode">Inode containing file metadata.</param>
        /// <param name="offset">Byte offset within file.</param>
        /// <param name="count">Number of bytes to read.</param>
        /// <returns>Byte array containing file data.</returns>
        public byte[] ReadFileData(V7Inode inode, uint offset, uint count)
        {
            if (offset + count > inode.Size)
                count = inode.Size - offset;

            if (count == 0)
                return Array.Empty<byte>();

            byte[] result = new byte[count];
            uint bytesRead = 0;
            uint currentOffset = offset;

            while (bytesRead < count)
            {
                uint fileBlockNum = currentOffset / (uint)_filesystemBlockSize;
                int blockOffset = (int)(currentOffset % (uint)_filesystemBlockSize);
                int bytesToRead = Math.Min((int)(count - bytesRead), _filesystemBlockSize - blockOffset);

                uint physicalBlock = MapFileBlock(inode, fileBlockNum);

                if (physicalBlock == 0)
                {
                    // Sparse block - fill with zeros
                    Array.Fill(result, (byte)0, (int)bytesRead, bytesToRead);
                }
                else
                {
                    byte[] blockData = new byte[_filesystemBlockSize];
                    ReadFilesystemBlock(physicalBlock, blockData);
                    Array.Copy(blockData, blockOffset, result, bytesRead, bytesToRead);
                }

                bytesRead += (uint)bytesToRead;
                currentOffset += (uint)bytesToRead;
            }

            return result;
        }

        /// <summary>
        /// Reads entire file contents.
        /// </summary>
        public byte[] ReadFile(V7Inode inode)
        {
            return ReadFileData(inode, 0, inode.Size);
        }
    }
}

