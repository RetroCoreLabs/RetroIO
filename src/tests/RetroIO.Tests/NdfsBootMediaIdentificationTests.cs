//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using NUnit.Framework;
using RetroIO.Storage.Ndfs.Boot;
using RetroIO.Storage.Ndfs.Creation;

namespace RetroIO.Tests
{
    /// <summary>
    /// Media-type identification from a real ND page-0 hard-disk boot sector.
    /// </summary>
    /// <remarks>
    /// The fixtures are the genuine bootstraps embedded in <see cref="NdfsBootBlobs"/> — the SMD blob
    /// is byte-identical to D:\ND\HDD\BIGDISK0-K.IMG (PACK-ONE), the Winchester blob to 1325.img, and
    /// the SCSI blob to scsi-1.img (provenance confirmed by SHA-256 in
    /// E:\Dev\Ronny\NDInsight\tools\boot-floppy\DISC-BOOT-SECTOR-ANATOMY.md §1.1). Using the compiled-in
    /// blobs keeps the test self-contained (no external .bin files, no NuGet-relative path reads),
    /// matching the repo convention that the boot regions live as base64 inside the assembly.
    ///
    /// Every expected value below is VERIFIED directly from the page-0 bytes and cross-checked in the
    /// anatomy doc: the 9-word geometry block (§5.4), the device-class word (§5.3), and the
    /// SCSI zero-geometry LBA discriminator (§5.4). See also DISC-BOOT-SECTOR-ANATOMY.md §1.2 / §5.1
    /// for why the classifier must ignore the volume label at bytes 2000..2047.
    /// </remarks>
    [TestFixture]
    public class NdfsBootMediaIdentificationTests
    {
        // A real NDFS page 0 is 2048 bytes: the 2000-byte boot region (the embedded blob) followed
        // by the 48-byte volume label at bytes 2000..2047. The loader requires a full 2048-byte
        // page, so wrap each blob exactly as it sits on disk (label left zero unless a test plants
        // values there).
        private static byte[] AsPage0(byte[] bootRegion)
        {
            byte[] page = new byte[2048];
            Array.Copy(bootRegion, 0, page, 0, Math.Min(bootRegion.Length, page.Length));
            return page;
        }

        [Test]
        [Category("NDFS")]
        [Category("BootLoader")]
        public void Smd_Blob_Identifies_As_SmdEcc_Chs_With_Verified_Geometry()
        {
            NdfsBootCode? boot = NdfsBootLoader.LoadBootCodeFromBytes(AsPage0(NdfsBootBlobs.Smd));

            Assert.That(boot, Is.Not.Null);
            Assert.That(boot!.Format, Is.EqualTo(NdfsBootFormat.Binary));
            Assert.That(boot.ControllerType, Is.EqualTo(NdfsBootControllerType.SmdEcc));

            // Device-class word: SMD = 1 (anatomy §5.3).
            Assert.That(boot.DeviceClass, Is.EqualTo(1), "SMD device-class word must be 1");

            // Geometry block: 512 words/sector, 18 sec/track, 90 sec/cyl, 822/821 cylinders (§5.4).
            Assert.That(boot.Geometry.Found, Is.True, "SMD carries a geometry block");
            Assert.That(boot.Geometry.WordsPerSector, Is.EqualTo(512));
            Assert.That(boot.Geometry.SectorsPerTrack, Is.EqualTo(18));
            Assert.That(boot.Geometry.SectorsPerCylinder, Is.EqualTo(90));
            Assert.That(boot.Geometry.Cylinders, Is.EqualTo(822));
            Assert.That(boot.Geometry.CylindersAlt, Is.EqualTo(821));
            Assert.That(boot.Geometry.IsZeroGeometry, Is.False);

            // A CHS disk (non-zero geometry).
            Assert.That(boot.MediaAddressing, Is.EqualTo(NdfsBootMediaAddressing.Chs));
        }

        [Test]
        [Category("NDFS")]
        [Category("BootLoader")]
        public void Winchester_Blob_Identifies_As_Winchester_Chs_With_Verified_Geometry()
        {
            NdfsBootCode? boot = NdfsBootLoader.LoadBootCodeFromBytes(AsPage0(NdfsBootBlobs.Winchester));

            Assert.That(boot, Is.Not.Null);
            Assert.That(boot!.Format, Is.EqualTo(NdfsBootFormat.Binary));
            Assert.That(boot.ControllerType, Is.EqualTo(NdfsBootControllerType.Winchester));

            // Device-class word: Winchester = 2 (anatomy §5.3), read at page word 0o77.
            Assert.That(boot.DeviceClass, Is.EqualTo(2), "Winchester device-class word must be 2");

            // Geometry: 512/9/72/1021/1011 — the Micropolis 1325 (§5.4).
            Assert.That(boot.Geometry.Found, Is.True);
            Assert.That(boot.Geometry.WordsPerSector, Is.EqualTo(512));
            Assert.That(boot.Geometry.SectorsPerTrack, Is.EqualTo(9));
            Assert.That(boot.Geometry.SectorsPerCylinder, Is.EqualTo(72));
            Assert.That(boot.Geometry.Cylinders, Is.EqualTo(1021));
            Assert.That(boot.Geometry.CylindersAlt, Is.EqualTo(1011));
            Assert.That(boot.Geometry.IsZeroGeometry, Is.False);

            Assert.That(boot.MediaAddressing, Is.EqualTo(NdfsBootMediaAddressing.Chs));
        }

