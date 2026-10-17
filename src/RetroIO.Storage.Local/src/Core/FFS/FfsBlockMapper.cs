//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BSD.Structures;
using RetroIO.Storage.Local.FFS.Structures;

namespace RetroIO.Storage.Local.FFS.Core
{
    /// <summary>
    /// Maps FFS filesystem blocks to physical sectors and handles indirect block traversal.
    /// Supports fragment-level and block-level I/O with triple indirect addressing.
    /// </summary>
    public class FfsBlockMapper
    {
        private readonly IBlockDevice _device;
        private readonly FfsSuperblock _superblock;
        private readonly int _blockSize;
        private readonly int _fragSize;
        private readonly int _fragsPerBlock;
        private readonly int _pointersPerBlock;

        /// <summary>
        /// Creates a block mapper and takes block size, fragment size and fragments per block from the superblock.
        /// </summary>
        /// <param name="device">
        /// The device that holds the file system.
        /// </param>
        /// <param name="superblock">
        /// The superblock of the file system.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="device"/> or <paramref name="superblock"/> is null.
        /// </exception>
        public FfsBlockMapper(IBlockDevice device, FfsSuperblock superblock)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _superblock = superblock ?? throw new ArgumentNullException(nameof(superblock));

            _blockSize = (int)superblock.fs_bsize;
            _fragSize = (int)superblock.fs_fsize;
            _fragsPerBlock = (int)superblock.fs_frag;
            _pointersPerBlock = FfsConstants.PointersPerBlock(_blockSize);
        }

        /// <summary>
        /// Reads one filesystem block starting from the specified fragment address.
        /// </summary>
        /// <param name="startFragNum">
        /// Starting fragment address (will read _fragsPerBlock fragments).
        /// </param>
        /// <param name="buffer">
        /// Buffer to read into (must be at least _blockSize bytes).
        /// </param>
        /// <param name="bytesNeeded">
        /// How many bytes of the block the caller actually needs, or -1 for the whole block.
        /// Sectors beyond this are not read and are zero-filled instead.
        /// </param>
        public void ReadFilesystemBlock(uint startFragNum, Span<byte> buffer, int bytesNeeded = -1)
        {
            if (buffer.Length < _blockSize)
                throw new ArgumentException($"Buffer must be at least {_blockSize} bytes");

            // Convert starting fragment to device sectors
            // NOTE: startFragNum is already a fragment address from inode, don't multiply!
            uint sectorNum = FragToSector(startFragNum);

            int sectorSize = _device.BlockSize;
            int sectorsPerBlock = _blockSize / sectorSize;

            // FFS allocates the TAIL of a file in fragments, not in whole blocks. A file whose
            // last block is partial may own only 1..frag-1 fragments there, so blindly reading a
            // full _blockSize from that address runs past the end of what the file owns. When the
            // tail happens to sit at the end of the filesystem that overrun also runs past the end
            // of the partition and the device throws.
            //
            // Observed on a real SunOS pack: /lib/fonts on partition 'g' (88893 sectors) has its
            // tail at fragment 44444. A full-block read spans sectors 88888..88903, and 88893 is
            // the first sector past the partition - which is exactly the address that failed.
            //
            // So: read only the sectors the caller asked for, and never past the device. Anything
            // not read is zeroed, which is what a short tail should look like to the caller.
            if (bytesNeeded < 0 || bytesNeeded > _blockSize)
            {
                bytesNeeded = _blockSize;
            }

            int sectorsNeeded = (bytesNeeded + sectorSize - 1) / sectorSize;
            uint totalSectors = _device.TotalBlocks;

            for (int i = 0; i < sectorsPerBlock; i++)
            {
                int bufferOffset = i * sectorSize;
                Span<byte> sectorBuffer = buffer.Slice(bufferOffset, sectorSize);

                if (i >= sectorsNeeded || (totalSectors > 0 && sectorNum + (uint)i >= totalSectors))
                {
                    sectorBuffer.Clear();
                    continue;
                }

                _device.ReadBlock(BlockAddress.FromLBA(sectorNum + (uint)i), sectorBuffer);
            }
        }

