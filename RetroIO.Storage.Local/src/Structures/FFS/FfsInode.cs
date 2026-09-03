//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using RetroIO.Storage.Local.BSD.Structures;

namespace RetroIO.Storage.Local.FFS.Structures
{
    /// <summary>
    /// Berkeley FFS on-disk inode structure (exactly 128 bytes).
    /// Represents file metadata and block addresses.
    /// Uses 12 direct blocks + 3 indirect blocks (single, double, triple).
    ///
    /// REFERENCE: SunOS 3.4 source code
    /// File: D:\data\Torrent2\sunos_3.4_src\squashfs-root\sun\sys\ufs\inode.h
    ///
    /// Original structure (struct icommon):
    /// struct icommon {
    ///     u_short ic_mode;        /*  0: mode and type of file */
    ///     short   ic_nlink;       /*  2: number of links to file */
    ///     short   ic_uid;         /*  4: owner's user id */
    ///     short   ic_gid;         /*  6: owner's group id */
    ///     quad    ic_size;        /*  8: number of bytes in file (8 bytes) */
    ///     time_t  ic_atime;       /* 16: time last accessed */
    ///     long    ic_atspare;     /* 20: reserved */
    ///     time_t  ic_mtime;       /* 24: time last modified */
    ///     long    ic_mtspare;     /* 28: reserved */
    ///     time_t  ic_ctime;       /* 32: last time inode changed */
    ///     long    ic_ctspare;     /* 36: reserved */
    ///     daddr_t ic_db[NDADDR];  /* 40: disk block addresses (12 × 4 bytes = 48) */
    ///     daddr_t ic_ib[NIADDR];  /* 88: indirect blocks (3 × 4 bytes = 12) */
    ///     long    ic_flags;       /* 100: status flags */
    ///     long    ic_blocks;      /* 104: blocks actually held */
    ///     long    ic_gen;         /* 108: generation number */
    ///     long    ic_spare[4];    /* 112: reserved (16 bytes) */
    /// };
    ///
    /// NOTE: quad type is defined as: typedef struct _quad { long val[2]; } quad;
    ///       On mc68000 (big-endian), di_size uses val[1] (second 32-bit word).
    /// </summary>
    public class FfsInode
    {
        // =================================================================
        // COMMON FIELDS (offset 0-23, 24 bytes)
        // =================================================================

        /// <summary>
        /// File type and permissions (16 bits).
        /// Upper 4 bits = file type, lower 12 bits = permissions.
        /// </summary>
        public ushort di_mode { get; set; }

        /// <summary>
        /// Number of hard links to this inode.
        /// </summary>
        public ushort di_nlink { get; set; }

        /// <summary>
        /// Owner user ID (16 bits in FFS).
        /// </summary>
        public ushort di_uid { get; set; }

        /// <summary>
        /// Owner group ID (16 bits in FFS).
        /// </summary>
        public ushort di_gid { get; set; }

        /// <summary>
        /// File size in bytes (64-bit for FFS).
        /// </summary>
        public ulong di_size { get; set; }

        // =================================================================
        // BLOCK ADDRESSES (offset 24-83, 60 bytes = 15 × 4)
        // =================================================================

        /// <summary>
        /// Block addresses (15 entries × 4 bytes = 60 bytes).
        /// [0-11]: Direct blocks (12 × blocksize, typically 48-96 KB direct access)
        /// [12]: Single indirect block
        /// [13]: Double indirect block
        /// [14]: Triple indirect block
        /// </summary>
        public uint[] di_db { get; set; } = new uint[FfsConstants.NDADDR];  // 12 direct
        public uint[] di_ib { get; set; } = new uint[FfsConstants.NIADDR];  // 3 indirect

        // =================================================================
        // FLAGS AND GENERATION (offset 84-91, 8 bytes)
        // =================================================================

        /// <summary>
        /// Status flags (see FFS flags).
        /// </summary>
        public uint di_flags { get; set; }

