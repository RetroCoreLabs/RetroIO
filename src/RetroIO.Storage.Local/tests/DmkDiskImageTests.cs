//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Drivers;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Tests of the DMK reader against four real SuperDOS/DragonDOS disks in DMK form and against copies of them that were damaged on purpose.
    /// </summary>
    /// <remarks>
    /// The images are the FEdit example disks of the pydmk project: 40 tracks single sided, 40 tracks double sided, 80 tracks single sided and 80 tracks
    /// double sided. The expected hashes are the MD5 of the decoded sector images worked out by a separate decoder written from the format notes
    /// (every header CRC and data CRC of every sector checked, first pointer of a sector wins, cylinder by cylinder with side 0 then side 1).
    /// The expected file names and sizes are those of the pydmk directory listing; FEDIT.BIN also describes its own length in its 9 byte header.
    /// Each test skips when its image is missing. Set RETROIO_DRAGON_TEST_IMAGES to the folder that holds the dmk folder.
    /// </remarks>
    [TestFixture]
    public class DmkDiskImageTests
    {
        private static readonly string[] Images = { "FEdit.dmk", "FEdit_40_2.dmk", "FEdit_80_1.dmk", "FEdit_80_2.dmk" };
        private static readonly int[] Cylinders = { 40, 40, 80, 80 };
        private static readonly int[] Sides = { 1, 2, 1, 2 };
        private static readonly string[] Md5 =
        {
            "34490d791b96a6365b84d03eb5efc6d7",
            "4acf9a71c1f63358fec596a3829a4401",
            "6b493a629bcc9cd418db6f50e2e65249",
            "e2da755ae9ac408a7a0515ee01c557bb"
        };

        private static string ImagePath(string name)
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable("RETROIO_DRAGON_TEST_IMAGES");
            string root = !string.IsNullOrEmpty(fromEnvironment)
                ? fromEnvironment
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "RetroFS", "test-images");
            string path = Path.Combine(root, "dmk", name);
            if (!File.Exists(path))
            {
                Assert.Ignore("Test image not found: " + name);
            }

            return path;
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void RealImage_DecodesToTheSectorsTheSeparateDecoderFound(int index)
        {
            using (FileStream stream = File.OpenRead(ImagePath(Images[index])))
            {
                DmkDiskImage image = DmkDiskImage.Parse(stream);

                Assert.That(image.Cylinders, Is.EqualTo(Cylinders[index]));
                Assert.That(image.Sides, Is.EqualTo(Sides[index]));
                Assert.That(image.TrackLength, Is.EqualTo(0x1900));
                Assert.That(image.SectorsPerTrack, Is.EqualTo(18));
                Assert.That(image.WriteProtected, Is.False);

                byte[] sectors = image.GetSectorImage();
                Assert.That(sectors.Length, Is.EqualTo(Cylinders[index] * Sides[index] * 18 * 256));
                Assert.That(Convert.ToHexString(MD5.HashData(sectors)).ToLowerInvariant(), Is.EqualTo(Md5[index]));
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void RealImage_LooksLikeDmk(int index)
        {
            byte[] all = File.ReadAllBytes(ImagePath(Images[index]));

            Assert.That(DmkDiskImage.LooksLikeDmk(all.AsSpan(0, 16), all.Length), Is.True);
            Assert.That(DmkDiskImage.LooksLikeDmk(all.AsSpan(0, 16), all.Length + 1), Is.False, "the size must be exact");
        }

        [Test]
        public void NotDmk_AreNotTakenForDmk()
        {
            Assert.That(DmkDiskImage.LooksLikeDmk(new byte[8], 8), Is.False, "too short");
            Assert.That(DmkDiskImage.LooksLikeDmk(new byte[16], 16), Is.False, "no tracks");

            // A VDK header ("dk", header size 12, version bytes, 40 cylinders, 1 head): bytes 12 to 15 would be sector data, and the size does not fit.
            byte[] vdk = new byte[16];
            vdk[0] = (byte)'d';
            vdk[1] = (byte)'k';
            vdk[2] = 12;
            vdk[8] = 40;
            vdk[9] = 1;
            Assert.That(DmkDiskImage.LooksLikeDmk(vdk, 184332), Is.False);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void Driver_ListsTheFilesOfTheDisk(int index)
        {
            DragonDmkDriver driver = new DragonDmkDriver();
            using (FileStream stream = File.OpenRead(ImagePath(Images[index])))
            {
                driver.Mount(stream, true);

                DirectoryEntry[] entries = driver.ListDirectory("/");

                // pydmk lists FEDIT.BIN 11920, INTRO.DOC 7068, MENUS.DOC 11197, MENUS2.DOC 5814, (TEST.TXT 100 on the 40 track single sided disk only), HINTS.DOC 3332.
                string[] names = index == 0
                    ? new[] { "FEDIT.BIN", "INTRO.DOC", "MENUS.DOC", "MENUS2.DOC", "TEST.TXT", "HINTS.DOC" }
                    : new[] { "FEDIT.BIN", "INTRO.DOC", "MENUS.DOC", "MENUS2.DOC", "HINTS.DOC" };
                long[] sizes = index == 0
                    ? new long[] { 11920, 7068, 11197, 5814, 100, 3332 }
                    : new long[] { 11920, 7068, 11197, 5814, 3332 };

                Assert.That(entries.Length, Is.EqualTo(names.Length));
                for (int i = 0; i < names.Length; i++)
                {
                    Assert.That(entries[i].Name, Is.EqualTo(names[i]), "name " + i);
                    Assert.That(entries[i].Metadata.Size, Is.EqualTo(sizes[i]), "size of " + names[i]);
                }

                // The file describes its own length: 9 byte header 55 02, load address 2404, length 2E87 (11911), exec address 2404, AA.
                byte[] bin = driver.ReadFile("FEDIT.BIN");
                Assert.That(bin.Length, Is.EqualTo(11920));
                Assert.That(bin[0], Is.EqualTo(0x55));
                Assert.That(bin[1], Is.EqualTo(0x02));
                Assert.That((bin[2] << 8) | bin[3], Is.EqualTo(0x2404));
                Assert.That((bin[4] << 8) | bin[5], Is.EqualTo(11911));
                Assert.That((bin[6] << 8) | bin[7], Is.EqualTo(0x2404));
                Assert.That(bin[8], Is.EqualTo(0xAA));
            }

            driver.Dispose();
        }

        [Test]
        public void Driver_ReadsTheSameFilesFromAllFourDisks()
        {
            byte[][] intro = new byte[Images.Length][];
            byte[][] menus = new byte[Images.Length][];
            for (int i = 0; i < Images.Length; i++)
            {
                DragonDmkDriver driver = new DragonDmkDriver();
                using (FileStream stream = File.OpenRead(ImagePath(Images[i])))
                {
                    driver.Mount(stream, true);
                    intro[i] = driver.ReadFile("INTRO.DOC");
                    menus[i] = driver.ReadFile("MENUS.DOC");
                }

                driver.Dispose();
            }

            for (int i = 1; i < Images.Length; i++)
            {
                Assert.That(intro[i], Is.EqualTo(intro[0]), Images[i] + " INTRO.DOC");
                Assert.That(menus[i], Is.EqualTo(menus[0]), Images[i] + " MENUS.DOC");
            }

            Assert.That(intro[0].Length, Is.EqualTo(7068));
            Assert.That(menus[0].Length, Is.EqualTo(11197));
        }

        [Test]
        public void Driver_ReportsTheGeometryOfTheDisk()
        {
            DragonDmkDriver driver = new DragonDmkDriver();
            using (FileStream stream = File.OpenRead(ImagePath("FEdit_80_2.dmk")))
            {
                driver.Mount(stream, true);

                BlockDeviceInfo? info = driver.GetBlockDeviceInfo();
                Assert.That(info, Is.Not.Null);
                Assert.That(info!.CylinderCount, Is.EqualTo(80));
                Assert.That(info.HeadCount, Is.EqualTo(2));
                Assert.That(info.SectorsPerTrack, Is.EqualTo(18));
                Assert.Throws<UnauthorizedAccessException>(() => driver.WriteFile("X", new byte[1]));
            }

            driver.Dispose();
        }

        [Test]
        public void Provider_RatesTheRealImagesHighAndOtherFilesLow()
        {
            string path = ImagePath("FEdit.dmk");
            byte[] all = File.ReadAllBytes(path);
            DragonDmkDriverProvider provider = new DragonDmkDriverProvider();

            DetectionHints dmk = new DetectionHints { FileExtension = ".dmk", FileSize = all.Length, HeaderBytes = all.AsSpan(0, 512).ToArray() };
            Assert.That(provider.DetectConfidence(dmk), Is.GreaterThanOrEqualTo(0.9f));

            DetectionHints renamed = new DetectionHints { FileExtension = ".bin", FileSize = all.Length, HeaderBytes = all.AsSpan(0, 512).ToArray() };
            Assert.That(provider.DetectConfidence(renamed), Is.EqualTo(0.5f).Within(0.001f));

            DetectionHints other = new DetectionHints { FileExtension = ".dsk", FileSize = 184320, HeaderBytes = new byte[512] };
            Assert.That(provider.DetectConfidence(other), Is.EqualTo(0.0f));
        }

        // ---- damaged copies ----

        [Test]
        public void Damaged_DataCrc_NamesTheSector()
        {
            byte[] all = File.ReadAllBytes(ImagePath("FEdit.dmk"));

            // Cylinder 0 side 0 sector 1: the header is at 0xEE of the first track block (file offset 16), the data mark follows the 22 + 12 byte gap.
            // Flip a byte in the data of sector 1 (its data starts 0x11B into the block).
            all[16 + 0x11B + 10] ^= 0xFF;

            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => DmkDiskImage.Parse(new MemoryStream(all)))!;
            Assert.That(ex.Message, Does.Contain("cylinder 0 side 0 sector 1"));
            Assert.That(ex.Message, Does.Contain("data CRC"));
        }

        [Test]
        public void Damaged_HeaderCrc_NamesTheSector()
        {
            byte[] all = File.ReadAllBytes(ImagePath("FEdit.dmk"));

            // The sector header of sector 1 starts at 0xEE: FE, cylinder, side, sector, size code, CRC. Change the sector number's CRC byte.
            all[16 + 0xEE + 6] ^= 0x01;

            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => DmkDiskImage.Parse(new MemoryStream(all)))!;
            Assert.That(ex.Message, Does.Contain("header CRC"));
        }

        [Test]
        public void Damaged_FileSize_IsRejected()
        {
            byte[] all = File.ReadAllBytes(ImagePath("FEdit.dmk"));
            byte[] cut = new byte[all.Length - 1];
            Buffer.BlockCopy(all, 0, cut, 0, cut.Length);

            Assert.Throws<InvalidDataException>(() => DmkDiskImage.Parse(new MemoryStream(cut)));
            Assert.Throws<InvalidDataException>(() => DmkDiskImage.Parse(new MemoryStream(new byte[10])));
        }

        [Test]
        public void SingleDensityPointer_IsReportedNotGuessed()
        {
            byte[] all = File.ReadAllBytes(ImagePath("FEdit.dmk"));

            // Clear bit 15 of the first sector pointer of the first track: the sector is then single density.
            all[16 + 1] &= 0x7F;

            Assert.Throws<NotSupportedException>(() => DmkDiskImage.Parse(new MemoryStream(all)));
        }

        [Test]
        public void SingleDensityFlag_IsReportedNotGuessed()
        {
            byte[] all = File.ReadAllBytes(ImagePath("FEdit.dmk"));
            all[4] |= 0x40;

            Assert.Throws<NotSupportedException>(() => DmkDiskImage.Parse(new MemoryStream(all)));
        }

        [Test]
        public void RealDiskSpecificationFile_IsReportedNotRead()
        {
            byte[] all = File.ReadAllBytes(ImagePath("FEdit.dmk"));
            all[12] = 0x78;
            all[13] = 0x56;
            all[14] = 0x34;
            all[15] = 0x12;

            Assert.Throws<NotSupportedException>(() => DmkDiskImage.Parse(new MemoryStream(all)));
        }

        [Test]
        public void OtherSectorSize_IsReportedNotGuessed()
        {
            byte[] all = File.ReadAllBytes(ImagePath("FEdit.dmk"));

            // Size code 2 (512 bytes) in the header of sector 1. The header CRC is then wrong too, so the CRC error comes first; fix the CRC for code 2.
            all[16 + 0xEE + 4] = 2;
            int crc = HeaderCrc(all, 16 + 0xEE);
            all[16 + 0xEE + 5] = (byte)(crc >> 8);
            all[16 + 0xEE + 6] = (byte)crc;

            Assert.Throws<NotSupportedException>(() => DmkDiskImage.Parse(new MemoryStream(all)));
        }

        // CRC of the sector header written independently of the reader: polynomial 1021H, start FFFFH, over A1 A1 A1 and the five header bytes.
        private static int HeaderCrc(byte[] data, int headerOffset)
        {
            byte[] bytes = { 0xA1, 0xA1, 0xA1, data[headerOffset], data[headerOffset + 1], data[headerOffset + 2], data[headerOffset + 3], data[headerOffset + 4] };
            int crc = 0xFFFF;
            for (int i = 0; i < bytes.Length; i++)
            {
                crc ^= bytes[i] << 8;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
                }
            }

            return crc;
        }
    }
}
