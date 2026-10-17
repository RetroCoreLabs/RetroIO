//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Storage.Local.FFS.Structures
{
    /// <summary>
    /// Constants for Berkeley Fast File System (FFS/UFS).
    /// Based on 4.2BSD/SunOS 3.4 with variable block sizes (4096 or 8192).
    /// </summary>
    public static class FfsConstants
    {
        // =================================================================
        // MAGIC NUMBERS
        // =================================================================

        /// <summary>
        /// FFS superblock magic number (0x011954).
        /// </summary>
        public const uint FS_MAGIC = 0x011954;

        /// <summary>
        /// Cylinder group magic number (0x090255).
        /// </summary>
        public const uint CG_MAGIC = 0x090255;

        // =================================================================
        // SUPERBLOCK LOCATION
        // =================================================================

        /// <summary>
        /// Superblock location in bytes from start of filesystem (8192 = 16 sectors).
        /// This is after the boot block area.
        /// </summary>
        public const int SBLOCK_OFFSET = 8192;

        /// <summary>
        /// Superblock location in 512-byte sectors (16).
        /// </summary>
        public const int SBLOCK_SECTOR = SBLOCK_OFFSET / 512;

        /// <summary>
        /// Minimum superblock size in bytes.
        /// Actual structure is larger (1356+ bytes) but we read at least this much.
        /// </summary>
        public const int SBSIZE = 2048;

        // =================================================================
        // BLOCK AND FRAGMENT SIZES
        // =================================================================

        /// <summary>
        /// Minimum filesystem block size (4096 bytes).
        /// </summary>
        public const int MIN_BSIZE = 4096;

        /// <summary>
        /// Maximum filesystem block size (8192 bytes).
        /// </summary>
        public const int MAX_BSIZE = 8192;

        /// <summary>
        /// Minimum fragment size (512 bytes).
        /// </summary>
        public const int MIN_FSIZE = 512;

        /// <summary>
        /// Maximum fragment size (4096 bytes).
        /// </summary>
        public const int MAX_FSIZE = 4096;

        /// <summary>
        /// Maximum number of fragments per block (8192 / 512 = 16).
        /// </summary>
        public const int MAXFRAG = 16;

        // =================================================================
        // INODE CONSTANTS
        // =================================================================

        /// <summary>
        /// Size of each inode in bytes (exactly 128 bytes).
        /// </summary>
        public const int INODE_SIZE = 128;

        /// <summary>
        /// Root directory inode number (always 2).
        /// </summary>
        public const uint ROOTINO = 2;

        /// <summary>
        /// Number of direct block pointers in inode (12).
        /// </summary>
        public const int NDADDR = 12;

        /// <summary>
        /// Number of indirect block pointers in inode (3).
        /// [0] = single indirect
        /// [1] = double indirect
        /// [2] = triple indirect
        /// </summary>
        public const int NIADDR = 3;

        /// <summary>
        /// Total number of block addresses in inode (12 direct + 3 indirect = 15).
        /// </summary>
        public const int TOTAL_ADDR = NDADDR + NIADDR;

        // =================================================================
        // DIRECTORY CONSTANTS
        // =================================================================

        /// <summary>
        /// Directory entry header size (inode + reclen + type + namlen = 8 bytes).
        /// </summary>
        public const int DIRENT_HEADER_SIZE = 8;

        /// <summary>
        /// Maximum filename length in directory entry (255 characters).
        /// </summary>
        public const int MAXNAMLEN = 255;

        /// <summary>
        /// Directory entry alignment (4 bytes).
        /// </summary>
        public const int DIRENT_ALIGNMENT = 4;

        /// <summary>
        /// Directory block size for optimal I/O.
        /// </summary>
        public const int DIRBLKSIZ = 512;

        // =================================================================
        // FILE TYPE AND MODE CONSTANTS
        // =================================================================

        /// <summary>
        /// File type mask (upper 4 bits of mode).
        /// </summary>
        public const ushort IFMT = 0xF000;

        /// <summary>
        /// Named pipe (FIFO).
        /// </summary>
        public const ushort IFIFO = 0x1000;

        /// <summary>
        /// Character device.
        /// </summary>
        public const ushort IFCHR = 0x2000;

        /// <summary>
        /// Directory.
        /// </summary>
        public const ushort IFDIR = 0x4000;

        /// <summary>
        /// Block device.
        /// </summary>
        public const ushort IFBLK = 0x6000;

        /// <summary>
        /// Regular file.
        /// </summary>
        public const ushort IFREG = 0x8000;

        /// <summary>
        /// Symbolic link.
        /// </summary>
        public const ushort IFLNK = 0xA000;

        /// <summary>
        /// Socket.
        /// </summary>
        public const ushort IFSOCK = 0xC000;

        /// <summary>
        /// Set UID on execution.
        /// </summary>
        public const ushort ISUID = 0x0800;

        /// <summary>
        /// Set GID on execution.
        /// </summary>
        public const ushort ISGID = 0x0400;

        /// <summary>
        /// Sticky bit.
        /// </summary>
        public const ushort ISVTX = 0x0200;

        /// <summary>
        /// Permission bits mask (lower 12 bits).
        /// </summary>
        public const ushort IPERM = 0x0FFF;

        // =================================================================
        // CYLINDER GROUP CONSTANTS
        // =================================================================

        /// <summary>
        /// Minimum number of cylinder groups.
        /// </summary>
        public const int MIN_NCYL = 1;

        /// <summary>
        /// Maximum number of cylinder groups (practical limit).
        /// </summary>
        public const int MAX_NCYL = 32768;

        /// <summary>
        /// Cylinder group summary size in bytes.
        /// </summary>
        public const int CSUM_SIZE = 16;

        // =================================================================
        // ROTATION OPTIMIZATION CONSTANTS
        // =================================================================

        /// <summary>
        /// Number of rotational positions tracked (8).
        /// </summary>
        public const int NRPOS = 8;

        /// <summary>
        /// Maximum distinct block layouts per cylinder group (1).
        /// </summary>
        public const int MAXBPC = 1;

        // =================================================================
        // BLOCK ALLOCATION CONSTANTS
        // =================================================================

        /// <summary>
        /// Minimum free blocks for user allocations (typically 10%).
        /// Filesystem reserves space for root when below this threshold.
        /// </summary>
        public const int MINFREE_PERCENT = 10;

        /// <summary>
        /// Target blocks per file for distribution heuristic.
        /// </summary>
        public const int BLKPREF_TARGET = 2;

        // =================================================================
        // FILESYSTEM STATE FLAGS
        // =================================================================

        /// <summary>
        /// Filesystem is clean (properly unmounted).
        /// </summary>
        public const int FS_CLEAN = 0x01;

        /// <summary>
        /// Filesystem needs consistency check (fsck).
        /// </summary>
        public const int FS_DIRTY = 0x02;

        /// <summary>
        /// Filesystem is read-only.
        /// </summary>
        public const int FS_READONLY = 0x04;

        // =================================================================
        // OPTIMIZATION FLAGS
        // =================================================================

        /// <summary>
        /// Optimize for space utilization.
        /// </summary>
        public const int FS_OPTSPACE = 0;

        /// <summary>
        /// Optimize for time (performance).
        /// </summary>
        public const int FS_OPTTIME = 1;

        // =================================================================
        // CALCULATED CONSTANTS (HELPERS)
        // =================================================================

        /// <summary>
        /// Calculates number of inodes per block given block size.
        /// </summary>
        /// <param name="blockSize">
        /// Block size in bytes (4096 or 8192).
        /// </param>
        /// <returns>
        /// Number of inodes per block (32 or 64).
        /// </returns>
        public static int InodesPerBlock(int blockSize)
        {
            return blockSize / INODE_SIZE;
        }

        /// <summary>
        /// Calculates number of indirect pointers per block.
        /// </summary>
        /// <param name="blockSize">
        /// Block size in bytes.
        /// </param>
        /// <returns>
        /// Number of 32-bit pointers (1024 or 2048).
        /// </returns>
        public static int PointersPerBlock(int blockSize)
        {
            return blockSize / sizeof(uint);
        }

        /// <summary>
        /// Calculates number of fragments per block.
        /// </summary>
        /// <param name="blockSize">
        /// Block size in bytes.
        /// </param>
        /// <param name="fragSize">
        /// Fragment size in bytes.
        /// </param>
        /// <returns>
        /// Number of fragments per block.
        /// </returns>
        public static int FragmentsPerBlock(int blockSize, int fragSize)
        {
            if (fragSize == 0)
                return 0;
            return blockSize / fragSize;
        }
    }
}
