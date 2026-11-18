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
using RetroIO.Storage.Local.FileSystems;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// A write that does not fit is refused BEFORE the disk changes, on FAT12, FAT16 and FAT32:
    /// the old file of the same name is still there and whole, no cluster is taken, and the
    /// message names the file. Before 2026-11-18 an overwrite freed the old file's clusters first
    /// and then ran out part way, so the old file was lost and a broken chain was left behind; a
    /// new file that did not fit kept the clusters it had taken before running out.
    /// </summary>
    [TestFixture]
    public class FatWriteDiskFullTests
    {
        /// <summary>
        /// The three FAT kinds, each on the small test layout the read/write tests use.
        /// </summary>
        public static readonly string[] Kinds = { "FAT12", "FAT16", "FAT32" };

        /// <summary>
        /// Overwriting a file with more than the free room plus the old file's room is refused, and
        /// the image is the same byte for byte afterwards.
        /// </summary>
        /// <param name="kind">
        /// The FAT kind.
        /// </param>
        [TestCaseSource(nameof(Kinds))]
        public void Overwrite_TooBig_IsRefused_AndTheOldFileStays(string kind)
        {
            Volume v = Create(kind);
            byte[] old = Pattern(3000, 7);
            v.Fs.WriteFile("/OLD.TXT", old);
            FillLeaving(v, 4);
            int freeBefore = v.Fs.GetBlocksFree();
            byte[] before = v.Image.ToArray();

            byte[] tooBig = Pattern(old.Length + (freeBefore + 2) * 512, 9);
            IOException? ex = Assert.Throws<IOException>(() => v.Fs.WriteFile("/OLD.TXT", tooBig));

            Assert.That(ex!.Message, Does.Contain("OLD.TXT"), "the refusal names the file");
            Assert.That(v.Image.ToArray(), Is.EqualTo(before), "the image is not changed by a refused write");
            Assert.That(v.Fs.ReadFile("/OLD.TXT"), Is.EqualTo(old), "the old file is still there and whole");
            Assert.That(v.Fs.GetBlocksFree(), Is.EqualTo(freeBefore));
        }

        /// <summary>
        /// A new file bigger than the free room is refused, and the image is the same byte for byte
        /// afterwards (no clusters kept from the attempt).
        /// </summary>
        /// <param name="kind">
        /// The FAT kind.
        /// </param>
        [TestCaseSource(nameof(Kinds))]
        public void NewFile_TooBig_IsRefused_AndNothingIsTaken(string kind)
        {
            Volume v = Create(kind);
            FillLeaving(v, 4);
            int freeBefore = v.Fs.GetBlocksFree();
            byte[] before = v.Image.ToArray();

            IOException? ex = Assert.Throws<IOException>(() => v.Fs.WriteFile("/NEW.BIN", new byte[(freeBefore + 1) * 512]));

            Assert.That(ex!.Message, Does.Contain("NEW.BIN"), "the refusal names the file");
            Assert.That(v.Image.ToArray(), Is.EqualTo(before), "the image is not changed by a refused write");
            Assert.That(v.Fs.GetBlocksFree(), Is.EqualTo(freeBefore));
            Assert.That(v.Fs.GetMetadata("/NEW.BIN"), Is.Null);
        }

        /// <summary>
        /// An overwrite that fits only because the old file's clusters come back still works: the
        /// check counts them.
        /// </summary>
        /// <param name="kind">
        /// The FAT kind.
        /// </param>
        [TestCaseSource(nameof(Kinds))]
        public void Overwrite_ThatFitsWithTheOldRoom_IsWritten(string kind)
        {
            Volume v = Create(kind);
            v.Fs.WriteFile("/OLD.TXT", Pattern(4096, 3));
            FillLeaving(v, 2);
            int freeBefore = v.Fs.GetBlocksFree();

            byte[] replacement = Pattern(4096 + freeBefore * 512, 5);
            v.Fs.WriteFile("/OLD.TXT", replacement);

            Assert.That(v.Fs.ReadFile("/OLD.TXT"), Is.EqualTo(replacement));
            Assert.That(v.Fs.GetBlocksFree(), Is.EqualTo(0));
        }

        // Fills the disk with one file so that 'leave' free clusters (one sector each here) remain.
        private static void FillLeaving(Volume v, int leave)
        {
            int free = v.Fs.GetBlocksFree();
            v.Fs.WriteFile("/FILL.BIN", new byte[(free - leave) * 512]);
            Assert.That(v.Fs.GetBlocksFree(), Is.EqualTo(leave), "the filler left the asked room");
        }

        private static byte[] Pattern(int length, int seed)
        {
            byte[] data = new byte[length];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(i * 31 + seed);
            }
            return data;
        }

        // The same layouts as the read/write tests: one sector per cluster, two FAT copies.
        private static Volume Create(string kind)
        {
            MemoryStream image;
            int sectors;
            IBlockFileSystem fs;
            switch (kind)
            {
                case "FAT12":
                    sectors = 2880;
                    image = FatImageBuilder.BuildFat12(sectors, 1, 1, 2, 224, 9);
                    fs = new Fat12FileSystem();
                    break;
                case "FAT16":
                    sectors = 4200;
                    image = FatImageBuilder.BuildFat16(sectors, 1, 1, 2, 512, 17);
                    fs = new Fat16FileSystem();
                    break;
                default:
                    sectors = 4300;
                    image = FatImageBuilder.BuildFat32(sectors, 1, 32, 2, 34, rootCluster: 2);
                    fs = new Fat32FileSystem();
                    break;
            }
            var descriptor = new BlockDeviceDescriptor
            {
                Name = "Test " + kind,
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)sectors,
            };
            var device = new GenericBlockDevice(descriptor);
            device.Mount(image, readOnly: false);
            fs.Initialize(device);
            return new Volume(image, fs);
        }

        private sealed class Volume
        {
            public Volume(MemoryStream image, IBlockFileSystem fs)
            {
                Image = image;
                Fs = fs;
            }

            public MemoryStream Image { get; }

            public IBlockFileSystem Fs { get; }
        }
    }
}
