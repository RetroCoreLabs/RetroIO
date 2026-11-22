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
using RetroIO.Storage.Local;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Local.FileSystems.Cpm;
using RetroIO.Tests;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Amstrad CPC CP/M disks in DSK files (both the standard and the extended container), checked
    /// against cpmtools where it is installed.
    /// </summary>
    [TestFixture]
    public class AmstradCpmTests
    {
        private static string Image(string name) => TestData.FromVariable(TestData.TestImagesVariable, Path.Combine("cpm", "amstrad", name));

        // Enchanter: extended container, sectors in ID order. Deadline: extended, sectors
        // interleaved on the track. Wishbringer: the older standard container.
        [TestCase("Enchanter_1984_Infocom_cpm_version.dsk", "ENCHANTE.COM")]
        [TestCase("Deadline_1982_Infocom_cpm_version.dsk", "DEADLINE.COM")]
        [TestCase("Wishbringer_1985_Infocom_cpm_version.dsk", null)]
        public void RealDisk_IsDetectedAndMounted(string imageName, string? expectedFile)
        {
            string path = RequireImage(Image(imageName));
            DriverFactory factory = new DriverFactory();
            factory.Register(new CpmDriverProvider());
            factory.Register(new DragonDskDriverProvider());
            IFileSystemDriver driver = factory.Detect(path)!;
            Assert.That(driver.FileSystemType, Is.EqualTo("CPM"));
            using FileStream stream = File.OpenRead(path);
            driver.Mount(stream, readOnly: true);
            DirectoryEntry[] files = driver.ListDirectory("/");
            Assert.That(files.Length, Is.GreaterThan(0));
            if (expectedFile != null)
            {
                Assert.That(driver.GetMetadata("/" + expectedFile), Is.Not.Null, expectedFile);
            }
        }

        [Test]
        public void SystemFormat_HasTwoReservedTracksAnd171Blocks()
        {
            byte[] image = File.ReadAllBytes(RequireImage(Image("Enchanter_1984_Infocom_cpm_version.dsk")));
            AmstradCpmRecordMap map = AmstradCpmRecordMap.TryRead(image, out byte first, out int tracks)!;
            Assert.That(first, Is.EqualTo(0x41));
            Assert.That(tracks, Is.EqualTo(40));
            CpmDiskParameters p = CpmDriver.AmstradParameters(map.RecordCount, first);
            Assert.That(p.BlockCount, Is.EqualTo(171), "38 tracks of 4.5 KB in 1 KB blocks");
            Assert.That(p.ReservedTracks, Is.EqualTo(2));
        }

        [Test]
        public void InterleavedTrack_IsReadInSectorIdOrder()
        {
            // Deadline track 0 is stored 45h, 41h, 46h, ...: sector 41h is the second in the block.
            byte[] image = File.ReadAllBytes(RequireImage(Image("Deadline_1982_Infocom_cpm_version.dsk")));
            Assert.That(image[0x100 + 0x18 + 2], Is.EqualTo(0x45));
            AmstradCpmRecordMap map = AmstradCpmRecordMap.TryRead(image, out _, out _)!;
            // The first data record is track 2, sector 41h; find where track 2 puts 41h by hand.
            int track2 = 256 + 2 * (image[0x34] * 256);
            int slot = -1;
            for (int s = 0; s < 9; s++)
            {
                if (image[track2 + 0x18 + s * 8 + 2] == 0x41)
                {
                    slot = s;
                }
            }

            Assert.That(map.ImageOffset(0), Is.EqualTo(track2 + 256 + slot * 512));
        }

        [TestCase("Enchanter_1984_Infocom_cpm_version.dsk", "ENCHANTE.COM")]
        [TestCase("Deadline_1982_Infocom_cpm_version.dsk", "DEADLINE.COM")]
        public void RealDisk_RetroIOAndCpmtoolsAgree(string imageName, string existing)
        {
            byte[] image = File.ReadAllBytes(RequireImage(Image(imageName)));
            AmstradCpmRecordMap map = AmstradCpmRecordMap.TryRead(image, out byte first, out _)!;
            CpmDiskParameters p = CpmDriver.AmstradParameters(map.RecordCount, first);
            CpmFileSystem fs = new CpmFileSystem(image, p, map);
            byte[] ours = fs.Read(fs.Find(0, existing)!);
            byte[] written = new byte[2500];
            for (int i = 0; i < written.Length; i++)
            {
                written[i] = (byte)(i * 11);
            }

            fs.Write(2, "NEW.DAT", written);

            // cpmtools reads a raw image, not DSK: lay the tracks out flat in sector ID order. Its
            // cpcsys definition reads the sectors with no skew.
            string folder = Path.Combine(Path.GetTempPath(), "retroio-amstrad-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                byte[] flat = new byte[40 * 9 * 512];
                for (int r = 0; r < map.RecordCount; r++)
                {
                    Array.Copy(fs.Image, map.ImageOffset(r), flat, 2 * 9 * 512 + r * 128, 128);
                }

                File.WriteAllBytes(Path.Combine(folder, "disk.img"), flat);
                Cpmtools.Run(folder, "cpmcp -f cpcsys disk.img 0:" + existing + " existing.out && cpmcp -f cpcsys disk.img 2:NEW.DAT new.out");
                Assert.That(File.ReadAllBytes(Path.Combine(folder, "existing.out")), Is.EqualTo(ours), existing + " as cpmtools reads it");
                byte[] back = File.ReadAllBytes(Path.Combine(folder, "new.out"));
                Assert.That(back.AsSpan(0, written.Length).SequenceEqual(written), Is.True, "NEW.DAT as cpmtools reads it");
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        [Test]
        public void Write_ThroughTheDriver_ReadsBackOnAFreshMount()
        {
            byte[] original = File.ReadAllBytes(RequireImage(Image("Wishbringer_1985_Infocom_cpm_version.dsk")));
            using MemoryStream stream = new MemoryStream();
            stream.Write(original, 0, original.Length);
            CpmDriver writer = new CpmDriver();
            writer.Mount(stream, readOnly: false);
            byte[] data = new byte[1500];
            data.AsSpan().Fill(0x5A);
            writer.WriteFile("/NOTE.TXT", data);

            CpmDriver reader = new CpmDriver();
            using MemoryStream saved = new MemoryStream(stream.ToArray(), writable: false);
            reader.Mount(saved, readOnly: true);
            Assert.That(reader.ReadFile("/NOTE.TXT").AsSpan(0, data.Length).SequenceEqual(data), Is.True);
            Assert.That(stream.Length, Is.EqualTo(original.Length), "the DSK file keeps its size");
        }

        // Amstrad PCW games disks (sector IDs 1-9, one reserved track); Poste Haste stores its
        // sectors interleaved 1, 6, 2, 7, ...
        [TestCase("PacMania.dsk", "PACMANIA.EMS")]
        [TestCase("Poste_Haste.dsk", "BASIC.COM")]
        public void PcwDisk_IsDetectedAndMounted(string name, string expectedFile)
        {
            string path = RequireImage(Pcw(name));
            DriverFactory factory = new DriverFactory();
            factory.Register(new CpmDriverProvider());
            factory.Register(new DragonDskDriverProvider());
            IFileSystemDriver driver = factory.Detect(path)!;
            Assert.That(driver.FileSystemType, Is.EqualTo("CPM"));
            using FileStream stream = File.OpenRead(path);
            driver.Mount(stream, readOnly: true);
            Assert.That(driver.GetMetadata("/" + expectedFile), Is.Not.Null, expectedFile);
        }

        [Test]
        public void PcwFormat_HasOneReservedTrackAnd175Blocks()
        {
            byte[] image = File.ReadAllBytes(RequireImage(Pcw("PacMania.dsk")));
            AmstradCpmRecordMap map = AmstradCpmRecordMap.TryRead(image, out byte first, out _)!;
            Assert.That(first, Is.EqualTo(0x01));
            CpmDiskParameters p = CpmDriver.AmstradParameters(map.RecordCount, first);
            Assert.That(p.ReservedTracks, Is.EqualTo(1));
            Assert.That(p.BlockCount, Is.EqualTo(175), "39 tracks of 4.5 KB in 1 KB blocks");
        }

        [Test]
        public void PcwDisk_Write_ReadsBackOnAFreshMount()
        {
            byte[] original = File.ReadAllBytes(RequireImage(Pcw("Poste_Haste.dsk")));
            using MemoryStream stream = new MemoryStream();
            stream.Write(original, 0, original.Length);
            CpmDriver writer = new CpmDriver();
            writer.Mount(stream, readOnly: false);
            byte[] data = new byte[3000];
            data.AsSpan().Fill(0x41);
            writer.WriteFile("/USER3/NOTE.TXT", data);
            CpmDriver reader = new CpmDriver();
            using MemoryStream saved = new MemoryStream(stream.ToArray(), writable: false);
            reader.Mount(saved, readOnly: true);
            Assert.That(reader.ReadFile("/USER3/NOTE.TXT").AsSpan(0, data.Length).SequenceEqual(data), Is.True);
            Assert.That(reader.ReadFile("/BASIC.COM"), Is.EqualTo(MountedRead(original, "/BASIC.COM")));
        }

        private static string Pcw(string name) => TestData.FromVariable(TestData.TestImagesVariable, Path.Combine("cpm", "pcw", name));

        private static byte[] MountedRead(byte[] image, string path)
        {
            CpmDriver driver = new CpmDriver();
            using MemoryStream stream = new MemoryStream(image, writable: false);
            driver.Mount(stream, readOnly: true);
            return driver.ReadFile(path);
        }

        [Test]
        public void ImageThatIsNotDsk_IsNotAmstrad()
        {
            Assert.That(AmstradCpmRecordMap.TryRead(new byte[200000], out _, out _), Is.Null);
        }

        private static string RequireImage(string path)
        {
            if (!File.Exists(path))
            {
                Assert.Ignore("Amstrad test image not found: " + path + " (see the README, CP/M test images)");
            }

            return path;
        }
    }
}
