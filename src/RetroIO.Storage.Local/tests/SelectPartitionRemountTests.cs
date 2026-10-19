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
using RetroIO.Storage.Local.BSD.Structures;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Local.V7.Structures;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Tests that selecting a partition on a driver that is already mounted moves the mounted file system
    /// to that partition. The disks are built in memory: a BSD disk label at sector 1 with two partitions,
    /// each holding its own superblock with its own name.
    /// </summary>
    [TestFixture]
    public class SelectPartitionRemountTests
    {
        private const int SectorSize = 512;
        private const int Sectors = 2048;
        private const uint OffsetA = 100;
        private const uint OffsetC = 1000;

        private static byte[] MakeLabelledImage(byte fsTypeA, byte fsTypeC)
        {
            byte[] image = new byte[SectorSize * Sectors];
            BsdPartition[] parts = new BsdPartition[]
            {
                new BsdPartition { Size = 800, Offset = OffsetA, FsType = fsTypeA },
                default,
                new BsdPartition { Size = 800, Offset = OffsetC, FsType = fsTypeC }
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
            return image;
        }

        // A V7 superblock sits in file system block 1 (1024 bytes) of its partition.
        private static void PutV7Superblock(byte[] image, uint partitionOffsetSectors, string name)
        {
            V7Superblock sb = new V7Superblock { ISize = 10, FSize = 400, FsName = name };
            byte[] buffer = new byte[V7Constants.SUPERBLOCK_SIZE];
            sb.WriteTo(buffer, 0);
            buffer.CopyTo(image, (int)partitionOffsetSectors * SectorSize + V7Constants.FS_BLOCK_SIZE);
        }

        // An FFS superblock sits 8192 bytes into its partition. Only the fields Validate reads are set.
        private static void PutFfsSuperblock(byte[] image, uint partitionOffsetSectors, string name)
        {
            int at = (int)partitionOffsetSectors * SectorSize + 8192;
            BsdEndianHelper.WriteUInt32BE(image, at + 44, 1);          // fs_ncg
            BsdEndianHelper.WriteUInt32BE(image, at + 48, 4096);       // fs_bsize
            BsdEndianHelper.WriteUInt32BE(image, at + 52, 512);        // fs_fsize
            BsdEndianHelper.WriteUInt32BE(image, at + 56, 8);          // fs_frag
            BsdEndianHelper.WriteUInt32BE(image, at + 184, 64);        // fs_ipg
            BsdEndianHelper.WriteUInt32BE(image, at + 1372, 0x011954); // fs_magic
            Encoding.ASCII.GetBytes(name).CopyTo(image, at + 212);     // fs_fsmnt
        }

        [Test]
        public void V7Driver_SelectPartition_AfterMount_MovesTheMountedFileSystem()
        {
            byte[] image = MakeLabelledImage(V7Constants.FS_V71K, V7Constants.FS_V71K);
            PutV7Superblock(image, OffsetA, "AAA");
            PutV7Superblock(image, OffsetC, "CCC");
            V7Driver driver = new V7Driver();
            driver.SetPartition('a');
            driver.Mount(new MemoryStream(image), true);
            Assert.That(driver.VolumeLabel, Is.EqualTo("AAA"));

            Assert.That(driver.SelectPartition("c"), Is.True);

            Assert.That(driver.CurrentPartition, Is.EqualTo("c"));
            Assert.That(driver.VolumeLabel, Is.EqualTo("CCC"));
        }

        [Test]
        public void V7Driver_SelectPartition_AfterMount_PartitionThatCannotMount_KeepsTheOldOne()
        {
            // Partition 'c' is in the label but is typed FFS, so the V7 file system refuses it.
            byte[] image = MakeLabelledImage(V7Constants.FS_V71K, V7Constants.FS_BSDFFS);
            PutV7Superblock(image, OffsetA, "AAA");
            V7Driver driver = new V7Driver();
            driver.SetPartition('a');
            driver.Mount(new MemoryStream(image), true);

            Assert.That(driver.SelectPartition("c"), Is.False);

            Assert.That(driver.CurrentPartition, Is.EqualTo("a"));
            Assert.That(driver.VolumeLabel, Is.EqualTo("AAA"));
        }

        [Test]
        public void FfsDriver_SelectPartition_AfterMount_MovesTheMountedFileSystem()
        {
            byte[] image = MakeLabelledImage(V7Constants.FS_BSDFFS, V7Constants.FS_BSDFFS);
            PutFfsSuperblock(image, OffsetA, "AAA");
            PutFfsSuperblock(image, OffsetC, "CCC");
            FfsDriver driver = new FfsDriver();
            driver.SetPartition('a');
            driver.Mount(new MemoryStream(image), true);
            Assert.That(driver.VolumeLabel, Is.EqualTo("AAA"));

            Assert.That(driver.SelectPartition("c"), Is.True);

            Assert.That(driver.CurrentPartition, Is.EqualTo("c"));
            Assert.That(driver.VolumeLabel, Is.EqualTo("CCC"));
        }

        [Test]
        public void FfsDriver_SelectPartition_AfterMount_PartitionThatCannotMount_KeepsTheOldOne()
        {
            byte[] image = MakeLabelledImage(V7Constants.FS_BSDFFS, V7Constants.FS_V71K);
            PutFfsSuperblock(image, OffsetA, "AAA");
            FfsDriver driver = new FfsDriver();
            driver.SetPartition('a');
            driver.Mount(new MemoryStream(image), true);

            Assert.That(driver.SelectPartition("c"), Is.False);

            Assert.That(driver.CurrentPartition, Is.EqualTo("a"));
            Assert.That(driver.VolumeLabel, Is.EqualTo("AAA"));
        }
    }
}
