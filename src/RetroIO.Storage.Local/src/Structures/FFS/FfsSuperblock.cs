//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Storage.Local.BSD.Structures;

namespace RetroIO.Storage.Local.FFS.Structures
{
    /// <summary>
    /// Berkeley Fast File System (FFS) / Unix File System (UFS) superblock structure.
    ///
    /// REFERENCE: SunOS 3.4 source code
    /// File: D:\data\Torrent2\sunos_3.4_src\squashfs-root\sun\sys\ufs\fs.h
    /// Also: D:\data\Torrent2\sunos_3.4_src\squashfs-root\sun\sys\h\types.h (quad type definition)
    ///
    /// IMPORTANT NAMING NOTE:
    /// FFS (Fast File System) = UFS (Unix File System) - they are the SAME filesystem!
    /// This is the MODERN BSD filesystem, completely different from V7 filesystem.
    ///
    /// FFS/UFS History:
    /// - Developed at UC Berkeley (4.2BSD, 1983)
    /// - Major improvement over V7 filesystem
    /// - Used by: FreeBSD, NetBSD, OpenBSD, SunOS, Solaris
    /// - Variants: UFS1 (32-bit), UFS2 (64-bit)
    ///
    /// Key Features:
    /// - Variable block sizes (4096, 8192 bytes)
    /// - Cylinder groups for locality optimization
    /// - Block fragments for small files
    /// - Long filenames (up to 255 bytes)
    /// - Superblock at offset 8192 bytes (block 16)
    /// - Magic number: 0x011954
    ///
    /// For the OLDER UNIX V7 filesystem, see V7Superblock (from 1979).
    ///
    /// Structure contains:
    /// - Filesystem metadata, cylinder group info, and optimization parameters
    /// - Size: 1356+ bytes (exact size depends on number of cylinder groups)
    /// - Based on SunOS 3.4 / 4.2BSD implementation
    /// </summary>
    public class FfsSuperblock
    {
        // =================================================================
        // LINK TO CHAIN (offset 0, 4 bytes)
        // =================================================================

        /// <summary>
        /// Linked list of filesystems (unused in disk format, always 0).
        /// </summary>
        public uint fs_link { get; set; }

        // =================================================================
        // BASIC GEOMETRY (offset 4-31, 28 bytes)
        // =================================================================

        /// <summary>
        /// Block address of super-block (usually 16 for standard FFS at 8192 bytes).
        /// </summary>
        public uint fs_sblkno { get; set; }

        /// <summary>
        /// Block address of cylinder group block (usually 24).
        /// </summary>
        public uint fs_cblkno { get; set; }

        /// <summary>
        /// Block address of inode blocks (usually 32).
        /// </summary>
        public uint fs_iblkno { get; set; }

        /// <summary>
        /// Block address of first data block after cyl group overhead.
        /// </summary>
        public uint fs_dblkno { get; set; }

        /// <summary>
        /// Cylinder group offset in cylinder.
        /// </summary>
        public uint fs_cgoffset { get; set; }

        /// <summary>
        /// Used to calculate mod fs_cgmask.
        /// </summary>
        public uint fs_cgmask { get; set; }

        /// <summary>
        /// Last time written (Unix timestamp).
        /// </summary>
        public uint fs_time { get; set; }

        // =================================================================
        // FILESYSTEM SIZE (offset 32-47, 16 bytes)
        // =================================================================

        /// <summary>
        /// Number of sectors in filesystem.
        /// </summary>
        public uint fs_size { get; set; }

        /// <summary>
        /// Number of sectors in data area.
        /// </summary>
        public uint fs_dsize { get; set; }

        /// <summary>
        /// Number of cylinder groups.
        /// </summary>
        public uint fs_ncg { get; set; }

        /// <summary>
        /// Size of basic blocks in filesystem (4096 or 8192 bytes).
        /// </summary>
        public uint fs_bsize { get; set; }

        // =================================================================
        // FRAGMENT SUPPORT (offset 48-59, 12 bytes)
        // =================================================================

        /// <summary>
        /// Size of frag blocks in filesystem (512, 1024, 2048, or 4096 bytes).
        /// </summary>
        public uint fs_fsize { get; set; }

        /// <summary>
        /// Number of frags in a block (fs_bsize / fs_fsize).
        /// </summary>
        public uint fs_frag { get; set; }

