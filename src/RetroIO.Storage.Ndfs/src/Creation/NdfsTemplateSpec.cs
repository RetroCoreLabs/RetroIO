//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Storage.Ndfs.Creation
{
    /// <summary>
    /// Internal specification for an NDFS disk image template.
    /// Contains all the technical details needed to create a specific disk type.
    /// </summary>
    /// <remarks>
    /// The page numbers in the pre-defined templates below are the values the creator works out with
    /// the SINTRAN rules (see <see cref="NdfsImageCreator"/>); the creator computes them again for every
    /// image, so they are for reference and tests only.
    /// </remarks>
    internal class NdfsTemplateSpec
    {
        /// <summary>
        /// NDFS filesystem capacity in pages (2KB each).
        /// This is what SINTRAN reports as total capacity.
        /// </summary>
        public uint NdfsPages { get; set; }

        /// <summary>
        /// Physical file size in blocks (2KB each).
        /// Includes spare sectors for bad block remapping.
        /// </summary>
        public uint FileBlocks { get; set; }

        /// <summary>
        /// Object file index block number.
        /// </summary>
        public uint ObjectFileBlock { get; set; }

        /// <summary>
        /// User file index block number.
        /// </summary>
        public uint UserFileBlock { get; set; }

        /// <summary>
        /// Bit file (allocation bitmap) block number.
        /// For hard disks, should be ~50% of NdfsPages.
        /// </summary>
        public uint BitFileBlock { get; set; }

        /// <summary>
        /// Initial unreserved pages (typically = NdfsPages for empty disk).
        /// </summary>
        public uint UnreservedPages { get; set; }

        /// <summary>
        /// Controller type for this template.
        /// </summary>
        public NdfsControllerType ControllerType { get; set; }

        /// <summary>
        /// Whether this is a FLOMON floppy disk.
        /// </summary>
        public bool HasFlomon { get; set; }

        /// <summary>
        /// Whether Extended Info block should be written.
        /// </summary>
        public bool ExtValid { get; set; }

        /// <summary>
        /// Default boot loader type for this template.
        /// </summary>
        public NdfsBootLoaderType BootLoaderType { get; set; }

        /// <summary>
        /// Number of spare blocks (FileBlocks - NdfsPages).
        /// </summary>
        public uint SpareBlocks { get; set; }

        /// <summary>
        /// Makes a copy. The pre-defined templates are shared static objects; the image creator works
        /// on a copy because it stores the computed page numbers in the specification.
        /// </summary>
        /// <returns>
        /// A new specification with the same values.
        /// </returns>
        public NdfsTemplateSpec Clone()
        {
            return (NdfsTemplateSpec)MemberwiseClone();
        }
    }

    /// <summary>
    /// Pre-defined template specifications.
    /// </summary>
    internal static class NdfsTemplates
    {
        /// <summary>
        /// 360KB floppy disk (154 pages, FLOMON format).
        /// </summary>
        public static readonly NdfsTemplateSpec Floppy360KB = new NdfsTemplateSpec
        {
            NdfsPages = 154,
            FileBlocks = 154,
            ObjectFileBlock = 76,
            UserFileBlock = 78,
            BitFileBlock = 72,
            UnreservedPages = 0,
            ControllerType = NdfsControllerType.Floppy,
            HasFlomon = true,
            ExtValid = false,
            BootLoaderType = NdfsBootLoaderType.Floppy,
            SpareBlocks = 0
        };

        /// <summary>
        /// 1.2MB floppy disk (616 pages, FLOMON format).
        /// </summary>
        public static readonly NdfsTemplateSpec Floppy12MB = new NdfsTemplateSpec
        {
            NdfsPages = 616,
            FileBlocks = 616,
            ObjectFileBlock = 316,
            UserFileBlock = 318,
            BitFileBlock = 306,
            UnreservedPages = 111,
            ControllerType = NdfsControllerType.Floppy,
            HasFlomon = true,
            ExtValid = false,
            BootLoaderType = NdfsBootLoaderType.Floppy,
            SpareBlocks = 0
        };

        /// <summary>
        /// 75MB SMD hard disk - DISC-75MB-1 (36,945 NDFS pages, 38,400 file blocks).
        /// </summary>
        public static readonly NdfsTemplateSpec Smd75MB = new NdfsTemplateSpec
        {
            NdfsPages = 36945,
            FileBlocks = 38400,
            ObjectFileBlock = 18476,
            UserFileBlock = 18478,
            // KERNEL-CORRECTED: floor(36945/2)=18472 rounded down to a multiple of 9 = 18468,
            // the real PACK-ONE bit_file_ptr (ALBIT 137526B). Plain pages/2 gave 18472 (off by 4).
            BitFileBlock = 18468,
            UnreservedPages = 18466,
            ControllerType = NdfsControllerType.Smd,
            HasFlomon = false,
            ExtValid = true,
            BootLoaderType = NdfsBootLoaderType.Smd,
            SpareBlocks = 1455          // 38400 - 36945
        };

        /// <summary>
        /// 74MB Winchester (ST-506) hard disk - DISC-74MB-1, 36,396 NDFS pages.
        /// </summary>
        /// <remarks>
        /// The 36,396-page directory size is DOCUMENTED: ND-30.003.007 (SINTRAN III System
        /// Supervisor), @BACKUP-SYSTEM / DEVICE-COPY from "DISC-74MB-1, unit 1 ... Pages to copy:
        /// 36396".
        ///
        /// The physical drive is the Micropolis 1325 — a 5.25" ST-506/MFM drive, which on ND
        /// attaches to the WINCHESTER controller (IOX base 500 octal). MEASURED across 7 real
        /// images (D:\ND\HDD\1325.img, c3_2024_*.img, tingo_raw_debug.img): device 36,864 pages
        /// = 75,497,472 bytes = exactly 72.0 MiB, declared capacity 36,396, spare 468 pages.
        ///
        /// BUG FIXED: FileBlocks used to be 36,360 — SMALLER than the 36,396 pages of capacity the
        /// image declares — so a created image was 36 pages short of its own declared size and any
        /// access to the tail would run off the end of the file. (The real WD0.img has exactly this
        /// defect; the 36,360 figure came from ndtool with no documentary basis.) Every real ND
        /// drive reserves a spare region for bad-sector remapping, so the device is always LARGER
        /// than the capacity — never smaller.
        /// </remarks>
        public static readonly NdfsTemplateSpec Winchester74MB = new NdfsTemplateSpec
        {
            NdfsPages = 36396,
            FileBlocks = 36864,         // real device: Micropolis 1325 = 72.0 MiB exactly
            ObjectFileBlock = 18204,
            UserFileBlock = 18206,
            // floor(36396/2)=18198, already a multiple of 9 -> 18198 (ALBIT 137526B).
            BitFileBlock = 18198,
            UnreservedPages = 18191,
            ControllerType = NdfsControllerType.Winchester,
            HasFlomon = false,
            ExtValid = true,
            BootLoaderType = NdfsBootLoaderType.Winchester,
            SpareBlocks = 468           // 36864 - 36396: the bad-sector remap area
        };

        /// <summary>
        /// 126MB SCSI hard disk (ND-3201 controller) - raw 64,656 pages, usable 61,036 pages.
        /// </summary>
        /// <remarks>
        /// DOCUMENTED against a real dump (NDInsight SINTRAN/Devices/SCSI/scsi-disk-format.md) and
        /// verified byte-for-byte against the real image D:\ND\HDD\scsi-1.img:
        ///   raw = 129,312 blocks = 64,656 ND pages; usable filesystem = 61,036 pages;
        ///   the 3,620-page difference is a reserved top-of-disk region (defect/reallocation table,
        ///   disk-parameter block and spare-extent pool) unique to the ND-3201 SCSI controller.
        /// 3,620 / 61,036 = 5.93% — which is exactly the SCSI overhead constant used for Custom sizes.
        /// The real disk's bit file sits at 30,510 = 9*floor(floor(61036/2)/9), confirming the ALBIT
        /// placement formula on a third controller class.
        /// SCSI bootstraps use the indirect IOXT opcode (device number in the T register).
        /// </remarks>
        public static readonly NdfsTemplateSpec Scsi126MB = new NdfsTemplateSpec
        {
            NdfsPages = 61036,
            FileBlocks = 64656,         // raw device incl. the reserved top-of-disk region
            ObjectFileBlock = 30506,
            UserFileBlock = 30508,
            BitFileBlock = 30510,
            UnreservedPages = 30510,
            ControllerType = NdfsControllerType.Scsi,
            HasFlomon = false,
            ExtValid = true,
            BootLoaderType = NdfsBootLoaderType.Scsi,
            SpareBlocks = 3620          // 64656 - 61036 (VERIFIED on the real dump)
        };

        /// <summary>
        /// Get template specification for a given template type.
        /// </summary>
        public static NdfsTemplateSpec? GetTemplate(NdfsImageTemplate template)
        {
            return template switch
            {
                NdfsImageTemplate.Floppy360KB => Floppy360KB,
                NdfsImageTemplate.Floppy12MB => Floppy12MB,
                NdfsImageTemplate.Smd75MB => Smd75MB,
                NdfsImageTemplate.Winchester74MB => Winchester74MB,
                NdfsImageTemplate.Scsi126MB => Scsi126MB,
                _ => null
            };
        }
    }
}