        /// <summary>
        /// Reads one fragment from the specified fragment number.
        /// </summary>
        public void ReadFragment(uint fragNum, Span<byte> buffer)
        {
            if (buffer.Length < _fragSize)
                throw new ArgumentException($"Buffer must be at least {_fragSize} bytes");

            uint sectorNum = FragToSector(fragNum);

            // Read sectors for this fragment
            int sectorsPerFrag = _fragSize / _device.BlockSize;
            for (int i = 0; i < sectorsPerFrag; i++)
            {
                int bufferOffset = i * _device.BlockSize;
                Span<byte> sectorBuffer = buffer.Slice(bufferOffset, _device.BlockSize);
                _device.ReadBlock(BlockAddress.FromLBA(sectorNum + (uint)i), sectorBuffer);
            }
        }

        /// <summary>
        /// Converts fragment number to sector number.
        ///
        /// In FFS, fragment addresses from inodes (di_db[], di_ib[]) are stored as
        /// "filesystem blocks" that need conversion to DEV_BSIZE (512-byte) sectors.
        /// According to SunOS fs.h: fsbtodb(fs, b) is b shifted left by fs_fsbtodb bits
        ///
        /// When using PartitionBlockDevice, addresses are partition-relative.
        /// </summary>
        private uint FragToSector(uint fragNum)
        {
            // Convert using fs_fsbtodb shift (equivalent to multiply by sectorsPerFrag)
            // For typical FFS: fs_fsbtodb = 1 or 2
            // If fs_fsize = 1024 bytes, sectorsPerFrag = 1024/512 = 2 (shift by 1)
            // If fs_fsize = 512 bytes, sectorsPerFrag = 512/512 = 1 (shift by 0)
            int sectorsPerFrag = _fragSize / _device.BlockSize;
            uint result = fragNum * (uint)sectorsPerFrag;

            return result;
        }

