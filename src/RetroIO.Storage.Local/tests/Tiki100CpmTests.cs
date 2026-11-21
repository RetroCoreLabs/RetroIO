//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Local.FileSystems.Cpm;
using RetroIO.Tests;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// TIKI-100 (TIKO) floppies: the layouts from the TIKO 4.01 kernel and real disks, and the
    /// sizes TIKI shares with Kaypro and Osborne disks.
    /// </summary>
    [TestFixture]
    public class Tiki100CpmTests
    {
        private static string Image(string name) => TestData.FromVariable(TestData.TestImagesVariable, Path.Combine("cpm", "tiki100", name));

        [TestCase("tiko_kjerne_v4.01.dsk", "KOPI.COM")]
        [TestCase("AR-games.dsk", "TIKIJSW.COM")]
        [TestCase("ar-games-200k.dsk", "TIKIJSW.COM")]
        [TestCase("FD0054.dsk", "SJAKK.COM")]
        public void RealDisk_IsDetectedAsCpmWithTheTikiLayout(string name, string expectedFile)
        {
            string path = RequireImage(Image(name));
            DriverFactory factory = new DriverFactory();
            factory.Register(new CpmDriverProvider());
            factory.Register(new PcFloppyDriverProvider());
            IFileSystemDriver driver = factory.Detect(path)!;
            Assert.That(driver.FileSystemType, Is.EqualTo("CPM"));
            using FileStream stream = File.OpenRead(path);
            driver.Mount(stream, readOnly: true);
            Assert.That(driver.GetMetadata("/" + expectedFile), Is.Not.Null, expectedFile);

            byte[] image = File.ReadAllBytes(path);
            Assert.That(CpmDriver.TrySelectFlatLayout(image.Length, image, out CpmDiskParameters? chosen, out _), Is.True);
            CpmDriver.TryTiki100(image.Length, out CpmDiskParameters? tiki, out _);
            Assert.That(chosen!.ReservedTracks, Is.EqualTo(tiki!.ReservedTracks), "the TIKI layout, not Kaypro or Osborne");
            Assert.That(chosen.BlockSize, Is.EqualTo(tiki.BlockSize));
        }

        [Test]
        public void KernelDisk_MenuText_IsTheMenuProgramText()
        {
            CpmDriver driver = Mount(Image("tiko_kjerne_v4.01.dsk"));
            // MENY.TXT is text with a few screen control codes (19h blanks the screen, as the file's own
            // comment says) and Norwegian letters in their 7-bit forms: } for a-ring, | for o-slash.
            string menu = Encoding.ASCII.GetString(driver.ReadFile("/MENY.TXT"));
            Assert.That(menu, Does.Contain("; Kode for blanking av skjerm"));
            Assert.That(menu, Does.Contain("Slutt p} meny. Kj|r vanlig TIKO."));
        }

        [TestCase(92160, 83, 32, 3)]
        [TestCase(204800, 190, 64, 2)]
        [TestCase(409600, 195, 128, 1)]
        [TestCase(819200, 395, 128, 1)]
        public void Layouts_AreTheKernelAndMeasuredValues(int length, int blocks, int entries, int reserved)
        {
            Assert.That(CpmDriver.TryTiki100(length, out CpmDiskParameters? p, out _), Is.True);
            Assert.That(p!.BlockCount, Is.EqualTo(blocks));
            Assert.That(p.DirectoryEntries, Is.EqualTo(entries));
            Assert.That(p.ReservedTracks, Is.EqualTo(reserved));
        }

        [Test]
        public void Disk800K_HelpFileOnAHardDiskTopic_ReadsBack()
        {
            CpmDriver driver = Mount(Image("FD0054.dsk"));
            string text = Encoding.ASCII.GetString(driver.ReadFile("/LES.MEG"));
            Assert.That(text, Does.Contain("TIKI-100 SYSTEM FOR WINCHESTER HARDDISK"));
            Assert.That(driver.GetMetadata("/ROBOT.DOC"), Is.Not.Null);
        }

        [Test]
        public void SkewTable_FirstRecordsOfATrack_FollowTheKernelTable()
        {
            CpmDriver.TryTiki100(92160, out _, out ICpmRecordMap? map);
            long data = 3 * 18 * 128;
            Assert.That(map!.ImageOffset(0), Is.EqualTo(data), "logical 0 is physical sector 1");
            Assert.That(map.ImageOffset(1), Is.EqualTo(data + 5 * 128), "logical 1 is physical sector 6");
            Assert.That(map.ImageOffset(18), Is.EqualTo(data + 18 * 128), "the next track starts again at sector 1");
        }

        [TestCase("t90.dsk")]
        [TestCase("t200.dsk")]
        [TestCase("t400.dsk")]
        [TestCase("t800.dsk")]
        public void EmptyDisk_WriteThenReadBack(string name)
        {
            byte[] image = File.ReadAllBytes(RequireImage(Image(name)));
            CpmDriver.TryTiki100(image.Length, out CpmDiskParameters? p, out ICpmRecordMap? map);
            CpmFileSystem fs = new CpmFileSystem(image, p!, map!);
            Assert.That(fs.Files.Count, Is.EqualTo(0), "formatted and empty");
            byte[] text = Encoding.ASCII.GetBytes("HILSEN FRA RETROIO\r\n");
            fs.Write(0, "HEI.TXT", text);
            CpmFileSystem again = new CpmFileSystem(fs.Image, p!, map!);
            Assert.That(again.Read(again.Find(0, "HEI.TXT")!).AsSpan(0, text.Length).SequenceEqual(text), Is.True);
        }

        private static CpmDriver Mount(string path)
        {
            RequireImage(path);
            CpmDriver driver = new CpmDriver();
            using FileStream stream = File.OpenRead(path);
            driver.Mount(stream, readOnly: true);
            return driver;
        }

        private static string RequireImage(string path)
        {
            if (!File.Exists(path))
            {
                Assert.Ignore("TIKI-100 test image not found: " + path + " (see the README, CP/M test images)");
            }

            return path;
        }
    }
}
