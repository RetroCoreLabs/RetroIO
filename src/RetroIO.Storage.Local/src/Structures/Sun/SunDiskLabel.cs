//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;

namespace RetroIO.Storage.Local.Sun.Structures
{
    /// <summary>
    /// Sun Disk Label (also called Sun VTOC - Volume Table of Contents).
    ///
    /// Sun disk labels are stored at sector 0 and use a different format than BSD Disk Labels.
    /// They are used by SunOS, Solaris, and other Sun Unix variants.
    ///
    /// Structure (512 bytes total):
    /// - ASCII label text (128 bytes)
    /// - VTOC (Volume Table of Contents) containing up to 8 partitions
    /// - Geometry information
    /// - Magic number for validation
    /// </summary>
    public class SunDiskLabel
    {
        // Sun disk label magic number at offset 508-509 (0xDABE in big-endian)
        /// <summary>
        /// The magic number of a Sun disk label, 0xDABE.
        /// </summary>
        public const ushort SUN_LABEL_MAGIC = 0xDABE;

        // Maximum partitions in Sun disk label
        /// <summary>
        /// The most partitions a Sun disk label holds.
        /// </summary>
        public const int MAX_PARTITIONS = 8;

        // =================================================================
        // HEADER FIELDS
        // =================================================================

        /// <summary>
        /// ASCII label text (128 bytes).
        /// </summary>
        public string LabelText { get; set; } = string.Empty;

        /// <summary>
        /// Number of cylinders.
        /// </summary>
        public ushort NumCylinders { get; set; }

        /// <summary>
        /// Number of alternate cylinders.
        /// </summary>
        public ushort NumAltCylinders { get; set; }

        /// <summary>
        /// Number of heads (tracks per cylinder).
        /// </summary>
        public ushort NumHeads { get; set; }

        /// <summary>
        /// Number of sectors per track.
        /// </summary>
        public ushort NumSectors { get; set; }

        /// <summary>
        /// Interleave factor.
        /// </summary>
        public ushort Interleave { get; set; }

        /// <summary>
        /// Data cylinders per unit.
        /// </summary>
        public ushort DataCylinders { get; set; }

        /// <summary>
        /// Alternate cylinders per unit.
        /// </summary>
        public ushort AltCylinders { get; set; }

        /// <summary>
        /// Physical cylinders per unit.
        /// </summary>
        public ushort PhysCylinders { get; set; }

        /// <summary>
        /// RPM (rotations per minute).
        /// </summary>
        public ushort Rpm { get; set; }

        /// <summary>
        /// Write precompensation cylinder.
        /// </summary>
        public ushort WritePrecomp { get; set; }

        /// <summary>
        /// Reduced write current cylinder.
        /// </summary>
        public ushort ReducedWriteCurrent { get; set; }

        /// <summary>
        /// Partition table (up to 8 partitions).
        /// </summary>
        public SunPartition[] Partitions { get; set; } = new SunPartition[MAX_PARTITIONS];

        /// <summary>
        /// Magic number (must be 0xDABE).
        /// </summary>
        public ushort Magic { get; set; }

        /// <summary>
        /// Checksum of disk label.
        /// </summary>
        public ushort Checksum { get; set; }

        // =================================================================
        // VALIDATION
        // =================================================================

        /// <summary>
        /// Validates the Sun disk label.
        /// </summary>
        public bool IsValid()
        {
            // Check magic number
            if (Magic != SUN_LABEL_MAGIC)
                return false;

            // Check geometry sanity
            if (NumCylinders == 0 || NumHeads == 0 || NumSectors == 0)
                return false;

            if (NumHeads > 255 || NumSectors > 255)
                return false;

            return true;
        }

        // =================================================================
        // PARSING
        // =================================================================

