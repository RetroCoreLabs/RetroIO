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
    /// Headerless DSK images that hold an OS-9 volume that is no floppy: hard disk volumes such as the CoCo SDC and DriveWire images of the
    /// NitrOS-9 project, including DriveWire files that are shorter than their volume.
    /// </summary>
    /// <remarks>
    /// The volumes are built from the RBF field layout (NitrOS-9 defs/rbf.d). The shapes are those of the real NitrOS-9 3.3.0 snapshot images,
    /// decoded from their sector 0: the DriveWire disks say DD.TOT 524268, DD.TKS 18, DD.MAP 65534, DD.BIT 1, DD.DIR 257 and the files hold 293 to
    /// 6202 sectors; the CoCo SDC disks are 18432 sectors in a file of exactly that size; the UUCPbb disks are 10000 sectors with 5 sectors per
    /// track. Before the fix, DragonDskDriver refused all three with "Invalid track count" and its provider scored them 0.3, below the 0.5 the
    /// driver factory needs. Smaller volumes of the same shape are used here, so the tests do not hold 134 MB.
    /// </remarks>
    [TestFixture]
    public class DragonDskOs9VolumeTests
    {
        private const int SectorSize = 256;

        [Test]
        public void ShortVolume_MountsAsOs9_WithTheSectorsTheFileDoesNotHoldAsFree()
        {
            // 4000 sectors in the volume, the file stops after the last used sector, as "os9 format" without -e leaves it.
            byte[] image = BuildVolume(4000, 18, 2, out byte[] content, out int usedSectors, shortFile: true);
            Assert.That(image.Length / SectorSize, Is.EqualTo(usedSectors), "the file holds only the used sectors");

            using (MemoryStream stream = new MemoryStream(image, true))
            {
                DragonDskDriver driver = new DragonDskDriver();
                driver.Mount(stream, false);

                Assert.That(driver.FileSystemType, Is.EqualTo("OS-9"));
                Assert.That(driver.GetBlockDeviceInfo()!.TotalBlocks, Is.EqualTo(4000u), "the device is DD.TOT sectors long");
                Assert.That(driver.ReadFile("/HELLO"), Is.EqualTo(content));
                Assert.That(driver.GetBlocksFree(), Is.EqualTo(4000 - usedSectors));
                Assert.That(driver.CanWrite, Is.False, "a short file would grow to the whole volume when written back");
                UnauthorizedAccessException? refused = Assert.Throws<UnauthorizedAccessException>(() => driver.WriteFile("/NEW", new byte[] { 1 }));
                Assert.That(refused!.Message, Does.Contain("holds " + usedSectors + " of the 4000 sectors"));
                driver.Dispose();
            }
        }

        [Test]
        public void WholeHardDiskVolume_WithFiveSectorsPerTrack_MountsAsOs9AndCanBeWritten()
        {
            // The shape of uucpbb21_6809.dsk: DD.TKS 5, single density, the file is the whole volume. 1000 sectors guessed as 18 per track is
            // no floppy track count.
            byte[] image = BuildVolume(1000, 5, 0, out byte[] content, out int usedSectors, shortFile: false);
            Assert.That(image.Length, Is.EqualTo(1000 * SectorSize));

            using (MemoryStream stream = new MemoryStream(image, true))
            {
                DragonDskDriver driver = new DragonDskDriver();
                driver.Mount(stream, false);

                Assert.That(driver.FileSystemType, Is.EqualTo("OS-9"));
                Assert.That(driver.GetBlockDeviceInfo()!.TotalBlocks, Is.EqualTo(1000u));
                Assert.That(driver.ReadFile("/HELLO"), Is.EqualTo(content));
                Assert.That(driver.GetBlocksFree(), Is.EqualTo(1000 - usedSectors));
                Assert.That(driver.CanWrite, Is.True);

                driver.WriteFile("/NEW", new byte[] { 1, 2, 3 });
                Assert.That(driver.ReadFile("/NEW"), Is.EqualTo(new byte[] { 1, 2, 3 }));
                Assert.That(stream.Length, Is.EqualTo(1000 * SectorSize), "the file keeps its size");
                driver.Dispose();
            }
        }

        [Test]
        public void ShortVolume_WhoseFileHasAFloppySize_IsStillMountedAsTheWholeVolume()
        {
            // The file is 1440 sectors, the size of a 40 track double sided floppy, but sector 0 says the volume has 4000. Mounted as a
            // floppy, the volume would be larger than the device and the OS-9 reader would refuse it.
            byte[] image = BuildVolume(4000, 18, 2, out byte[] content, out _, shortFile: true);
            Array.Resize(ref image, 1440 * SectorSize);

            using (MemoryStream stream = new MemoryStream(image, false))
            {
                DragonDskDriver driver = new DragonDskDriver();
                driver.Mount(stream, true);

                Assert.That(driver.FileSystemType, Is.EqualTo("OS-9"));
                Assert.That(driver.GetBlockDeviceInfo()!.TotalBlocks, Is.EqualTo(4000u));
                Assert.That(driver.ReadFile("/HELLO"), Is.EqualTo(content));
                driver.Dispose();
            }
        }

        [Test]
        public void FloppyVolume_StillUsesTheFloppyLayout()
        {
            // 1440 sectors, 18 per track, double sided: the 40 track layout, as before.
            byte[] image = BuildVolume(1440, 18, 3, out byte[] content, out _, shortFile: false);

            using (MemoryStream stream = new MemoryStream(image, false))
            {
                DragonDskDriver driver = new DragonDskDriver();
                driver.Mount(stream, true);

                BlockDeviceInfo info = driver.GetBlockDeviceInfo()!;
                Assert.That(driver.FileSystemType, Is.EqualTo("OS-9"));
                Assert.That(info.CylinderCount, Is.EqualTo(40));
                Assert.That(info.HeadCount, Is.EqualTo(2));
                Assert.That(driver.ReadFile("/HELLO"), Is.EqualTo(content));
                driver.Dispose();
            }
        }

        [Test]
        public void NonFloppyImage_WithoutAnOs9Sector0_IsStillRefused()
        {
            // Sector 0 all 0xFF, as on the DECB disk FPGABoot.DSK, in a file of no floppy size: nothing to mount.
            byte[] image = new byte[1000 * SectorSize];
            Array.Fill(image, (byte)0xFF, 0, SectorSize);

            using (MemoryStream stream = new MemoryStream(image, false))
            {
                DragonDskDriver driver = new DragonDskDriver();
                InvalidDataException? refused = Assert.Throws<InvalidDataException>(() => driver.Mount(stream, true));
                Assert.That(refused!.Message, Does.Contain("Invalid track count"));
            }
        }

        [Test]
        public void Provider_PicksAHeaderlessOs9VolumeOfAnySize()
        {
            byte[] image = BuildVolume(4000, 18, 2, out _, out _, shortFile: true);
            DetectionHints hints = new DetectionHints
            {
                FileExtension = ".dsk",
                FileSize = image.Length,
                HeaderBytes = image.AsSpan(0, 512).ToArray()
            };

            Assert.That(new DragonDskDriverProvider().DetectConfidence(hints), Is.GreaterThanOrEqualTo(0.5f), "the driver factory needs 0.5");

            // The same size with an empty sector 0 is not an OS-9 volume and gets only the extension score.
            hints.HeaderBytes = new byte[512];
            Assert.That(new DragonDskDriverProvider().DetectConfidence(hints), Is.LessThan(0.5f));
        }

        [Test]
        public void TryReadOs9Volume_RefusesFieldsThatDoNotAgree()
        {
            byte[] image = BuildVolume(4000, 18, 2, out _, out _, shortFile: true);
            byte[] id = image.AsSpan(0, SectorSize).ToArray();
            Assert.That(DragonDskDriver.TryReadOs9Volume(id, image.Length, out long total), Is.True);
            Assert.That(total, Is.EqualTo(4000));

            byte[] bad = (byte[])id.Clone();
            bad[18] = 17;
            Assert.That(DragonDskDriver.TryReadOs9Volume(bad, image.Length, out _), Is.False, "DD.SPT differs from DD.TKS");

            bad = (byte[])id.Clone();
            bad[5] = 0;
            bad[4] = 0;
            Assert.That(DragonDskDriver.TryReadOs9Volume(bad, image.Length, out _), Is.False, "no allocation map");

            bad = (byte[])id.Clone();
            bad[10] = 1;
            Assert.That(DragonDskDriver.TryReadOs9Volume(bad, image.Length, out _), Is.False, "the root descriptor is inside the map");

            Assert.That(DragonDskDriver.TryReadOs9Volume(id, image.Length + 1, out _), Is.False, "the file is not whole sectors");
            Assert.That(DragonDskDriver.TryReadOs9Volume(id, 3 * SectorSize, out _), Is.False, "the file stops before the root descriptor");
        }

        // Builds an OS-9 volume: sector 0, the allocation map from sector 1, the root directory descriptor behind the map, one directory data
        // sector with ".", ".." and HELLO, the descriptor of HELLO and its data. With shortFile the image stops after the last used sector.
        private static byte[] BuildVolume(int totalSectors, int sectorsPerTrack, byte format, out byte[] content, out int usedSectors, bool shortFile)
        {
            int mapBytes = (totalSectors + 7) / 8;
            int mapSectors = (mapBytes + SectorSize - 1) / SectorSize;
            int rootFd = 1 + mapSectors;
            int rootData = rootFd + 1;
            int fileFd = rootFd + 2;
            int fileData = rootFd + 3;
            content = new byte[700];
            for (int i = 0; i < content.Length; i++)
            {
                content[i] = (byte)(i * 7 + 3);
            }

            int fileSectors = (content.Length + SectorSize - 1) / SectorSize;
            usedSectors = fileData + fileSectors;

            byte[] image = new byte[(shortFile ? usedSectors : totalSectors) * SectorSize];
            image[0] = (byte)(totalSectors >> 16);
            image[1] = (byte)(totalSectors >> 8);
            image[2] = (byte)totalSectors;
            image[3] = (byte)sectorsPerTrack;
            image[4] = (byte)(mapBytes >> 8);
            image[5] = (byte)mapBytes;
            image[7] = 1;
            image[8] = (byte)(rootFd >> 16);
            image[9] = (byte)(rootFd >> 8);
            image[10] = (byte)rootFd;
            image[13] = 0xFF;
            image[16] = format;
            image[17] = (byte)(sectorsPerTrack >> 8);
            image[18] = (byte)sectorsPerTrack;
            PutName(image, 31, "TEST");

            // Allocation map: sectors 0 up to the last used one are taken.
            for (int s = 0; s < usedSectors; s++)
            {
                image[SectorSize + (s / 8)] |= (byte)(0x80 >> (s % 8));
            }

            // Root directory descriptor: directory attributes, size 3 entries, one segment of one sector.
            int at = rootFd * SectorSize;
            image[at] = 0xBF;
            PutUInt32(image, at + 9, 3 * 32);
            PutSegment(image, at + 16, rootData, 1);

            at = rootData * SectorSize;
            PutName(image, at, "..");
            PutUInt24(image, at + 29, rootFd);
            PutName(image, at + 32, ".");
            PutUInt24(image, at + 32 + 29, rootFd);
            PutName(image, at + 64, "HELLO");
            PutUInt24(image, at + 64 + 29, fileFd);

            at = fileFd * SectorSize;
            image[at] = 0x0B;
            PutUInt32(image, at + 9, content.Length);
            PutSegment(image, at + 16, fileData, fileSectors);
            Buffer.BlockCopy(content, 0, image, fileData * SectorSize, content.Length);
            return image;
        }

        private static void PutName(byte[] data, int at, string name)
        {
            for (int i = 0; i < name.Length; i++)
            {
                data[at + i] = (byte)name[i];
            }

            data[at + name.Length - 1] |= 0x80;
        }

        private static void PutUInt24(byte[] data, int at, int value)
        {
            data[at] = (byte)(value >> 16);
            data[at + 1] = (byte)(value >> 8);
            data[at + 2] = (byte)value;
        }

        private static void PutUInt32(byte[] data, int at, int value)
        {
            data[at] = (byte)(value >> 24);
            data[at + 1] = (byte)(value >> 16);
            data[at + 2] = (byte)(value >> 8);
            data[at + 3] = (byte)value;
        }

        private static void PutSegment(byte[] data, int at, int lsn, int count)
        {
            PutUInt24(data, at, lsn);
            data[at + 3] = (byte)(count >> 8);
            data[at + 4] = (byte)count;
        }
    }
}