        // =================================================================
        // ALLOCATION LIMITS (offset 60-79, 20 bytes)
        // =================================================================

        /// <summary>
        /// Minimum % of free blocks.
        /// </summary>
        public uint fs_minfree { get; set; }

        /// <summary>
        /// Rotational delay (milliseconds).
        /// </summary>
        public uint fs_rotdelay { get; set; }

        /// <summary>
        /// Disk revolutions per second (RPM / 60).
        /// </summary>
        public uint fs_rps { get; set; }

        // =================================================================
        // ALLOCATION MASKS (offset 80-95, 16 bytes)
        // =================================================================

        /// <summary>
        /// fs_bmask = ~(fs_bsize - 1).
        /// </summary>
        public uint fs_bmask { get; set; }

        /// <summary>
        /// fs_fmask = ~(fs_fsize - 1).
        /// </summary>
        public uint fs_fmask { get; set; }

        /// <summary>
        /// fs_bshift = log2(fs_bsize).
        /// </summary>
        public uint fs_bshift { get; set; }

        /// <summary>
        /// fs_fshift = log2(fs_fsize).
        /// </summary>
        public uint fs_fshift { get; set; }

        // =================================================================
        // BLOCK ALLOCATION PARAMETERS (offset 96-119, 24 bytes)
        // =================================================================

        /// <summary>
        /// Maximum contiguous blocks.
        /// </summary>
        public uint fs_maxcontig { get; set; }

        /// <summary>
        /// Maximum blocks per cylinder group.
        /// </summary>
        public uint fs_maxbpg { get; set; }

        /// <summary>
        /// fs_fragshift = log2(fs_frag).
        /// </summary>
        public uint fs_fragshift { get; set; }

        /// <summary>
        /// fs_fsbtodb: block-to-DEV_BSIZE shift.
        /// </summary>
        public uint fs_fsbtodb { get; set; }

        /// <summary>
        /// Size of frag blocks in bytes.
        /// </summary>
        public uint fs_sbsize { get; set; }

        /// <summary>
        /// Actual size of cylinder group summary.
        /// </summary>
        public uint fs_csmask { get; set; }

        /// <summary>
        /// fs_csshift: csum block number shift.
        /// </summary>
        public uint fs_csshift { get; set; }

        /// <summary>
        /// fs_nindir: Number of indirect blocks (NINDIR).
        /// </summary>
        public uint fs_nindir { get; set; }

        /// <summary>
        /// fs_inopb: Number of inodes per block (INOPB). CRITICAL for inode location calculation!
        /// </summary>
        public uint fs_inopb { get; set; }

        /// <summary>
        /// fs_nspf: Number of sectors per fragment (NSPF).
        /// </summary>
        public uint fs_nspf { get; set; }

        // =================================================================
        // CYLINDER GROUP PARAMETERS (offset 128-143, 16 bytes)
        // =================================================================

        /// <summary>
        /// Actual number of sectors per track.
        /// </summary>
        public uint fs_ntrak { get; set; }

        /// <summary>
        /// Actual number of sectors per cylinder.
        /// </summary>
        public uint fs_nsect { get; set; }

        /// <summary>
        /// Actual number of sectors per track.
        /// </summary>
        public uint fs_spc { get; set; }

        /// <summary>
        /// Number of cylinders in filesystem.
        /// </summary>
        public uint fs_ncyl { get; set; }

        /// <summary>
        /// Number of cylinders per cylinder group.
        /// </summary>
        public uint fs_cpg { get; set; }

        /// <summary>
        /// Number of inodes per cylinder group.
        /// </summary>
        public uint fs_ipg { get; set; }

        // =================================================================
        // FREE SPACE COUNTERS (offset 144-167, 24 bytes)
        // =================================================================

        /// <summary>
        /// Number of frags per cylinder group.
        /// </summary>
        public uint fs_fpg { get; set; }

        /// <summary>
        /// Total number of directories.
        /// </summary>
        public uint fs_cstotal_cs_ndir { get; set; }

        /// <summary>
        /// Total number of free blocks.
        /// </summary>
        public uint fs_cstotal_cs_nbfree { get; set; }

        /// <summary>
        /// Total number of free inodes.
        /// </summary>
        public uint fs_cstotal_cs_nifree { get; set; }

        /// <summary>
        /// Total number of free fragments.
        /// </summary>
        public uint fs_cstotal_cs_nffree { get; set; }