        /// <summary>
        /// Maps a file block number to physical fragment address using inode's addressing scheme.
        /// Handles direct, single indirect, double indirect, and triple indirect blocks.
        ///
        /// IMPORTANT: Inode block addresses (di_db, di_ib) are FRAGMENT addresses.
        /// This method returns fragment addresses directly without conversion.
        /// </summary>
        /// <param name="inode">
        /// The inode containing block addresses.
        /// </param>
        /// <param name="fileBlockNum">
        /// Block number within the file (0-based).
        /// </param>
        /// <returns>
        /// Physical fragment address, or 0 if sparse/unallocated.
        /// </returns>
        public uint MapFileBlock(FfsInode inode, uint fileBlockNum)
        {
            // Direct blocks (0-11): 12 × blocksize (48 KB for 4K blocks, 96 KB for 8K blocks)
            // IMPORTANT: di_db[] contains FRAGMENT addresses - return them as-is!
            if (fileBlockNum < FfsConstants.NDADDR)
            {
                uint fragAddr = inode.di_db[fileBlockNum];

                return fragAddr;
            }

            fileBlockNum -= FfsConstants.NDADDR;

            // Single indirect (12): pointersPerBlock blocks
            // For 4K blocks: 1024 pointers = 4 MB
            // For 8K blocks: 2048 pointers = 16 MB
            if (fileBlockNum < _pointersPerBlock)
            {
                return ReadIndirectBlock(inode.di_ib[0], fileBlockNum);
            }

            fileBlockNum -= (uint)_pointersPerBlock;

            // Double indirect (13): pointersPerBlock × pointersPerBlock blocks
            // For 4K blocks: 1024 × 1024 = 1048576 blocks = 4 GB
            // For 8K blocks: 2048 × 2048 = 4194304 blocks = 32 GB
            if (fileBlockNum < (uint)(_pointersPerBlock * _pointersPerBlock))
            {
                uint level1Index = fileBlockNum / (uint)_pointersPerBlock;
                uint level2Index = fileBlockNum % (uint)_pointersPerBlock;

                uint level1Block = ReadIndirectBlock(inode.di_ib[1], level1Index);
                if (level1Block == 0)
                    return 0;

                return ReadIndirectBlock(level1Block, level2Index);
            }

            fileBlockNum -= (uint)(_pointersPerBlock * _pointersPerBlock);

            // Triple indirect (14): pointersPerBlock³ blocks
            // For 4K blocks: 1024³ = 1073741824 blocks = 4 TB
            // For 8K blocks: 2048³ = 8589934592 blocks = 64 TB
            if (fileBlockNum < (ulong)_pointersPerBlock * (ulong)_pointersPerBlock * (ulong)_pointersPerBlock)
            {
                uint level1Index = fileBlockNum / (uint)(_pointersPerBlock * _pointersPerBlock);
                uint remainder = fileBlockNum % (uint)(_pointersPerBlock * _pointersPerBlock);
                uint level2Index = remainder / (uint)_pointersPerBlock;
                uint level3Index = remainder % (uint)_pointersPerBlock;

                uint level1Block = ReadIndirectBlock(inode.di_ib[2], level1Index);
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
        /// Indirect block addresses in the inode (di_ib[]) are fragment addresses.
        /// Pointers INSIDE indirect blocks are also fragment addresses.
        /// </summary>
        /// <param name="indirectFragNum">
        /// Fragment number of the indirect block.
        /// </param>
        /// <param name="index">
        /// Index within the indirect block (0 to pointersPerBlock-1).
        /// </param>
        /// <returns>
        /// Fragment address stored at that index, or 0 if sparse.
        /// </returns>
        private uint ReadIndirectBlock(uint indirectFragNum, uint index)
        {
            if (indirectFragNum == 0)
                return 0;

            if (index >= _pointersPerBlock)
                throw new ArgumentOutOfRangeException(nameof(index));

            // Read the indirect block (indirectFragNum is already a fragment address)
            byte[] indirectData = new byte[_blockSize];
            ReadFilesystemBlock(indirectFragNum, indirectData);

            // Extract the pointer at the specified index (4 bytes per pointer, big-endian)
            // This pointer is a FRAGMENT address - return as-is!
            int offset = (int)index * 4;
            uint fragAddr = BsdEndianHelper.ReadUInt32BE(indirectData, offset);

            return fragAddr;
        }

        /// <summary>
        /// Reads file data starting from specified block and offset.
        /// </summary>
        /// <param name="inode">
        /// Inode containing file metadata.
        /// </param>
        /// <param name="offset">
        /// Byte offset within file.
        /// </param>
        /// <param name="count">
        /// Number of bytes to read.
        /// </param>
        /// <returns>
        /// Byte array containing file data.
        /// </returns>
        public byte[] ReadFileData(FfsInode inode, ulong offset, ulong count)
        {
            if (offset + count > inode.di_size)
                count = inode.di_size - offset;

            if (count == 0)
                return Array.Empty<byte>();

            byte[] result = new byte[count];
            ulong bytesRead = 0;
            ulong currentOffset = offset;

            while (bytesRead < count)
            {
                uint fileBlockNum = (uint)(currentOffset / (ulong)_blockSize);
                int blockOffset = (int)(currentOffset % (ulong)_blockSize);
                int bytesToRead = (int)Math.Min((ulong)(count - bytesRead), (ulong)(_blockSize - blockOffset));

                // MapFileBlock returns a fragment address, not a block number
                uint physicalFragAddr = MapFileBlock(inode, fileBlockNum);

                if (physicalFragAddr == 0)
                {
                    // Sparse block - fill with zeros
                    Array.Fill(result, (byte)0, (int)bytesRead, bytesToRead);
                }
                else
                {
                    byte[] blockData = new byte[_blockSize];

                    // Only the bytes up to blockOffset+bytesToRead are wanted; telling the reader
                    // that keeps it from touching sectors this file does not own (see the tail
                    // fragment note in ReadFilesystemBlock).
                    ReadFilesystemBlock(physicalFragAddr, blockData, blockOffset + bytesToRead);
                    Array.Copy(blockData, blockOffset, result, (int)bytesRead, bytesToRead);
                }

                bytesRead += (ulong)bytesToRead;
                currentOffset += (ulong)bytesToRead;
            }

            return result;
        }

        /// <summary>
        /// Reads entire file contents.
        /// </summary>
        public byte[] ReadFile(FfsInode inode)
        {
            return ReadFileData(inode, 0, inode.di_size);
        }
    }
}
