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
    /// Kaypro II and Kaypro 4 CP/M disks: the layouts from the Kaypro ROM, and real raw images,
    /// checked against cpmtools where it is installed.
    /// </summary>
    [TestFixture]
    public class KayproCpmTests
    {
        private static string Image(string name) => TestData.FromVariable(TestData.TestImagesVariable, Path.Combine("cpm", "kaypro", name));

        [Test]
        public void KayproIiLayout_IsTheRomDpb()
        {
            Assert.That(CpmDriver.TryKaypro(204800, out CpmDiskParameters? p, out _), Is.True);
            Assert.That(p!.BlockSize, Is.EqualTo(1024));
            Assert.That(p.BlockCount, Is.EqualTo(195), "DSM 194");
            Assert.That(p.DirectoryEntries, Is.EqualTo(64));
            Assert.That(p.DirectoryBlocks, Is.EqualTo(4), "AL0 F0h keeps four blocks, more than 64 entries fill");
            Assert.That(p.ReservedTracks, Is.EqualTo(1));
        }

        [Test]
        public void Kaypro4Layout_IsTheRomDpb()
        {
            Assert.That(CpmDriver.TryKaypro(409600, out CpmDiskParameters? p, out _), Is.True);
            Assert.That(p!.BlockSize, Is.EqualTo(2048));
            Assert.That(p.BlockCount, Is.EqualTo(197), "DSM 196");
            Assert.That(p.ExtentMask, Is.EqualTo(1));
            Assert.That(p.DirectoryBlocks, Is.EqualTo(2), "AL0 C0h");
        }

        [Test]
        public void FromDpbBytes_BlockMaskNotMatchingTheShift_IsNotADpb()
        {
            byte[] dpb = { 0x28, 0x00, 0x03, 0x0F, 0x00, 0xC2, 0x00, 0x3F, 0x00, 0xF0, 0x00, 0x10, 0x00, 0x01, 0x00 };
            Assert.Throws<ArgumentException>(() => CpmDiskParameters.FromDpbBytes(dpb));
        }

        [TestCase("cpm22-rom149.img", "PIP.COM")]
        [TestCase("MBasic.img", "CHASE.BAS")]
        [TestCase("cpm22-kaypro4.img", "PIP.COM")]
        [TestCase("Zork.img", "ZORK1.DAT")]
        public void RealDisk_IsDetectedAsCpm(string imageName, string expectedFile)
        {
            string path = Image(imageName);
            RequireImage(path);
            DriverFactory factory = new DriverFactory();
            factory.Register(new CpmDriverProvider());
            factory.Register(new PcFloppyDriverProvider());
            IFileSystemDriver driver = factory.Detect(path)!;
            Assert.That(driver.FileSystemType, Is.EqualTo("CPM"));
            using FileStream stream = File.OpenRead(path);
            driver.Mount(stream, readOnly: true);
            Assert.That(driver.GetMetadata("/" + expectedFile), Is.Not.Null, expectedFile);
        }

        [Test]
        public void BlankDisk_WithNoFiles_IsNotClaimed()
        {
            string path = Image("cpm22-kaypro4-blank.img");
            RequireImage(path);
            DriverFactory factory = new DriverFactory();
            factory.Register(new CpmDriverProvider());
            Assert.That(factory.Detect(path)?.FileSystemType, Is.Not.EqualTo("CPM"), "an empty directory gives no sign of CP/M");
        }

        [Test]
        public void MbasicDisk_ChaseBas_IsTheBasicGame()
        {
            CpmDriver driver = Mount(Image("MBasic.img"));
            string text = Encoding.ASCII.GetString(driver.ReadFile("/CHASE.BAS"));
            Assert.That(text, Does.Contain("You touched the fence"));
        }

        [Test]
        public void Write_OnKayproIi_NeverUsesTheFourDirectoryBlocks()
        {
            byte[] image = File.ReadAllBytes(RequireImage(Image("cpm22-rom149.img")));
            CpmDriver.TryKaypro(image.Length, out CpmDiskParameters? p, out ICpmRecordMap? map);
            CpmFileSystem fs = new CpmFileSystem(image, p!, map!);
            for (int f = 0; f < fs.Files.Count; f++)
            {
                fs.Delete(fs.Files[f].User, fs.Files[f].Name);
                f--;
            }

            fs.Write(0, "FIRST.DAT", new byte[3000]);
            Assert.That(fs.Find(0, "FIRST.DAT")!.Blocks[0], Is.EqualTo(4), "blocks 0-3 are the directory's (AL0 F0h)");
        }

        // cpmtools has its own Kaypro definitions (kpii, kpiv); it reads the real image, and the
        // same image after RetroIO wrote a file to it.
        [TestCase("cpm22-rom149.img", "kpii", "PIP.COM")]
        [TestCase("cpm22-kaypro4.img", "kpiv", "PIP.COM")]
        public void RealDisk_RetroIOAndCpmtoolsAgree(string imageName, string format, string existing)
        {
            byte[] image = File.ReadAllBytes(RequireImage(Image(imageName)));
            CpmDriver.TryKaypro(image.Length, out CpmDiskParameters? p, out ICpmRecordMap? map);
            CpmFileSystem fs = new CpmFileSystem(image, p!, map!);
            byte[] ours = fs.Read(fs.Find(0, existing)!);
            byte[] written = new byte[3000];
            for (int i = 0; i < written.Length; i++)
            {
                written[i] = (byte)(i * 13);
            }

            fs.Write(5, "NEW.DAT", written);

            string folder = Path.Combine(Path.GetTempPath(), "retroio-kaypro-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                // cpmtools reads the image size from the format, so give it exactly the disk.
                int diskLength = format == "kpii" ? 204800 : 409600;
                File.WriteAllBytes(Path.Combine(folder, "disk.img"), fs.Image.AsSpan(0, diskLength).ToArray());
                Cpmtools.Run(folder, "cpmcp -f " + format + " disk.img 0:" + existing + " existing.out && cpmcp -f " + format + " disk.img 5:NEW.DAT new.out");
                Assert.That(File.ReadAllBytes(Path.Combine(folder, "existing.out")), Is.EqualTo(ours), existing + " as cpmtools reads it");
                byte[] back = File.ReadAllBytes(Path.Combine(folder, "new.out"));
                Assert.That(back.AsSpan(0, written.Length).SequenceEqual(written), Is.True, "NEW.DAT as cpmtools reads it");
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
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
                Assert.Ignore("Kaypro test image not found: " + path + " (see the README, CP/M test images)");
            }

            return path;
        }
    }
}