        // =================================================================
        // FILESYSTEM STATE AND FLAGS (offset 168-687, 520 bytes)
        // =================================================================

        /// <summary>
        /// Filesystem clean flag (1 = clean, 0 = needs fsck).
        /// </summary>
        public byte fs_fmod { get; set; }

        /// <summary>
        /// Filesystem read-only flag.
        /// </summary>
        public byte fs_clean { get; set; }

        /// <summary>
        /// Filesystem is mounted read-only.
        /// </summary>
        public byte fs_ronly { get; set; }

        /// <summary>
        /// Filesystem flags.
        /// </summary>
        public byte fs_flags { get; set; }

        /// <summary>
        /// Filesystem mount name (512 bytes).
        /// </summary>
        public string fs_fsmnt { get; set; } = string.Empty;

        // =================================================================
        // CYLINDER GROUP SUMMARY AREA (offset 688-699, 12 bytes)
        // =================================================================

        /// <summary>
        /// Block address of cylinder summary area.
        /// </summary>
        public uint fs_csaddr { get; set; }

        /// <summary>
        /// Size of cylinder group summary area.
        /// </summary>
        public uint fs_cssize { get; set; }

        /// <summary>
        /// Cylinder group size.
        /// </summary>
        public uint fs_cgsize { get; set; }

        // =================================================================
        // ROTATION OPTIMIZATION (offset 700-711, 12 bytes)
        // =================================================================

        /// <summary>
        /// Number of rotational positions (usually 8).
        /// </summary>
        public uint fs_nrpos { get; set; }

        /// <summary>
        /// Post-processing block list offset.
        /// </summary>
        public uint fs_postbloff { get; set; }

        /// <summary>
        /// Rotational block list head offset.
        /// </summary>
        public uint fs_rotbloff { get; set; }

        // =================================================================
        // MAGIC NUMBER (offset 1372, 4 bytes)
        // =================================================================

        /// <summary>
        /// FFS magic number (0x011954).
        /// Located at offset 1372 bytes from start of superblock.
        /// </summary>
        public uint fs_magic { get; set; }

        // =================================================================
        // OPTIMIZATION PREFERENCE (offset 1376, 4 bytes)
        // =================================================================

        /// <summary>
        /// Optimization preference (FS_OPTTIME or FS_OPTSPACE).
        /// </summary>
        public uint fs_optim { get; set; }

        // =================================================================
        // HELPER PROPERTIES
        // =================================================================

        /// <summary>
        /// True if filesystem is clean (properly unmounted).
        /// </summary>
        public bool IsClean => fs_clean != 0;

        /// <summary>
        /// True if filesystem is read-only.
        /// </summary>
        public bool IsReadOnly => fs_ronly != 0;

        /// <summary>
        /// Last update time as DateTime.
        /// </summary>
        public DateTime LastUpdateTime => DateTimeOffset.FromUnixTimeSeconds(fs_time).DateTime;

        /// <summary>
        /// Total number of inodes in filesystem.
        /// </summary>
        public uint TotalInodes => fs_ncg * fs_ipg;

        /// <summary>
        /// Blocks per cylinder group.
        /// </summary>
        public uint BlocksPerCylinderGroup => fs_fpg / fs_frag;

        /// <summary>
        /// Number of inodes per block.
        /// </summary>
        public int InodesPerBlock => (int)(fs_bsize / FfsConstants.INODE_SIZE);

        // =================================================================
        // VALIDATION
        // =================================================================

        /// <summary>
        /// Validates superblock structure.
        /// </summary>
        public bool Validate()
        {
            // Check magic number
            if (fs_magic != FfsConstants.FS_MAGIC)
                return false;

            // Check block size
            if (fs_bsize != FfsConstants.MIN_BSIZE && fs_bsize != FfsConstants.MAX_BSIZE)
                return false;

            // Check fragment size
            if (fs_fsize < FfsConstants.MIN_FSIZE || fs_fsize > FfsConstants.MAX_FSIZE)
                return false;

            // Check fragments per block
            if (fs_frag == 0 || fs_frag != fs_bsize / fs_fsize)
                return false;

            // Check cylinder groups
            if (fs_ncg < FfsConstants.MIN_NCYL || fs_ncg > FfsConstants.MAX_NCYL)
                return false;

            // Check inodes per group
            if (fs_ipg == 0)
                return false;

            return true;
        }

