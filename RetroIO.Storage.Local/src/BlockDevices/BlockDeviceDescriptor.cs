//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.BlockDevices
{
    /// <summary>
    /// Configuration descriptor for block device geometry.
    /// Replaces the need for separate D64/D71/D81/etc. device classes.
    /// Instead of code duplication, we use data-driven configuration.
    /// </summary>
    public sealed class BlockDeviceDescriptor
    {
        /// <summary>
        /// Human-readable name for this device type.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Block size in bytes (256 for Commodore, 512 for PC/Amiga/etc).
        /// </summary>
        public int BlockSize { get; set; }

        /// <summary>
        /// Addressing mode used by this device.
        /// </summary>
        public BlockAddressMode AddressMode { get; set; }

        /// <summary>
        /// Total number of tracks (for Track/Sector addressing).
        /// </summary>
        public int TotalTracks { get; set; }

        /// <summary>
        /// Total number of cylinders (for CHS addressing).
        /// </summary>
        public int Cylinders { get; set; }

        /// <summary>
        /// Number of heads (for CHS addressing).
        /// </summary>
        public int Heads { get; set; }

        /// <summary>
        /// Sectors per track table (for variable geometry like D64 ZBR).
        /// Array is 1-indexed for track/sector addressing (index 0 unused).
        /// If null or empty, use UniformSectorsPerTrack instead.
        /// </summary>
        public int[]? SectorsPerTrackTable { get; set; }

        /// <summary>
        /// Uniform sectors per track (for flat geometry like D81, PC floppies).
        /// Used when SectorsPerTrackTable is null.
        /// </summary>
        public int UniformSectorsPerTrack { get; set; }

        /// <summary>
        /// Total number of blocks on the device.
        /// </summary>
        public uint TotalBlocks { get; set; }

        /// <summary>
        /// Expected image size in bytes (for validation during mount).
        /// </summary>
        public long ExpectedImageSize { get; set; }

        /// <summary>
        /// True if this device supports variable geometry (ZBR).
        /// </summary>
        public bool SupportsVariableGeometry { get; set; }

        // ===================================================================
        // PREDEFINED DESCRIPTORS - Commodore Disk Formats
        // ===================================================================

        /// <summary>
        /// Commodore D64 disk image (35 tracks, 1541 format).
        /// 174,848 bytes = 683 sectors × 256 bytes.
        /// Uses Zone Bit Recording (ZBR): 21/19/18/17 sectors per track.
        /// </summary>
        public static BlockDeviceDescriptor D64_35Track => new BlockDeviceDescriptor
        {
            Name = "Commodore D64 (35 tracks)",
            BlockSize = 256,
            AddressMode = BlockAddressMode.TrackSector,
            TotalTracks = 35,
            SectorsPerTrackTable = BuildD64SectorsPerTrack(35),
            TotalBlocks = 683,
            ExpectedImageSize = 174848,
            SupportsVariableGeometry = true
        };

        /// <summary>
        /// Commodore D64 disk image (40 tracks, extended format).
        /// 196,608 bytes = 768 sectors × 256 bytes.
        /// </summary>
        public static BlockDeviceDescriptor D64_40Track => new BlockDeviceDescriptor
        {
            Name = "Commodore D64 (40 tracks, extended)",
            BlockSize = 256,
            AddressMode = BlockAddressMode.TrackSector,
            TotalTracks = 40,
            SectorsPerTrackTable = BuildD64SectorsPerTrack(40),
            TotalBlocks = 768,
            ExpectedImageSize = 196608,
            SupportsVariableGeometry = true
        };

        /// <summary>
        /// Commodore D64 disk image (42 tracks, extended format).
        /// 205,312 bytes = 802 sectors × 256 bytes.
        /// </summary>
        public static BlockDeviceDescriptor D64_42Track => new BlockDeviceDescriptor
        {
            Name = "Commodore D64 (42 tracks, extended)",
            BlockSize = 256,
            AddressMode = BlockAddressMode.TrackSector,
            TotalTracks = 42,
            SectorsPerTrackTable = BuildD64SectorsPerTrack(42),
            TotalBlocks = 802,
            ExpectedImageSize = 205312,
            SupportsVariableGeometry = true
        };

        /// <summary>
        /// Commodore D71 disk image (70 tracks, 1571 double-sided format).
        /// 349,696 bytes = 1366 sectors × 256 bytes.
        /// Each side uses same ZBR pattern as D64.
        /// </summary>
        public static BlockDeviceDescriptor D71 => new BlockDeviceDescriptor
        {
            Name = "Commodore D71 (70 tracks, double-sided)",
            BlockSize = 256,
            AddressMode = BlockAddressMode.TrackSector,
            TotalTracks = 70,
            SectorsPerTrackTable = BuildD71SectorsPerTrack(),
            TotalBlocks = 1366,
            ExpectedImageSize = 349696,
            SupportsVariableGeometry = true
        };

        /// <summary>
        /// Commodore D81 disk image (80 tracks, 1581 3.5" HD format).
        /// 819,200 bytes = 3200 sectors × 256 bytes.
        /// Flat geometry: 40 sectors per track on all tracks.
        /// </summary>
        public static BlockDeviceDescriptor D81 => new BlockDeviceDescriptor
        {
            Name = "Commodore D81 (80 tracks, 3.5\" HD)",
            BlockSize = 256,
            AddressMode = BlockAddressMode.TrackSector,
            TotalTracks = 80,
            UniformSectorsPerTrack = 40,
            TotalBlocks = 3200,
            ExpectedImageSize = 819200,
            SupportsVariableGeometry = false
        };

        // ===================================================================
        // PREDEFINED DESCRIPTORS - PC Floppy Formats
        // ===================================================================

        /// <summary>
        /// PC 5.25" DD floppy (360KB).
        /// 368,640 bytes = 40 cylinders × 2 heads × 9 sectors × 512 bytes.
        /// </summary>
        public static BlockDeviceDescriptor PC_360K => new BlockDeviceDescriptor
        {
            Name = "PC 5.25\" DD (360KB)",
            BlockSize = 512,
            AddressMode = BlockAddressMode.CylinderHeadSector,
            Cylinders = 40,
            Heads = 2,
            UniformSectorsPerTrack = 9,
            TotalBlocks = 720,
            ExpectedImageSize = 368640,
            SupportsVariableGeometry = false
        };

        /// <summary>
        /// PC 3.5" DD floppy (720KB).
        /// 737,280 bytes = 80 cylinders × 2 heads × 9 sectors × 512 bytes.
        /// </summary>
        public static BlockDeviceDescriptor PC_720K => new BlockDeviceDescriptor
        {
            Name = "PC 3.5\" DD (720KB)",
            BlockSize = 512,
            AddressMode = BlockAddressMode.CylinderHeadSector,
            Cylinders = 80,
            Heads = 2,
            UniformSectorsPerTrack = 9,
            TotalBlocks = 1440,
            ExpectedImageSize = 737280,
            SupportsVariableGeometry = false
        };

        /// <summary>
        /// PC 5.25" HD floppy (1.2MB).
        /// 1,228,800 bytes = 80 cylinders × 2 heads × 15 sectors × 512 bytes.
        /// </summary>
        public static BlockDeviceDescriptor PC_1_2M => new BlockDeviceDescriptor
        {
            Name = "PC 5.25\" HD (1.2MB)",
            BlockSize = 512,
            AddressMode = BlockAddressMode.CylinderHeadSector,
            Cylinders = 80,
            Heads = 2,
            UniformSectorsPerTrack = 15,
            TotalBlocks = 2400,
            ExpectedImageSize = 1228800,
            SupportsVariableGeometry = false
        };

        /// <summary>
        /// PC 3.5" HD floppy (1.44MB).
        /// 1,474,560 bytes = 80 cylinders × 2 heads × 18 sectors × 512 bytes.
        /// </summary>
        public static BlockDeviceDescriptor PC_1_44M => new BlockDeviceDescriptor
        {
            Name = "PC 3.5\" HD (1.44MB)",
            BlockSize = 512,
            AddressMode = BlockAddressMode.CylinderHeadSector,
            Cylinders = 80,
            Heads = 2,
            UniformSectorsPerTrack = 18,
            TotalBlocks = 2880,
            ExpectedImageSize = 1474560,
            SupportsVariableGeometry = false
        };

        /// <summary>
        /// PC 3.5" ED floppy (2.88MB).
        /// 2,949,120 bytes = 80 cylinders × 2 heads × 36 sectors × 512 bytes.
        /// </summary>
        public static BlockDeviceDescriptor PC_2_88M => new BlockDeviceDescriptor
        {
            Name = "PC 3.5\" ED (2.88MB)",
            BlockSize = 512,
            AddressMode = BlockAddressMode.CylinderHeadSector,
            Cylinders = 80,
            Heads = 2,
            UniformSectorsPerTrack = 36,
            TotalBlocks = 5760,
            ExpectedImageSize = 2949120,
            SupportsVariableGeometry = false
        };

        // ===================================================================
        // PREDEFINED DESCRIPTORS - PC/XT and PC/AT Hard Disk Formats
        // ===================================================================
        //
        // Real ST-506/ST-412 MFM-era CHS geometries — all use 17 sectors/track, the norm for
        // these controllers, per the classic IBM XT/AT fixed drive-type tables (PCjs: XT types
        // 306/2/17, 306/4/17, 306/6/17, 375/8/17; AT types 1-5: 306/4/17, 615/4/17, 615/6/17,
        // 940/8/17, 940/6/17). Capacity = cylinders * heads * sectors/track * 512.

        /// <summary>
        /// PC/XT 5MB hard disk (IBM XT BIOS drive type 0).
        /// 5,326,848 bytes = 306 cylinders x 2 heads x 17 sectors x 512 bytes.
        /// </summary>
        public static BlockDeviceDescriptor PcXt_5MB => new BlockDeviceDescriptor
        {
            Name = "PC/XT 5MB (306/2/17)",
            BlockSize = 512,
            AddressMode = BlockAddressMode.CylinderHeadSector,
            Cylinders = 306,
            Heads = 2,
            UniformSectorsPerTrack = 17,
            TotalBlocks = 306 * 2 * 17,
            ExpectedImageSize = 306L * 2 * 17 * 512,
            SupportsVariableGeometry = false
        };

        /// <summary>
        /// PC/XT 10MB hard disk — the "usual" XT drive, ST-412-class (also IBM AT type 1).
        /// 10,653,696 bytes = 306 cylinders x 4 heads x 17 sectors x 512 bytes.
        /// </summary>
        public static BlockDeviceDescriptor PcXt_10MB => new BlockDeviceDescriptor
        {
            Name = "PC/XT 10MB (306/4/17, ST-412)",
            BlockSize = 512,
            AddressMode = BlockAddressMode.CylinderHeadSector,
            Cylinders = 306,
            Heads = 4,
            UniformSectorsPerTrack = 17,
            TotalBlocks = 306 * 4 * 17,
            ExpectedImageSize = 306L * 4 * 17 * 512,
            SupportsVariableGeometry = false
        };

        /// <summary>
        /// PC/XT 15MB hard disk (IBM XT BIOS drive type 2).
        /// 15,980,544 bytes = 306 cylinders x 6 heads x 17 sectors x 512 bytes.
        /// </summary>
        public static BlockDeviceDescriptor PcXt_15MB => new BlockDeviceDescriptor
        {
            Name = "PC/XT 15MB (306/6/17)",
            BlockSize = 512,
            AddressMode = BlockAddressMode.CylinderHeadSector,
            Cylinders = 306,
            Heads = 6,
            UniformSectorsPerTrack = 17,
            TotalBlocks = 306 * 6 * 17,
            ExpectedImageSize = 306L * 6 * 17 * 512,
            SupportsVariableGeometry = false
        };

        /// <summary>
        /// PC/XT 26MB hard disk (IBM XT BIOS drive type 1).
        /// 26,112,000 bytes = 375 cylinders x 8 heads x 17 sectors x 512 bytes.
        /// </summary>
        public static BlockDeviceDescriptor PcXt_26MB => new BlockDeviceDescriptor
        {
            Name = "PC/XT 26MB (375/8/17)",
            BlockSize = 512,
            AddressMode = BlockAddressMode.CylinderHeadSector,
            Cylinders = 375,
            Heads = 8,
            UniformSectorsPerTrack = 17,
            TotalBlocks = 375 * 8 * 17,
            ExpectedImageSize = 375L * 8 * 17 * 512,
            SupportsVariableGeometry = false
        };

        /// <summary>
        /// PC/AT 20MB hard disk (IBM AT BIOS drive type 2) — very common early AT size,
        /// matches the Seagate ST-225.
        /// 21,411,840 bytes = 615 cylinders x 4 heads x 17 sectors x 512 bytes.
        /// </summary>
        public static BlockDeviceDescriptor PcAt_20MB => new BlockDeviceDescriptor
        {
            Name = "PC/AT 20MB (615/4/17, ST-225 class)",
            BlockSize = 512,
            AddressMode = BlockAddressMode.CylinderHeadSector,
            Cylinders = 615,
            Heads = 4,
            UniformSectorsPerTrack = 17,
            TotalBlocks = 615 * 4 * 17,
            ExpectedImageSize = 615L * 4 * 17 * 512,
            SupportsVariableGeometry = false
        };

        /// <summary>
        /// PC/AT 30MB hard disk (IBM AT BIOS drive type 3) — common AT upgrade size.
        /// 32,117,760 bytes = 615 cylinders x 6 heads x 17 sectors x 512 bytes.
        /// </summary>
        public static BlockDeviceDescriptor PcAt_30MB => new BlockDeviceDescriptor
        {
            Name = "PC/AT 30MB (615/6/17)",
            BlockSize = 512,
            AddressMode = BlockAddressMode.CylinderHeadSector,
            Cylinders = 615,
            Heads = 6,
            UniformSectorsPerTrack = 17,
            TotalBlocks = 615 * 6 * 17,
            ExpectedImageSize = 615L * 6 * 17 * 512,
            SupportsVariableGeometry = false
        };

        /// <summary>
        /// PC/AT 47MB hard disk (IBM AT BIOS drive type 5) — later, larger MFM/RLL-era drive.
        /// 49,090,560 bytes = 940 cylinders x 6 heads x 17 sectors x 512 bytes.
        /// </summary>
        public static BlockDeviceDescriptor PcAt_47MB => new BlockDeviceDescriptor
        {
            Name = "PC/AT 47MB (940/6/17)",
            BlockSize = 512,
            AddressMode = BlockAddressMode.CylinderHeadSector,
            Cylinders = 940,
            Heads = 6,
            UniformSectorsPerTrack = 17,
            TotalBlocks = 940 * 6 * 17,
            ExpectedImageSize = 940L * 6 * 17 * 512,
            SupportsVariableGeometry = false
        };

        /// <summary>
        /// PC/AT 62MB hard disk (IBM AT BIOS drive type 4) — larger early AT BIOS type.
        /// 65,454,080 bytes = 940 cylinders x 8 heads x 17 sectors x 512 bytes.
        /// </summary>
        public static BlockDeviceDescriptor PcAt_62MB => new BlockDeviceDescriptor
        {
            Name = "PC/AT 62MB (940/8/17)",
            BlockSize = 512,
            AddressMode = BlockAddressMode.CylinderHeadSector,
            Cylinders = 940,
            Heads = 8,
            UniformSectorsPerTrack = 17,
            TotalBlocks = 940 * 8 * 17,
            ExpectedImageSize = 940L * 8 * 17 * 512,
            SupportsVariableGeometry = false
        };

        // ===================================================================
        // PREDEFINED DESCRIPTORS - Amiga Formats
        // ===================================================================

        /// <summary>
        /// Amiga ADF (880KB, standard double-density).
        /// 901,120 bytes = 80 cylinders × 2 heads × 11 sectors × 512 bytes.
        /// OFS/FFS filesystem with uniform geometry.
        /// </summary>
        public static BlockDeviceDescriptor AmigaADF => new BlockDeviceDescriptor
        {
            Name = "Amiga ADF (880KB)",
            BlockSize = 512,
            AddressMode = BlockAddressMode.CylinderHeadSector,
            Cylinders = 80,
            Heads = 2,
            UniformSectorsPerTrack = 11,
            TotalBlocks = 1760,
            ExpectedImageSize = 901120,
            SupportsVariableGeometry = false
        };

        /// <summary>
        /// Amiga ADF High-Density (1.76MB).
        /// 1,802,240 bytes = 80 cylinders × 2 heads × 22 sectors × 512 bytes.
        /// </summary>
        public static BlockDeviceDescriptor AmigaADF_HD => new BlockDeviceDescriptor
        {
            Name = "Amiga ADF HD (1.76MB)",
            BlockSize = 512,
            AddressMode = BlockAddressMode.CylinderHeadSector,
            Cylinders = 80,
            Heads = 2,
            UniformSectorsPerTrack = 22,
            TotalBlocks = 3520,
            ExpectedImageSize = 1802240,
            SupportsVariableGeometry = false
        };

        // ===================================================================
        // PREDEFINED DESCRIPTORS - Dragon/CoCo Formats
        // ===================================================================

        /// <summary>
        /// Dragon/CoCo VDK (single-sided, 40 tracks).
        /// 184,320 bytes = 40 cylinders × 1 head × 18 sectors × 256 bytes.
        /// DragonDOS or OS-9 filesystem.
        /// </summary>
        public static BlockDeviceDescriptor DragonVDK_SS40 => new BlockDeviceDescriptor
        {
            Name = "Dragon/CoCo VDK SS/40T (180KB)",
            BlockSize = 256,
            AddressMode = BlockAddressMode.LBA,
            Cylinders = 40,
            Heads = 1,
            UniformSectorsPerTrack = 18,
            TotalBlocks = 720,
            ExpectedImageSize = 184320,
            SupportsVariableGeometry = false
        };

        /// <summary>
        /// Dragon/CoCo VDK (single-sided, 80 tracks).
        /// 368,640 bytes = 80 cylinders × 1 head × 18 sectors × 256 bytes.
        /// </summary>
        public static BlockDeviceDescriptor DragonVDK_SS80 => new BlockDeviceDescriptor
        {
            Name = "Dragon/CoCo VDK SS/80T (360KB)",
            BlockSize = 256,
            AddressMode = BlockAddressMode.LBA,
            Cylinders = 80,
            Heads = 1,
            UniformSectorsPerTrack = 18,
            TotalBlocks = 1440,
            ExpectedImageSize = 368640,
            SupportsVariableGeometry = false
        };

        /// <summary>
        /// Dragon/CoCo VDK (double-sided, 40 tracks).
        /// 368,640 bytes = 40 cylinders × 2 heads × 18 sectors × 256 bytes.
        /// </summary>
        public static BlockDeviceDescriptor DragonVDK_DS40 => new BlockDeviceDescriptor
        {
            Name = "Dragon/CoCo VDK DS/40T (360KB)",
            BlockSize = 256,
            AddressMode = BlockAddressMode.LBA,
            Cylinders = 40,
            Heads = 2,
            UniformSectorsPerTrack = 18,
            TotalBlocks = 1440,
            ExpectedImageSize = 368640,
            SupportsVariableGeometry = false
        };

        /// <summary>
        /// Dragon/CoCo VDK (double-sided, 80 tracks).
        /// 737,280 bytes = 80 cylinders × 2 heads × 18 sectors × 256 bytes.
        /// </summary>
        public static BlockDeviceDescriptor DragonVDK_DS80 => new BlockDeviceDescriptor
        {
            Name = "Dragon/CoCo VDK DS/80T (720KB)",
            BlockSize = 256,
            AddressMode = BlockAddressMode.LBA,
            Cylinders = 80,
            Heads = 2,
            UniformSectorsPerTrack = 18,
            TotalBlocks = 2880,
            ExpectedImageSize = 737280,
            SupportsVariableGeometry = false
        };

        // ===================================================================
        // HELPER METHODS - Build Sector Tables
        // ===================================================================

        /// <summary>
        /// Builds sectors-per-track table for D64 format with ZBR.
        /// Pattern: tracks 1-17 = 21, 18-24 = 19, 25-30 = 18, 31+ = 17.
        /// </summary>
        private static int[] BuildD64SectorsPerTrack(int trackCount)
        {
            int[] table = new int[trackCount + 1]; // 1-indexed

            for (int t = 1; t <= trackCount; t++)
            {
                if (t <= 17)
                    table[t] = 21;
                else if (t <= 24)
                    table[t] = 19;
                else if (t <= 30)
                    table[t] = 18;
                else
                    table[t] = 17;
            }

            return table;
        }

        /// <summary>
        /// Builds sectors-per-track table for D71 format (double-sided).
        /// Tracks 1-35: side 1 (same as D64)
        /// Tracks 36-70: side 2 (same pattern as side 1)
        /// </summary>
        private static int[] BuildD71SectorsPerTrack()
        {
            int[] table = new int[71]; // Tracks 1-70

            // Side 1: Tracks 1-35
            for (int t = 1; t <= 35; t++)
            {
                if (t <= 17)
                    table[t] = 21;
                else if (t <= 24)
                    table[t] = 19;
                else if (t <= 30)
                    table[t] = 18;
                else
                    table[t] = 17;
            }

            // Side 2: Tracks 36-70 (same pattern)
            for (int t = 36; t <= 70; t++)
            {
                int sideTrack = t - 35;
                if (sideTrack <= 17)
                    table[t] = 21;
                else if (sideTrack <= 24)
                    table[t] = 19;
                else if (sideTrack <= 30)
                    table[t] = 18;
                else
                    table[t] = 17;
            }

            return table;
        }

        /// <summary>
        /// Gets sectors per track for a given track number.
        /// Handles both variable (ZBR) and uniform geometry.
        /// </summary>
        public int GetSectorsPerTrack(int track)
        {
            if (SectorsPerTrackTable != null && SectorsPerTrackTable.Length > track)
            {
                return SectorsPerTrackTable[track];
            }

            return UniformSectorsPerTrack;
        }
    }
}
