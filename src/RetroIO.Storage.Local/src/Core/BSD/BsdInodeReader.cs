//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Storage.Local.BSD.Structures;
using RetroIO.Storage.Local.V7.Structures;

namespace RetroIO.Storage.Local.V7.Core
{
    /// <summary>
    /// Reads and caches inodes from the inode table.
    /// </summary>
    public class V7InodeReader
    {
        private readonly V7BlockMapper _mapper;
        private readonly V7Superblock _superblock;
        private readonly uint _partitionStartBlock;
        private readonly Dictionary<ushort, V7Inode> _inodeCache;

        public V7InodeReader(V7BlockMapper mapper, V7Superblock superblock, uint partitionStartBlock)
        {
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _superblock = superblock ?? throw new ArgumentNullException(nameof(superblock));
            _partitionStartBlock = partitionStartBlock;
            _inodeCache = new Dictionary<ushort, V7Inode>();
        }

        /// <summary>
        /// Reads inode from disk.
        /// Inodes are numbered starting from 1.
        /// Inode 1 = bad blocks, Inode 2 = root directory.
        /// </summary>
        public V7Inode ReadInode(ushort inodeNumber)
        {
            if (inodeNumber == 0)
                throw new ArgumentException("Inode 0 is invalid", nameof(inodeNumber));

            // Check cache first
            if (_inodeCache.TryGetValue(inodeNumber, out V7Inode? cachedInode))
            {
                return cachedInode;
            }

            // Calculate which block contains this inode
            uint inodeBlock = V7Inode.InodeToBlock(inodeNumber);
            int offset = V7Inode.InodeToOffset(inodeNumber);

            // Inode blocks start at block 2 (block 0 = boot, block 1 = superblock).
            // The block number stays PARTITION-RELATIVE here: V7BlockMapper.PartitionStartBlock is
            // now the single place the partition offset is applied. Adding _partitionStartBlock
            // here as well would double it.
            byte[] blockData = new byte[V7Constants.FS_BLOCK_SIZE];
            _mapper.ReadFilesystemBlock(inodeBlock, blockData);

            // Parse the inode
            V7Inode inode = V7Inode.Parse(blockData, offset);

            // Cache it
            _inodeCache[inodeNumber] = inode;

            return inode;
        }

        /// <summary>
        /// Reads root directory inode (always inode 2).
        /// </summary>
        public V7Inode ReadRootInode()
        {
            return ReadInode(V7Constants.ROOT_INODE);
        }

        /// <summary>
        /// Clears the inode cache.
        /// </summary>
        public void ClearCache()
        {
            _inodeCache.Clear();
        }

        /// <summary>
        /// Validates inode structure.
        /// </summary>
        public bool ValidateInode(V7Inode inode)
        {
            // Check if allocated
            if (!inode.IsAllocated)
                return false;

            // Check file size is reasonable
            if (inode.Size > uint.MaxValue)
                return false;

            // Check link count
            if (inode.NLink == 0)
                return false;

            return true;
        }
    }
}

