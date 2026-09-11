//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;

namespace RetroIO.Storage.Ndfs.Creation
{
    /// <summary>
    /// Boot sector library with real bootstraps extracted from genuine NDFS disks.
    /// </summary>
    /// <remarks>
    /// The bootstrap occupies page-0 bytes <b>0–1999</b> (words 0B–1747B), immediately before the
    /// extended-info block at byte 2000 — NOT just "the first 1024 bytes". A real SMD bootstrap runs
    /// well past byte 1024 (verified: the PACK-ONE SMD boot program has ~800 bytes of live code
    /// between bytes 1024 and 1855). Truncating at 1024 produces a bootstrap that is cut in half and
    /// will not boot on real hardware. See <c>NDInsight/SINTRAN/Filesystem/boot-creation.md</c>.
    ///
    /// The blobs are the full 2000-byte regions, embedded (base64) in <see cref="NdfsBootBlobs"/> so
    /// they compile INTO the assembly rather than being read from a repo-relative
    /// <c>boot-sectors/*.bin</c> file. That mechanism was doubly broken: it truncated at 1024, and
    /// its relative path does not resolve when RetroIO.Storage.Ndfs is used as a NuGet package outside the
    /// repo. Each blob carries the correct detector signature: SMD/Winchester = PIOF + literal IOX
    /// in the controller window; SCSI = PIOF + indirect IOXT; Floppy = a FLOMON BPUN stream.
    /// </remarks>
    internal static class NdfsBootSectors
    {
        /// <summary>
        /// Size of the page-0 boot region in bytes (0..1999). The extended-info block begins at
        /// byte 2000, so the bootstrap must never extend past this.
        /// </summary>
        private const int BOOT_SECTOR_SIZE = NdfsBootBlobs.BootRegionSize;

        /// <summary>
        /// Gets the boot region for the specified controller type. The returned array is always
        /// <see cref="BOOT_SECTOR_SIZE"/> bytes; <see cref="NdfsBootLoaderType.None"/> yields all
        /// zeros (a valid but non-bootable volume).
        /// </summary>
        public static byte[] GetBootSector(NdfsBootLoaderType type)
        {
            byte[]? blob = type switch
            {
                NdfsBootLoaderType.Floppy => NdfsBootBlobs.Floppy,
                NdfsBootLoaderType.Smd => NdfsBootBlobs.Smd,
                NdfsBootLoaderType.Winchester => NdfsBootBlobs.Winchester,
                NdfsBootLoaderType.Scsi => NdfsBootBlobs.Scsi,
                _ => null // None
            };

            // Normalize to exactly BOOT_SECTOR_SIZE bytes. A FLOMON floppy stream may be shorter
            // than the full region (the remainder stays zero); a hard-disk bootstrap fills it.
            byte[] bootSector = new byte[BOOT_SECTOR_SIZE];
            if (blob != null)
                Array.Copy(blob, 0, bootSector, 0, Math.Min(blob.Length, BOOT_SECTOR_SIZE));
            return bootSector;
        }
    }
}