        /// <summary>
        /// Generation number (for NFS).
        /// </summary>
        public uint di_gen { get; set; }

        // =================================================================
        // BLOCK COUNTS (offset 92-95, 4 bytes)
        // =================================================================

        /// <summary>
        /// Number of blocks actually allocated (in 512-byte units).
        /// </summary>
        public uint di_blocks { get; set; }

        // =================================================================
        // TIMESTAMPS (offset 96-127, 32 bytes = 3 × 8 + 8 padding)
        // =================================================================

        /// <summary>
        /// Last access time (Unix timestamp, seconds).
        /// </summary>
        public uint di_atime { get; set; }

        /// <summary>
        /// Last access time (nanoseconds).
        /// </summary>
        public uint di_atimensec { get; set; }

        /// <summary>
        /// Last modification time (Unix timestamp, seconds).
        /// </summary>
        public uint di_mtime { get; set; }

        /// <summary>
        /// Last modification time (nanoseconds).
        /// </summary>
        public uint di_mtimensec { get; set; }

        /// <summary>
        /// Creation/inode change time (Unix timestamp, seconds).
        /// </summary>
        public uint di_ctime { get; set; }

        /// <summary>
        /// Creation/inode change time (nanoseconds).
        /// </summary>
        public uint di_ctimensec { get; set; }

        // =================================================================
        // HELPER PROPERTIES - FILE TYPE
        // =================================================================

        /// <summary>
        /// File type extracted from di_mode field.
        /// </summary>
        public ushort FileType => (ushort)(di_mode & FfsConstants.IFMT);

        /// <summary>
        /// Permission bits extracted from di_mode field.
        /// </summary>
        public ushort Permissions => (ushort)(di_mode & FfsConstants.IPERM);

        /// <summary>
        /// True if this is a regular file.
        /// </summary>
        public bool IsRegularFile => FileType == FfsConstants.IFREG;

        /// <summary>
        /// True if this is a directory.
        /// </summary>
        public bool IsDirectory => FileType == FfsConstants.IFDIR;

        /// <summary>
        /// True if this is a character device.
        /// </summary>
        public bool IsCharDevice => FileType == FfsConstants.IFCHR;

        /// <summary>
        /// True if this is a block device.
        /// </summary>
        public bool IsBlockDevice => FileType == FfsConstants.IFBLK;

        /// <summary>
        /// True if this is a symbolic link.
        /// </summary>
        public bool IsSymLink => FileType == FfsConstants.IFLNK;

        /// <summary>
        /// True if this is a socket.
        /// </summary>
        public bool IsSocket => FileType == FfsConstants.IFSOCK;

        /// <summary>
        /// True if this is a FIFO (named pipe).
        /// </summary>
        public bool IsFifo => FileType == FfsConstants.IFIFO;

        /// <summary>
        /// True if inode is allocated (mode != 0).
        /// </summary>
        public bool IsAllocated => di_mode != 0;

        // =================================================================
        // HELPER PROPERTIES - TIMESTAMPS
        // =================================================================

        /// <summary>
        /// Last access time as DateTime.
        /// </summary>
        public DateTime AccessTime => DateTimeOffset.FromUnixTimeSeconds(di_atime).DateTime;

        /// <summary>
        /// Last modification time as DateTime.
        /// </summary>
        public DateTime ModificationTime => DateTimeOffset.FromUnixTimeSeconds(di_mtime).DateTime;

        /// <summary>
        /// Creation/change time as DateTime.
        /// </summary>
        public DateTime ChangeTime => DateTimeOffset.FromUnixTimeSeconds(di_ctime).DateTime;

        // =================================================================
        // HELPER PROPERTIES - PERMISSIONS
        // =================================================================

        /// <summary>
        /// True if Set UID bit is set.
        /// </summary>
        public bool HasSetUid => (di_mode & FfsConstants.ISUID) != 0;

