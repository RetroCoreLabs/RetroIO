//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Storage.Ndfs.Creation
{
    /// <summary>
    /// Boot loader types with controller-specific firmware. The bootstrap occupies page-0 bytes
    /// 0..1999 (the extended-info block begins at byte 2000), not merely "the first 1024 bytes".
    /// </summary>
    /// <remarks>
    /// Every raw HARD-DISK bootstrap starts with the same word 0 — <b>PIOF</b> (octal 150405) —
    /// which disables interrupts and paging before any I/O. The controller is distinguished NOT by
    /// word 0 but by the first I/O instruction: a literal <c>IOX</c> in the controller's device
    /// window (SMD/Winchester), or the indirect <c>IOXT</c> opcode (SCSI). Floppies are different:
    /// they carry a FLOMON/BPUN ASCII stream, not raw code.
    /// </remarks>
    public enum NdfsBootLoaderType
    {
        /// <summary>
        /// No boot loader — the boot region is left zeroed. A valid but non-bootable volume.
        /// </summary>
        None,

        /// <summary>
        /// Floppy boot loader (FLOMON / BPUN binary-load stream).
        /// Starts with an ASCII preamble (e.g. <c>00 30 00 2F 00 32</c> = "0/2"), then <c>!</c>
        /// (0x21) and a load record. FLOMON = Address/Count/Checksum all zero after <c>!</c>.
        /// </summary>
        Floppy,

        /// <summary>
        /// SMD/ECC controller bootstrap. Word 0 = PIOF; I/O via literal <c>IOX</c> in the octal
        /// device window 1540–1547 (Load Core Addr 1541, Load Block Addr 1543, Read Status 1544,
        /// Load Control Word 1545, Load Word Count 1547). Boots via ALD 21540.
        /// </summary>
        Smd,

        /// <summary>
        /// Winchester (ST-506) controller bootstrap. Word 0 = PIOF; I/O via literal <c>IOX</c> in
        /// the octal device window 500–517. Same raw-bootstrap shape as SMD, different device
        /// window. Boots via ALD 20500.
        /// </summary>
        Winchester,

        /// <summary>
        /// SCSI (NCR-5386) controller bootstrap. Word 0 = PIOF; I/O via the indirect <c>IOXT</c>
        /// opcode (octal 150415), which takes the device address from the T register at runtime —
        /// there is no literal device window in the instruction stream.
        /// </summary>
        Scsi
    }
}