        [Test]
        [Category("NDFS")]
        [Category("BootLoader")]
        public void Scsi_Blob_Identifies_As_Scsi_Lba_With_Zero_Geometry()
        {
            NdfsBootCode? boot = NdfsBootLoader.LoadBootCodeFromBytes(AsPage0(NdfsBootBlobs.Scsi));

            Assert.That(boot, Is.Not.Null);
            Assert.That(boot!.Format, Is.EqualTo(NdfsBootFormat.Binary));
            Assert.That(boot.ControllerType, Is.EqualTo(NdfsBootControllerType.Scsi));

            // Device-class word: SCSI = 3 (anatomy §5.3).
            Assert.That(boot.DeviceClass, Is.EqualTo(3), "SCSI device-class word must be 3");

            // SCSI still anchors words-per-sector at 512, but ALL CHS slots are zero — the clean
            // LBA-vs-CHS discriminator (§5.4).
            Assert.That(boot.Geometry.Found, Is.True);
            Assert.That(boot.Geometry.WordsPerSector, Is.EqualTo(512));
            Assert.That(boot.Geometry.SectorsPerTrack, Is.EqualTo(0));
            Assert.That(boot.Geometry.SectorsPerCylinder, Is.EqualTo(0));
            Assert.That(boot.Geometry.Cylinders, Is.EqualTo(0));
            Assert.That(boot.Geometry.CylindersAlt, Is.EqualTo(0));
            Assert.That(boot.Geometry.IsZeroGeometry, Is.True, "SCSI geometry must be zero (LBA)");

            Assert.That(boot.MediaAddressing, Is.EqualTo(NdfsBootMediaAddressing.Lba));
        }

        [Test]
        [Category("NDFS")]
        [Category("BootLoader")]
        public void ControllerClassification_Ignores_Volume_Label_Region()
        {
            // The classifier must never read past the 1000-word boot region into the NDFS volume
            // label (bytes 2000..2047). Regression guard: plant a phantom literal-IOX word AND a
            // phantom IOXT word inside the label; a correct scan ignores both and the SCSI blob is
            // still classified SCSI purely from its genuine early IOXT. (Anatomy §1.2 / §5.1 —
            // scsi-1.img's real label word 1007 = 0167154 already reads as a phantom "IOX 3154".)
            byte[] data = AsPage0(NdfsBootBlobs.Scsi);

            // Word 1007 (byte 2014): literal IOX in the SMD window (0164000 | 1540 = 0167140).
            data[1007 * 2] = 0xEE;      // 0o167140 high byte
            data[1007 * 2 + 1] = 0x60;
            // Word 1010 (byte 2020): IOXT opcode (already SCSI, but prove label words are ignored).
            data[1010 * 2] = 0xD1;      // 0o150415
            data[1010 * 2 + 1] = 0x0D;

            NdfsBootCode? boot = NdfsBootLoader.LoadBootCodeFromBytes(data);

            Assert.That(boot, Is.Not.Null);
            // A whole-page scan would meet the planted SMD-window IOX in the label and could
            // misreport SmdEcc; the capped scan stops at word 1000 and keeps the real SCSI verdict.
            Assert.That(boot!.ControllerType, Is.EqualTo(NdfsBootControllerType.Scsi),
                "Label-region words must not affect controller classification");
        }

        [Test]
        [Category("NDFS")]
        [Category("BootLoader")]
        public void Floppy_Blob_Has_No_HardDisk_Geometry()
        {
            // A FLOMON floppy stream is not a page-0 relocator: no geometry block, no device class.
            NdfsBootCode? boot = NdfsBootLoader.LoadBootCodeFromBytes(AsPage0(NdfsBootBlobs.Floppy));

            Assert.That(boot, Is.Not.Null);
            Assert.That(boot!.Format, Is.EqualTo(NdfsBootFormat.BPUN).Or.EqualTo(NdfsBootFormat.FloMon));
            Assert.That(boot.ControllerType, Is.EqualTo(NdfsBootControllerType.Unknown));
            Assert.That(boot.MediaAddressing, Is.EqualTo(NdfsBootMediaAddressing.Unknown));
            Assert.That(boot.Geometry.Found, Is.False);
        }
    }
}
