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
    /// Commodore 128 CP/M disks: the C128 ROM's track and sector rule, the BIOS disk layouts, and
    /// real disks read through them.
    /// </summary>
    [TestFixture]
    public class C128CpmTests
    {
        private static string Image(string name) => TestData.FromVariable(TestData.TestImagesVariable, Path.Combine("cpm", "c128", name));

        // Expected values follow the C128 Z80 ROM routine at 03E3h by hand: record + 2, zones of
        // 21/19/18/17 sectors, track 18 position 0 skipped, skew table at 0FB5h.
        [TestCase(0, 1, 10, TestName = "Record 0 is track 1 position 2 (positions 0 and 1 are never CP/M)")]
        [TestCase(1, 1, 15)]
        [TestCase(2, 1, 20)]
        [TestCase(3, 1, 4, TestName = "Record 3 wraps round the 21-sector track")]
        [TestCase(354, 17, 16, TestName = "Record 354 is the last position of track 17")]
        [TestCase(355, 18, 5, TestName = "Record 355 is track 18 position 1: the Commodore header sector is skipped")]
        [TestCase(679, 35, 12, TestName = "Record 679 is the last sector of side 1")]
        [TestCase(680, 36, 10, TestName = "Record 680 starts side 2 the way record 0 starts side 1")]
        public void TrackAndSector_FollowsTheC128Rom(int record, int expectedTrack, int expectedSector)
        {
            C128CpmRecordMap.TrackAndSector(record, out int track, out int sector);
            Assert.That(track, Is.EqualTo(expectedTrack), "track");
            Assert.That(sector, Is.EqualTo(expectedSector), "sector");
        }

        [Test]
        public void TrackAndSector_UsesEverySectorOfASideOnceExceptThreeAndNeverTrack18Sector0()
        {
            // One side has 683 sectors. CP/M leaves out track 1 sectors 0 and 5 and track 18 sector 0,
            // so records 0-679 must land on the other 680, each exactly once.
            bool[,] seen = new bool[36, 21];
            for (int record = 0; record < 680; record++)
            {
                C128CpmRecordMap.TrackAndSector(record, out int track, out int sector);
                Assert.That(seen[track, sector], Is.False, "record " + record + " lands on a sector already used: " + track + "/" + sector);
                seen[track, sector] = true;
            }

            Assert.That(seen[1, 0], Is.False, "track 1 sector 0 is the CBM boot sector");
            Assert.That(seen[18, 0], Is.False, "track 18 sector 0 is the Commodore header");
        }

        [Test]
        public void DiskParameters_DoubleSided_AreWhatTheBiosMacroGives()
        {
            // CXDISK dpb$c128$DS: dpb 1024,1,340,2048,128,0.
            CpmDiskParameters p = CpmDiskParameters.FromCpm3Macro(1024, 1, 340, 2048, 128, 0);
            Assert.That(p.BlockSize, Is.EqualTo(2048));
            Assert.That(p.BlockCount, Is.EqualTo(170), "DSM 169");
            Assert.That(p.ExtentMask, Is.EqualTo(1));
            Assert.That(p.DirectoryEntries, Is.EqualTo(128));
            Assert.That(p.DirectoryBlocks, Is.EqualTo(2));
            Assert.That(p.WideBlockNumbers, Is.False);
        }

        [Test]
        public void DiskParameters_SingleSided_AreWhatTheBiosMacroGives()
        {
            // CXDISK dpb$c128$SS: dpb 1024,1,170,1024,64,0.
            CpmDiskParameters p = CpmDiskParameters.FromCpm3Macro(1024, 1, 170, 1024, 64, 0);
            Assert.That(p.BlockSize, Is.EqualTo(1024));
            Assert.That(p.BlockCount, Is.EqualTo(170));
            Assert.That(p.ExtentMask, Is.EqualTo(0));
            Assert.That(p.DirectoryEntries, Is.EqualTo(64));
        }

        [Test]
        public void TurboPascalD71_IsDetectedAsCpmAheadOfCbmDos()
        {
            string path = Image("TurboPascal.d71");
            RequireImage(path);
            DriverFactory factory = new DriverFactory();
            factory.Register(new CommodoreDiskDriverProvider());
            factory.Register(new CpmDriverProvider());
            IFileSystemDriver? driver = factory.Detect(path);
            Assert.That(driver, Is.Not.Null);
            Assert.That(driver!.FileSystemType, Is.EqualTo("CPM"));
        }

        [Test]
        public void TurboPascalD71_ListsTurboPascalAndReadsItsMessageFile()
        {
            CpmDriver driver = Mount("TurboPascal.d71");
            DirectoryEntry[] entries = driver.ListDirectory("/");
            Assert.That(Names(entries), Does.Contain("|TURBO.COM|").And.Contain("|TURBO.MSG|").And.Contain("|TINST.COM|"));

            // TURBO.MSG is Turbo Pascal's compiler message text.
            byte[] msg = driver.ReadFile("/TURBO.MSG");
            Assert.That(msg.Length % 128, Is.EqualTo(0), "CP/M files are whole 128-byte records");
            string text = Encoding.ASCII.GetString(msg);
            Assert.That(text, Does.Contain(" are not allowed").And.Contain("Undefined").And.Contain(" out of range"));
        }

        [Test]
        public void TurboPascalD71_NoTwoFilesShareABlockAndNoneUsesTheDirectory()
        {
            CpmFileSystem fs = FileSystemOf("TurboPascal.d71", doubleSided: true);
            bool[] used = new bool[170];
            for (int f = 0; f < fs.Files.Count; f++)
            {
                for (int b = 0; b < fs.Files[f].Blocks.Count; b++)
                {
                    int block = fs.Files[f].Blocks[b];
                    Assert.That(block, Is.GreaterThanOrEqualTo(2), fs.Files[f].Name + " uses a directory block");
                    Assert.That(used[block], Is.False, fs.Files[f].Name + " shares block " + block);
                    used[block] = true;
                }
            }
        }

        [Test]
        public void MbasicD71_ReadsMbasicCom()
        {
            CpmDriver driver = Mount("MBASIC.d71");
            byte[] mbasic = driver.ReadFile("/MBASIC.COM");
            Assert.That(mbasic.Length, Is.GreaterThan(16 * 1024), "MBASIC.COM is a program of more than one extent");
            Assert.That(Encoding.ASCII.GetString(mbasic), Does.Contain("Microsoft"));
        }

        [Test]
        public void SystemD64_SingleSided_ReadsCpmPlusSys()
        {
            CpmDriver driver = Mount("28may87-1.d64");
            Assert.That(Names(driver.ListDirectory("/")), Does.Contain("|CPM+.SYS|"));
            Assert.That(driver.GetMetadata("/CPM+.SYS")!.Size, Is.GreaterThan(0));
        }

        // CXDISK c1581$adj: track 78 and up moves one on; drive track = track / 2, odd = top side;
        // first 512-byte drive sector = 2 * sector + 1.
        [TestCase(0, 0, 0, false, 1, TestName = "CP/M track 0 sector 0 is drive track 0 bottom, sector 1")]
        [TestCase(77, 4, 38, true, 9, TestName = "CP/M track 77 is drive track 38 top")]
        [TestCase(78, 0, 39, true, 1, TestName = "CP/M track 78 skips the bottom of drive track 39 (the Commodore directory)")]
        [TestCase(79, 0, 40, false, 1)]
        [TestCase(158, 4, 79, true, 9, TestName = "CP/M track 158 is the top of the last drive track")]
        public void C1581DriveAddress_FollowsTheBios(int cpmTrack, int cpmSector, int expectedTrack, bool expectedTop, int expectedSector)
        {
            C1581CpmRecordMap.DriveAddress(cpmTrack, cpmSector, out int track, out bool top, out int sector);
            Assert.That(track, Is.EqualTo(expectedTrack), "drive track");
            Assert.That(top, Is.EqualTo(expectedTop), "top side");
            Assert.That(sector, Is.EqualTo(expectedSector), "drive sector");
        }

        [Test]
        public void DiskParameters_1581_AreWhatTheBiosMacroGives()
        {
            // CXDISK dpb$1581: dpb 1024,5,159,2048,128,0.
            CpmDiskParameters p = CpmDriver.C1581Parameters();
            Assert.That(p.BlockSize, Is.EqualTo(2048));
            Assert.That(p.BlockCount, Is.EqualTo(397), "DSM 396");
            Assert.That(p.ExtentMask, Is.EqualTo(0));
            Assert.That(p.WideBlockNumbers, Is.True, "DSM above 255 gives two-byte block numbers");
        }

        [Test]
        public void TurboPascalD81_IsDetectedAsCpm()
        {
            string path = Image("TurboPascal.d81");
            RequireImage(path);
            DriverFactory factory = new DriverFactory();
            factory.Register(new CommodoreDiskDriverProvider());
            factory.Register(new CpmDriverProvider());
            Assert.That(factory.Detect(path)!.FileSystemType, Is.EqualTo("CPM"));
        }

        [Test]
        public void OrdinaryCbmD81_IsNotClaimedAsCpm()
        {
            string path = TestData.FromVariable(TestData.TestImagesVariable, Path.Combine("d81", "Die_Hard_1_1989_Activision.d81"));
            RequireImage(path);
            DriverFactory factory = new DriverFactory();
            factory.Register(new CommodoreDiskDriverProvider());
            factory.Register(new CpmDriverProvider());
            Assert.That(factory.Detect(path)!.FileSystemType, Is.EqualTo("CBM-DOS"));
        }

        // STUDRECS.PAS and TRACK.PAS lie past CP/M track 78; without the skip they read damaged.
        [TestCase("/STUDRECS.PAS")]
        [TestCase("/TRACK.PAS")]
        [TestCase("/HANGMAN.PAS")]
        public void TurboPascalD81_SourcePastTheSkippedTrackReadsAsCleanText(string file)
        {
            CpmDriver driver = Mount("TurboPascal.d81");
            byte[] data = driver.ReadFile(file);
            int end = Array.IndexOf(data, (byte)0x1A);
            Assert.That(end, Is.GreaterThan(0), "a CP/M text file ends with Ctrl-Z");
            for (int i = 0; i < end; i++)
            {
                byte c = data[i];
                bool text = (c >= 0x20 && c < 0x7F) || c == 9 || c == 10 || c == 12 || c == 13;
                Assert.That(text, Is.True, file + ": byte " + c + " at " + i + " is not Pascal source text");
            }
        }

        private static CpmDriver Mount(string name)
        {
            string path = Image(name);
            RequireImage(path);
            CpmDriver driver = new CpmDriver();
            using FileStream stream = File.OpenRead(path);
            driver.Mount(stream, readOnly: true);
            return driver;
        }

        private static CpmFileSystem FileSystemOf(string name, bool doubleSided)
        {
            string path = Image(name);
            RequireImage(path);
            byte[] image = File.ReadAllBytes(path);
            CpmDiskParameters p = doubleSided
                ? CpmDiskParameters.FromCpm3Macro(1024, 1, 340, 2048, 128, 0)
                : CpmDiskParameters.FromCpm3Macro(1024, 1, 170, 1024, 64, 0);
            return new CpmFileSystem(image, p, new C128CpmRecordMap(doubleSided, p.BlockCount * (p.BlockSize / 256)));
        }

        private static void RequireImage(string path)
        {
            if (!File.Exists(path))
            {
                Assert.Ignore("C128 CP/M test image not found: " + path + " (see the README, CP/M test images)");
            }
        }

        private static string Names(DirectoryEntry[] entries)
        {
            StringBuilder names = new StringBuilder("|");
            for (int i = 0; i < entries.Length; i++)
            {
                names.Append(entries[i].Name).Append('|');
            }

            return names.ToString();
        }
    }
}
