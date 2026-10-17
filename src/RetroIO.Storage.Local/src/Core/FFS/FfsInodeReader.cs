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
    /// - cgstart(fs,c): cgbase + fs_cgoffset * (c &amp; ~fs_cgmask)
    /// - cgimin(fs,c): cgstart + fs_iblkno (inode area start in fragments)
    /// </summary>
    public class FfsInodeReader
    {
        private readonly FfsBlockMapper _mapper;
        private readonly FfsSuperblock _superblock;
        private readonly int _inodesPerBlock;

        public FfsInodeReader(FfsBlockMapper mapper, FfsSuperblock superblock)
        {
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _superblock = superblock ?? throw new ArgumentNullException(nameof(superblock));

            // CRITICAL: Use fs_inopb from superblock, NOT calculated value!
            // Per SunOS fs.h: #define INOPB(fs) ((fs)->fs_inopb)
            // Old code: _inodesPerBlock = (int)(_superblock.fs_bsize / FfsConstants.INODE_SIZE);
            // This caused inode offset misalignment in old SunOS disks.
            _inodesPerBlock = (int)_superblock.fs_inopb;

            Console.WriteLine($"[DEBUG FfsInodeReader Constructor]:");
            Console.WriteLine($"  fs_iblkno={_superblock.fs_iblkno} fragments");
            Console.WriteLine($"  fs_fsbtodb={_superblock.fs_fsbtodb}");
            Console.WriteLine($"  fs_ipg={_superblock.fs_ipg}");
            Console.WriteLine($"  fs_fpg={_superblock.fs_fpg}");
            Console.WriteLine($"  fs_frag={_superblock.fs_frag}");
            Console.WriteLine($"  fs_bsize={_superblock.fs_bsize}");
            Console.WriteLine($"  fs_fsize={_superblock.fs_fsize}");
            Console.WriteLine($"  fs_inopb={_superblock.fs_inopb} (inodes per block - FROM SUPERBLOCK)");
            Console.WriteLine($"  _inodesPerBlock={_inodesPerBlock}");
            Console.WriteLine($"  fs_cgoffset={_superblock.fs_cgoffset}");
            Console.WriteLine($"  fs_cgmask={_superblock.fs_cgmask}");
        }

        /// <summary>
        /// Reads an inode by its number.
        /// </summary>
        /// <param name="inodeNumber">Inode number (1-based, ROOTINO = 2).</param>
        /// <returns>Parsed inode structure.</returns>
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

            Console.WriteLine($"[DEBUG FfsInodeReader] Inode #{inodeNumber}:");
            Console.WriteLine($"  CG={cylinderGroup}, inodeInGroup={inodeInGroup}");
            Console.WriteLine($"  cgbase={cgbase} frags, cgstart={cgstart} frags, fs_iblkno={_superblock.fs_iblkno} frags");
            Console.WriteLine($"  cgimin={cgimin} frags, blockInGroup={blockInGroup}");
            Console.WriteLine($"  inodeFragmentAddr={inodeFragmentAddr} frags");
            Console.WriteLine($"  offsetInBlock={offsetInBlock} bytes");

            // Read the block containing the inode
            // NOTE: ReadFilesystemBlock now expects fragment addresses directly
            byte[] blockData = new byte[_superblock.fs_bsize];
            _mapper.ReadFilesystemBlock(inodeFragmentAddr, blockData);

            Console.WriteLine($"  Reading from fragment {inodeFragmentAddr}");
            Console.WriteLine($"  All 128 bytes of inode at offset {offsetInBlock}:");
            int bytesPrinted = Math.Min(128, blockData.Length - offsetInBlock);
            for (int i = 0; i < bytesPrinted; i++)
            {
                if (i % 16 == 0) Console.Write($"    Offset {offsetInBlock + i,3}: ");
                Console.Write($"{blockData[offsetInBlock + i]:X2} ");
                if ((i + 1) % 16 == 0) Console.WriteLine();
            }
            if (bytesPrinted % 16 != 0) Console.WriteLine();

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
