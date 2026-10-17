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
using RetroIO.Storage.Local.MBR.Structures;

namespace RetroIO.Storage.Local.Tests
{
    [TestFixture]
    public class PcImageFactoryTests
    {
        [TestCase(PcFloppySize.Size360K, 368640)]
        [TestCase(PcFloppySize.Size720K, 737280)]
        [TestCase(PcFloppySize.Size1_2M, 1228800)]
        [TestCase(PcFloppySize.Size1_44M, 1474560)]
        [TestCase(PcFloppySize.Size2_88M, 2949120)]
        public void CreateBlankFloppy_HasExpectedSize(PcFloppySize size, int expectedBytes)
        {
            byte[] image = PcImageFactory.CreateBlankFloppy(size);
            Assert.That(image.Length, Is.EqualTo(expectedBytes));
        }

        [Test]
        public void CreateBlankFloppy_MountsAndSupportsReadWrite_ViaPcFloppyDriver()
        {
            byte[] image = PcImageFactory.CreateBlankFloppy(PcFloppySize.Size1_44M, "TESTDISK");

            using var stream = new MemoryStream(image, writable: true);
            var driver = new PcFloppyDriver();
            driver.Mount(stream, readOnly: false);

            Assert.That(driver.FileSystemType, Is.EqualTo("FAT12"));
            Assert.That(driver.ListDirectory("/"), Is.Empty);

            byte[] data = { 1, 2, 3, 4, 5 };
            driver.WriteFile("HELLO.TXT", data);
            Assert.That(driver.ReadFile("HELLO.TXT"), Is.EqualTo(data));

            driver.CreateDirectory("SUBDIR");
            Assert.That(driver.ListDirectory("/").Length, Is.EqualTo(2));
        }

        [TestCase(PcHddPreset.Xt5MB)]
        [TestCase(PcHddPreset.Xt10MB)]
        [TestCase(PcHddPreset.Xt15MB)]
        [TestCase(PcHddPreset.Xt26MB)]
        [TestCase(PcHddPreset.At20MB)]
        [TestCase(PcHddPreset.At30MB)]
        [TestCase(PcHddPreset.At47MB)]
        [TestCase(PcHddPreset.At62MB)]
        public void CreateBlankHardDisk_HasValidMbrWithSinglePartitionSpanningWholeDisk(PcHddPreset preset)
        {
            byte[] image = PcImageFactory.CreateBlankHardDisk(preset);

            byte[] mbrBytes = new byte[512];
            Array.Copy(image, mbrBytes, 512);
            MbrPartitionTable table = MbrPartitionTable.Parse(mbrBytes);

            Assert.That(table.IsValid(), Is.True);
            Assert.That(table.GetPartitionCount(), Is.EqualTo(1));

            MbrPartitionEntry partition = table.Partitions[0];
            uint totalSectors = (uint)(image.Length / 512);
            Assert.That(partition.LbaStart, Is.GreaterThan(0));
            Assert.That((ulong)partition.LbaStart + partition.NumSectors, Is.EqualTo(totalSectors),
                "the single partition should span from its start sector all the way to the end of the disk");
        }

        [TestCase(PcHddPreset.Xt10MB)]
        [TestCase(PcHddPreset.At20MB)]
        public void CreateBlankHardDisk_MountsAndSupportsReadWrite_ViaPartition0(PcHddPreset preset)
        {
            byte[] image = PcImageFactory.CreateBlankHardDisk(preset, "TESTHDD");

            using var stream = new MemoryStream(image, writable: true);
            var driver = new PcFloppyDriver();
            IPartitionProvider partitionProvider = driver;

            Assert.That(partitionProvider.SelectPartition("0"), Is.True);
            driver.Mount(stream, readOnly: false);

            Assert.That(driver.ListDirectory("/"), Is.Empty);

            byte[] data = new byte[4096];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)i;
            driver.WriteFile("BIGFILE.BIN", data);
            Assert.That(driver.ReadFile("BIGFILE.BIN"), Is.EqualTo(data));
        }

        [Test]
        public void CreateBlankHardDisk_SmallPreset_FormatsFat12()
        {
            byte[] image = PcImageFactory.CreateBlankHardDisk(PcHddPreset.Xt10MB);
            using var stream = new MemoryStream(image, writable: true);
            var driver = new PcFloppyDriver();
            ((IPartitionProvider)driver).SelectPartition("0");
            driver.Mount(stream, readOnly: false);

            Assert.That(driver.FileSystemType, Is.EqualTo("FAT12"));
        }

        [Test]
        public void CreateBlankHardDisk_LargePreset_FormatsFat16()
        {
            byte[] image = PcImageFactory.CreateBlankHardDisk(PcHddPreset.At62MB);
            using var stream = new MemoryStream(image, writable: true);
            var driver = new PcFloppyDriver();
            ((IPartitionProvider)driver).SelectPartition("0");
            driver.Mount(stream, readOnly: false);

            Assert.That(driver.FileSystemType, Is.EqualTo("FAT16"));
        }
    }
}
