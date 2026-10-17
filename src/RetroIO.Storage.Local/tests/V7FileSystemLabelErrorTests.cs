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
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.BSD.Structures;
using RetroIO.Storage.Local.V7.FileSystems;
using RetroIO.Storage.Local.V7.Structures;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Tests what <see cref="V7FileSystem.Initialize"/> does with a BSD disk label that names a
    /// partition it cannot use. The disk is built in memory, so no image file is needed.
    /// </summary>
    [TestFixture]
    public class V7FileSystemLabelErrorTests
    {
        private const int SectorSize = 512;
        private const int Sectors = 2048;

        // A 1 MB in-memory disk. When labelPartitions is given, a valid BSD label is written at sector 1.
        internal static MemoryBlockDevice MakeDisk(BsdPartition[]? labelPartitions)
        {
            byte[] image = new byte[SectorSize * Sectors];

            if (labelPartitions != null)
            {
                BsdDiskLabel label = new BsdDiskLabel
                {
                    Magic = V7Constants.DISK_MAGIC,
                    Magic2 = V7Constants.DISK_MAGIC,
                    NumPartitions = (ushort)labelPartitions.Length,
                    BytesPerSector = SectorSize
                };
                for (int i = 0; i < labelPartitions.Length; i++)
                    label.Partitions[i] = labelPartitions[i];

                byte[] sector = new byte[SectorSize];
                label.WriteTo(sector, 0);
                sector.CopyTo(image, SectorSize);
            }

            BlockDeviceDescriptor descriptor = new BlockDeviceDescriptor
            {
                Name = "Label test disk",
                BlockSize = SectorSize,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = Sectors,
                ExpectedImageSize = image.Length
            };
            MemoryBlockDevice device = new MemoryBlockDevice(descriptor);
            device.Mount(new MemoryStream(image), false);
            return device;
        }

        [Test]
        public void Initialize_PartitionNamedByTheLabelIsUnused_SaysSo()
        {
            // Regression: this exception was thrown inside a try whose bare catch swallowed it, so
            // Initialize carried on at block 0 and failed later with a misleading superblock error.
            using MemoryBlockDevice device = MakeDisk(new BsdPartition[]
            {
                default,                                                        // 'a' is unused
                default,
                new BsdPartition { Size = 1000, Offset = 100, FsType = V7Constants.FS_V71K }
            });
            V7FileSystem fs = new V7FileSystem { PartitionLetter = 'a' };

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => fs.Initialize(device))!;

            Assert.That(ex.Message, Does.Contain("not in use"));
        }

        [Test]
        public void Initialize_PartitionNamedByTheLabelIsNotV7_SaysSo()
        {
            using MemoryBlockDevice device = MakeDisk(new BsdPartition[]
            {
                new BsdPartition { Size = 1000, Offset = 100, FsType = V7Constants.FS_BSDFFS }
            });
            V7FileSystem fs = new V7FileSystem { PartitionLetter = 'a' };

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => fs.Initialize(device))!;

            Assert.That(ex.Message, Does.Contain("not a V7 filesystem"));
        }

        [Test]
        public void Initialize_NoLabelOnTheDisk_FallsBackToBlockZero_AndReportsTheSuperblock()
        {
            // No label is not an error in itself: the filesystem is assumed to start at block 0.
            // This disk is blank, so it is the superblock check that fails.
            using MemoryBlockDevice device = MakeDisk(null);
            V7FileSystem fs = new V7FileSystem { PartitionLetter = 'a' };

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => fs.Initialize(device))!;

            Assert.That(ex.Message, Does.Contain("Invalid superblock"));
        }
    }
}