        // =================================================================
        // PARSING
        // =================================================================

        /// <summary>
        /// Parses superblock from byte array (big-endian format for SunOS).
        /// Superblock should start at offset 8192 bytes from filesystem start.
        /// </summary>
        public static FfsSuperblock Parse(byte[] buffer, int offset = 0)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (offset < 0 || offset + FfsConstants.SBSIZE > buffer.Length)
                throw new ArgumentException($"Buffer too small. Need {FfsConstants.SBSIZE} bytes from offset {offset}.");

            var sb = new FfsSuperblock();

            // Read all fields using big-endian format (SunOS)
            // Based on actual SunOS 3.4 fs.h structure (squashfs-root/sun/sys/ufs/fs.h)

            // struct fs_link, fs_rlink (0-7)
            sb.fs_link = BsdEndianHelper.ReadUInt32BE(buffer, offset + 0);
            // Skip fs_rlink pointer (offset + 4)

            // Block addresses (8-23)
            sb.fs_sblkno = BsdEndianHelper.ReadUInt32BE(buffer, offset + 8);
            sb.fs_cblkno = BsdEndianHelper.ReadUInt32BE(buffer, offset + 12);
            sb.fs_iblkno = BsdEndianHelper.ReadUInt32BE(buffer, offset + 16);
            sb.fs_dblkno = BsdEndianHelper.ReadUInt32BE(buffer, offset + 20);

            // Cylinder group parameters (24-35)
            sb.fs_cgoffset = BsdEndianHelper.ReadUInt32BE(buffer, offset + 24);
            sb.fs_cgmask = BsdEndianHelper.ReadUInt32BE(buffer, offset + 28);
            sb.fs_time = BsdEndianHelper.ReadUInt32BE(buffer, offset + 32);

            // Filesystem size (36-47)
            sb.fs_size = BsdEndianHelper.ReadUInt32BE(buffer, offset + 36);
            sb.fs_dsize = BsdEndianHelper.ReadUInt32BE(buffer, offset + 40);
            sb.fs_ncg = BsdEndianHelper.ReadUInt32BE(buffer, offset + 44);

            // Block and fragment sizes (48-59)
            sb.fs_bsize = BsdEndianHelper.ReadUInt32BE(buffer, offset + 48);
            sb.fs_fsize = BsdEndianHelper.ReadUInt32BE(buffer, offset + 52);
            sb.fs_frag = BsdEndianHelper.ReadUInt32BE(buffer, offset + 56);

            // Allocation parameters (60-67)
            sb.fs_minfree = BsdEndianHelper.ReadUInt32BE(buffer, offset + 60);
            sb.fs_rotdelay = BsdEndianHelper.ReadUInt32BE(buffer, offset + 64);
            sb.fs_rps = BsdEndianHelper.ReadUInt32BE(buffer, offset + 68);

            // Block masks (72-87)
            sb.fs_bmask = BsdEndianHelper.ReadUInt32BE(buffer, offset + 72);
            sb.fs_fmask = BsdEndianHelper.ReadUInt32BE(buffer, offset + 76);
            sb.fs_bshift = BsdEndianHelper.ReadUInt32BE(buffer, offset + 80);
            sb.fs_fshift = BsdEndianHelper.ReadUInt32BE(buffer, offset + 84);

            // Max allocation parameters (88-95)
            sb.fs_maxcontig = BsdEndianHelper.ReadUInt32BE(buffer, offset + 88);
            sb.fs_maxbpg = BsdEndianHelper.ReadUInt32BE(buffer, offset + 92);

            // Fragment parameters (96-119)
            sb.fs_fragshift = BsdEndianHelper.ReadUInt32BE(buffer, offset + 96);
            sb.fs_fsbtodb = BsdEndianHelper.ReadUInt32BE(buffer, offset + 100);
            sb.fs_sbsize = BsdEndianHelper.ReadUInt32BE(buffer, offset + 104);
            sb.fs_csmask = BsdEndianHelper.ReadUInt32BE(buffer, offset + 108);

            // Inode/block parameters (112-127) - CRITICAL FOR INODE ADDRESSING!
            sb.fs_csshift = BsdEndianHelper.ReadUInt32BE(buffer, offset + 112);
            sb.fs_nindir = BsdEndianHelper.ReadUInt32BE(buffer, offset + 116);
            sb.fs_inopb = BsdEndianHelper.ReadUInt32BE(buffer, offset + 120);
            sb.fs_nspf = BsdEndianHelper.ReadUInt32BE(buffer, offset + 124);