        /// <summary>
        /// Parses a Sun disk label from byte array.
        /// </summary>
        public static SunDiskLabel? Parse(byte[] buffer)
        {
            if (buffer == null || buffer.Length < 512)
                return null;

            var label = new SunDiskLabel();

            try
            {
                // Parse ASCII label text (offset 0, 128 bytes)
                label.LabelText = Encoding.ASCII.GetString(buffer, 0, 128).TrimEnd('\0');

                // Parse geometry (offset 432, all 2-byte big-endian fields packed together)
                // Based on actual Sun disk label format at offset 0x1B0 (432 decimal)
                int geomOffset = 432;
                label.NumCylinders = ReadUInt16BE(buffer, geomOffset + 0);       // 432
                label.NumAltCylinders = ReadUInt16BE(buffer, geomOffset + 2);    // 434
                label.NumHeads = ReadUInt16BE(buffer, geomOffset + 4);           // 436
                label.NumSectors = ReadUInt16BE(buffer, geomOffset + 6);         // 438
                label.Interleave = ReadUInt16BE(buffer, geomOffset + 8);         // 440
                label.DataCylinders = ReadUInt16BE(buffer, geomOffset + 10);     // 442
                label.AltCylinders = ReadUInt16BE(buffer, geomOffset + 12);      // 444 (overlaps with partition table - may be unused)
                label.PhysCylinders = ReadUInt16BE(buffer, geomOffset + 14);     // 446 (overlaps with partition table - may be unused)
                label.Rpm = ReadUInt16BE(buffer, geomOffset + 16);               // 448 (overlaps with partition table - may be unused)
                label.WritePrecomp = ReadUInt16BE(buffer, geomOffset + 18);      // 450 (overlaps with partition table - may be unused)
                label.ReducedWriteCurrent = ReadUInt16BE(buffer, geomOffset + 20); // 452

                // Parse VTOC partition tags and flags (offset 142, 8 partitions × 4 bytes each)
                // Each partition has: 2 bytes tag + 2 bytes flags (big-endian)
                int vtocOffset = 142;
                ushort[] tags = new ushort[MAX_PARTITIONS];
                ushort[] flags = new ushort[MAX_PARTITIONS];
                for (int i = 0; i < MAX_PARTITIONS; i++)
                {
                    tags[i] = ReadUInt16BE(buffer, vtocOffset + (i * 4));
                    flags[i] = ReadUInt16BE(buffer, vtocOffset + (i * 4) + 2);
                }

                // Parse partitions (offset 444, 8 partitions × 8 bytes each)
                // Verified by hex dump analysis of actual Sun disk images
                int partOffset = 444;
                for (int i = 0; i < MAX_PARTITIONS; i++)
                {
                    label.Partitions[i] = SunPartition.Parse(buffer, partOffset, tags[i], flags[i]);
                    partOffset += 8;
                }

                // Parse magic and checksum (offset 508-511)
                label.Magic = ReadUInt16BE(buffer, 508);
                label.Checksum = ReadUInt16BE(buffer, 510);

                return label;
            }
            catch
            {
                return null;
            }
        }

        private static ushort ReadUInt16BE(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        private static uint ReadUInt32BE(byte[] buffer, int offset)
        {
            return (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) |
                         (buffer[offset + 2] << 8) | buffer[offset + 3]);
        }

        /// <summary>
        /// Gets total disk size in sectors.
        /// </summary>
        public uint GetTotalSectors()
        {
            return (uint)(NumCylinders * NumHeads * NumSectors);
        }

        /// <summary>
        /// Gets geometry information as string.
        /// </summary>
        public string GetGeometryString()
        {
            return $"{NumCylinders} cylinders × {NumHeads} heads × {NumSectors} sectors = {GetTotalSectors()} sectors";
        }
    }

    /// <summary>
    /// Sun partition table entry (8 bytes).
    /// </summary>
    public struct SunPartition
    {
        /// <summary>
        /// Starting cylinder.
        /// </summary>
        public uint StartCylinder;

        /// <summary>
        /// Number of sectors in partition.
        /// </summary>
        public uint NumSectors;

        /// <summary>
        /// Partition tag (usage type).
        /// </summary>
        public ushort Tag;

        /// <summary>
        /// Partition flags.
        /// </summary>
        public ushort Flags;

        /// <summary>
        /// True if partition is unused.
        /// </summary>
        public readonly bool IsUnused => NumSectors == 0;

