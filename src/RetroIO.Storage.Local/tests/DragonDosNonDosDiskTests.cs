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
    /// So the disks have no DragonDOS directory, and the driver mounts them but refuses to list them as a DragonDOS volume instead of
    /// showing the code and data in the directory track as files. Set RETROIO_DRAGON_TEST_IMAGES to the folder with the vdk folder.
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

        [TestCase("HTCHHKER.VDK")]
        [TestCase("PLANETFALL.VDK")]
        public void GameDisk_MountsButIsNotListedAsADragonDosVolume(string name)
        {
            DragonVdkDriver driver = new DragonVdkDriver();
            using (FileStream stream = File.OpenRead(Image(name)))
            {
                driver.Mount(stream, true);

                Assert.That(driver.VolumeLabel, Is.EqualTo("VDK DISK"));
                Assert.That(() => driver.ListDirectory("/"), Throws.InstanceOf<InvalidDataException>().With.Message.Contain("not a DragonDOS volume"));
                Assert.That(() => driver.ReadFile("A.BIN"), Throws.InstanceOf<InvalidDataException>());
                Assert.That(driver.GetBlockDeviceInfo()!.CylinderCount, Is.EqualTo(40));
                byte[] sector = new byte[256];
                driver.ReadBlock(BlockAddress.FromLBA(0), sector);
                Assert.That(sector[0], Is.EqualTo(0x4F), "the raw sectors can still be read");
                driver.Dispose();
            }
        }

        [Test]
        public void GameDisk_CannotBeWritten()
        {
            DragonVdkDriver driver = new DragonVdkDriver();
            using (FileStream stream = new FileStream(Image("PLANETFALL.VDK"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                driver.Mount(stream, false);
                Assert.That(driver.CanWrite, Is.False);
                Assert.That(() => driver.WriteFile("A.BIN", new byte[] { 1 }), Throws.InstanceOf<UnauthorizedAccessException>());
                driver.Dispose();
            }
        }
    }
}
