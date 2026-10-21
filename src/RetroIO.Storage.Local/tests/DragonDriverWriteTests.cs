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
    /// Tests that a write through the VDK, DSK/JVC and DMK drivers reaches the image file and reads back after a new mount, for the
    /// DragonDOS, DECB and OS-9 volumes.
    /// </summary>
    /// <remarks>
    /// Each test works on a copy of an image in a temporary folder (images built in the test, or a copy of a real image from the RetroFS
    /// test-image folder, or the folder in RETROIO_DRAGON_TEST_IMAGES; a test skips when its real image is missing). The file is read back
    /// through a new driver on a new stream, and the raw bytes of the file are looked at, so what is tested is what is in the file.
    /// </remarks>
    [TestFixture]
    public class DragonDriverWriteTests
    {
        private string _folder = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "dragon-driver-write-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
            }
        }

        private static string RealImage(string folder, string name)
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable("RETROIO_DRAGON_TEST_IMAGES");
            string root = !string.IsNullOrEmpty(fromEnvironment)
                ? fromEnvironment
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "RetroFS", "test-images");
            string path = Path.Combine(root, folder, name);
            if (!File.Exists(path))
            {
                Assert.Ignore("Test image not found: " + name);
            }

            return path;
        }

        private string Save(string name, byte[] bytes)
        {
            string path = Path.Combine(_folder, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private string Copy(string source, string name)
        {
            string path = Path.Combine(_folder, name);
            File.Copy(source, path);
            return path;
        }

        private static byte[] ReadRaw(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] bytes = new byte[stream.Length];
                stream.ReadExactly(bytes, 0, bytes.Length);
                return bytes;
            }
        }

        private static FileStream OpenReadWrite(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }

        private static byte[] Header12(int tracks, int heads)
        {
            return new byte[] { (byte)'d', (byte)'k', 12, 0, 0x10, 0x10, (byte)'X', 0x10, (byte)tracks, (byte)heads, 0, 0 };
        }

        private static byte[] Join(byte[] a, byte[] b)
        {
            byte[] all = new byte[a.Length + b.Length];
            Array.Copy(a, all, a.Length);
            Array.Copy(b, 0, all, a.Length, b.Length);
            return all;
        }

        // Mounts a new driver of the given type on a copy of the bytes that are in the file right now. What it shows is what a later
        // mount of the file shows, whatever the first driver still holds in memory.
        private static T Fresh<T>(string path) where T : IFileSystemDriver, new()
        {
            T driver = new T();
            driver.Mount(new MemoryStream(ReadRaw(path)), true);
            return driver;
        }

        private static byte[] ReadBack(IFileSystemDriver driver, string path)
        {
            return driver.ReadFile(path);
        }

        // ------------------------------------------------------------------ VDK, DragonDOS

        [Test]
        public void Vdk_WriteDeleteRename_ReachTheFile_AndReadBackAfterANewMount()
        {
            byte[] sectors = DragonDosTestDisk.Format(40, 1);
            byte[] header = Header12(40, 1);
            string path = Save("t.vdk", Join(header, sectors));
            byte[] first = DragonDosTestDisk.Pattern(1234, 1);
            byte[] second = DragonDosTestDisk.Pattern(5000, 2);

            using (FileStream stream = OpenReadWrite(path))
            {
                DragonVdkDriver driver = new DragonVdkDriver();
                driver.Mount(stream, false);
                Assert.That(driver.CanWrite, Is.True);
                Assert.That(driver.Capabilities.HasFlag(DriverCapabilities.Write), Is.True);

                driver.WriteFile("A.BIN", first);
                Assert.That(Fresh<DragonVdkDriver>(path).ReadFile("A.BIN"), Is.EqualTo(first), "after the first write the file has it");
                driver.WriteFile("B.BIN", second);
                Assert.That(Fresh<DragonVdkDriver>(path).ReadFile("B.BIN"), Is.EqualTo(second));
                driver.RenameFile("B.BIN", "C.BIN");
                Assert.That(Fresh<DragonVdkDriver>(path).ReadFile("C.BIN"), Is.EqualTo(second), "after the rename the file has the new name");
                Assert.That(Fresh<DragonVdkDriver>(path).GetMetadata("B.BIN"), Is.Null);
                driver.WriteFile("D.BIN", new byte[] { 1 });
                Assert.That(Fresh<DragonVdkDriver>(path).GetMetadata("D.BIN"), Is.Not.Null);
                driver.DeleteFile("D.BIN");
                Assert.That(Fresh<DragonVdkDriver>(path).GetMetadata("D.BIN"), Is.Null, "after the delete the file does not have it");

                // The file on disk is already complete: nothing waits for Dispose.
                byte[] raw = ReadRaw(path);
                Assert.That(raw.Length, Is.EqualTo(12 + sectors.Length));
                Assert.That(raw.AsSpan(0, 12).ToArray(), Is.EqualTo(header), "the VDK header is kept");
                Assert.That(raw.AsSpan(12 + (360 * 256) + (2 * 256) + 1, 1)[0], Is.EqualTo((byte)'A'), "entry 0 of the directory is in the file");
                driver.Dispose();
            }

            using (FileStream stream = File.OpenRead(path))
            {
                DragonVdkDriver again = new DragonVdkDriver();
                again.Mount(stream, true);
                Assert.That(ReadBack(again, "A.BIN"), Is.EqualTo(first));
                Assert.That(ReadBack(again, "C.BIN"), Is.EqualTo(second));
                Assert.That(again.ListDirectory("/").Length, Is.EqualTo(2));
                Assert.That(again.GetMetadata("D.BIN"), Is.Null);
                again.Dispose();
            }
        }

        [Test]
        public void Vdk_MountedReadOnly_CannotWrite_AndTheFileIsNotChanged()
        {
            byte[] bytes = Join(Header12(40, 1), DragonDosTestDisk.Format(40, 1));
            string path = Save("ro.vdk", bytes);

            using (FileStream stream = OpenReadWrite(path))
            {
                DragonVdkDriver driver = new DragonVdkDriver();
                driver.Mount(stream, true);

                Assert.That(driver.CanWrite, Is.False);
                Assert.That(driver.Capabilities.HasFlag(DriverCapabilities.Write), Is.False);
                Assert.That(() => driver.WriteFile("A", new byte[] { 1 }), Throws.InstanceOf<UnauthorizedAccessException>());
                Assert.That(() => driver.DeleteFile("A"), Throws.InstanceOf<UnauthorizedAccessException>());
                Assert.That(() => driver.RenameFile("A", "B"), Throws.InstanceOf<UnauthorizedAccessException>());
                Assert.That(() => driver.WriteBlock(BlockAddress.FromLBA(0), new byte[256]), Throws.InstanceOf<UnauthorizedAccessException>());
                driver.Dispose();
            }

            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
        }

        [Test]
        public void Vdk_DiskThatIsNotDragonDos_CannotBeWritten()
        {
            byte[] sectors = new byte[40 * 18 * 256];
            sectors[0] = 0x4F;
            string path = Save("game.vdk", Join(Header12(40, 1), sectors));

            using (FileStream stream = OpenReadWrite(path))
            {
                DragonVdkDriver driver = new DragonVdkDriver();
                driver.Mount(stream, false);

                Assert.That(driver.CanWrite, Is.False);
                Assert.That(() => driver.WriteFile("A", new byte[] { 1 }), Throws.InstanceOf<UnauthorizedAccessException>());
                driver.Dispose();
            }
        }

        // ------------------------------------------------------------------ DSK, DragonDOS (headerless and with a JVC header)

        [TestCase(1, false)]
        [TestCase(2, true)]
        public void Dsk_DragonDos_WriteReachesTheFile(int heads, bool withHeader)
        {
            byte[] sectors = DragonDosTestDisk.Format(40, heads);
            byte[] bytes = withHeader ? Join(new byte[] { 18, (byte)heads, 1, 1, 0 }, sectors) : sectors;
            string path = Save("t.dsk", bytes);
            byte[] data = DragonDosTestDisk.Pattern(7777, 3);

            using (FileStream stream = OpenReadWrite(path))
            {
                DragonDskDriver driver = new DragonDskDriver();
                driver.Mount(stream, false);
                Assert.That(driver.FileSystemType, Is.EqualTo("DragonDOS"));
                Assert.That(driver.CanWrite, Is.True);

                driver.WriteFile("HELLO.BIN", data);
                driver.Dispose();
            }

            byte[] raw = File.ReadAllBytes(path);
            Assert.That(raw.Length, Is.EqualTo(bytes.Length));
            int offset = withHeader ? 5 : 0;
            int spt = 18 * heads;
            Assert.That(raw[offset + (((20 * spt) + 2) * 256) + 1], Is.EqualTo((byte)'H'));
            Assert.That(raw[offset + (((16 * spt) + 2) * 256) + 1], Is.EqualTo((byte)'H'), "the backup directory on track 16 has the entry too");

            using (FileStream stream = File.OpenRead(path))
            {
                DragonDskDriver again = new DragonDskDriver();
                again.Mount(stream, true);
                Assert.That(ReadBack(again, "HELLO.BIN"), Is.EqualTo(data));
                again.Dispose();
            }
        }

        [Test]
        public void Dsk_PartialLastTrackImage_WritesBackThroughTheGap()
        {
            byte[] sectors = DragonDosTestDisk.Format(40, 1);
            byte[] cut = new byte[sectors.Length - (4 * 256)];
            Array.Copy(sectors, cut, cut.Length);
            string path = Save("cut.dsk", cut);
            byte[] data = DragonDosTestDisk.Pattern(900, 3);

            using (FileStream stream = OpenReadWrite(path))
            {
                DragonDskDriver driver = new DragonDskDriver();
                driver.Mount(stream, false);
                driver.WriteFile("HELLO.BIN", data);
                driver.Dispose();
            }

            using (FileStream stream = File.OpenRead(path))
            {
                DragonDskDriver again = new DragonDskDriver();
                again.Mount(stream, true);
                Assert.That(ReadBack(again, "HELLO.BIN"), Is.EqualTo(data));
                again.Dispose();
            }
        }

        // ------------------------------------------------------------------ DSK, DECB

        // A disk as Toolshed dskini makes it (the same layout as DecbWriteTests uses): 35 tracks, FAT in sector 2 of track 17,
        // 68 free granules, the directory in sectors 3 to 11 all 0xFF.
        private static byte[] BlankDecb()
        {
            byte[] image = new byte[35 * 18 * 256];
            int fat = ((17 * 18) + 1) * 256;
            int dir = ((17 * 18) + 2) * 256;
            for (int i = 0; i < 68; i++)
            {
                image[fat + i] = 0xFF;
            }

            for (int i = 0; i < 9 * 256; i++)
            {
                image[dir + i] = 0xFF;
            }

            return image;
        }

        [Test]
        public void Dsk_Decb_WriteDeleteRename_ReachTheFile()
        {
            byte[] bytes = BlankDecb();
            string path = Save("decb.dsk", bytes);
            byte[] first = DragonDosTestDisk.Pattern(3000, 1);
            byte[] second = DragonDosTestDisk.Pattern(100, 2);

            using (FileStream stream = OpenReadWrite(path))
            {
                DragonDskDriver driver = new DragonDskDriver();
                driver.Mount(stream, false);
                Assert.That(driver.FileSystemType, Is.EqualTo("DECB"));
                Assert.That(driver.CanWrite, Is.True);

                driver.WriteFile("/ONE.BIN", first);
                Assert.That(Fresh<DragonDskDriver>(path).ReadFile("/ONE.BIN"), Is.EqualTo(first));
                driver.WriteFile("/TWO.TXT", second);
                Assert.That(Fresh<DragonDskDriver>(path).ReadFile("/TWO.TXT"), Is.EqualTo(second));
                driver.RenameFile("/TWO.TXT", "/THREE.TXT");
                Assert.That(Fresh<DragonDskDriver>(path).ReadFile("/THREE.TXT"), Is.EqualTo(second));
                Assert.That(Fresh<DragonDskDriver>(path).GetMetadata("/TWO.TXT"), Is.Null);
                driver.WriteFile("/GONE.BIN", new byte[] { 1 });
                Assert.That(Fresh<DragonDskDriver>(path).GetMetadata("/GONE.BIN"), Is.Not.Null);
                driver.DeleteFile("/GONE.BIN");
                Assert.That(Fresh<DragonDskDriver>(path).GetMetadata("/GONE.BIN"), Is.Null);
                driver.Dispose();
            }

            Assert.That(File.ReadAllBytes(path), Is.Not.EqualTo(bytes), "the image file changed");
            using (FileStream stream = File.OpenRead(path))
            {
                DragonDskDriver again = new DragonDskDriver();
                again.Mount(stream, true);
                Assert.That(ReadBack(again, "/ONE.BIN"), Is.EqualTo(first));
                Assert.That(ReadBack(again, "/THREE.TXT"), Is.EqualTo(second));
                Assert.That(again.ListDirectory("/").Length, Is.EqualTo(2));
                again.Dispose();
            }
        }

        [Test]
        public void Dsk_Decb_RealImage_WriteReachesTheFile()
        {
            string path = Copy(RealImage("dsk", "syzygy.dsk"), "syzygy.dsk");
            byte[] data = DragonDosTestDisk.Pattern(2500, 6);
            int before;

            using (FileStream stream = OpenReadWrite(path))
            {
                DragonDskDriver driver = new DragonDskDriver();
                driver.Mount(stream, false);
                Assert.That(driver.FileSystemType, Is.EqualTo("DECB"));
                before = driver.ListDirectory("/").Length;
                driver.WriteFile("/NEWFILE.BIN", data);
                driver.Dispose();
            }

            using (FileStream stream = File.OpenRead(path))
            {
                DragonDskDriver again = new DragonDskDriver();
                again.Mount(stream, true);
                Assert.That(ReadBack(again, "/NEWFILE.BIN"), Is.EqualTo(data));
                Assert.That(again.ListDirectory("/").Length, Is.EqualTo(before + 1));
                again.Dispose();
            }
        }

        // ------------------------------------------------------------------ DSK, OS-9

        [Test]
        public void Dsk_Os9_RealImage_WriteReachesTheFile()
        {
            string path = Copy(RealImage("dsk", "NITR09.DSK"), "nitr09.dsk");
            byte[] data = DragonDosTestDisk.Pattern(3333, 8);
            int before;

            using (FileStream stream = OpenReadWrite(path))
            {
                DragonDskDriver driver = new DragonDskDriver();
                driver.Mount(stream, false);
                Assert.That(driver.FileSystemType, Does.Contain("OS-9").IgnoreCase.Or.Contain("RBF").IgnoreCase);
                Assert.That(driver.CanWrite, Is.True);
                before = driver.ListDirectory("/").Length;
                driver.WriteFile("/hello", data);
                driver.Dispose();
            }

            using (FileStream stream = File.OpenRead(path))
            {
                DragonDskDriver again = new DragonDskDriver();
                again.Mount(stream, true);
                Assert.That(ReadBack(again, "/hello"), Is.EqualTo(data));
                Assert.That(again.ListDirectory("/").Length, Is.EqualTo(before + 1));
                again.Dispose();
            }
        }

        // ------------------------------------------------------------------ DMK

        [Test]
        public void Dmk_WriteDeleteRename_RebuildSectorsAndBothCrcs_AndReadBack()
        {
            string path = Copy(RealImage("dmk", "FEdit.dmk"), "fedit.dmk");
            byte[] original = File.ReadAllBytes(path);
            byte[] first = DragonDosTestDisk.Pattern(2100, 5);
            byte[] second = DragonDosTestDisk.Pattern(600, 6);
            DmkDiskImage before;
            using (FileStream stream = File.OpenRead(path))
            {
                before = DmkDiskImage.Parse(stream);
            }

            using (FileStream stream = OpenReadWrite(path))
            {
                DragonDmkDriver driver = new DragonDmkDriver();
                driver.Mount(stream, false);
                Assert.That(driver.CanWrite, Is.True);

                driver.WriteFile("NEWFILE.BIN", first);
                Assert.That(Fresh<DragonDmkDriver>(path).ReadFile("NEWFILE.BIN"), Is.EqualTo(first));
                driver.WriteFile("TWO.TXT", second);
                Assert.That(Fresh<DragonDmkDriver>(path).ReadFile("TWO.TXT"), Is.EqualTo(second));
                driver.RenameFile("TWO.TXT", "THREE.TXT");
                Assert.That(Fresh<DragonDmkDriver>(path).ReadFile("THREE.TXT"), Is.EqualTo(second));
                Assert.That(Fresh<DragonDmkDriver>(path).GetMetadata("TWO.TXT"), Is.Null);
                driver.WriteFile("TMP.TXT", new byte[] { 1, 2, 3 });
                Assert.That(Fresh<DragonDmkDriver>(path).GetMetadata("TMP.TXT"), Is.Not.Null);
                driver.DeleteFile("TMP.TXT");
                Assert.That(Fresh<DragonDmkDriver>(path).GetMetadata("TMP.TXT"), Is.Null);
                driver.Dispose();
            }

            byte[] changed = File.ReadAllBytes(path);
            Assert.That(changed.Length, Is.EqualTo(original.Length), "the DMK file keeps its length");
            Assert.That(changed.AsSpan(0, 16).ToArray(), Is.EqualTo(original.AsSpan(0, 16).ToArray()), "the 16 byte disk header is kept");

            // Parse checks every header CRC and every data CRC of every sector: a sector that was changed without its CRC would stop here.
            DmkDiskImage after;
            using (FileStream stream = File.OpenRead(path))
            {
                after = DmkDiskImage.Parse(stream);
            }

            byte[] sectorsBefore = before.GetSectorImage();
            byte[] sectorsAfter = after.GetSectorImage();
            int changedSectors = 0;
            for (int i = 0; i < sectorsBefore.Length / 256; i++)
            {
                if (!sectorsBefore.AsSpan(i * 256, 256).SequenceEqual(sectorsAfter.AsSpan(i * 256, 256)))
                {
                    changedSectors++;
                }
            }

            Assert.That(changedSectors, Is.GreaterThan(5));
            int differingBytes = 0;
            for (int i = 0; i < original.Length; i++)
            {
                if (original[i] != changed[i])
                {
                    differingBytes++;
                }
            }

            Assert.That(differingBytes, Is.LessThanOrEqualTo(changedSectors * 261), "only the data and CRC bytes of changed sectors differ");
            Assert.That(differingBytes, Is.GreaterThan(0));

            using (FileStream stream = File.OpenRead(path))
            {
                DragonDmkDriver again = new DragonDmkDriver();
                again.Mount(stream, true);
                Assert.That(ReadBack(again, "NEWFILE.BIN"), Is.EqualTo(first));
                Assert.That(ReadBack(again, "THREE.TXT"), Is.EqualTo(second));
                Assert.That(again.GetMetadata("TMP.TXT"), Is.Null);
                Assert.That(ReadBack(again, "FEDIT.BIN").Length, Is.EqualTo(11920), "files that were on the disk are untouched");
                again.Dispose();
            }
        }

        [Test]
        public void Dmk_WriteProtectedHeader_MountsReadOnly()
        {
            byte[] bytes = File.ReadAllBytes(RealImage("dmk", "FEdit.dmk"));
            bytes[0] = 0xFF;
            string path = Save("wp.dmk", bytes);

            using (FileStream stream = OpenReadWrite(path))
            {
                DragonDmkDriver driver = new DragonDmkDriver();
                driver.Mount(stream, false);

                Assert.That(driver.CanWrite, Is.False);
                Assert.That(() => driver.WriteFile("A.BIN", new byte[] { 1 }), Throws.InstanceOf<UnauthorizedAccessException>());
                driver.Dispose();
            }

            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
        }

        [Test]
        public void Dmk_MountedReadOnly_DoesNotChangeTheFile()
        {
            string path = Copy(RealImage("dmk", "FEdit.dmk"), "ro.dmk");
            byte[] original = File.ReadAllBytes(path);

            using (FileStream stream = OpenReadWrite(path))
            {
                DragonDmkDriver driver = new DragonDmkDriver();
                driver.Mount(stream, true);
                Assert.That(() => driver.WriteFile("A.BIN", new byte[] { 1 }), Throws.InstanceOf<UnauthorizedAccessException>());
                driver.Dispose();
            }

            Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
        }

        [TestCase("FEdit_40_2.dmk")]
        [TestCase("FEdit_80_1.dmk")]
        [TestCase("FEdit_80_2.dmk")]
        public void Dmk_OtherRealDisks_WriteBackAndStillPassEveryCrc(string name)
        {
            string path = Copy(RealImage("dmk", name), name);
            byte[] data = DragonDosTestDisk.Pattern(4000, 9);

            using (FileStream stream = OpenReadWrite(path))
            {
                DragonDmkDriver driver = new DragonDmkDriver();
                driver.Mount(stream, false);
                driver.WriteFile("NEWFILE.BIN", data);
                driver.Dispose();
            }

            using (FileStream stream = File.OpenRead(path))
            {
                DmkDiskImage.Parse(stream);
            }

            using (FileStream stream = File.OpenRead(path))
            {
                DragonDmkDriver again = new DragonDmkDriver();
                again.Mount(stream, true);
                Assert.That(ReadBack(again, "NEWFILE.BIN"), Is.EqualTo(data));
                again.Dispose();
            }
        }
    }
}
