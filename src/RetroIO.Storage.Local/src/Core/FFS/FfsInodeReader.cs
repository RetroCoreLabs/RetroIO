//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Storage.Local.FFS.Structures;

namespace RetroIO.Storage.Local.FFS.Core
{
    /// <summary>
    /// Reads and manages FFS inodes from cylinder groups.
    /// Handles inode location calculations and caching.
    ///
    /// REFERENCE: SunOS 3.4 source code
    /// File: D:\data\Torrent2\sunos_3.4_src\squashfs-root\sun\sys\ufs\fs.h
    ///
    /// Key macros from fs.h:
    /// - INOPB(fs): fs->fs_inopb (number of inodes per block)
    /// - cgbase(fs,c): fs_fpg * c (cylinder group base in fragments)
    /// - cgstart(fs,c): cgbase + fs_cgoffset * (c AND NOT fs_cgmask)
    /// - cgimin(fs,c): cgstart + fs_iblkno (inode area start in fragments)
    /// </summary>
    public class FfsInodeReader
    {
        private readonly FfsBlockMapper _mapper;
        private readonly FfsSuperblock _superblock;
        private readonly int _inodesPerBlock;

        /// <summary>
        /// Creates an inode reader. The inodes per block value comes from the superblock.
        /// </summary>
        /// <param name="mapper">
        /// The block mapper of the file system.
        /// </param>
        /// <param name="superblock">
        /// The superblock of the file system.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="mapper"/> or <paramref name="superblock"/> is null.
        /// </exception>
        public FfsInodeReader(FfsBlockMapper mapper, FfsSuperblock superblock)
        {
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _superblock = superblock ?? throw new ArgumentNullException(nameof(superblock));

            // CRITICAL: Use fs_inopb from superblock, NOT calculated value!
            // Per SunOS fs.h: #define INOPB(fs) ((fs)->fs_inopb)
            // Old code: _inodesPerBlock = (int)(_superblock.fs_bsize / FfsConstants.INODE_SIZE);
            // This caused inode offset misalignment in old SunOS disks.
            _inodesPerBlock = (int)_superblock.fs_inopb;

        }

        /// <summary>
        /// Reads an inode by its number.
        /// </summary>
        /// <param name="inodeNumber">
        /// Inode number (1-based, ROOTINO = 2).
        /// </param>
        /// <returns>
        /// Parsed inode structure.
        /// </returns>
        public FfsInode ReadInode(uint inodeNumber)
        {
            if (inodeNumber == 0)
                throw new ArgumentException("Inode 0 is reserved and cannot be read", nameof(inodeNumber));

            // Calculate which cylinder group contains this inode
            // FFS inodes are 0-indexed: inode 0 (reserved), inode 1 (reserved), inode 2 (root)
            uint cylinderGroup = inodeNumber / _superblock.fs_ipg;
            uint inodeInGroup = inodeNumber % _superblock.fs_ipg;

            // Calculate block number within the inode area
            // Inodes start at fs_iblkno within each cylinder group
            uint blockInGroup = inodeInGroup / (uint)_inodesPerBlock;
            int offsetInBlock = (int)(inodeInGroup % (uint)_inodesPerBlock) * FfsConstants.INODE_SIZE;

            // Calculate absolute block number using FFS cgimin() formula:
            // cgimin(fs, c) = cgstart(fs, c) + fs_iblkno
            // cgstart(fs, c) = cgbase(fs, c) + fs_cgoffset * (c & ~fs_cgmask)
            // cgbase(fs, c) = (daddr_t)(fs_fpg * c)
            //
            // IMPORTANT: All cg* macros return FRAGMENT addresses, NOT sectors!
            // The (daddr_t) cast is just a type cast, not a unit conversion.
            // To convert fragments to DEV_BSIZE (512-byte) sectors: fsbtodb(fs, b) = b << fs_fsbtodb
            // To convert back: dbtofsb(fs, b) = b >> fs_fsbtodb

            // cgbase: start of cylinder group in fragments
            uint cgbase = _superblock.fs_fpg * cylinderGroup;

            // cgstart: actual start considering rotational offset (in fragments)
            uint cgstart = cgbase + _superblock.fs_cgoffset * (cylinderGroup & ~_superblock.fs_cgmask);

            // cgimin: start of inode area in fragments
            // Per SunOS 3.4 fs.h line 281: cgimin(fs, c) = cgstart(fs, c) + fs_iblkno
            // IMPORTANT: fs_iblkno is already in fragments (confirmed by disk analysis)
            uint cgimin = cgstart + _superblock.fs_iblkno;

            // Add block offset within inode area (in fragments)
            uint inodeFragmentAddr = cgimin + (blockInGroup * _superblock.fs_frag);

            // Read the block containing the inode
            // NOTE: ReadFilesystemBlock now expects fragment addresses directly
            byte[] blockData = new byte[_superblock.fs_bsize];
            _mapper.ReadFilesystemBlock(inodeFragmentAddr, blockData);

            // Parse the inode
            return FfsInode.Parse(blockData, offsetInBlock);
        }

        /// <summary>
        /// Reads the root directory inode.
        /// </summary>
        public FfsInode ReadRootInode()
        {
            return ReadInode(FfsConstants.ROOTINO);
        }

        /// <summary>
        /// Validates that an inode number is within valid range.
        /// </summary>
        public bool IsValidInodeNumber(uint inodeNumber)
        {
            if (inodeNumber == 0)
                return false;

            uint totalInodes = _superblock.fs_ncg * _superblock.fs_ipg;
            return inodeNumber <= totalInodes;
        }

        /// <summary>
        /// Gets the cylinder group number for an inode.
        /// </summary>
        public uint GetCylinderGroup(uint inodeNumber)
        {
            if (inodeNumber == 0)
                throw new ArgumentException("Inode 0 is reserved", nameof(inodeNumber));

            return inodeNumber / _superblock.fs_ipg;
        }
    }
}
