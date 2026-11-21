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

        // CXDISK set$up$c64: the CP/M track is the Commodore track, plus 1 from track 18 up.
        [TestCase(3, 0, 3, 0, TestName = "C64 CP/M track 3, the first data track, is Commodore track 3")]
        [TestCase(17, 16, 17, 16)]
        [TestCase(18, 0, 19, 0, TestName = "C64 CP/M track 18 steps over the Commodore directory track")]
        [TestCase(33, 16, 34, 16, TestName = "C64 CP/M track 33, the last, is Commodore track 34")]
        public void C64TrackAndSector_FollowsTheBios(int cpmTrack, int cpmSector, int expectedTrack, int expectedSector)
        {
            C64CpmRecordMap.TrackAndSector(cpmTrack, cpmSector, out int track, out int sector);
            Assert.That(track, Is.EqualTo(expectedTrack), "track");
            Assert.That(sector, Is.EqualTo(expectedSector), "sector");
        }

        [Test]
        public void DiskParameters_C64_AreWhatTheBiosMacroGives()
        {
            // CXDISK dpb$c64$cpm: dpb 256,17,34,1024,64,3.
            CpmDiskParameters p = CpmDriver.C64Parameters();
            Assert.That(p.BlockSize, Is.EqualTo(1024));
            Assert.That(p.BlockCount, Is.EqualTo(131), "DSM 130");
            Assert.That(p.DirectoryEntries, Is.EqualTo(64));
            Assert.That(p.ReservedTracks, Is.EqualTo(3));
        }

        [Test]
        public void C64CpmD64_IsDetectedAsCpmAndListsTheCpm22Programs()
        {
            string path = Image("cpm.d64");
            RequireImage(path);
            DriverFactory factory = new DriverFactory();
            factory.Register(new CommodoreDiskDriverProvider());
            factory.Register(new CpmDriverProvider());
            IFileSystemDriver driver = factory.Detect(path)!;
            Assert.That(driver.FileSystemType, Is.EqualTo("CPM"));
            using FileStream stream = File.OpenRead(path);
            driver.Mount(stream, readOnly: true);
            Assert.That(Names(driver.ListDirectory("/")), Does.Contain("|PIP.COM|").And.Contain("|DDT.COM|").And.Contain("|DUMP.ASM|"));
        }

        [Test]
        public void C64CpmD64_DumpAsmReadsAsAssemblerSource()
        {
            CpmDriver driver = Mount("cpm.d64");
            byte[] data = driver.ReadFile("/DUMP.ASM");
            int end = Array.IndexOf(data, (byte)0x1A);
            Assert.That(end, Is.GreaterThan(0), "a CP/M text file ends with Ctrl-Z");
            string text = Encoding.ASCII.GetString(data, 0, end);
            Assert.That(text, Does.Contain("ORG").IgnoreCase.And.Contain("BDOS").IgnoreCase);
        }

        [Test]
        public void OrdinaryCbmD64_IsNotClaimedAsCpm()
        {
            string folder = TestData.FromVariable(TestData.TestImagesVariable, "d64");
            if (!Directory.Exists(folder))
            {
                Assert.Ignore("D64 test images not found: " + folder);
            }

            string[] images = Directory.GetFiles(folder, "*.d64");
            Assert.That(images.Length, Is.GreaterThan(0), "no D64 images in " + folder);
            DriverFactory factory = new DriverFactory();
            factory.Register(new CommodoreDiskDriverProvider());
            factory.Register(new CpmDriverProvider());
            for (int i = 0; i < images.Length; i++)
            {
                IFileSystemDriver? driver = factory.Detect(images[i]);
                Assert.That(driver?.FileSystemType, Is.Not.EqualTo("CPM"), Path.GetFileName(images[i]) + " is a CBM DOS disk");
            }
        }

        [TestCase("TurboPascal.d71")]
        [TestCase("28may87-1.d64")]
        [TestCase("TurboPascal.d81")]
        [TestCase("cpm.d64")]
        public void Write_NewFile_ReadsBackOnAFreshMountAndLeavesOtherFilesAlone(string imageName)
        {
            byte[] original = LoadImage(imageName);
            CpmDriver before = MountBytes(original, writable: false);
            DirectoryEntry[] filesBefore = before.ListDirectory("/");
            string someFile = "/" + filesBefore[0].Name;
            byte[] someFileBefore = before.ReadFile(someFile);

            byte[] data = Pattern(5000);
            using MemoryStream stream = new MemoryStream();
            stream.Write(original, 0, original.Length);
            CpmDriver writer = new CpmDriver();
            writer.Mount(stream, readOnly: false);
            writer.WriteFile("/NEW.DAT", data);

            CpmDriver after = MountBytes(stream.ToArray(), writable: false);
            byte[] back = after.ReadFile("/NEW.DAT");
            Assert.That(back.Length, Is.EqualTo(5120), "5000 bytes are 40 records of 128");
            Assert.That(back.AsSpan(0, data.Length).SequenceEqual(data), Is.True, "the data comes back");
            for (int i = data.Length; i < back.Length; i++)
            {
                Assert.That(back[i], Is.EqualTo(0x1A), "the last record is filled with Ctrl-Z");
            }

            Assert.That(after.ReadFile(someFile), Is.EqualTo(someFileBefore), someFile + " is not changed");
            Assert.That(after.ListDirectory("/").Length, Is.EqualTo(filesBefore.Length + 1));
        }

        [Test]
        public void Write_FileOfTwoEntries_GetsTheExtentNumbersCpmUses()
        {
            // Double-sided C128: 2 KB blocks, EXM 1, 16 one-byte block numbers, so one entry holds
            // 32 KB = 256 records = two logical extents. 40 KB = 320 records: the first entry ends
            // in extent 1 with RC 128, the second is extent 2 with RC 64.
            byte[] original = LoadImage("TurboPascal.d71");
            CpmDiskParameters p = CpmDiskParameters.FromCpm3Macro(1024, 1, 340, 2048, 128, 0);
            CpmFileSystem fs = new CpmFileSystem(original, p, new C128CpmRecordMap(true, p.BlockCount * 8));
            byte[] data = Pattern(40 * 1024);
            fs.Write(0, "BIG.DAT", data);

            byte[] directory = CpmFileSystem.DirectoryBytes(fs.Image, p, new C128CpmRecordMap(true, p.BlockCount * 8));
            int entries = 0;
            for (int e = 0; e < p.DirectoryEntries; e++)
            {
                int at = e * 32;
                if (directory[at] == 0 && Encoding.ASCII.GetString(directory, at + 1, 11) == "BIG     DAT")
                {
                    int extent = directory[at + 12] | (directory[at + 14] << 5);
                    if (entries == 0)
                    {
                        Assert.That(extent, Is.EqualTo(1), "first entry extent");
                        Assert.That(directory[at + 15], Is.EqualTo(128), "first entry RC");
                    }
                    else
                    {
                        Assert.That(extent, Is.EqualTo(2), "second entry extent");
                        Assert.That(directory[at + 15], Is.EqualTo(64), "second entry RC");
                    }

                    entries++;
                }
            }

            Assert.That(entries, Is.EqualTo(2));
            CpmFileSystem again = new CpmFileSystem(fs.Image, p, new C128CpmRecordMap(true, p.BlockCount * 8));
            Assert.That(again.Read(again.Find(0, "BIG.DAT")!), Is.EqualTo(data));
        }

        [Test]
        public void Write_TooBigForTheDisk_IsDiskFullAndChangesNothing()
        {
            byte[] original = LoadImage("TurboPascal.d71");
            byte[] copy = (byte[])original.Clone();
            CpmDiskParameters p = CpmDiskParameters.FromCpm3Macro(1024, 1, 340, 2048, 128, 0);
            CpmFileSystem fs = new CpmFileSystem(copy, p, new C128CpmRecordMap(true, p.BlockCount * 8));
            IOException error = Assert.Throws<IOException>(() => fs.Write(0, "HUGE.DAT", new byte[400 * 1024]))!;
            Assert.That(error.Message, Does.StartWith("Disk full"));
            Assert.That(copy, Is.EqualTo(original), "a failed write leaves the image as it was");
        }

        [Test]
        public void Delete_ThenWrite_ReusesTheFreedBlocks()
        {
            byte[] image = LoadImage("TurboPascal.d71");
            CpmDiskParameters p = CpmDiskParameters.FromCpm3Macro(1024, 1, 340, 2048, 128, 0);
            CpmFileSystem fs = new CpmFileSystem(image, p, new C128CpmRecordMap(true, p.BlockCount * 8));
            fs.Write(0, "ONE.DAT", Pattern(6000));
            int[] firstBlocks = Copy(fs.Find(0, "ONE.DAT")!.Blocks);
            fs.Delete(0, "ONE.DAT");
            Assert.That(fs.Find(0, "ONE.DAT"), Is.Null);
            fs.Write(0, "TWO.DAT", Pattern(6000));
            Assert.That(Copy(fs.Find(0, "TWO.DAT")!.Blocks), Is.EqualTo(firstBlocks), "the lowest free blocks are the ones just freed");
        }

        [Test]
        public void Rename_KeepsTheDataUnderTheNewName()
        {
            byte[] image = LoadImage("TurboPascal.d71");
            CpmDiskParameters p = CpmDiskParameters.FromCpm3Macro(1024, 1, 340, 2048, 128, 0);
            CpmFileSystem fs = new CpmFileSystem(image, p, new C128CpmRecordMap(true, p.BlockCount * 8));
            byte[] msg = fs.Read(fs.Find(0, "TURBO.MSG")!);
            fs.Rename(0, "TURBO.MSG", "ERRORS.TXT");
            Assert.That(fs.Find(0, "TURBO.MSG"), Is.Null);
            Assert.That(fs.Read(fs.Find(0, "ERRORS.TXT")!), Is.EqualTo(msg));
        }

        [Test]
        public void Write_OnAReadOnlyMount_IsRefused()
        {
            CpmDriver driver = MountBytes(LoadImage("TurboPascal.d71"), writable: false);
            Assert.That(driver.CanWrite, Is.False);
            Assert.Throws<UnauthorizedAccessException>(() => driver.WriteFile("/X.DAT", new byte[10]));
        }

        [TestCase("A B.TXT")]
        [TestCase("TOOLONGNAME.TXT")]
        [TestCase("A.TEXT")]
        [TestCase("A*.TXT")]
        public void Write_BadCpmName_IsRefused(string name)
        {
            byte[] image = LoadImage("TurboPascal.d71");
            CpmDiskParameters p = CpmDiskParameters.FromCpm3Macro(1024, 1, 340, 2048, 128, 0);
            CpmFileSystem fs = new CpmFileSystem(image, p, new C128CpmRecordMap(true, p.BlockCount * 8));
            Assert.Throws<ArgumentException>(() => fs.Write(0, name, new byte[1]));
        }

        // An independent check of the directory entries RetroIO writes: the data area, laid out flat in
        // record order, is handed to cpmtools (run in WSL) with a matching disk definition. cpmtools
        // knows nothing of Commodore track layouts, so the record map is not what this checks; the
        // CP/M directory, extents and block lists are.
        [Test]
        public void Write_FilesWrittenByRetroIO_AreReadBackByCpmtools()
        {
            byte[] image = LoadImage("TurboPascal.d71");
            CpmDiskParameters p = CpmDiskParameters.FromCpm3Macro(1024, 1, 340, 2048, 128, 0);
            C128CpmRecordMap map = new C128CpmRecordMap(true, p.BlockCount * 8);
            CpmFileSystem fs = new CpmFileSystem(image, p, map);
            byte[] big = Pattern(40 * 1024);
            byte[] small = Encoding.ASCII.GetBytes("HELLO FROM RETROIO\r\n");
            fs.Write(0, "BIG.DAT", big);
            fs.Write(3, "SMALL.TXT", small);

            string folder = Path.Combine(Path.GetTempPath(), "retroio-cpmtools-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                byte[] flat = new byte[map.RecordCount * 256];
                for (int r = 0; r < map.RecordCount; r++)
                {
                    Array.Copy(fs.Image, map.ImageOffset(r), flat, r * 256, 256);
                }

                File.WriteAllBytes(Path.Combine(folder, "flat.img"), flat);
                File.WriteAllText(Path.Combine(folder, "diskdefs"),
                    "diskdef c128flat\n  seclen 256\n  tracks " + map.RecordCount + "\n  sectrk 1\n  blocksize 2048\n"
                    + "  maxdir 128\n  skew 0\n  boottrk 0\n  os 3\nend\n");

                string output = Cpmtools.Run(folder,
                    "cpmcp -f c128flat flat.img 0:BIG.DAT big.out && cpmcp -f c128flat flat.img 3:SMALL.TXT small.out && cpmls -f c128flat flat.img");
                Assert.That(output, Does.Contain("big.dat").IgnoreCase.And.Contain("turbo.com").IgnoreCase, output);

                byte[] bigBack = File.ReadAllBytes(Path.Combine(folder, "big.out"));
                Assert.That(bigBack.Length, Is.EqualTo(big.Length), "cpmtools sees BIG.DAT as 320 records");
                Assert.That(bigBack, Is.EqualTo(big));
                byte[] smallBack = File.ReadAllBytes(Path.Combine(folder, "small.out"));
                Assert.That(smallBack.AsSpan(0, small.Length).SequenceEqual(small), Is.True, "user 3 SMALL.TXT");
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        private static byte[] LoadImage(string name)
        {
            string path = Image(name);
            RequireImage(path);
            return File.ReadAllBytes(path);
        }

        private static CpmDriver MountBytes(byte[] image, bool writable)
        {
            MemoryStream stream = new MemoryStream(image, writable);
            CpmDriver driver = new CpmDriver();
            driver.Mount(stream, readOnly: !writable);
            return driver;
        }

        private static byte[] Pattern(int length)
        {
            byte[] data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (byte)(i * 7 + i / 251);
            }

            return data;
        }

        private static int[] Copy(System.Collections.Generic.IReadOnlyList<int> list)
        {
            int[] copy = new int[list.Count];
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = list[i];
            }

            return copy;
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
