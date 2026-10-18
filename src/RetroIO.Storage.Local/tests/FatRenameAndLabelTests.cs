//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.IO;
using System.Text;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.FileSystems;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Tests that a directory moved to another parent gets a correct dot-dot entry in FAT12, FAT16 and FAT32,
    /// and that <see cref="Fat32FileSystem.VolumeLabel"/> is read from the volume.
    /// </summary>
    [TestFixture]
    public class FatRenameAndLabelTests
    {
        private static GenericBlockDevice Mount(MemoryStream image, int totalSectors)
        {
            BlockDeviceDescriptor descriptor = new BlockDeviceDescriptor
            {
                Name = "Test FAT",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)totalSectors
            };
            GenericBlockDevice device = new GenericBlockDevice(descriptor);
            device.Mount(image, readOnly: false);
            return device;
        }

        // Start cluster of the first directory entry with this 8.3 name and the directory attribute.
        private static int FindDirectoryCluster(byte[] image, string name11)
        {
            byte[] wanted = Encoding.ASCII.GetBytes(name11);
            for (int p = 0; p + 32 <= image.Length; p += 32)
            {
                if ((image[p + 11] & 0x10) == 0)
                {
                    continue;
                }

                bool match = true;
                for (int i = 0; i < 11 && match; i++)
                {
                    match = image[p + i] == wanted[i];
                }
                if (match)
                {
                    return image[p + 26] | (image[p + 27] << 8) | (image[p + 20] << 16) | (image[p + 21] << 24);
                }
            }
            return -1;
        }

        // Reads the dot-dot entry of the directory whose own dot entry names this cluster.
        private static int FindDotDotOf(byte[] image, int cluster)
        {
            for (int p = 0; p + 64 <= image.Length; p += 32)
            {
                if (image[p] != (byte)'.' || image[p + 1] != (byte)' ' || (image[p + 11] & 0x10) == 0)
                {
                    continue;
                }
                if (image[p + 32] != (byte)'.' || image[p + 33] != (byte)'.' || image[p + 34] != (byte)' ')
                {
                    continue;
                }
                int dot = image[p + 26] | (image[p + 27] << 8) | (image[p + 20] << 16) | (image[p + 21] << 24);
                if (dot == cluster)
                {
                    return image[p + 32 + 26] | (image[p + 32 + 27] << 8) | (image[p + 32 + 20] << 16) | (image[p + 32 + 21] << 24);
                }
            }
            return -1;
        }

        private static void AssertMovedDirectoryPointsAtNewParent(IBlockFileSystem fs, GenericBlockDevice device, MemoryStream image)
        {
            fs.CreateDirectory("/AAA");
            fs.CreateDirectory("/BBB");
            fs.CreateDirectory("/AAA/SUB");

            fs.Rename("/AAA/SUB", "/BBB/SUB");

            device.Flush();
            byte[] bytes = image.ToArray();
            int bbb = FindDirectoryCluster(bytes, "BBB        ");
            int sub = FindDirectoryCluster(bytes, "SUB        ");
            Assert.That(bbb, Is.GreaterThan(1));
            Assert.That(sub, Is.GreaterThan(1));
            Assert.That(FindDotDotOf(bytes, sub), Is.EqualTo(bbb), "dot-dot must name the new parent");
        }

        [Test]
        public void Fat12_RenameDirectoryAcrossParents_RewritesDotDot()
        {
            MemoryStream image = FatImageBuilder.BuildFat12(2880, 1, 1, 2, 224, 9);
            GenericBlockDevice device = Mount(image, 2880);
            Fat12FileSystem fs = new Fat12FileSystem();
            fs.Initialize(device);

            AssertMovedDirectoryPointsAtNewParent(fs, device, image);
        }

        [Test]
        public void Fat16_RenameDirectoryAcrossParents_RewritesDotDot()
        {
            MemoryStream image = FatImageBuilder.BuildFat16(4200, 1, 1, 2, 512, 17);
            GenericBlockDevice device = Mount(image, 4200);
            Fat16FileSystem fs = new Fat16FileSystem();
            fs.Initialize(device);

            AssertMovedDirectoryPointsAtNewParent(fs, device, image);
        }

        [Test]
        public void Fat32_RenameDirectoryAcrossParents_RewritesDotDot()
        {
            MemoryStream image = FatImageBuilder.BuildFat32(4300, 1, 32, 2, 34, rootCluster: 2);
            GenericBlockDevice device = Mount(image, 4300);
            Fat32FileSystem fs = new Fat32FileSystem();
            fs.Initialize(device);

            AssertMovedDirectoryPointsAtNewParent(fs, device, image);
        }

        [Test]
        public void Fat12_RenameDirectoryToRoot_DotDotIsZero()
        {
            MemoryStream image = FatImageBuilder.BuildFat12(2880, 1, 1, 2, 224, 9);
            GenericBlockDevice device = Mount(image, 2880);
            Fat12FileSystem fs = new Fat12FileSystem();
            fs.Initialize(device);
            fs.CreateDirectory("/AAA");
            fs.CreateDirectory("/AAA/SUB");

            fs.Rename("/AAA/SUB", "/SUB");

            device.Flush();
            byte[] bytes = image.ToArray();
            Assert.That(FindDotDotOf(bytes, FindDirectoryCluster(bytes, "SUB        ")), Is.EqualTo(0));
        }

        [Test]
        public void Fat32_VolumeLabel_IsReadFromTheRootDirectory()
        {
            MemoryStream image = FatImageBuilder.BuildFat32(4300, 1, 32, 2, 34, rootCluster: 2);
            GenericBlockDevice device = Mount(image, 4300);
            Fat32FileSystem.Format(device, 4300, 1, 32, 2, 0xF8, "MYVOL");
            Fat32FileSystem fs = new Fat32FileSystem();
            fs.Initialize(device);

            Assert.That(fs.VolumeLabel, Is.EqualTo("MYVOL"));
        }

        [Test]
        public void Fat32_VolumeLabel_NoLabelOnTheVolume_IsNoName()
        {
            MemoryStream image = FatImageBuilder.BuildFat32(4300, 1, 32, 2, 34, rootCluster: 2);
            GenericBlockDevice device = Mount(image, 4300);
            Fat32FileSystem fs = new Fat32FileSystem();
            fs.Initialize(device);

            Assert.That(fs.VolumeLabel, Is.EqualTo("NO NAME"));
        }
    }
}
