//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Drivers;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Pins what is on HTCHHKER.VDK and PLANETFALL.VDK, two game disks that cannot be read as DragonDOS, and what the driver does with them.
    /// </summary>
    /// <remarks>
    /// Facts, each checked on the real image:
    ///  - The sector at track 0 sector 3 (LSN 2), where the DragonDOS BOOT command wants the signature OS, is not OS. (Run in XRoar with the
    ///    DragonDOS 1.0 ROM, BOOT answers ?BT ERROR.)
    ///  - Bytes FCH to FFH of sector 1 of track 20, where DragonDOS keeps the track count, the sectors per track and their complements, do not pass
    ///    the complement check. (The ROM routine DosGetDiskGeometry gives the invalid volume error then; DIR answers ?IV ERROR in XRoar.)
    ///  - Sector 0 starts with 6809 code (4FH 1FH 8BH: CLRA, TFR A,DP) and the signature OS is at LSN 612, track 34 sector 1.
    ///  - The FAT on track 17 sector 2 is a 68 granule DECB FAT: 0xFF in bytes 0 to 67 (PLANETFALL has 0xC1 at granule 32), then 0xFF
    ///    (HTCHHKER) or 0x00 (PLANETFALL) to the end of the sector. PLANETFALL's directory holds GAME.BIN at granule 32, 106 bytes; HTCHHKER's
    ///    directory is empty (0xFF). Toolshed decb lists the same on the 40 track image and on a cut of its first 35 tracks, and copies
    ///    GAME.BIN out as 106 bytes.
    /// So the disks have no DragonDOS directory, and the driver mounts them as DECB disks of 68 granules: the CoCo's view of them, with the
    /// game in sectors no file names. Set RETROIO_DRAGON_TEST_IMAGES to the folder with the vdk folder.
    /// </remarks>
    [TestFixture]
    public class DragonDosNonDosDiskTests
    {
        private static string Image(string name)
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable("RETROIO_DRAGON_TEST_IMAGES");
            string root = !string.IsNullOrEmpty(fromEnvironment)
                ? fromEnvironment
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "RetroFS", "test-images");
            string path = Path.Combine(root, "vdk", name);
            if (!File.Exists(path))
            {
                Assert.Ignore("Test image not found: " + name);
            }

            return path;
        }

        [TestCase("HTCHHKER.VDK")]
        [TestCase("PLANETFALL.VDK")]
        public void GameDisk_HasNoDragonDosBootSectorAndNoBam(string name)
        {
            byte[] file = File.ReadAllBytes(Image(name));
            int header = file[2] | (file[3] << 8);
            Assert.That(header, Is.EqualTo(256), "these two have an extended 256 byte VDK header");

            int Lsn(int n) => header + (n * 256);

            Assert.That(file[Lsn(2)] == (byte)'O' && file[Lsn(2) + 1] == (byte)'S', Is.False, "no DragonDOS boot signature at LSN 2");
            Assert.That(file[Lsn(0)], Is.EqualTo(0x4F), "sector 0 starts with 6809 code");
            Assert.That(file[Lsn(0) + 1], Is.EqualTo(0x1F));
            Assert.That(file[Lsn(0) + 2], Is.EqualTo(0x8B));

            int bam = Lsn(20 * 18);
            int tracks = file[bam + 0xFC];
            int sectors = file[bam + 0xFD];
            bool geometryOk = tracks == ((~file[bam + 0xFE]) & 0xFF) && sectors == ((~file[bam + 0xFF]) & 0xFF);
            Assert.That(geometryOk, Is.False, "the BAM geometry bytes fail their complement check");

            Assert.That(file[Lsn(34 * 18)] == (byte)'O' && file[Lsn(34 * 18) + 1] == (byte)'S', Is.True, "the signature OS is on track 34 sector 1");
        }

        [Test]
        public void PlanetFall_MountsAsDecbAndReadsGameBin()
        {
            byte[] file = File.ReadAllBytes(Image("PLANETFALL.VDK"));
            DragonVdkDriver driver = new DragonVdkDriver();
            using (MemoryStream stream = new MemoryStream(file, false))
            {
                driver.Mount(stream, true);

                Assert.That(driver.FileSystemType, Is.EqualTo("DECB"));
                Assert.That(driver.GetBlockDeviceInfo()!.CylinderCount, Is.EqualTo(40));
                DirectoryEntry[] entries = driver.ListDirectory("/");
                Assert.That(entries.Length, Is.EqualTo(1));
                Assert.That(entries[0].Name, Is.EqualTo("GAME.BIN"));

                // Granule 32 is the first half of track 16, LSN 288; the file is its first 106 bytes.
                byte[] expected = new byte[106];
                Array.Copy(file, 256 + (288 * 256), expected, 0, expected.Length);
                Assert.That(driver.ReadFile("GAME.BIN"), Is.EqualTo(expected));
                driver.Dispose();
            }
        }

        [Test]
        public void Hitchhiker_MountsAsAnEmptyDecbDisk()
        {
            DragonVdkDriver driver = new DragonVdkDriver();
            using (MemoryStream stream = new MemoryStream(File.ReadAllBytes(Image("HTCHHKER.VDK")), false))
            {
                driver.Mount(stream, true);

                Assert.That(driver.FileSystemType, Is.EqualTo("DECB"));
                Assert.That(driver.ListDirectory("/"), Is.Empty);
                byte[] sector = new byte[256];
                driver.ReadBlock(BlockAddress.FromLBA(0), sector);
                Assert.That(sector[0], Is.EqualTo(0x4F), "the raw sectors can still be read");
                driver.Dispose();
            }
        }

        /// <summary>
        /// PLANETFALL has 67 free granules of 68. A file of exactly 67 granules fills the disk and leaves tracks 35 to 39 as they were; one
        /// byte more does not fit. On a 78 granule reading of the disk both would fit.
        /// </summary>
        [Test]
        public void PlanetFall_WritesOnlyIntoTheSixtyEightGranules()
        {
            byte[] file = File.ReadAllBytes(Image("PLANETFALL.VDK"));
            const int GranuleBytes = 2304;
            const int FirstUnusedLsn = 35 * 18;
            const int Lsns = 40 * 18;

            DragonVdkDriver driver = new DragonVdkDriver();
            using (MemoryStream stream = new MemoryStream())
            {
                stream.Write(file, 0, file.Length);
                stream.Position = 0;
                driver.Mount(stream, false);
                Assert.That(driver.CanWrite, Is.True);

                Assert.That(() => driver.WriteFile("BIG.BIN", new byte[(67 * GranuleBytes) + 1]), Throws.InstanceOf<IOException>().With.Message.Contain("full"));

                byte[] data = new byte[67 * GranuleBytes];
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = (byte)(i * 7);
                }

                driver.WriteFile("FILL.BIN", data);
                Assert.That(driver.ReadFile("FILL.BIN"), Is.EqualTo(data));
                Assert.That(driver.ReadFile("GAME.BIN").Length, Is.EqualTo(106));

                byte[] sector = new byte[256];
                for (int lsn = FirstUnusedLsn; lsn < Lsns; lsn++)
                {
                    driver.ReadBlock(BlockAddress.FromLBA((uint)lsn), sector);
                    Assert.That(sector, Is.EqualTo(file.AsSpan(256 + (lsn * 256), 256).ToArray()), "LSN " + lsn + " past track 34 is untouched");
                }

                driver.Dispose();
            }
        }
    }
}