            // Optimization and id fields (128-143)
            // Skip fs_optim, fs_sparecon[3], fs_id[2] (128-151)

            // Cylinder summary area (152-163)
            sb.fs_csaddr = BsdEndianHelper.ReadUInt32BE(buffer, offset + 152);
            sb.fs_cssize = BsdEndianHelper.ReadUInt32BE(buffer, offset + 156);
            sb.fs_cgsize = BsdEndianHelper.ReadUInt32BE(buffer, offset + 160);

            // Disk geometry (164-183)
            sb.fs_ntrak = BsdEndianHelper.ReadUInt32BE(buffer, offset + 164);
            sb.fs_nsect = BsdEndianHelper.ReadUInt32BE(buffer, offset + 168);
            sb.fs_spc = BsdEndianHelper.ReadUInt32BE(buffer, offset + 172);
            sb.fs_ncyl = BsdEndianHelper.ReadUInt32BE(buffer, offset + 176);

            // Cylinder group parameters (180-191)
            sb.fs_cpg = BsdEndianHelper.ReadUInt32BE(buffer, offset + 180);
            sb.fs_ipg = BsdEndianHelper.ReadUInt32BE(buffer, offset + 184);
            sb.fs_fpg = BsdEndianHelper.ReadUInt32BE(buffer, offset + 188);

            // Cylinder group summary totals (192-207, struct csum = 16 bytes)
            sb.fs_cstotal_cs_ndir = BsdEndianHelper.ReadUInt32BE(buffer, offset + 192);
            sb.fs_cstotal_cs_nbfree = BsdEndianHelper.ReadUInt32BE(buffer, offset + 196);
            sb.fs_cstotal_cs_nifree = BsdEndianHelper.ReadUInt32BE(buffer, offset + 200);
            sb.fs_cstotal_cs_nffree = BsdEndianHelper.ReadUInt32BE(buffer, offset + 204);

            // Filesystem state flags (208-211)
            sb.fs_fmod = buffer[offset + 208];
            sb.fs_clean = buffer[offset + 209];
            sb.fs_ronly = buffer[offset + 210];
            sb.fs_flags = buffer[offset + 211];

            // Mount name (212-723, 512 bytes)
            sb.fs_fsmnt = ReadString(buffer, offset + 212, 512);

            // Skip fs_cgrotor, fs_csp[] pointer array, fs_cpc (724-...)

            // Rotation optimization - skip for now, not critical
            // sb.fs_nrpos = BsdEndianHelper.ReadUInt32BE(buffer, offset + ???);
            // sb.fs_postbloff = BsdEndianHelper.ReadUInt32BE(buffer, offset + ???);
            // sb.fs_rotbloff = BsdEndianHelper.ReadUInt32BE(buffer, offset + ???);

            // Magic number (offset 1372, 4 bytes from start of superblock)
            sb.fs_magic = BsdEndianHelper.ReadUInt32BE(buffer, offset + 1372);

            // Optimization preference (offset 1376)
            if (offset + 1376 + 4 <= buffer.Length)
            {
                sb.fs_optim = BsdEndianHelper.ReadUInt32BE(buffer, offset + 1376);
            }

            return sb;
        }

        private static string ReadString(byte[] buffer, int offset, int maxLength)
        {
            int length = 0;
            while (length < maxLength && offset + length < buffer.Length && buffer[offset + length] != 0)
            {
                length++;
            }

            if (length == 0)
                return string.Empty;

            return System.Text.Encoding.ASCII.GetString(buffer, offset, length);
        }

        /// <summary>
        /// Returns a text with the mount name, magic, size, block and fragment size, group counts and the result of the validity check.
        /// </summary>
        /// <returns>
        /// The text.
        /// </returns>
        public override string ToString()
        {
            return $"FFS Superblock: {fs_fsmnt}, " +
                   $"Magic=0x{fs_magic:X6}, " +
                   $"Size={fs_size} sectors, " +
                   $"BlockSize={fs_bsize}, FragSize={fs_fsize}, " +
                   $"CylGroups={fs_ncg}, InodesPerGroup={fs_ipg}, " +
                   $"Valid={Validate()}";
        }
    }
}
