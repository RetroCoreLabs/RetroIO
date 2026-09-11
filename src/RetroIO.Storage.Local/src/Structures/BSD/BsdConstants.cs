//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Storage.Local.BSD.Structures
{
    /// <summary>
    /// Constants for BSD 2.11 V7-style filesystem.
    /// Based on 2.11BSD with 1KB blocks.
    /// </summary>
    public static class BsdConstants
    {
        // =================================================================
        // DISK LABEL CONSTANTS
        // =================================================================

        /// <summary>
        /// Magic number for BSD disk label (0x82564557 = "UWeV" in ASCII).
        /// Appears twice in disk label structure for validation.
        /// </summary>
        public const uint DISK_MAGIC = 0x82564557;

        /// <summary>
        /// Maximum number of partitions in disk label.
        /// </summary>
        public const int MAX_PARTITIONS = 8;

        /// <summary>
        /// Physical sector containing disk label (always sector 1).
        /// </summary>
        public const int LABEL_SECTOR = 1;

        /// <summary>
        /// Size of disk label structure in bytes.
        /// </summary>
        public const int LABEL_SIZE = 512;

        /// <summary>
        /// Size of each partition entry in bytes.
        /// </summary>
        public const int PARTITION_SIZE = 12;

        // =================================================================
        // FILESYSTEM BLOCK CONSTANTS
        // =================================================================

        /// <summary>
        /// Filesystem block size in bytes (always 1024 for V7).
        /// </summary>
        public const int FS_BLOCK_SIZE = 1024;

        /// <summary>
        /// Physical sector size for traditional BSD systems.
        /// </summary>
        public const int SECTOR_SIZE_512 = 512;

        /// <summary>
        /// Physical sector size for ND hardware.
        /// </summary>
        public const int SECTOR_SIZE_1024 = 1024;

        /// <summary>
        /// Boot block sector (always sector 0).
        /// </summary>
        public const int BOOT_SECTOR = 0;

        /// <summary>
        /// Boot block size in bytes.
        /// </summary>
        public const int BOOT_SIZE = 512;

        /// <summary>
        /// Superblock location (filesystem block 1).
        /// </summary>
        public const int SUPERBLOCK_BLOCK = 1;

        /// <summary>
        /// Superblock size in bytes.
        /// </summary>
        public const int SUPERBLOCK_SIZE = 1024;

        // =================================================================
        // INODE CONSTANTS
        // =================================================================

        /// <summary>
        /// Size of each inode in bytes.
        /// </summary>
        public const int INODE_SIZE = 64;

        /// <summary>
        /// Number of inodes per filesystem block (1024 / 64 = 16).
        /// </summary>
        public const int INODES_PER_BLOCK = FS_BLOCK_SIZE / INODE_SIZE;

        /// <summary>
        /// Root directory inode number (always 2).
        /// </summary>
        public const ushort ROOT_INODE = 2;

        /// <summary>
        /// Reserved inode for bad blocks (inode 1).
        /// </summary>
        public const ushort BAD_BLOCKS_INODE = 1;

        /// <summary>
        /// Number of direct block addresses in inode.
        /// </summary>
        public const int DIRECT_BLOCKS = 4;

        /// <summary>
        /// Total number of block addresses in inode (4 direct + 3 indirect).
        /// </summary>
        public const int TOTAL_ADDR = 7;

        /// <summary>
        /// Number of indirect pointers per block (1024 / 4 = 256).
        /// </summary>
        public const int NINDIR = FS_BLOCK_SIZE / sizeof(uint);

        // =================================================================
        // DIRECTORY CONSTANTS
        // =================================================================

        /// <summary>
        /// Minimum directory entry size (header = 6 bytes, minimum).
        /// Some entries like "." can be as small as 8 bytes.
        /// </summary>
        public const int MIN_DIRENT_SIZE = 6;

        /// <summary>
        /// Maximum filename length (63 characters + null terminator).
        /// </summary>
        public const int MAX_NAME_LEN = 63;

        /// <summary>
        /// Directory entry alignment (4 bytes).
        /// </summary>
        public const int DIRENT_ALIGNMENT = 4;

        /// <summary>
        /// Size of directory entry header (inode + reclen + namlen).
        /// </summary>
        public const int DIRENT_HEADER_SIZE = 6;

        /// <summary>
        /// Directory block size for new directories.
        /// </summary>
        public const int DIRBLKSIZ = 512;

        // =================================================================
        // FREE LIST CONSTANTS
        // =================================================================

        /// <summary>
        /// Number of free block entries in superblock cache.
        /// </summary>
        public const int NICFREE = 50;

        /// <summary>
        /// Number of free inode entries in superblock cache.
        /// </summary>
        public const int NICINOD = 100;

        // =================================================================
        // FILE TYPE AND MODE CONSTANTS
        // =================================================================

        /// <summary>
        /// File type mask.
        /// </summary>
        public const ushort IFMT = 0xF000;

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
        /// Permission bits mask.
        /// </summary>
        public const ushort IPERM = 0x0FFF;

        // =================================================================
        // FILESYSTEM TYPE CONSTANTS (for partition table)
        // =================================================================

        /// <summary>
        /// Unused partition.
        /// </summary>
        public const byte FS_UNUSED = 0;

        /// <summary>
        /// Swap space.
        /// </summary>
        public const byte FS_SWAP = 1;

        /// <summary>
        /// Version 6 Unix filesystem.
        /// </summary>
        public const byte FS_V6 = 2;

        /// <summary>
        /// Version 7 Unix filesystem.
        /// </summary>
        public const byte FS_V7 = 3;

        /// <summary>
        /// System V filesystem.
        /// </summary>
        public const byte FS_SYSV = 4;

        /// <summary>
        /// 2.11BSD filesystem (1K blocks).
        /// </summary>
        public const byte FS_V71K = 5;

        /// <summary>
        /// V8 filesystem (4K blocks).
        /// </summary>
        public const byte FS_V8 = 6;

        /// <summary>
        /// 4.2BSD Fast Filesystem.
        /// </summary>
        public const byte FS_BSDFFS = 7;

        /// <summary>
        /// MS-DOS FAT filesystem.
        /// </summary>
        public const byte FS_MSDOS = 8;

        /// <summary>
        /// 4.4BSD Log-structured filesystem.
        /// </summary>
        public const byte FS_BSDLFS = 9;

        /// <summary>
        /// Other/unknown filesystem.
        /// </summary>
        public const byte FS_OTHER = 10;

        /// <summary>
        /// OS/2 HPFS.
        /// </summary>
        public const byte FS_HPFS = 11;

        /// <summary>
        /// ISO 9660 (CD-ROM).
        /// </summary>
        public const byte FS_ISO9660 = 12;

        // =================================================================
        // DISK TYPE CONSTANTS
        // =================================================================

        /// <summary>
        /// SMD, XSMD disk.
        /// </summary>
        public const byte DTYPE_SMD = 1;

        /// <summary>
        /// MSCP (RA, RD drives).
        /// </summary>
        public const byte DTYPE_MSCP = 2;

        /// <summary>
        /// Other DEC (RK, RL).
        /// </summary>
        public const byte DTYPE_DEC = 3;

        /// <summary>
        /// SCSI disk.
        /// </summary>
        public const byte DTYPE_SCSI = 4;

        /// <summary>
        /// ESDI disk.
        /// </summary>
        public const byte DTYPE_ESDI = 5;

        /// <summary>
        /// ST506 disk.
        /// </summary>
        public const byte DTYPE_ST506 = 6;

        /// <summary>
        /// Floppy disk.
        /// </summary>
        public const byte DTYPE_FLOPPY = 7;
    }
}

