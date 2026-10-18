//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Local.BSD.Structures;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Local.V7.Structures;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Tests that <see cref="FfsDriver.SelectPartition(string)"/> checks the letter against the disk label.
    /// </summary>
    [TestFixture]
    public class FfsDriverSelectPartitionTests
    {
        private const int SectorSize = 512;
        private const int Sectors = 2048;

        // A 1 MB image with a BSD label at sector 1. Partition 'a' is in use, 'b' is unused, 'c' is in use.
        private static MemoryStream MakeLabelledImage()
        {
            byte[] image = new byte[SectorSize * Sectors];
            BsdPartition[] parts = new BsdPartition[]
            {
                new BsdPartition { Size = 1000, Offset = 100, FsType = V7Constants.FS_BSDFFS },
                default,
                new BsdPartition { Size = 500, Offset = 1200, FsType = V7Constants.FS_BSDFFS }
            };
            BsdDiskLabel label = new BsdDiskLabel
            {
                Magic = V7Constants.DISK_MAGIC,
                Magic2 = V7Constants.DISK_MAGIC,
                NumPartitions = (ushort)parts.Length,
                BytesPerSector = SectorSize
            };
            for (int i = 0; i < parts.Length; i++)
                label.Partitions[i] = parts[i];

            byte[] sector = new byte[SectorSize];
            label.WriteTo(sector, 0);
            sector.CopyTo(image, SectorSize);
            return new MemoryStream(image);
        }

        // Mounts the labelled image. The image holds no FFS superblock, so Mount throws after the block device
        // is attached; that is all this test needs, because the partition check reads the disk label only.
        private static FfsDriver MountLabelled()
        {
            FfsDriver driver = new FfsDriver();
            try
            {
                driver.Mount(MakeLabelledImage(), false);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is InvalidDataException)
            {
                // Expected: no superblock.
            }
            return driver;
        }

        [Test]
        public void SelectPartition_AfterMount_PartitionInTheLabel_ReturnsTrue()
        {
            FfsDriver driver = MountLabelled();

            Assert.That(driver.SelectPartition("c"), Is.True);
            Assert.That(driver.CurrentPartition, Is.EqualTo("c"));
        }

        [Test]
        public void SelectPartition_AfterMount_UnusedPartition_ReturnsFalse()
        {
            FfsDriver driver = MountLabelled();

            Assert.That(driver.SelectPartition("b"), Is.False);
        }

        [Test]
        public void SelectPartition_AfterMount_LetterBeyondTheLabel_ReturnsFalse()
        {
            FfsDriver driver = MountLabelled();

            Assert.That(driver.SelectPartition("h"), Is.False);
        }

        [Test]
        public void V7Driver_SelectPartition_AfterMount_UnusedPartition_ReturnsFalse()
        {
            V7Driver driver = new V7Driver();
            try
            {
                driver.Mount(MakeLabelledImage(), false);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is InvalidDataException)
            {
                // Expected: no V7 superblock. The disk label is still readable.
            }

            Assert.That(driver.SelectPartition("c"), Is.True);
            Assert.That(driver.SelectPartition("b"), Is.False);
        }

        [Test]
        public void SelectPartition_BeforeMount_AcceptsAnyLetterFromAToH()
        {
            // Before mount there is no disk to check against, so the letter is only stored.
            FfsDriver driver = new FfsDriver();

            Assert.That(driver.SelectPartition("g"), Is.True);
            Assert.That(driver.SelectPartition("i"), Is.False);
        }
    }
}
