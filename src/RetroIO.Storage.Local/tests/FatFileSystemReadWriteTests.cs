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
    /// Builds minimal, spec-valid FAT12/16/32 boot sectors + FAT tables + directory areas by hand,
    /// at the exact byte offsets documented in ai-docs/implement-fat.md §3. Deliberately uses small
    /// synthetic volumes rather than real fixed floppy/HDD sizes — CanHandle's signature check
    /// (offset 0x36 for FAT12/16, 0x52 for FAT32) means these objects are always constructed
    /// directly in tests, never through PcFloppyDriver's cluster-count auto-detection heuristic,
    /// so a "real" cluster count is not required for these tests to be meaningful.
    /// </summary>
    internal static class FatImageBuilder
    {
        public const int SectorSize = 512;

        public static MemoryStream BuildFat12(int totalSectors, int sectorsPerCluster, int reservedSectors,
            int numberOfFats, int rootEntryCount, int sectorsPerFat)
        {
            byte[] boot = new byte[SectorSize];
            WriteCommonBpb(boot, sectorsPerCluster, reservedSectors, numberOfFats, rootEntryCount,
                (ushort)totalSectors, sectorsPerFat);
            WriteAsciiAt(boot, 0x36, "FAT12   ");
            boot[510] = 0x55;
            boot[511] = 0xAA;

            byte[] image = new byte[(long)totalSectors * SectorSize];
            Array.Copy(boot, 0, image, 0, SectorSize);
            return new MemoryStream(image);
        }

        public static MemoryStream BuildFat16(int totalSectors, int sectorsPerCluster, int reservedSectors,
            int numberOfFats, int rootEntryCount, int sectorsPerFat)
        {
            byte[] boot = new byte[SectorSize];
            WriteCommonBpb(boot, sectorsPerCluster, reservedSectors, numberOfFats, rootEntryCount,
                0, sectorsPerFat);
            WriteUInt32At(boot, 32, (uint)totalSectors);
            WriteAsciiAt(boot, 0x36, "FAT16   ");
            boot[510] = 0x55;
            boot[511] = 0xAA;

            byte[] image = new byte[(long)totalSectors * SectorSize];
            Array.Copy(boot, 0, image, 0, SectorSize);
            return new MemoryStream(image);
        }

        public static MemoryStream BuildFat32(int totalSectors, int sectorsPerCluster, int reservedSectors,
            int numberOfFats, int sectorsPerFat32, uint rootCluster)
        {
            byte[] boot = new byte[SectorSize];
            WriteCommonBpb(boot, sectorsPerCluster, reservedSectors, numberOfFats, 0, 0, 0);
            WriteUInt32At(boot, 32, (uint)totalSectors);
            WriteUInt32At(boot, 36, (uint)sectorsPerFat32);
            WriteUInt32At(boot, 44, rootCluster);
            WriteAsciiAt(boot, 0x52, "FAT32   ");
            boot[510] = 0x55;
            boot[511] = 0xAA;

            byte[] image = new byte[(long)totalSectors * SectorSize];
            Array.Copy(boot, 0, image, 0, SectorSize);

            // FAT32's root directory must exist as an allocated (all-zero, i.e. "end of directory"
            // at byte 0) cluster from the start — mark its FAT entry as end-of-chain.
            uint fatStart = (uint)reservedSectors;
            uint rootFatOffset = fatStart * SectorSize + rootCluster * 4;
            image[rootFatOffset] = 0xFF;
            image[rootFatOffset + 1] = 0xFF;
            image[rootFatOffset + 2] = 0xFF;
            image[rootFatOffset + 3] = 0x0F;
            if (numberOfFats > 1)
            {
                uint fat2Offset = (fatStart + (uint)sectorsPerFat32) * SectorSize + rootCluster * 4;
                image[fat2Offset] = 0xFF;
                image[fat2Offset + 1] = 0xFF;
                image[fat2Offset + 2] = 0xFF;
                image[fat2Offset + 3] = 0x0F;
            }

            return new MemoryStream(image);
        }

        private static void WriteCommonBpb(byte[] boot, int sectorsPerCluster, int reservedSectors,
            int numberOfFats, int rootEntryCount, ushort totalSectors16, int sectorsPerFat16)
        {
            boot[11] = (byte)(SectorSize & 0xFF);
            boot[12] = (byte)((SectorSize >> 8) & 0xFF);
            boot[13] = (byte)sectorsPerCluster;
            boot[14] = (byte)(reservedSectors & 0xFF);
            boot[15] = (byte)((reservedSectors >> 8) & 0xFF);
            boot[16] = (byte)numberOfFats;
            boot[17] = (byte)(rootEntryCount & 0xFF);
            boot[18] = (byte)((rootEntryCount >> 8) & 0xFF);
            boot[19] = (byte)(totalSectors16 & 0xFF);
            boot[20] = (byte)((totalSectors16 >> 8) & 0xFF);
            boot[21] = 0xF8; // media descriptor: fixed disk
            boot[22] = (byte)(sectorsPerFat16 & 0xFF);
            boot[23] = (byte)((sectorsPerFat16 >> 8) & 0xFF);
        }

        private static void WriteAsciiAt(byte[] buffer, int offset, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                buffer[offset + i] = (byte)text[i];
            }
        }

        private static void WriteUInt32At(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }

    [TestFixture]
    public class Fat12FileSystemReadWriteTests
    {
        // 1.44MB-shaped layout (2880 sectors, 1 spc, 2 FATs of 9 sectors, 224 root entries) —
        // standard real-world FAT12 parameters, kept small enough to run fast.
        private GenericBlockDevice CreateDevice(out Fat12FileSystem fs)
        {
            MemoryStream image = FatImageBuilder.BuildFat12(2880, 1, 1, 2, 224, 9);
            var descriptor = new BlockDeviceDescriptor
            {
                Name = "Test FAT12",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = 2880
            };
            var device = new GenericBlockDevice(descriptor);
            device.Mount(image, readOnly: false);
            fs = new Fat12FileSystem();
            fs.Initialize(device);
            return device;
        }

        [Test]
        public void RootDirectory_InitiallyEmpty()
        {
            CreateDevice(out Fat12FileSystem fs);
            Assert.That(fs.ListDirectory("/"), Is.Empty);
        }

        [Test]
        public void WriteFile_ThenRead_RoundTrips()
        {
            CreateDevice(out Fat12FileSystem fs);
            byte[] data = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            fs.WriteFile("/HELLO.TXT", data);

            byte[] readBack = fs.ReadFile("/HELLO.TXT");
            Assert.That(readBack, Is.EqualTo(data));
        }

        [Test]
        public void WriteFile_MultiCluster_RoundTrips()
        {
            CreateDevice(out Fat12FileSystem fs);
            byte[] data = new byte[512 * 5 + 37]; // spans multiple 1-sector clusters, partial tail
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(i % 251);
            }

            fs.WriteFile("/BIG.BIN", data);
            byte[] readBack = fs.ReadFile("/BIG.BIN");
            Assert.That(readBack, Is.EqualTo(data));
        }

        [Test]
        public void CreateDirectory_ThenWriteFileInside_RoundTrips()
        {
            CreateDevice(out Fat12FileSystem fs);
            fs.CreateDirectory("/SUBDIR");

            DirectoryEntry[] root = fs.ListDirectory("/");
            Assert.That(root.Length, Is.EqualTo(1));
            Assert.That(root[0].Name, Is.EqualTo("SUBDIR"));
            Assert.That(root[0].Metadata!.IsDirectory, Is.True);

            byte[] data = { 9, 9, 9 };
            fs.WriteFile("/SUBDIR/INNER.TXT", data);

            DirectoryEntry[] sub = fs.ListDirectory("/SUBDIR");
            Assert.That(sub.Length, Is.EqualTo(1));
            Assert.That(sub[0].Name, Is.EqualTo("INNER.TXT"));

            Assert.That(fs.ReadFile("/SUBDIR/INNER.TXT"), Is.EqualTo(data));
        }

        [Test]
        public void CreateDirectory_TwoLevelsDeep_RoundTrips()
        {
            CreateDevice(out Fat12FileSystem fs);
            fs.CreateDirectory("/A");
            fs.CreateDirectory("/A/B");
            fs.WriteFile("/A/B/DEEP.TXT", new byte[] { 42 });

            Assert.That(fs.ListDirectory("/A/B").Length, Is.EqualTo(1));
            Assert.That(fs.ReadFile("/A/B/DEEP.TXT"), Is.EqualTo(new byte[] { 42 }));

            // "." and ".." must never leak into listings.
            DirectoryEntry[] bEntries = fs.ListDirectory("/A/B");
            for (int i = 0; i < bEntries.Length; i++)
            {
                Assert.That(bEntries[i].Name, Is.Not.EqualTo("."));
                Assert.That(bEntries[i].Name, Is.Not.EqualTo(".."));
            }
        }

        [Test]
        public void DeleteFile_RemovesEntryAndFreesSpace()
        {
            CreateDevice(out Fat12FileSystem fs);
            fs.WriteFile("/DEL.TXT", new byte[600]);
            int freeBefore = fs.GetBlocksFree();

            fs.DeleteFile("/DEL.TXT");

            Assert.That(fs.ListDirectory("/"), Is.Empty);
            Assert.That(fs.GetBlocksFree(), Is.GreaterThan(freeBefore));
            Assert.Throws<FileNotFoundException>(() => fs.ReadFile("/DEL.TXT"));
        }

        [Test]
        public void DeleteDirectory_NonEmpty_Throws()
        {
            CreateDevice(out Fat12FileSystem fs);
            fs.CreateDirectory("/D");
            fs.WriteFile("/D/X.TXT", new byte[] { 1 });

            Assert.Throws<IOException>(() => fs.DeleteDirectory("/D"));
        }

        [Test]
        public void DeleteDirectory_Empty_Succeeds()
        {
            CreateDevice(out Fat12FileSystem fs);
            fs.CreateDirectory("/D");
            fs.DeleteDirectory("/D");
            Assert.That(fs.ListDirectory("/"), Is.Empty);
        }

        [Test]
        public void Rename_SameDirectory_UpdatesName()
        {
            CreateDevice(out Fat12FileSystem fs);
            fs.WriteFile("/OLD.TXT", new byte[] { 7 });
            fs.Rename("/OLD.TXT", "/NEW.TXT");

            Assert.That(fs.ReadFile("/NEW.TXT"), Is.EqualTo(new byte[] { 7 }));
            Assert.Throws<FileNotFoundException>(() => fs.ReadFile("/OLD.TXT"));
        }

        [Test]
        public void Rename_CrossDirectory_PreservesData_NoRecopy()
        {
            CreateDevice(out Fat12FileSystem fs);
            fs.CreateDirectory("/DEST");
            byte[] data = new byte[900]; // multi-cluster, so a naive copy-based move would be detectable
            for (int i = 0; i < data.Length; i++) data[i] = (byte)i;
            fs.WriteFile("/SRC.BIN", data);

            fs.Rename("/SRC.BIN", "/DEST/SRC.BIN");

            Assert.That(fs.ReadFile("/DEST/SRC.BIN"), Is.EqualTo(data));
            Assert.Throws<FileNotFoundException>(() => fs.ReadFile("/SRC.BIN"));
        }

        [Test]
        public void WriteFile_Overwrite_ReplacesContentAndFreesOldChain()
        {
            CreateDevice(out Fat12FileSystem fs);
            fs.WriteFile("/F.TXT", new byte[1000]);
            int freeAfterFirst = fs.GetBlocksFree();

            fs.WriteFile("/F.TXT", new byte[] { 1, 2, 3 });

            Assert.That(fs.ReadFile("/F.TXT"), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(fs.GetBlocksFree(), Is.GreaterThan(freeAfterFirst));
        }

        [Test]
        public void BothFatCopies_StayInSync_AfterWrite()
        {
            GenericBlockDevice device = CreateDevice(out Fat12FileSystem fs);
            fs.WriteFile("/SYNC.TXT", new byte[600]);

            // Boot sector for this layout: reserved=1, sectorsPerFat=9 -> copy #1 at LBA 1..9, copy #2 at LBA 10..18.
            byte[] copy1 = new byte[512 * 9];
            byte[] copy2 = new byte[512 * 9];
            for (int i = 0; i < 9; i++)
            {
                byte[] sector = new byte[512];
                device.ReadBlock(BlockAddress.FromLBA((uint)(1 + i)), sector);
                Array.Copy(sector, 0, copy1, i * 512, 512);
            }
            for (int i = 0; i < 9; i++)
            {
                byte[] sector = new byte[512];
                device.ReadBlock(BlockAddress.FromLBA((uint)(10 + i)), sector);
                Array.Copy(sector, 0, copy2, i * 512, 512);
            }

            Assert.That(copy2, Is.EqualTo(copy1));
        }

        [Test]
        public void WriteFile_InvalidShortName_Throws()
        {
            CreateDevice(out Fat12FileSystem fs);
            Assert.Throws<ArgumentException>(() => fs.WriteFile("/THISNAMEISWAYTOOLONG.TXT", new byte[] { 1 }));
        }
    }

    [TestFixture]
    public class Fat16FileSystemReadWriteTests
    {
        // Small synthetic FAT16 volume: 4200 sectors, 1 spc, 2 FATs of 17 sectors, 512 root entries.
        private GenericBlockDevice CreateDevice(out Fat16FileSystem fs)
        {
            MemoryStream image = FatImageBuilder.BuildFat16(4200, 1, 1, 2, 512, 17);
            var descriptor = new BlockDeviceDescriptor
            {
                Name = "Test FAT16",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = 4200
            };
            var device = new GenericBlockDevice(descriptor);
            device.Mount(image, readOnly: false);
            fs = new Fat16FileSystem();
            fs.Initialize(device);
            return device;
        }

        [Test]
        public void RootDirectory_InitiallyEmpty()
        {
            CreateDevice(out Fat16FileSystem fs);
            Assert.That(fs.ListDirectory("/"), Is.Empty);
        }

        [Test]
        public void WriteFile_ThenRead_RoundTrips()
        {
            CreateDevice(out Fat16FileSystem fs);
            byte[] data = { 1, 2, 3, 4, 5 };
            fs.WriteFile("/HELLO.TXT", data);
            Assert.That(fs.ReadFile("/HELLO.TXT"), Is.EqualTo(data));
        }

        [Test]
        public void CreateDirectory_TwoLevelsDeep_RoundTrips()
        {
            CreateDevice(out Fat16FileSystem fs);
            fs.CreateDirectory("/A");
            fs.CreateDirectory("/A/B");
            fs.WriteFile("/A/B/DEEP.TXT", new byte[] { 42 });

            Assert.That(fs.ListDirectory("/A/B").Length, Is.EqualTo(1));
            Assert.That(fs.ReadFile("/A/B/DEEP.TXT"), Is.EqualTo(new byte[] { 42 }));
        }

        [Test]
        public void DeleteFile_RemovesEntry()
        {
            CreateDevice(out Fat16FileSystem fs);
            fs.WriteFile("/DEL.TXT", new byte[600]);
            fs.DeleteFile("/DEL.TXT");
            Assert.That(fs.ListDirectory("/"), Is.Empty);
        }

        [Test]
        public void DeleteDirectory_NonEmpty_Throws()
        {
            CreateDevice(out Fat16FileSystem fs);
            fs.CreateDirectory("/D");
            fs.WriteFile("/D/X.TXT", new byte[] { 1 });
            Assert.Throws<IOException>(() => fs.DeleteDirectory("/D"));
        }

        [Test]
        public void Rename_CrossDirectory_PreservesData()
        {
            CreateDevice(out Fat16FileSystem fs);
            fs.CreateDirectory("/DEST");
            byte[] data = new byte[900];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)i;
            fs.WriteFile("/SRC.BIN", data);

            fs.Rename("/SRC.BIN", "/DEST/SRC.BIN");
            Assert.That(fs.ReadFile("/DEST/SRC.BIN"), Is.EqualTo(data));
        }

        [Test]
        public void BothFatCopies_StayInSync_AfterWrite()
        {
            GenericBlockDevice device = CreateDevice(out Fat16FileSystem fs);
            fs.WriteFile("/SYNC.TXT", new byte[600]);

            // reserved=1, sectorsPerFat=17 -> copy #1 at LBA 1..17, copy #2 at LBA 18..34.
            byte[] copy1 = new byte[512 * 17];
            byte[] copy2 = new byte[512 * 17];
            for (int i = 0; i < 17; i++)
            {
                byte[] sector = new byte[512];
                device.ReadBlock(BlockAddress.FromLBA((uint)(1 + i)), sector);
                Array.Copy(sector, 0, copy1, i * 512, 512);
            }
            for (int i = 0; i < 17; i++)
            {
                byte[] sector = new byte[512];
                device.ReadBlock(BlockAddress.FromLBA((uint)(18 + i)), sector);
                Array.Copy(sector, 0, copy2, i * 512, 512);
            }

            Assert.That(copy2, Is.EqualTo(copy1));
        }
    }

    [TestFixture]
    public class Fat32FileSystemReadWriteTests
    {
        // Small synthetic FAT32 volume: 4300 sectors, 1 spc, 2 FATs of 34 sectors, root at cluster 2.
        private GenericBlockDevice CreateDevice(out Fat32FileSystem fs)
        {
            MemoryStream image = FatImageBuilder.BuildFat32(4300, 1, 32, 2, 34, rootCluster: 2);
            var descriptor = new BlockDeviceDescriptor
            {
                Name = "Test FAT32",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = 4300
            };
            var device = new GenericBlockDevice(descriptor);
            device.Mount(image, readOnly: false);
            fs = new Fat32FileSystem();
            fs.Initialize(device);
            return device;
        }

        [Test]
        public void RootDirectory_InitiallyEmpty()
        {
            CreateDevice(out Fat32FileSystem fs);
            Assert.That(fs.ListDirectory("/"), Is.Empty);
        }

        [Test]
        public void WriteFile_ThenRead_RoundTrips()
        {
            CreateDevice(out Fat32FileSystem fs);
            byte[] data = { 1, 2, 3, 4, 5 };
            fs.WriteFile("/HELLO.TXT", data);
            Assert.That(fs.ReadFile("/HELLO.TXT"), Is.EqualTo(data));
        }

        [Test]
        public void CreateDirectory_TwoLevelsDeep_RoundTrips()
        {
            CreateDevice(out Fat32FileSystem fs);
            fs.CreateDirectory("/A");
            fs.CreateDirectory("/A/B");
            fs.WriteFile("/A/B/DEEP.TXT", new byte[] { 42 });

            Assert.That(fs.ListDirectory("/A/B").Length, Is.EqualTo(1));
            Assert.That(fs.ReadFile("/A/B/DEEP.TXT"), Is.EqualTo(new byte[] { 42 }));
        }

        [Test]
        public void DeleteFile_RemovesEntry()
        {
            CreateDevice(out Fat32FileSystem fs);
            fs.WriteFile("/DEL.TXT", new byte[600]);
            fs.DeleteFile("/DEL.TXT");
            Assert.That(fs.ListDirectory("/"), Is.Empty);
        }

        [Test]
        public void DeleteDirectory_NonEmpty_Throws()
        {
            CreateDevice(out Fat32FileSystem fs);
            fs.CreateDirectory("/D");
            fs.WriteFile("/D/X.TXT", new byte[] { 1 });
            Assert.Throws<IOException>(() => fs.DeleteDirectory("/D"));
        }

        [Test]
        public void Rename_CrossDirectory_PreservesData()
        {
            CreateDevice(out Fat32FileSystem fs);
            fs.CreateDirectory("/DEST");
            byte[] data = new byte[900];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)i;
            fs.WriteFile("/SRC.BIN", data);

            fs.Rename("/SRC.BIN", "/DEST/SRC.BIN");
            Assert.That(fs.ReadFile("/DEST/SRC.BIN"), Is.EqualTo(data));
        }

        [Test]
        public void RootDirectory_CanGrowBeyondOneCluster()
        {
            // Unlike FAT12/16, FAT32's root is a regular cluster chain and must be able to grow.
            // 1 sector/cluster = 512/32 = 16 entries per cluster; write enough files to force a
            // second root cluster to be allocated.
            CreateDevice(out Fat32FileSystem fs);
            for (int i = 0; i < 20; i++)
            {
                fs.WriteFile($"/F{i:D2}.TXT", new byte[] { (byte)i });
            }

            DirectoryEntry[] root = fs.ListDirectory("/");
            Assert.That(root.Length, Is.EqualTo(20));
            Assert.That(fs.ReadFile("/F19.TXT"), Is.EqualTo(new byte[] { 19 }));
        }

        [Test]
        public void BothFatCopies_StayInSync_AfterWrite()
        {
            GenericBlockDevice device = CreateDevice(out Fat32FileSystem fs);
            fs.WriteFile("/SYNC.TXT", new byte[600]);

            // reserved=32, sectorsPerFat32=34 -> copy #1 at LBA 32..65, copy #2 at LBA 66..99.
            byte[] copy1 = new byte[512 * 34];
            byte[] copy2 = new byte[512 * 34];
            for (int i = 0; i < 34; i++)
            {
                byte[] sector = new byte[512];
                device.ReadBlock(BlockAddress.FromLBA((uint)(32 + i)), sector);
                Array.Copy(sector, 0, copy1, i * 512, 512);
            }
            for (int i = 0; i < 34; i++)
            {
                byte[] sector = new byte[512];
                device.ReadBlock(BlockAddress.FromLBA((uint)(66 + i)), sector);
                Array.Copy(sector, 0, copy2, i * 512, 512);
            }

            Assert.That(copy2, Is.EqualTo(copy1));
        }
    }
}
