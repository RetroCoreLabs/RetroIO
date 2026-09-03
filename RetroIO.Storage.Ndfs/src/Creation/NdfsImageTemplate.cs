//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Storage.Ndfs.Creation
{
    /// <summary>
    /// Pre-defined NDFS disk image templates with accurate sizing.
    /// </summary>
    public enum NdfsImageTemplate
    {
        /// <summary>
        /// Floppy, SINTRAN format 0 — 154 NDFS pages (315,392 bytes).
        /// 512 bytes/sector, single-sided, single-density.
        /// </summary>
        /// <remarks>
        /// DOCUMENTED: ND-60.128.5 SINTRAN III Reference Manual, DEVICE-FUNCTION SET-FLOPPY-FORMAT
        /// table — format 0 = "capacity in pages 154". The manual states the SINTRAN file system
        /// "can only be used with formats 0 and 17(8)", so 154 and 616 are the ONLY valid NDFS
        /// floppy sizes. (Real 156- and 640-page images are padded 154/616 images, not formats.)
        /// </remarks>
        Floppy360KB,

        /// <summary>
        /// Floppy, SINTRAN format 17(octal) — 616 NDFS pages (1,261,568 bytes).
        /// 1024 bytes/sector, double-sided, double-density.
        /// </summary>
        /// <remarks>
        /// DOCUMENTED: ND-60.128.5, SET-FLOPPY-FORMAT table — format 17(8) = "capacity in pages 616".
        /// One of the only two floppy formats the SINTRAN file system supports (see Floppy360KB).
        /// </remarks>
        Floppy12MB,

        /// <summary>
        /// 75MB SMD hard disk — DISC-75MB-1. 36,945 NDFS pages, 38,400 device blocks.
        /// </summary>
        /// <remarks>
        /// DOCUMENTED: ND-30.003.007 (SINTRAN III System Supervisor) — "A system has one 75 MB disk
        /// pack giving 36,945 pages". The 38,400-page device size comes from the real golden image
        /// (PACK-ONE / SMD0.IMG / BIGDISK0), whose extended-info declares pages_available = 36,945.
        /// The 1,455-page gap is drive-level bad-track spare pool; SMD carries no reserved-block
        /// table on disk (its last block is all zeros), so the 3.94% figure is INFERRED from these
        /// two endpoints, not a documented rule.
        /// </remarks>
        Smd75MB,

        /// <summary>
        /// 74MB Winchester (ST-506 / MFM) hard disk — DISC-74MB-1, Micropolis 1325.
        /// 36,396 usable pages on a 36,864-page (72 MiB) device.
        /// </summary>
        /// <remarks>
        /// Capacity DOCUMENTED: ND-30.003.007, @BACKUP-SYSTEM / DEVICE-COPY — "DISC-74MB-1, unit 1
        /// ... Pages to copy: 36396". Device geometry MEASURED across 7 real images (1325.img,
        /// c3_2024_*.img, tingo_raw_debug.img): 36,864 pages = exactly 72.0 MiB, spare 468 pages.
        /// The drive is a Micropolis 1325, a 5.25" ST-506/MFM unit, which on ND attaches to the
        /// WINCHESTER controller (literal IOX in the octal 500-507 window).
        /// </remarks>
        Winchester74MB,

        /// <summary>
        /// 126MB SCSI hard disk (ND-3201 controller) — 61,036 usable pages, 64,656 raw pages.
        /// </summary>
        /// <remarks>
        /// DOCUMENTED against a real dump and verified byte-for-byte on the real image scsi-1.img:
        /// raw 64,656 pages, usable 61,036, with a 3,620-page reserved top-of-disk region (defect/
        /// reallocation table + disk-parameter block + spare-extent pool) unique to the ND-3201.
        /// 3,620 / 61,036 = 5.93%, matching the SCSI overhead constant. SCSI bootstraps use the
        /// indirect IOXT opcode (device number taken from the T register).
        /// </remarks>
        Scsi126MB,

        /// <summary>
        /// Custom size - requires manual configuration via NdfsImageCreationOptions.
        /// </summary>
        Custom
    }
}
