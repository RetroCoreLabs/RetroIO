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
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Local.FileSystems;
using RetroIO.Storage.Local.Streams;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Tests of the OS-9 (RBF) reader against a real NitrOS-9 Dragon disk and against small disks built from the field layout.
    /// </summary>
    /// <remarks>
    /// The expected values for the real disk, NITR09.DSK, were worked out by a separate walk over the raw sectors (identification sector, root
    /// descriptor, directory files, segment lists), not read back from the reader: 83 files holding 199595 bytes, 970 allocation map bits set
    /// (954 reached from the root plus the reserved sectors 2 to 17), so 470 free sectors. The disk's own startup file says it is NitrOS-9
    /// Level 1 for the Dragon, and its floppy driver is the Dragon DDisk driver.
    /// Each real-image test skips when the image is missing. Set RETROIO_DRAGON_TEST_IMAGES to the folder that holds the dsk folder.
    /// </remarks>
    [TestFixture]
    public class Os9RbfFileSystemTests
    {
        private const int SectorSize = 256;

        private FileStream? _stream;
        private DragonDskDriver? _driver;
        private byte[] _raw = Array.Empty<byte>();

        [SetUp]
        public void SetUp()
        {
            string path = ImagePath("dsk", "NITR09.DSK");
            if (!File.Exists(path))
            {
                Assert.Ignore("Test image not found: NITR09.DSK");
            }

            _raw = File.ReadAllBytes(path);
            _stream = File.OpenRead(path);
            _driver = new DragonDskDriver();
            _driver.Mount(_stream, true);
        }

        [TearDown]
        public void TearDown()
        {
            _driver?.Dispose();
            _stream?.Dispose();
        }

        private static string ImagePath(string folder, string name)
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable("RETROIO_DRAGON_TEST_IMAGES");
            string root = !string.IsNullOrEmpty(fromEnvironment)
                ? fromEnvironment
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "RetroFS", "test-images");
            return Path.Combine(root, folder, name);
        }

        // ---- the real disk ----

        [Test]
        public void RealDisk_MountsAsOs9WithTheGeometryFromSectorZero()
        {
            // The image has no header and is 368640 bytes. Sector 0 says 18 sectors per track and bit 0 of the format byte is set, so it is
            // 40 tracks on 2 sides, not 80 tracks on one side.
            Assert.That(_driver!.FileSystemType, Is.EqualTo("OS-9"));
            Assert.That(_driver.VolumeLabel, Is.EqualTo("NitrOS-9/6809 Level 1 Disk 1"));

            BlockDeviceInfo? info = _driver.GetBlockDeviceInfo();
            Assert.That(info, Is.Not.Null);
            Assert.That(info!.CylinderCount, Is.EqualTo(40));
            Assert.That(info.HeadCount, Is.EqualTo(2));
            Assert.That(info.SectorsPerTrack, Is.EqualTo(18));
            Assert.That(info.TotalBlocks, Is.EqualTo(1440u));
        }

        [Test]
        public void RealDisk_RootDirectory_HasTheFiveEntries()
        {
            DirectoryEntry[] root = _driver!.ListDirectory("/");

            string[] names = { "OS9Boot", "CMDS", "SYS", "DEFS", "startup" };
            long[] sizes = { 14495, 2336, 192, 256, 154 };
            bool[] directories = { false, true, true, true, false };
            string[] attributes = { "------wr", "d-ewrewr", "d-ewrewr", "d-ewrewr", "----r-wr" };

            Assert.That(root.Length, Is.EqualTo(names.Length));
            for (int i = 0; i < names.Length; i++)
            {
                Assert.That(root[i].Name, Is.EqualTo(names[i]), "name " + i);
                Assert.That(root[i].Metadata.Size, Is.EqualTo(sizes[i]), "size of " + names[i]);
                Assert.That(root[i].Metadata.IsDirectory, Is.EqualTo(directories[i]), "directory flag of " + names[i]);
                Assert.That(root[i].Metadata.Attributes, Is.EqualTo(attributes[i]), "attributes of " + names[i]);
            }
        }

        [Test]
        public void RealDisk_SubDirectories_HaveTheirEntries()
        {
            Assert.That(_driver!.ListDirectory("/CMDS").Length, Is.EqualTo(71));
            Assert.That(_driver.ListDirectory("/SYS").Length, Is.EqualTo(4));
            Assert.That(_driver.ListDirectory("/DEFS").Length, Is.EqualTo(6));
            Assert.That(_driver.ListDirectory("/cmds").Length, Is.EqualTo(71), "names are compared without regard to case");
        }

        [Test]
        public void RealDisk_WholeTree_Has83FilesAnd199595Bytes()
        {
            long files = 0;
            long bytes = 0;
            CountTree("/", ref files, ref bytes);

            Assert.That(files, Is.EqualTo(83));
            Assert.That(bytes, Is.EqualTo(199595));
        }

        [Test]
        public void RealDisk_ReadFile_CmdsAsm_EqualsTheRawSegment()
        {
            // The file descriptor of /CMDS/asm says one segment: 27 sectors from sector 104, file size 6912 (= 27 x 256).
            byte[] data = _driver!.ReadFile("/CMDS/asm");

            Assert.That(data.Length, Is.EqualTo(6912));
            Assert.That(data, Is.EqualTo(Slice(104 * SectorSize, 6912)));
            Assert.That(_driver.ReadFile("/cmds/ASM"), Is.EqualTo(data), "names are compared without regard to case");
        }

        [Test]
        public void RealDisk_ReadFile_Os9Boot_IsCutAtTheFileSize()
        {
            // One segment of 57 sectors from sector 28 (14592 bytes), file size 14495: the last sector is cut short.
            byte[] data = _driver!.ReadFile("/OS9Boot");

            Assert.That(data.Length, Is.EqualTo(14495));
            Assert.That(data, Is.EqualTo(Slice(28 * SectorSize, 14495)));
            Assert.That(data[0], Is.EqualTo(0x87), "the boot file starts with a module header");
            Assert.That(data[1], Is.EqualTo(0xCD));
        }

        [Test]
        public void RealDisk_ReadFile_Startup_IsTheTextThatNamesTheDragon()
        {
            byte[] data = _driver!.ReadFile("/startup");
            string text = System.Text.Encoding.ASCII.GetString(data);

            Assert.That(text, Does.Contain("Welcome to NitrOS-9 Level 1"));
            Assert.That(text, Does.Contain("Drag"));
        }

        [Test]
        public void RealDisk_FreeSectors_AreTheClearBitsOfTheAllocationMap()
        {
            // 1440 sectors, 970 map bits set (bit 7 of byte 0 is sector 0).
            Assert.That(_driver!.GetBlocksFree(), Is.EqualTo(470));
        }

        [Test]
        public void RealDisk_Dates_AreRead()
        {
            // FD.DAT 68 0B 1C 11 36 = 2004-11-28 17:54, FD.Creat 68 0B 1C = 2004-11-28.
            FileMetadata? boot = _driver!.GetMetadata("/OS9Boot");

            Assert.That(boot, Is.Not.Null);
            Assert.That(boot!.ModifiedTime, Is.EqualTo(new DateTime(2004, 11, 28, 17, 54, 0)));
            Assert.That(boot.CreatedTime, Is.EqualTo(new DateTime(2004, 11, 28)));
        }

        [Test]
        public void RealDisk_Errors_AreTheRightKind()
        {
            Assert.Throws<FileNotFoundException>(() => _driver!.ReadFile("/NOSUCH"));
            Assert.Throws<IOException>(() => _driver!.ReadFile("/CMDS"));
            Assert.Throws<DirectoryNotFoundException>(() => _driver!.ListDirectory("/startup"));
            Assert.That(_driver!.GetMetadata("/NOSUCH"), Is.Null);
            Assert.Throws<UnauthorizedAccessException>(() => _driver!.WriteFile("/X", new byte[1]));
        }

        [Test]
        public void RealDisk_AsAVdkImage_IsAlsoMountedAsOs9()
        {
            // The same sectors behind a 12 byte VDK header (40 cylinders, 2 heads).
            byte[] vdk = new byte[12 + _raw.Length];
            vdk[0] = (byte)'d';
            vdk[1] = (byte)'k';
            vdk[2] = 12;
            vdk[4] = 0x10;
            vdk[5] = 0x10;
            vdk[8] = 40;
            vdk[9] = 2;
            Buffer.BlockCopy(_raw, 0, vdk, 12, _raw.Length);

            DragonVdkDriver driver = new DragonVdkDriver();
            using (MemoryStream stream = new MemoryStream(vdk, false))
            {
                driver.Mount(stream, true);

                Assert.That(driver.FileSystemType, Is.EqualTo("OS-9"));
                Assert.That(driver.ReadFile("/CMDS/asm"), Is.EqualTo(Slice(104 * SectorSize, 6912)));
            }

            driver.Dispose();
        }

        // ---- other real images must not be taken for OS-9 ----

        [TestCase("dsk", "WALKABOU.DSK")]
        [TestCase("dsk", "syzygy.dsk")]
        [TestCase("vdk", "6809TUT1.VDK")]
        [TestCase("vdk", "6809EXPR.VDK")]
        [TestCase("vdk", "HTCHHKER.VDK")]
        [TestCase("vdk", "PLANETFALL.VDK")]
        public void OtherRealImages_AreNotOs9(string folder, string name)
        {
            string path = ImagePath(folder, name);
            if (!File.Exists(path))
            {
                Assert.Ignore("Test image not found: " + name);
            }

            byte[] all = File.ReadAllBytes(path);
            int header = folder == "vdk" ? (all[2] | (all[3] << 8)) : 0;
            GenericBlockDevice device = new GenericBlockDevice(all.Length - header == 161280
                ? new BlockDeviceDescriptor { Name = "35T", BlockSize = 256, AddressMode = BlockAddressMode.LBA, Cylinders = 35, Heads = 1, UniformSectorsPerTrack = 18, TotalBlocks = 630, ExpectedImageSize = 161280 }
                : BlockDeviceDescriptor.DragonVDK_SS40);
            using (MemoryStream stream = new MemoryStream(all, header, all.Length - header, false))
            {
                device.Mount(stream, true);

                Assert.That(new Os9RbfFileSystem().CanHandle(device), Is.False);
            }

            device.Dispose();
        }

        // ---- disks built from the field layout ----

        [Test]
        public void BuiltDisk_BlankImage_IsNotOs9()
        {
            Assert.That(CanHandle(new byte[40 * 18 * SectorSize]), Is.False);
        }

        [Test]
        public void BuiltDisk_WithTwoSegmentsAndADeletedEntry_ReadsAsTheLayoutSays()
        {
            byte[] image = BuildVolume();

            Os9RbfFileSystem fs = Mount(image, out GenericBlockDevice device);

            DirectoryEntry[] root = fs.ListDirectory("/");
            Assert.That(root.Length, Is.EqualTo(1), "the entry whose first byte is zero is a deleted file and is not listed");
            Assert.That(root[0].Name, Is.EqualTo("SPLIT"));
            Assert.That(root[0].Metadata.Size, Is.EqualTo(300));

            // Two segments: 1 sector at 40 (bytes 0..255) and 1 sector at 50 (bytes 256..299 of the file are the first 44 bytes of that sector).
            byte[] data = fs.ReadFile("/split");
            Assert.That(data.Length, Is.EqualTo(300));
            Assert.That(data[0], Is.EqualTo(0xA1));
            Assert.That(data[255], Is.EqualTo(0xA1));
            Assert.That(data[256], Is.EqualTo(0xB2));
            Assert.That(data[299], Is.EqualTo(0xB2));

            device.Dispose();
        }

        [Test]
        public void BuiltDisk_FileSizeLargerThanItsSegments_IsReportedNotCutShort()
        {
            byte[] image = BuildVolume();

            // Raise the size in the descriptor at sector 30 to 600 bytes while the segments hold 512.
            int fd = 30 * SectorSize;
            image[fd + 9] = 0;
            image[fd + 10] = 0;
            image[fd + 11] = 0x02;
            image[fd + 12] = 0x58;

            Os9RbfFileSystem fs = Mount(image, out GenericBlockDevice device);

            Assert.Throws<InvalidDataException>(() => fs.ReadFile("/SPLIT"));

            device.Dispose();
        }

        [Test]
        public void BuiltDisk_SegmentOutsideTheVolume_IsReportedNotRead()
        {
            byte[] image = BuildVolume();

            // First segment of the file starts at sector 5000, past the 720 sectors of the volume.
            int fd = 30 * SectorSize;
            image[fd + 16] = 0;
            image[fd + 17] = 0x13;
            image[fd + 18] = 0x88;

            Os9RbfFileSystem fs = Mount(image, out GenericBlockDevice device);

            Assert.Throws<InvalidDataException>(() => fs.ReadFile("/SPLIT"));

            device.Dispose();
        }

        [Test]
        public void BuiltDisk_FreeSectors_CountClearMapBitsTimesSectorsPerBit()
        {
            byte[] image = BuildVolume();

            Os9RbfFileSystem fs = Mount(image, out GenericBlockDevice device);

            // 720 sectors at 2 sectors per bit are 360 clusters. The map has bits 0 to 3 set and no others, so 356 clusters are free: 712 sectors.
            Assert.That(fs.GetBlocksFree(), Is.EqualTo((720 / 2 - 4) * 2));

            device.Dispose();
        }

        [Test]
        public void BuiltDisk_MapBitOrder_IsMostSignificantBitFirst()
        {
            // 716 sectors, 1 sector per bit, a map of 90 bytes (720 bits, the last 4 are past the end of the volume).
            // The last map byte is 0xF0. Most significant bit first, that is bits 712 to 715: four used sectors inside the volume,
            // so 712 are free. Least significant bit first it would be bits 716 to 719, past the end, and all 716 would be free.
            // (On the real disk both orders count the same number of set bits, so only a built map can tell them apart.)
            byte[] image = BuildVolume();
            PutUInt24(image, 0, 716);
            image[7] = 1;
            image[SectorSize] = 0;
            image[SectorSize + 89] = 0xF0;

            Os9RbfFileSystem fs = Mount(image, out GenericBlockDevice device);

            Assert.That(fs.GetBlocksFree(), Is.EqualTo(712));

            device.Dispose();
        }

        // ---- helpers ----

        private byte[] Slice(int offset, int length)
        {
            byte[] result = new byte[length];
            Buffer.BlockCopy(_raw, offset, result, 0, length);
            return result;
        }

        private void CountTree(string path, ref long files, ref long bytes)
        {
            DirectoryEntry[] entries = _driver!.ListDirectory(path);
            for (int i = 0; i < entries.Length; i++)
            {
                string child = path == "/" ? "/" + entries[i].Name : path + "/" + entries[i].Name;
                if (entries[i].Metadata.IsDirectory)
                {
                    CountTree(child, ref files, ref bytes);
                }
                else
                {
                    files++;
                    bytes += entries[i].Metadata.Size;
                    Assert.That(_driver.ReadFile(child).Length, Is.EqualTo(entries[i].Metadata.Size), child);
                }
            }
        }

        private static bool CanHandle(byte[] image)
        {
            GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.DragonVDK_SS40);
            device.Mount(new MemoryStream(image, false), true);
            bool result = new Os9RbfFileSystem().CanHandle(device);
            device.Dispose();
            return result;
        }

        private static Os9RbfFileSystem Mount(byte[] image, out GenericBlockDevice device)
        {
            device = new GenericBlockDevice(BlockDeviceDescriptor.DragonVDK_SS40);
            device.Mount(new MemoryStream(image, false), true);
            Os9RbfFileSystem fs = new Os9RbfFileSystem();
            Assert.That(fs.CanHandle(device), Is.True);
            fs.Initialize(device);
            return fs;
        }

        // Builds a 720 sector volume (one side, 40 tracks of 18) from the layout: map of 90 bytes at sector 1 with 2 sectors per bit
        // (bits 0 to 3 set), root descriptor at sector 20, root directory at sectors 21 and 22 (size 128: '..', '.', a deleted entry, SPLIT),
        // and the file SPLIT with its descriptor at sector 30, two segments (sector 40 and sector 50, 1 sector each) and size 300.
        private static byte[] BuildVolume()
        {
            byte[] image = new byte[720 * SectorSize];

            // Identification sector.
            PutUInt24(image, 0, 720);
            image[3] = 18;
            image[4] = 0;
            image[5] = 90;
            image[6] = 0;
            image[7] = 2;
            PutUInt24(image, 8, 20);
            image[16] = 0;
            image[17] = 0;
            image[18] = 18;
            byte[] name = System.Text.Encoding.ASCII.GetBytes("TESTDISK");
            Buffer.BlockCopy(name, 0, image, 31, name.Length);
            image[31 + name.Length - 1] |= 0x80;

            // Allocation map (sector 1): bits 0 to 3 set, most significant bit first.
            image[SectorSize] = 0xF0;

            // Root directory descriptor (sector 20): directory, size 128, one segment of 1 sector at sector 21.
            int rootFd = 20 * SectorSize;
            image[rootFd] = 0xBF;
            PutUInt32(image, rootFd + 9, 128);
            PutUInt24(image, rootFd + 16, 21);
            image[rootFd + 19] = 0;
            image[rootFd + 20] = 1;

            // Root directory (sector 21): '..' and '.', a deleted entry, SPLIT.
            int dir = 21 * SectorSize;
            PutName(image, dir, "..");
            PutUInt24(image, dir + 29, 20);
            PutName(image, dir + 32, ".");
            PutUInt24(image, dir + 32 + 29, 20);
            PutName(image, dir + 64, "GONE");
            image[dir + 64] = 0;
            PutUInt24(image, dir + 64 + 29, 31);
            PutName(image, dir + 96, "SPLIT");
            PutUInt24(image, dir + 96 + 29, 30);

            // File descriptor (sector 30): two segments, size 300.
            int fd = 30 * SectorSize;
            image[fd] = 0x03;
            PutUInt32(image, fd + 9, 300);
            PutUInt24(image, fd + 16, 40);
            image[fd + 19] = 0;
            image[fd + 20] = 1;
            PutUInt24(image, fd + 21, 50);
            image[fd + 24] = 0;
            image[fd + 25] = 1;

            for (int i = 0; i < SectorSize; i++)
            {
                image[(40 * SectorSize) + i] = 0xA1;
                image[(50 * SectorSize) + i] = 0xB2;
            }

            return image;
        }

        private static void PutName(byte[] data, int offset, string name)
        {
            for (int i = 0; i < name.Length; i++)
            {
                data[offset + i] = (byte)name[i];
            }

            data[offset + name.Length - 1] |= 0x80;
        }

        private static void PutUInt24(byte[] data, int offset, int value)
        {
            data[offset] = (byte)(value >> 16);
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)value;
        }

        private static void PutUInt32(byte[] data, int offset, int value)
        {
            data[offset] = (byte)(value >> 24);
            data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)value;
        }
    }
}