        /// <summary>
        /// Parses a Sun partition from byte array.
        /// Sun partition format (from SunOS dklabel.h): dkl_cylno (4 bytes BE), then dkl_nblk (4 bytes BE)
        /// Tags and flags are stored separately in the VTOC section at offset 142.
        /// </summary>
        public static SunPartition Parse(byte[] buffer, int offset, ushort tag, ushort flags)
        {
            SunPartition partition;
            // Official Sun format from dklabel.h: dkl_cylno FIRST, then dkl_nblk SECOND
            partition.StartCylinder = ReadUInt32BE(buffer, offset);      // dkl_cylno (starting cylinder)
            partition.NumSectors = ReadUInt32BE(buffer, offset + 4);     // dkl_nblk (number of blocks)
            partition.Tag = tag;   // From VTOC at offset 142+ (2 bytes per partition)
            partition.Flags = flags; // From VTOC at offset 144+ (2 bytes per partition)

            return partition;
        }

        private static uint ReadUInt32BE(byte[] buffer, int offset)
        {
            return (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) |
                         (buffer[offset + 2] << 8) | buffer[offset + 3]);
        }

        /// <summary>
        /// Gets partition tag name.
        /// For old SunOS 3.x images without VTOC tags, this will return "unassigned" (tag 0).
        /// Use GetDisplayName() for better fallback naming.
        /// </summary>
        public readonly string GetTagName()
        {
            return Tag switch
            {
                0 => "unassigned",
                1 => "boot",
                2 => "root",
                3 => "swap",
                4 => "usr",
                5 => "backup",
                6 => "stand",
                7 => "var",
                8 => "home",
                _ => $"tag-{Tag}"
            };
        }

        /// <summary>
        /// Gets display name for partition.
        /// If tag is 0 (unassigned), uses conventional Sun partition naming as fallback.
        /// This handles old SunOS 3.x images that don't have VTOC tags.
        /// </summary>
        public readonly string GetDisplayName(char partitionLetter)
        {
            // If tag is set (non-zero), use the actual tag name
            if (Tag != 0)
            {
                return GetTagName();
            }

            // Fallback: Use conventional Sun partition names for old images without VTOC
            return partitionLetter switch
            {
                'a' => "root (conventional)",
                'b' => "swap (conventional)",
                'c' => "whole disk (conventional)",
                'd' => "usr (conventional)",
                'e' => "var (conventional)",
                'f' => "home (conventional)",
                'g' => "alternates (conventional)",
                'h' => "boot (conventional)",
                _ => "unassigned"
            };
        }

        /// <summary>
        /// True when this partition is raw swap and therefore carries no filesystem.
        ///
        /// Attempting to mount swap always fails, and that failure is expected rather than an
        /// error — callers that walk every partition need to tell the two apart.
        ///
        /// Uses the VTOC tag when the image has one (tag 3 = swap). Old SunOS 3.x labels carry
        /// no VTOC tags at all (every tag reads 0 — verified on both sun2-sunos-3.2.img and
        /// SUN2.img), so for those we fall back to the Sun convention that partition 'b' is
        /// swap, exactly as <see cref="GetDisplayName"/> already does for naming.
        /// </summary>
        /// <param name="partitionLetter">
        /// Partition letter ('a'..'h').
        /// </param>
        /// <returns>
        /// True if this partition should be treated as swap.
        /// </returns>
        public readonly bool IsSwap(char partitionLetter)
        {
            if (Tag != 0)
            {
                return Tag == 3;
            }

            return partitionLetter == 'b';
        }

        /// <summary>
        /// True when this partition spans the entire disk rather than holding its own
        /// filesystem ('c' by Sun convention, tag 5 = backup when tagged).
        /// </summary>
        /// <param name="partitionLetter">
        /// Partition letter ('a'..'h').
        /// </param>
        /// <returns>
        /// True if this partition is the whole-disk/backup slice.
        /// </returns>
        public readonly bool IsWholeDisk(char partitionLetter)
        {
            if (Tag != 0)
            {
                return Tag == 5;
            }

            return partitionLetter == 'c';
        }
    }
}
