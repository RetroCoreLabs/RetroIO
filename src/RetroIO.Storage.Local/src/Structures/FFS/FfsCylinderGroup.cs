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
    /// FFS cylinder group structure.
    /// Contains metadata and allocation bitmaps for a group of cylinders.
    /// Cylinder groups enable efficient block allocation and fault isolation.
    /// </summary>
    public class FfsCylinderGroup
    {
        // =================================================================
        // CYLINDER GROUP HEADER (first part of structure)
        // =================================================================

        /// <summary>
        /// Link in free list (unused, always 0).
        /// </summary>
        public uint cg_link { get; set; }

        /// <summary>
        /// Cylinder group magic number (0x090255).
        /// </summary>
        public uint cg_magic { get; set; }

        /// <summary>
        /// Time last written (Unix timestamp).
        /// </summary>
        public uint cg_time { get; set; }

        /// <summary>
        /// Cylinder group number (0-based).
        /// </summary>
        public uint cg_cgx { get; set; }

        /// <summary>
        /// Number of cylinders in this group.
        /// </summary>
        public ushort cg_ncyl { get; set; }

        /// <summary>
        /// Number of inodes in this group.
        /// </summary>
        public ushort cg_niblk { get; set; }

        /// <summary>
        /// Number of data blocks in this group.
        /// </summary>
        public uint cg_ndblk { get; set; }

        // =================================================================
        // CYLINDER GROUP SUMMARY (allocation counts)
        // =================================================================

        /// <summary>
        /// Number of directories in this group.
        /// </summary>
        public uint cg_cs_ndir { get; set; }

        /// <summary>
        /// Number of free blocks in this group.
        /// </summary>
        public uint cg_cs_nbfree { get; set; }

        /// <summary>
        /// Number of free inodes in this group.
        /// </summary>
        public uint cg_cs_nifree { get; set; }

        /// <summary>
        /// Number of free fragments in this group.
        /// </summary>
        public uint cg_cs_nffree { get; set; }

        // =================================================================
        // ROTATIONAL POSITION OPTIMIZATION
        // =================================================================

        /// <summary>
        /// Number of rotational positions tracked.
        /// </summary>
        public uint cg_nrpos { get; set; }

        /// <summary>
        /// Offset to block map (from start of cylinder group structure).
        /// </summary>
        public uint cg_btotoff { get; set; }

        /// <summary>
        /// Offset to free block bitmap (from start of cylinder group structure).
        /// </summary>
        public uint cg_boff { get; set; }

        /// <summary>
        /// Offset to free inode bitmap (from start of cylinder group structure).
        /// </summary>
        public uint cg_iusedoff { get; set; }

        /// <summary>
        /// Offset to free fragment bitmap (from start of cylinder group structure).
        /// </summary>
        public uint cg_freeoff { get; set; }

        /// <summary>
        /// Offset to next cylinder group (from start of cylinder group structure).
        /// </summary>
        public uint cg_nextfreeoff { get; set; }

        // =================================================================
        // HELPER PROPERTIES
        // =================================================================

        /// <summary>
        /// True if cylinder group has valid magic number.
        /// </summary>
        public bool IsValid => cg_magic == FfsConstants.CG_MAGIC;

        /// <summary>
        /// Last update time as DateTime.
        /// </summary>
        public DateTime LastUpdateTime => DateTimeOffset.FromUnixTimeSeconds(cg_time).DateTime;

        /// <summary>
        /// Percentage of free blocks in this cylinder group.
        /// </summary>
        public double PercentFree
        {
            get
            {
                if (cg_ndblk == 0)
                    return 0.0;
                return (cg_cs_nbfree * 100.0) / cg_ndblk;
            }
        }

        /// <summary>
        /// Percentage of free inodes in this cylinder group.
        /// </summary>
        public double PercentFreeInodes
        {
            get
            {
                if (cg_niblk == 0)
                    return 0.0;
                return (cg_cs_nifree * 100.0) / cg_niblk;
            }
        }

        // =================================================================
        // PARSING
        // =================================================================

        /// <summary>
        /// Parses cylinder group from byte array (big-endian format for SunOS).
        /// </summary>
        /// <param name="buffer">
        /// Buffer containing cylinder group structure.
        /// </param>
        /// <param name="offset">
        /// Offset where structure starts.
        /// </param>
        /// <returns>
        /// Parsed cylinder group.
        /// </returns>
        public static FfsCylinderGroup Parse(byte[] buffer, int offset = 0)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (offset < 0 || offset + 128 > buffer.Length)  // Minimum size for header
                throw new ArgumentOutOfRangeException(nameof(offset));

            var cg = new FfsCylinderGroup();

            // Read header fields (big-endian)
            cg.cg_link = BsdEndianHelper.ReadUInt32BE(buffer, offset + 0);
            cg.cg_magic = BsdEndianHelper.ReadUInt32BE(buffer, offset + 4);
            cg.cg_time = BsdEndianHelper.ReadUInt32BE(buffer, offset + 8);
            cg.cg_cgx = BsdEndianHelper.ReadUInt32BE(buffer, offset + 12);

            cg.cg_ncyl = ReadUInt16BE(buffer, offset + 16);
            cg.cg_niblk = ReadUInt16BE(buffer, offset + 18);
            cg.cg_ndblk = BsdEndianHelper.ReadUInt32BE(buffer, offset + 20);

            // Cylinder group summary (offset 24-39, 16 bytes)
            cg.cg_cs_ndir = BsdEndianHelper.ReadUInt32BE(buffer, offset + 24);
            cg.cg_cs_nbfree = BsdEndianHelper.ReadUInt32BE(buffer, offset + 28);
            cg.cg_cs_nifree = BsdEndianHelper.ReadUInt32BE(buffer, offset + 32);
            cg.cg_cs_nffree = BsdEndianHelper.ReadUInt32BE(buffer, offset + 36);

            // Rotational optimization offsets (offset 40-63)
            cg.cg_nrpos = BsdEndianHelper.ReadUInt32BE(buffer, offset + 40);
            cg.cg_btotoff = BsdEndianHelper.ReadUInt32BE(buffer, offset + 44);
            cg.cg_boff = BsdEndianHelper.ReadUInt32BE(buffer, offset + 48);
            cg.cg_iusedoff = BsdEndianHelper.ReadUInt32BE(buffer, offset + 52);
            cg.cg_freeoff = BsdEndianHelper.ReadUInt32BE(buffer, offset + 56);
            cg.cg_nextfreeoff = BsdEndianHelper.ReadUInt32BE(buffer, offset + 60);

            return cg;
        }

        private static ushort ReadUInt16BE(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        /// <summary>
        /// Validates cylinder group structure.
        /// </summary>
        public bool Validate()
        {
            // Check magic number
            if (cg_magic != FfsConstants.CG_MAGIC)
                return false;

            // Check counts are reasonable
            if (cg_cs_nbfree > cg_ndblk)
                return false;

            if (cg_cs_nifree > cg_niblk)
                return false;

            return true;
        }

        public override string ToString()
        {
            return $"Cylinder Group {cg_cgx}: " +
                   $"Magic=0x{cg_magic:X6}, " +
                   $"Blocks={cg_ndblk} ({cg_cs_nbfree} free), " +
                   $"Inodes={cg_niblk} ({cg_cs_nifree} free), " +
                   $"Dirs={cg_cs_ndir}, " +
                   $"Valid={IsValid}";
        }
    }
}
