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
    /// CP/M-86 on a 160 KB IBM PC floppy: the IBM PC CP/M-86 1.00 system disk, checked against
    /// cpmtools where it is installed.
    /// </summary>
    [TestFixture]
    public class Cpm86Tests
    {
        private static string Image(string name) => TestData.FromVariable(TestData.TestImagesVariable, Path.Combine("cpm", "cpm86", name));

        [Test]
        public void SystemDisk_IsDetectedAsCpmAndNotAsDos()
        {
            string path = RequireImage(Image("CPM86_10.IMG"));
            DriverFactory factory = new DriverFactory();
            factory.Register(new CpmDriverProvider());
            factory.Register(new PcFloppyDriverProvider());
            IFileSystemDriver driver = factory.Detect(path)!;
            Assert.That(driver.FileSystemType, Is.EqualTo("CPM"));
            using FileStream stream = File.OpenRead(path);
            driver.Mount(stream, readOnly: true);
            // The disk keeps CPM.SYS in user area 15 (entry byte 0 is 0Fh), read-only and system.
            Assert.That(driver.GetMetadata("/USER15/CPM.SYS"), Is.Not.Null);
            Assert.That(driver.GetMetadata("/ASM86.CMD"), Is.Not.Null);
        }

        [Test]
        public void HelpFile_IsTheCpm86HelpText()
        {
            CpmDriver driver = new CpmDriver();
            using FileStream stream = File.OpenRead(RequireImage(Image("CPM86_10.IMG")));
            driver.Mount(stream, readOnly: true);
            byte[] help = driver.ReadFile("/HELP.HLP");
            Assert.That(help.Length, Is.EqualTo((128 + 67) * 128), "two entries: 128 and 67 records");
            Assert.That(Encoding.ASCII.GetString(help), Does.Contain("ASM86 filespec"));
        }

        [Test]
        public void DosFloppyOfTheSameSize_IsNotCpm86()
        {
            byte[] dos = new byte[163840];
            dos[0x200] = 0xFE;
            dos[0x201] = 0xFF;
            dos[0x202] = 0xFF;
            Assert.That(CpmDriver.TryIbmPcCpm86(dos.Length, dos, out _, out _), Is.False);
        }

        [Test]
        public void SystemDisk_RetroIOAndCpmtoolsAgree()
        {
            byte[] image = File.ReadAllBytes(RequireImage(Image("CPM86_10.IMG")));
            CpmDriver.TryIbmPcCpm86(image.Length, image, out CpmDiskParameters? p, out ICpmRecordMap? map);
            CpmFileSystem fs = new CpmFileSystem(image, p!, map!);
            byte[] ours = fs.Read(fs.Find(0, "HELP.HLP")!);
            byte[] written = new byte[4000];
            for (int i = 0; i < written.Length; i++)
            {
                written[i] = (byte)(i * 3);
            }

            fs.Write(1, "NEW.DAT", written);

            string folder = Path.Combine(Path.GetTempPath(), "retroio-cpm86-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                // An independent definition of the measured layout for cpmtools.
                File.WriteAllBytes(Path.Combine(folder, "disk.img"), fs.Image);
                File.WriteAllText(Path.Combine(folder, "diskdefs"),
                    "diskdef ibmpc160\n  seclen 512\n  tracks 40\n  sectrk 8\n  blocksize 1024\n  maxdir 64\n"
                    + "  skew 0\n  boottrk 1\n  os 2.2\nend\n");
                Cpmtools.Run(folder, "cpmcp -f ibmpc160 disk.img 0:HELP.HLP help.out && cpmcp -f ibmpc160 disk.img 1:NEW.DAT new.out");
                Assert.That(File.ReadAllBytes(Path.Combine(folder, "help.out")), Is.EqualTo(ours));
                byte[] back = File.ReadAllBytes(Path.Combine(folder, "new.out"));
                Assert.That(back.AsSpan(0, written.Length).SequenceEqual(written), Is.True);
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        [Test]
        public void Disk320K_IsDetectedAndEveryCmdFileHasACodeGroupHeader()
        {
            string path = RequireImage(Image("CPM86_110_320K.IMG"));
            DriverFactory factory = new DriverFactory();
            factory.Register(new CpmDriverProvider());
            factory.Register(new PcFloppyDriverProvider());
            IFileSystemDriver driver = factory.Detect(path)!;
            Assert.That(driver.FileSystemType, Is.EqualTo("CPM"));
            using FileStream stream = File.OpenRead(path);
            driver.Mount(stream, readOnly: true);
            DirectoryEntry[] files = driver.ListDirectory("/");
            int commands = 0;
            for (int i = 0; i < files.Length; i++)
            {
                if (!files[i].Name.EndsWith(".CMD", StringComparison.Ordinal))
                {
                    continue;
                }

                // A CP/M-86 .CMD file starts with group descriptors: type 1 (code), then its length in
                // 16-byte paragraphs, which must fit after the 128-byte header.
                byte[] cmd = driver.ReadFile("/" + files[i].Name);
                int paragraphs = cmd[1] | (cmd[2] << 8);
                Assert.That(cmd[0], Is.EqualTo(1), files[i].Name);
                Assert.That(128 + paragraphs * 16, Is.LessThanOrEqualTo(cmd.Length), files[i].Name);
                commands++;
            }

            Assert.That(commands, Is.EqualTo(18));
        }

        [Test]
        public void Disk320K_ImageDiskCopy_IsTheSameDisk()
        {
            byte[] raw = File.ReadAllBytes(RequireImage(Image("CPM86_110_320K.IMG")));
            byte[] imd = File.ReadAllBytes(RequireImage(Image("CPM86_110_320K.IMD")));
            Assert.That(ImdDiskImage.Decode(imd).Flat, Is.EqualTo(raw));
        }

        [Test]
        public void SerpentineMap_SideOneRunsBackFromTheLastCylinder()
        {
            SerpentineCpmRecordMap map = new SerpentineCpmRecordMap(40, 4096, 1, 158 * 16);
            Assert.That(map.ImageOffset(0), Is.EqualTo(2 * 4096), "CP/M track 1 is cylinder 1 side 0");
            Assert.That(map.ImageOffset(39 * 32), Is.EqualTo(79 * 4096), "CP/M track 40 is cylinder 39 side 1");
            Assert.That(map.ImageOffset(78 * 32), Is.EqualTo(1 * 4096), "CP/M track 79 is cylinder 0 side 1");
        }

        [Test]
        public void DosFloppyOf320K_IsNotCpm86()
        {
            byte[] dos = new byte[327680];
            dos[0x200] = 0xFF;
            dos[0x201] = 0xFF;
            dos[0x202] = 0xFF;
            Assert.That(CpmDriver.TryIbmPcCpm86(dos.Length, dos, out _, out _), Is.False);
        }

        private static string RequireImage(string path)
        {
            if (!File.Exists(path))
            {
                Assert.Ignore("CP/M-86 test image not found: " + path + " (see the README, CP/M test images)");
            }

            return path;
        }
    }
}