        /// <summary>
        /// True if Set GID bit is set.
        /// </summary>
        public bool HasSetGid => (di_mode & FfsConstants.ISGID) != 0;

        /// <summary>
        /// True if Sticky bit is set.
        /// </summary>
        public bool HasStickyBit => (di_mode & FfsConstants.ISVTX) != 0;

        /// <summary>
        /// Gets permissions as octal string (e.g., "0755").
        /// </summary>
        public string PermissionsOctal => Convert.ToString(Permissions, 8).PadLeft(4, '0');

        /// <summary>
        /// Gets permission string (e.g., "rwxr-xr-x").
        /// </summary>
        public string PermissionsString
        {
            get
            {
                char[] perms = new char[9];
                perms[0] = (Permissions & 0x100) != 0 ? 'r' : '-';  // Owner read
                perms[1] = (Permissions & 0x080) != 0 ? 'w' : '-';  // Owner write
                perms[2] = (Permissions & 0x040) != 0 ? 'x' : '-';  // Owner execute
                perms[3] = (Permissions & 0x020) != 0 ? 'r' : '-';  // Group read
                perms[4] = (Permissions & 0x010) != 0 ? 'w' : '-';  // Group write
                perms[5] = (Permissions & 0x008) != 0 ? 'x' : '-';  // Group execute
                perms[6] = (Permissions & 0x004) != 0 ? 'r' : '-';  // Other read
                perms[7] = (Permissions & 0x002) != 0 ? 'w' : '-';  // Other write
                perms[8] = (Permissions & 0x001) != 0 ? 'x' : '-';  // Other execute

                // Handle SetUID, SetGID, Sticky
                if (HasSetUid) perms[2] = (Permissions & 0x040) != 0 ? 's' : 'S';
                if (HasSetGid) perms[5] = (Permissions & 0x008) != 0 ? 's' : 'S';
                if (HasStickyBit) perms[8] = (Permissions & 0x001) != 0 ? 't' : 'T';

                return new string(perms);
            }
        }

        // =================================================================
        // INODE LOCATION CALCULATIONS
        // =================================================================

        /// <summary>
        /// Calculates which filesystem block contains the given inode number.
        /// FFS inodes are numbered starting from 1 (ROOTINO = 2).
        /// </summary>
        /// <param name="inodeNumber">Inode number (1-based).</param>
        /// <param name="inodesPerBlock">Number of inodes per block (block_size / 128).</param>
        /// <param name="inodeBlockStart">First block containing inodes (from superblock fs_iblkno).</param>
        /// <returns>Block number containing the inode.</returns>
        public static uint InodeToBlock(uint inodeNumber, int inodesPerBlock, uint inodeBlockStart)
        {
            // Inode 0 is reserved, inode 1 is bad blocks, inode 2 is root
            // Formula: block = ((ino - 1) / inodes_per_block) + inode_block_start
            return ((inodeNumber - 1) / (uint)inodesPerBlock) + inodeBlockStart;
        }

        /// <summary>
        /// Calculates offset within block for the given inode number.
        /// </summary>
        /// <param name="inodeNumber">Inode number (1-based).</param>
        /// <param name="inodesPerBlock">Number of inodes per block.</param>
        /// <returns>Byte offset within block.</returns>
        public static int InodeToOffset(uint inodeNumber, int inodesPerBlock)
        {
            return ((int)((inodeNumber - 1) % (uint)inodesPerBlock)) * FfsConstants.INODE_SIZE;
        }

        // =================================================================
        // PARSING AND SERIALIZATION
        // =================================================================

