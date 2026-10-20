//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Local.Drivers;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Pins <c>DragonDosFileSystem.GetBlocksFree</c> against the DragonDOS 1.2 source.
    /// </summary>
    /// <remarks>
    /// Source read: the DragonDOS ROM listing (ddos12.asm) and its definitions (dosdefs.asm), in the
    /// RetroDocs reference folder for DragonDOS.
    /// The routine DOSGetFree (label LD162) reads the two BAM sectors at the directory track LSN
    /// (SectorsPerTrack * DirPrimary = $0168) and counts SET BITS in the first BAMEntriesSec = 180
    /// bytes of each: LSRA, then count when the carry is set. The comment at GetBit says: a 1 bit is
    /// a free sector, a 0 bit a used sector, and the BAM is split so that LSNs 0..1439 are bits of
    /// sector 1 and LSNs 1440..2879 are bits of sector 2. SectorsPerBAM = 8: one BAM byte covers
    /// eight sectors. The old code counted whole bytes equal to 0xFF.
    /// </remarks>
    [TestFixture]
    public class DragonDosBlocksFreeTests
    {
        private const int SectorSize = 256;
        private const int DirLsn = 18 * 20; // $0168

        private static byte[] BlankDisk()
        {
            // 40 tracks, 1 side, 18 sectors per track, 256 byte sectors, no header.
            return new byte[40 * 18 * SectorSize];
        }

        private static int BamOffset(int bamSector)
        {
            return (DirLsn + bamSector) * SectorSize;
        }

        private static int Free(byte[] image)
        {
            DragonDskDriver driver = new DragonDskDriver();
            driver.Mount(new MemoryStream(image, writable: true), true);
            int free = driver.GetBlocksFree();
            driver.Dispose();
            return free;
        }

        [Test]
        public void EveryBitSetInTheFirstSector_CountsAsFree()
        {
            byte[] image = BlankDisk();
            for (int i = 0; i < 90; i++) image[BamOffset(0) + i] = 0xFF;

            Assert.That(Free(image), Is.EqualTo(90 * 8));
        }

        [Test]
        public void AByteThatIsNotFullyFree_CountsItsSetBits()
        {
            byte[] image = BlankDisk();
            image[BamOffset(0) + 0] = 0xFC; // 6 free
            image[BamOffset(0) + 1] = 0x0F; // 4 free
            image[BamOffset(0) + 2] = 0x01; // 1 free

            Assert.That(Free(image), Is.EqualTo(11));
        }

        [Test]
        public void TheSecondBamSectorCounts_AndOnlyItsFirst180Bytes()
        {
            byte[] image = BlankDisk();
            image[BamOffset(1) + 0] = 0xFF;      // 8 free
            image[BamOffset(1) + 179] = 0x03;    // 2 free (last counted byte)
            image[BamOffset(1) + 180] = 0xFF;    // beyond BAMEntriesSec, not counted

            Assert.That(Free(image), Is.EqualTo(10));
        }

        [Test]
        public void ByteOffset180AndUpOfTheFirstSector_IsNotCounted()
        {
            byte[] image = BlankDisk();
            image[BamOffset(0) + 179] = 0x80;    // 1 free
            image[BamOffset(0) + 180] = 0xFF;    // geometry area etc., not part of the bitmap

            Assert.That(Free(image), Is.EqualTo(1));
        }
    }
}