        /// <summary>
        /// Parses inode from byte array (big-endian format for SunOS).
        /// </summary>
        public static FfsInode Parse(byte[] buffer, int offset)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (offset < 0 || offset + FfsConstants.INODE_SIZE > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            var inode = new FfsInode();

            // Based on actual SunOS 3.4 inode.h structure (squashfs-root/sun/sys/ufs/inode.h)
            // struct icommon layout:

            // Common fields (0-7)
            inode.di_mode = ReadUInt16BE(buffer, offset + 0);    // ic_mode
            inode.di_nlink = ReadUInt16BE(buffer, offset + 2);   // ic_nlink
            inode.di_uid = ReadUInt16BE(buffer, offset + 4);     // ic_uid (short = 2 bytes)
            inode.di_gid = ReadUInt16BE(buffer, offset + 6);     // ic_gid (short = 2 bytes)

            // File size (8-15) - quad = 8 bytes.
            //
            // ic_size is 'quad', declared as: typedef struct _quad { long val[2]; } quad;
            // That is TWO 32-bit words, and which word holds the low half depends on the port:
            //
            //   SunOS / mc68000 : i_size == ic_size.val[1]  -> high word at +8, low at +12
            //                     (a true big-endian 64-bit read gives the right answer)
            //   NDIX / ND-100   : i_size == ic_size.val[0]  -> low word at +8, high at +12
            //                     NDIX h/inode.h:98  #define di_size di_ic.ic_size.val[0]
            //                     NDIX h/types.h:34  quad = struct { long val[2]; }
            //                     i.e. NDIX is a 32-bit-size FFS that reads the size from
            //                     val[0]; val[1] at +12 is unused. Confirmed by the NDIX team
            //                     against the kernel headers, and by their nd500-mkfs output
            //                     being byte-identical to a rootfs.img that mounts.
            //
            // Verified on a real NDIX di70 root image: its root inode holds 00 00 00 44 at +8
            // and 00 00 00 00 at +12, i.e. 68 bytes - which is exactly the sum of DIRSIZ over
            // its five entries, DIRSIZ = 8 + roundup(namlen+1, 4):
            //     '.'=12  '..'=12  'lost+found'=20  'etc'=12  'dev'=12  => 68
            // (The same relation holds for /etc and /dev, which report 40 each.) Note this is
            // the PACKED size, not the span of the on-disk d_reclen fields, which is 1024.
            // Reading the field as a big-endian 64-bit quad instead yields 68 << 32, which then
            // overflows every downstream length.
            //
            // Resolve by halves. Files on these filesystems are far below 4 GB, so at most one
            // half is ever non-zero and the choice is unambiguous. Both halves non-zero would
            // mean a genuinely >4 GB file, which we then read as a true 64-bit big-endian quad.
            uint sizeHighWord = BsdEndianHelper.ReadUInt32BE(buffer, offset + 8);
            uint sizeLowWord = BsdEndianHelper.ReadUInt32BE(buffer, offset + 12);

            if (sizeHighWord == 0)
            {
                // SunOS / mc68000 layout (and any zero-length file).
                inode.di_size = sizeLowWord;
            }
            else if (sizeLowWord == 0)
            {
                // NDIX / ND-100 layout: the size sits in val[0].
                inode.di_size = sizeHighWord;
            }
            else
            {
                // Both halves populated - treat as a real 64-bit quad.
                inode.di_size = ((ulong)sizeHighWord << 32) | sizeLowWord;
            }

            // Timestamps (16-39) - time_t (4 bytes) + spare (4 bytes) each
            inode.di_atime = BsdEndianHelper.ReadUInt32BE(buffer, offset + 16);  // ic_atime
            // Skip ic_atspare (offset + 20)
            inode.di_mtime = BsdEndianHelper.ReadUInt32BE(buffer, offset + 24);  // ic_mtime
            // Skip ic_mtspare (offset + 28)
            inode.di_ctime = BsdEndianHelper.ReadUInt32BE(buffer, offset + 32);  // ic_ctime
            // Skip ic_ctspare (offset + 36)

            // Direct block addresses (40-87) - ic_db[12], each daddr_t (4 bytes)
            for (int i = 0; i < FfsConstants.NDADDR; i++)
            {
                inode.di_db[i] = BsdEndianHelper.ReadUInt32BE(buffer, offset + 40 + (i * 4));

                // Debug: Warn if block pointer seems too large (likely reading from wrong offset)
                if (inode.di_db[i] > 10000 && inode.di_db[i] != 0)
                {
                    Console.WriteLine($"[WARNING FfsInode.Parse] di_db[{i}] = {inode.di_db[i]} (0x{inode.di_db[i]:X8}) - suspiciously large!");
                    Console.WriteLine($"  Raw bytes at offset {offset + 40 + (i * 4)}: " +
                        $"{buffer[offset + 40 + (i * 4)]:X2} {buffer[offset + 40 + (i * 4) + 1]:X2} " +
                        $"{buffer[offset + 40 + (i * 4) + 2]:X2} {buffer[offset + 40 + (i * 4) + 3]:X2}");
                    Console.WriteLine($"  Expected inode offset in block: {offset} bytes");
                    Console.WriteLine($"  ic_mode at offset {offset}: {buffer[offset]:X2} {buffer[offset + 1]:X2}");
                    Console.WriteLine($"  Full 128 bytes starting at offset {offset}:");
                    for (int dumpIdx = 0; dumpIdx < Math.Min(128, buffer.Length - offset); dumpIdx += 16)
                    {
                        Console.Write($"    Offset {offset + dumpIdx,3}: ");
                        for (int b = 0; b < 16 && (dumpIdx + b) < 128 && (offset + dumpIdx + b) < buffer.Length; b++)
                        {
                            Console.Write($"{buffer[offset + dumpIdx + b]:X2} ");
                        }
                        Console.WriteLine();
                    }
                }
            }

            // Indirect block addresses (88-99) - ic_ib[3], each daddr_t (4 bytes)
            for (int i = 0; i < FfsConstants.NIADDR; i++)
            {
                inode.di_ib[i] = BsdEndianHelper.ReadUInt32BE(buffer, offset + 88 + (i * 4));
            }

            // Status flags (100-103) - ic_flags
            inode.di_flags = BsdEndianHelper.ReadUInt32BE(buffer, offset + 100);

            // Block count (104-107) - ic_blocks
            inode.di_blocks = BsdEndianHelper.ReadUInt32BE(buffer, offset + 104);

            // Generation number (108-111) - ic_gen
            inode.di_gen = BsdEndianHelper.ReadUInt32BE(buffer, offset + 108);

            // Spare fields (112-127) - ic_spare[4] - skip for now
            inode.di_atimensec = 0;  // Not in SunOS 3.4 format
            inode.di_mtimensec = 0;
            inode.di_ctimensec = 0;

            return inode;
        }

        private static ushort ReadUInt16BE(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        private static ulong ReadUInt64BE(byte[] buffer, int offset)
        {
            return ((ulong)BsdEndianHelper.ReadUInt32BE(buffer, offset) << 32) |
                   BsdEndianHelper.ReadUInt32BE(buffer, offset + 4);
        }

        // =================================================================
        // FILE TYPE NAME
        // =================================================================

        /// <summary>
        /// Gets file type as string.
        /// </summary>
        public string FileTypeName
        {
            get
            {
                return FileType switch
                {
                    FfsConstants.IFREG => "regular file",
                    FfsConstants.IFDIR => "directory",
                    FfsConstants.IFCHR => "character device",
                    FfsConstants.IFBLK => "block device",
                    FfsConstants.IFLNK => "symbolic link",
                    FfsConstants.IFSOCK => "socket",
                    FfsConstants.IFIFO => "FIFO",
                    _ => $"unknown(0x{FileType:X4})"
                };
            }
        }

        public override string ToString()
        {
            if (!IsAllocated)
                return "Inode (free)";

            return $"FFS Inode: {FileTypeName}, size={di_size}, links={di_nlink}, " +
                   $"perm={PermissionsString}, uid={di_uid}, gid={di_gid}";
        }
    }
}
