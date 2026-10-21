//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.FileSystems;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Tests the write side of <c>DecbFileSystem</c> (create, overwrite, delete, rename) against the raw bytes of the
    /// image and against Toolshed (<c>decb</c>), which is a separate program and not our reader.
    /// </summary>
    /// <remarks>
    /// The expected bytes in the layout tests are written out by hand from the DECB format and from disks made by
    /// Toolshed 2.2. FAT: sector 2 of track 17, one byte per granule, 0xFF free, below 0xC0 the next granule, 0xC0 plus the
    /// number of sectors used (1 to 9) ends a chain. Directory: sectors 3 to 11, 32 byte entries. The Toolshed tests
    /// run <c>decb</c> through WSL (Ubuntu) and are skipped with a reason when that is not there.
    /// Real image: syzygy.dsk from the RetroFS test-image folder, or the folder in RETROIO_DRAGON_TEST_IMAGES.
    /// </remarks>
    [TestFixture]
    public class DecbWriteTests
    {
        private const int SectorSize = 256;
        private const int SectorsPerTrack = 18;
        private const int ImageSize = 35 * SectorsPerTrack * SectorSize;
        private const int FatOffset = ((17 * SectorsPerTrack) + 1) * SectorSize;
        private const int DirOffset = ((17 * SectorsPerTrack) + 2) * SectorSize;
        private const int GranuleBytes = 2304;

        private static BlockDeviceDescriptor Descriptor()
        {
            return new BlockDeviceDescriptor
            {
                Name = "CoCo DSK 35T/SS",
                BlockSize = 256,
                AddressMode = BlockAddressMode.LBA,
                Cylinders = 35,
                Heads = 1,
                UniformSectorsPerTrack = 18,
                TotalBlocks = 630,
                ExpectedImageSize = ImageSize,
                SupportsVariableGeometry = false
            };
        }

        // A freshly formatted disk as Toolshed dskini makes it: 68 free FAT bytes (0xFF), the rest of the FAT sector
        // zero, the whole directory 0xFF.
        private static byte[] BlankImage()
        {
            byte[] image = new byte[ImageSize];
            for (int i = 0; i < 68; i++)
            {
                image[FatOffset + i] = 0xFF;
            }

            for (int i = 0; i < 9 * SectorSize; i++)
            {
                image[DirOffset + i] = 0xFF;
            }

            return image;
        }

        private static DecbFileSystem Open(byte[] image, bool readOnly)
        {
            GenericBlockDevice device = new GenericBlockDevice(Descriptor());
            device.Mount(new MemoryStream(image, writable: true), readOnly);
            DecbFileSystem fs = new DecbFileSystem();
            fs.Initialize(device);
            return fs;
        }

        private static byte[] Pattern(int length, int seed)
        {
            byte[] data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (byte)((i * 7) + seed + (i / 251));
            }

            return data;
        }

        private static int GranuleOffset(int granule)
        {
            int track = granule / 2;
            if (track >= 17)
            {
                track++;
            }

            return ((track * SectorsPerTrack) + ((granule % 2) * 9)) * SectorSize;
        }

        private static int EntryOffset(int index)
        {
            return DirOffset + (index * 32);
        }

        // ---- layout, byte by byte ----

        [TestCase(0, 1, 0xC1, 0)]
        [TestCase(1, 1, 0xC1, 1)]
        [TestCase(255, 1, 0xC1, 255)]
        [TestCase(256, 1, 0xC1, 256)]
        [TestCase(257, 1, 0xC2, 1)]
        [TestCase(2304, 1, 0xC9, 256)]
        [TestCase(2305, 2, 0xC1, 1)]
        [TestCase(4608, 2, 0xC9, 256)]
        [TestCase(4609, 3, 0xC1, 1)]
        public void WriteFile_Sizes_FatChainAndLastSectorBytesAreRight(int size, int granules, int lastFat, int lastBytes)
        {
            byte[] image = BlankImage();
            DecbFileSystem fs = Open(image, false);
            byte[] data = Pattern(size, 5);

            fs.WriteFile("/SIZE.BIN", data);

            // The directory entry, read raw.
            int e = EntryOffset(0);
            Assert.That(image[e + 0], Is.EqualTo((byte)'S'));
            Assert.That(image[e + 3], Is.EqualTo((byte)'E'));
            Assert.That(image[e + 4], Is.EqualTo((byte)' '), "name is space padded");
            Assert.That(image[e + 8], Is.EqualTo((byte)'B'));
            Assert.That(image[e + 10], Is.EqualTo((byte)'N'));
            Assert.That(image[e + 11], Is.EqualTo(2), "BIN is machine language");
            Assert.That(image[e + 12], Is.EqualTo(0), "BIN is binary");
            Assert.That((image[e + 14] << 8) | image[e + 15], Is.EqualTo(lastBytes));
            for (int i = 16; i < 32; i++)
            {
                Assert.That(image[e + i], Is.EqualTo(0), "entry byte " + i);
            }

            // Follow the chain by hand through the raw FAT and compare each granule with the data.
            int granule = image[e + 13];
            int position = 0;
            for (int g = 1; g <= granules; g++)
            {
                int value = image[FatOffset + granule];
                int sectors = g == granules ? lastFat - 0xC0 : 9;
                if (g < granules)
                {
                    Assert.That(value, Is.LessThan(68), "link of granule " + g);
                }
                else
                {
                    Assert.That(value, Is.EqualTo(lastFat), "end of chain");
                }

                int bytes = Math.Min(sectors * SectorSize, Math.Max(0, size - position));
                if (g == granules && size > 0)
                {
                    bytes = size - position;
                }

                for (int i = 0; i < bytes; i++)
                {
                    Assert.That(image[GranuleOffset(granule) + i], Is.EqualTo(data[position + i]), "granule " + g + " byte " + i);
                }

                position += bytes;
                if (g < granules)
                {
                    granule = value;
                }
            }

            Assert.That(position, Is.EqualTo(size));

            // Every other FAT byte stays free, and the FAT sector beyond byte 67 is untouched.
            int used = 0;
            for (int i = 0; i < 68; i++)
            {
                if (image[FatOffset + i] != 0xFF)
                {
                    used++;
                }
            }

            Assert.That(used, Is.EqualTo(granules));
            Assert.That(image[FatOffset + 68], Is.EqualTo(0));

            // The second entry is still the end marker.
            Assert.That(image[EntryOffset(1)], Is.EqualTo(0xFF));

            Assert.That(fs.ReadFile("SIZE.BIN"), Is.EqualTo(data));
            Assert.That(fs.GetMetadata("SIZE.BIN")!.Size, Is.EqualTo(size));
            Assert.That(fs.GetBlocksFree(), Is.EqualTo((68 - granules) * 9));
        }

        [TestCase("A.BAS", 0, 0x00)]
        [TestCase("A.DAT", 1, 0xFF)]
        [TestCase("A.BIN", 2, 0x00)]
        [TestCase("A.TXT", 3, 0xFF)]
        [TestCase("A.XYZ", 2, 0x00)]
        [TestCase("A", 2, 0x00)]
        public void WriteFile_NewFile_TypeAndAsciiFlagComeFromTheExtension(string name, int type, int flag)
        {
            byte[] image = BlankImage();
            DecbFileSystem fs = Open(image, false);

            fs.WriteFile(name, new byte[] { 1, 2, 3 });

            Assert.That(image[EntryOffset(0) + 11], Is.EqualTo(type));
            Assert.That(image[EntryOffset(0) + 12], Is.EqualTo(flag));
        }

        [Test]
        public void WriteFile_WithTypeAndFlag_StoresThemAndRejectsOthers()
        {
            byte[] image = BlankImage();
            DecbFileSystem fs = Open(image, false);

            fs.WriteFile("P.BIN", new byte[] { 9 }, 1, 0xFF);
            Assert.That(image[EntryOffset(0) + 11], Is.EqualTo(1));
            Assert.That(image[EntryOffset(0) + 12], Is.EqualTo(0xFF));

            Assert.Throws<ArgumentOutOfRangeException>(() => fs.WriteFile("Q.BIN", new byte[1], 4, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => fs.WriteFile("Q.BIN", new byte[1], 2, 0x55));
            Assert.That(image[EntryOffset(1)], Is.EqualTo(0xFF), "nothing was written by the rejected calls");
        }

        [Test]
        public void WriteFile_LowerCaseName_IsStoredInUpperCaseAndFoundEitherWay()
        {
            byte[] image = BlankImage();
            DecbFileSystem fs = Open(image, false);

            fs.WriteFile("/hello.bas", new byte[] { 1 });

            Assert.That(image[EntryOffset(0)], Is.EqualTo((byte)'H'));
            Assert.That(fs.ReadFile("HELLO.BAS"), Is.EqualTo(new byte[] { 1 }));
            Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(1));
            Assert.That(fs.ListDirectory("/")[0].Name, Is.EqualTo("HELLO.BAS"));
        }

        [TestCase("")]
        [TestCase(".BIN")]
        [TestCase("TOOLONGNAME.BIN")]
        [TestCase("A.LONG")]
        [TestCase("A B.BIN")]
        [TestCase("A,B.BIN")]
        public void WriteFile_BadName_ThrowsAndWritesNothing(string name)
        {
            byte[] image = BlankImage();
            byte[] before = (byte[])image.Clone();
            DecbFileSystem fs = Open(image, false);

            Assert.Throws<ArgumentException>(() => fs.WriteFile(name, new byte[] { 1 }));
            Assert.That(image, Is.EqualTo(before));
        }

        // ---- overwrite ----

        [Test]
        public void WriteFile_Overwrite_ReplacesDataKeepsTypeAndFreesTheOldGranules()
        {
            byte[] image = BlankImage();
            DecbFileSystem fs = Open(image, false);
            fs.WriteFile("F.BIN", Pattern(5000, 1), 3, 0xFF); // 3 granules, text type
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(65 * 9));

            byte[] smaller = Pattern(100, 9);
            fs.WriteFile("F.BIN", smaller);

            Assert.That(fs.ReadFile("F.BIN"), Is.EqualTo(smaller));
            Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(1), "still one file");
            Assert.That(image[EntryOffset(0) + 11], Is.EqualTo(3), "type kept");
            Assert.That(image[EntryOffset(0) + 12], Is.EqualTo(0xFF), "ASCII flag kept");
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(67 * 9), "two granules came back");

            byte[] bigger = Pattern(9000, 2);
            fs.WriteFile("F.BIN", bigger);
            Assert.That(fs.ReadFile("F.BIN"), Is.EqualTo(bigger));
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(64 * 9));
        }

        [Test]
        public void WriteFile_Overwrite_WhenAnEarlierSlotIsEmpty_MovesTheEntryAndDeletesTheOldOne()
        {
            // Toolshed overwrites by kill then create, and create takes the first empty slot.
            byte[] image = BlankImage();
            DecbFileSystem fs = Open(image, false);
            fs.WriteFile("A.BIN", Pattern(10, 1));
            fs.WriteFile("B.BIN", Pattern(20, 2));
            fs.DeleteFile("A.BIN");

            byte[] newData = Pattern(30, 3);
            fs.WriteFile("B.BIN", newData);

            Assert.That(image[EntryOffset(0)], Is.EqualTo((byte)'B'), "moved into the empty slot 0");
            Assert.That(image[EntryOffset(1)], Is.EqualTo(0x00), "the old slot is deleted");
            Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(1));
            Assert.That(fs.ReadFile("B.BIN"), Is.EqualTo(newData));
        }

        [Test]
        public void WriteFile_OnABlankDisk_FirstFileStartsAtGranule34_NextGranuleFollows()
        {
            // Pins the port of Toolshed find_free_granule (first granule searched from 34, each next one from the one
            // before it). The Toolshed 2.2 program installed here starts at 35; both are valid DECB, the format fixes no start.
            byte[] image = BlankImage();
            DecbFileSystem fs = Open(image, false);

            fs.WriteFile("A.BIN", Pattern(3000, 1));

            Assert.That(image[EntryOffset(0) + 13], Is.EqualTo(34));
            Assert.That(image[FatOffset + 34], Is.EqualTo(35));
            Assert.That(image[FatOffset + 35], Is.EqualTo(0xC0 + 3), "696 bytes left in the second granule: 3 sectors");
        }

        [Test]
        public void WriteFile_Overwrite_OfAFileThatFillsTheDisk_Works()
        {
            // The old granules count as free for the new data, as in Toolshed (kill, then create).
            byte[] image = BlankImage();
            DecbFileSystem fs = Open(image, false);
            byte[] full = Pattern(68 * GranuleBytes, 1);
            fs.WriteFile("BIG.BIN", full);
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(0));

            byte[] other = Pattern(68 * GranuleBytes, 2);
            fs.WriteFile("BIG.BIN", other);

            Assert.That(fs.ReadFile("BIG.BIN"), Is.EqualTo(other));
        }

        [Test]
        public void WriteFile_AllSixtyEightGranulesCanBeUsed()
        {
            byte[] image = BlankImage();
            DecbFileSystem fs = Open(image, false);
            byte[] full = Pattern((68 * GranuleBytes) - 10, 4);

            fs.WriteFile("ALL.BIN", full);

            Assert.That(fs.GetBlocksFree(), Is.EqualTo(0));
            Assert.That(fs.ReadFile("ALL.BIN"), Is.EqualTo(full));
            // The granule search must visit every granule once: 68 distinct granules in one chain.
            bool[] seen = new bool[68];
            int g = image[EntryOffset(0) + 13];
            int count = 0;
            while (g < 68)
            {
                Assert.That(seen[g], Is.False, "granule " + g + " twice");
                seen[g] = true;
                count++;
                g = image[FatOffset + g];
            }

            Assert.That(count, Is.EqualTo(68));
        }

        // ---- full disk and full directory ----

        [Test]
        public void WriteFile_DiskFull_ThrowsIOExceptionAndChangesNothing()
        {
            byte[] image = BlankImage();
            DecbFileSystem fs = Open(image, false);
            fs.WriteFile("A.BIN", Pattern(60 * GranuleBytes, 1)); // 8 granules left
            byte[] before = (byte[])image.Clone();

            Assert.Throws<IOException>(() => fs.WriteFile("B.BIN", Pattern((8 * GranuleBytes) + 1, 2)));

            Assert.That(image, Is.EqualTo(before));
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(8 * 9));
            fs.WriteFile("B.BIN", Pattern(8 * GranuleBytes, 2)); // exactly fits
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(0));
        }

        [Test]
        public void WriteFile_DiskFull_OnOverwriteKeepsTheOldFile()
        {
            byte[] image = BlankImage();
            DecbFileSystem fs = Open(image, false);
            byte[] old = Pattern(1000, 3);
            fs.WriteFile("OLD.BIN", old);
            fs.WriteFile("FILL.BIN", Pattern(66 * GranuleBytes, 1)); // all but 1 granule used
            byte[] before = (byte[])image.Clone();

            Assert.Throws<IOException>(() => fs.WriteFile("OLD.BIN", Pattern(3 * GranuleBytes, 4)));

            Assert.That(image, Is.EqualTo(before));
            Assert.That(fs.ReadFile("OLD.BIN"), Is.EqualTo(old));
        }

        [Test]
        public void WriteFile_DirectoryFull_At72Entries_ThrowsAndAFreedSlotIsReused()
        {
            byte[] image = BlankImage();
            DecbFileSystem fs = Open(image, false);
            // A disk has 68 granules and every file owns one, so 72 real files cannot exist. The 72 entries are made
            // raw here, all pointing at granule 0 (a one-sector file in the FAT), only to fill the directory.
            image[FatOffset + 0] = 0xC1;
            for (int i = 0; i < 72; i++)
            {
                int e = EntryOffset(i);
                string label = ("F" + i).PadRight(8) + "DAT";
                for (int c = 0; c < 11; c++)
                {
                    image[e + c] = (byte)label[c];
                }

                image[e + 11] = 1;
                image[e + 12] = 0xFF;
                image[e + 13] = 0;
                image[e + 14] = 0;
                image[e + 15] = 1;
                for (int c = 16; c < 32; c++)
                {
                    image[e + c] = 0;
                }
            }

            fs = Open(image, false);
            Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(72));
            byte[] before = (byte[])image.Clone();
            Assert.Throws<IOException>(() => fs.WriteFile("EXTRA.DAT", new byte[] { 1 }));
            Assert.That(image, Is.EqualTo(before));

            image[EntryOffset(40)] = 0x00; // deleted entry
            fs = Open(image, false);
            fs.WriteFile("EXTRA.DAT", new byte[] { 77 });
            Assert.That(image[EntryOffset(40)], Is.EqualTo((byte)'E'), "the deleted slot 40 was reused");
            Assert.That(fs.ReadFile("EXTRA.DAT"), Is.EqualTo(new byte[] { 77 }));
            Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(72));
        }

        // ---- delete ----

        [Test]
        public void DeleteFile_ClearsFirstByteFreesChainAndLeavesTheRestOfTheEntry()
        {
            byte[] image = BlankImage();
            DecbFileSystem fs = Open(image, false);
            fs.WriteFile("KEEP.BIN", Pattern(300, 1));
            fs.WriteFile("GONE.BIN", Pattern(5000, 2));
            fs.WriteFile("LAST.BIN", Pattern(10, 3));
            int goneGranule = image[EntryOffset(1) + 13];

            fs.DeleteFile("GONE.BIN");

            Assert.That(image[EntryOffset(1)], Is.EqualTo(0x00));
            Assert.That(image[EntryOffset(1) + 1], Is.EqualTo((byte)'O'), "rest of the name stays, as Toolshed kill leaves it");
            Assert.That(image[EntryOffset(1) + 13], Is.EqualTo(goneGranule));
            Assert.That(image[EntryOffset(2)], Is.EqualTo((byte)'L'), "the entries after it still count");
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(66 * 9));
            Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(2));
            Assert.Throws<System.IO.FileNotFoundException>(() => fs.ReadFile("GONE.BIN"));
            Assert.That(fs.ReadFile("KEEP.BIN"), Is.EqualTo(Pattern(300, 1)));
            Assert.That(fs.ReadFile("LAST.BIN"), Is.EqualTo(Pattern(10, 3)));

            int freeBytes = 0;
            for (int i = 0; i < 68; i++)
            {
                if (image[FatOffset + i] == 0xFF)
                {
                    freeBytes++;
                }
            }

            Assert.That(freeBytes, Is.EqualTo(66));
        }

        [Test]
        public void DeleteFile_Missing_ThrowsFileNotFound()
        {
            DecbFileSystem fs = Open(BlankImage(), false);
            Assert.Throws<System.IO.FileNotFoundException>(() => fs.DeleteFile("NOPE.BIN"));
        }

        // ---- rename ----

        [Test]
        public void Rename_ChangesOnlyTheNameBytes()
        {
            byte[] image = BlankImage();
            DecbFileSystem fs = Open(image, false);
            byte[] data = Pattern(3000, 6);
            fs.WriteFile("OLD.BAS", data);
            byte[] before = (byte[])image.Clone();

            fs.Rename("OLD.BAS", "/NEWER.TXT");

            int e = EntryOffset(0);
            for (int i = 0; i < DirOffset + (9 * SectorSize); i++)
            {
                if (i >= e && i < e + 11)
                {
                    continue;
                }

                Assert.That(image[i], Is.EqualTo(before[i]), "byte " + i + " changed");
            }

            Assert.That(System.Text.Encoding.ASCII.GetString(image, e, 11), Is.EqualTo("NEWER   TXT"));
            Assert.That(fs.ReadFile("NEWER.TXT"), Is.EqualTo(data));
            Assert.Throws<System.IO.FileNotFoundException>(() => fs.ReadFile("OLD.BAS"));
        }

        [Test]
        public void Rename_ToAnExistingName_ThrowsIOExceptionAndChangesNothing()
        {
            byte[] image = BlankImage();
            DecbFileSystem fs = Open(image, false);
            fs.WriteFile("ONE.BIN", new byte[] { 1 });
            fs.WriteFile("TWO.BIN", new byte[] { 2 });
            byte[] before = (byte[])image.Clone();

            Assert.Throws<IOException>(() => fs.Rename("ONE.BIN", "TWO.BIN"));
            Assert.That(image, Is.EqualTo(before));
        }

        [Test]
        public void Rename_Missing_ThrowsFileNotFound()
        {
            DecbFileSystem fs = Open(BlankImage(), false);
            Assert.Throws<System.IO.FileNotFoundException>(() => fs.Rename("NOPE.BIN", "OTHER.BIN"));
        }

        // ---- read only ----

        [Test]
        public void ReadOnlyDevice_AllWriteMembersThrowIOExceptionAndTheImageIsUnchanged()
        {
            byte[] image = BlankImage();
            Open(image, false).WriteFile("A.BIN", new byte[] { 1 });
            byte[] before = (byte[])image.Clone();
            DecbFileSystem fs = Open(image, true);

            Assert.Throws<IOException>(() => fs.WriteFile("B.BIN", new byte[] { 2 }));
            Assert.Throws<IOException>(() => fs.DeleteFile("A.BIN"));
            Assert.Throws<IOException>(() => fs.Rename("A.BIN", "C.BIN"));
            Assert.That(image, Is.EqualTo(before));
        }

        [Test]
        public void NotInitialized_WriteMembersThrowInvalidOperation()
        {
            DecbFileSystem fs = new DecbFileSystem();
            Assert.Throws<InvalidOperationException>(() => fs.WriteFile("A", new byte[1]));
            Assert.Throws<InvalidOperationException>(() => fs.DeleteFile("A"));
            Assert.Throws<InvalidOperationException>(() => fs.Rename("A", "B"));
        }

        // ---- a second mount sees the same disk ----

        [Test]
        public void ANewMountReadsWhatWasWritten()
        {
            byte[] image = BlankImage();
            DecbFileSystem first = Open(image, false);
            byte[] a = Pattern(7000, 1);
            byte[] b = Pattern(33, 2);
            first.WriteFile("A.BIN", a);
            first.WriteFile("B.TXT", b);
            first.DeleteFile("A.BIN");
            first.Rename("B.TXT", "C.TXT");

            DecbFileSystem second = Open(image, true);

            Assert.That(second.ListDirectory("/").Length, Is.EqualTo(1));
            Assert.That(second.ReadFile("C.TXT"), Is.EqualTo(b));
            Assert.That(second.GetBlocksFree(), Is.EqualTo(67 * 9));
        }

        // ---- a real disk ----

        private static string? RealImage(string name)
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable("RETROIO_DRAGON_TEST_IMAGES");
            string folder = !string.IsNullOrEmpty(fromEnvironment)
                ? fromEnvironment
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "RetroFS", "test-images");
            string path = Path.Combine(folder, "dsk", name);
            return File.Exists(path) ? path : null;
        }

        [Test]
        public void RealDisk_Syzygy_WriteDeleteRename_LeaveTheTwoOriginalFilesIntact()
        {
            string? path = RealImage("syzygy.dsk");
            if (path == null)
            {
                Assert.Ignore("syzygy.dsk not found");
            }

            byte[] image = File.ReadAllBytes(path!);
            DecbFileSystem fs = Open(image, false);
            byte[] ten = fs.ReadFile("SYZYGY10.BIN");
            byte[] thirty = fs.ReadFile("SYZYGY30.BIN");
            int freeBefore = fs.GetBlocksFree();

            byte[] extra = Pattern(10000, 8);
            fs.WriteFile("EXTRA.BIN", extra);
            Assert.That(fs.ReadFile("EXTRA.BIN"), Is.EqualTo(extra));
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(freeBefore - (5 * 9)));
            Assert.That(fs.ReadFile("SYZYGY10.BIN"), Is.EqualTo(ten));
            Assert.That(fs.ReadFile("SYZYGY30.BIN"), Is.EqualTo(thirty));

            fs.Rename("EXTRA.BIN", "MORE.BIN");
            fs.DeleteFile("MORE.BIN");
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(freeBefore));
            Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(2));
            Assert.That(fs.ReadFile("SYZYGY30.BIN"), Is.EqualTo(thirty));

            // Deleting one of the real files frees exactly its 13 granules and the other one still reads.
            fs.DeleteFile("SYZYGY30.BIN");
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(freeBefore + (13 * 9)));
            Assert.That(fs.ReadFile("SYZYGY10.BIN"), Is.EqualTo(ten));
        }

        // ---- Toolshed (decb in WSL) as the independent reader and writer ----

        private static string ToWslPath(string windowsPath)
        {
            char drive = char.ToLowerInvariant(windowsPath[0]);
            return "/mnt/" + drive + windowsPath.Substring(2).Replace('\\', '/');
        }

        // Runs decb in WSL Ubuntu. Returns the exit code, or -999 when WSL itself cannot start.
        private static int Decb(out string output, params string[] arguments)
        {
            output = string.Empty;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return -999;
            }

            try
            {
                ProcessStartInfo info = new ProcessStartInfo("wsl.exe");
                info.ArgumentList.Add("-d");
                info.ArgumentList.Add("Ubuntu");
                info.ArgumentList.Add("--");
                info.ArgumentList.Add("decb");
                for (int i = 0; i < arguments.Length; i++)
                {
                    info.ArgumentList.Add(arguments[i]);
                }

                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                using (Process? process = Process.Start(info))
                {
                    if (process == null)
                    {
                        return -999;
                    }

                    string standardOutput = process.StandardOutput.ReadToEnd();
                    string standardError = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    output = standardOutput + standardError;
                    return process.ExitCode;
                }
            }
            catch (Exception)
            {
                return -999;
            }
        }

        private static void RequireToolshed()
        {
            int code = Decb(out string output);
            if (code == -999 || output.IndexOf("Toolshed", StringComparison.Ordinal) < 0)
            {
                Assert.Ignore("Toolshed decb is not available through WSL Ubuntu on this machine");
            }
        }

        private static string NewTempFolder()
        {
            string folder = Path.Combine(Path.GetTempPath(), "decbwrite-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string FileLine(string fstat, string label)
        {
            string[] lines = fstat.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.TrimStart().StartsWith(label, StringComparison.Ordinal))
                {
                    int colon = line.IndexOf(':');
                    return line.Substring(colon + 1).Trim();
                }
            }

            return "(missing " + label + ")";
        }

        [Test]
        public void Toolshed_ReadsWhatWeWrite_DataSizeTypeAndFlag()
        {
            RequireToolshed();
            string folder = NewTempFolder();
            try
            {
                byte[] image = BlankImage();
                DecbFileSystem fs = Open(image, false);
                int[] sizes = { 0, 1, 255, 256, 257, 2304, 2305, 4608, 20000 };
                for (int i = 0; i < sizes.Length; i++)
                {
                    fs.WriteFile("S" + sizes[i] + ".BIN", Pattern(sizes[i], i));
                }

                fs.WriteFile("PROG.BAS", Pattern(500, 40));
                fs.WriteFile("DATA.DAT", Pattern(70, 41));
                fs.WriteFile("TEXT.TXT", Pattern(3000, 42), 3, 0xFF);

                string imagePath = Path.Combine(folder, "w.dsk");
                File.WriteAllBytes(imagePath, image);
                string wslImage = ToWslPath(imagePath);

                for (int i = 0; i < sizes.Length; i++)
                {
                    string name = "S" + sizes[i] + ".BIN";
                    string outPath = Path.Combine(folder, "out" + i + ".bin");
                    Assert.That(Decb(out string copyOutput, "copy", "-b", wslImage + "," + name, ToWslPath(outPath)), Is.EqualTo(0), copyOutput);
                    Assert.That(File.ReadAllBytes(outPath), Is.EqualTo(Pattern(sizes[i], i)), "Toolshed copy out of " + name);

                    Assert.That(Decb(out string stat, "fstat", wslImage + "," + name), Is.EqualTo(0), stat);
                    Assert.That(FileLine(stat, "File size"), Is.EqualTo(sizes[i] + " bytes"), name);
                    Assert.That(FileLine(stat, "File type"), Is.EqualTo("M/L Program"));
                    Assert.That(FileLine(stat, "Data type"), Is.EqualTo("Binary"));
                }

                Decb(out string statBasic, "fstat", wslImage + ",PROG.BAS");
                Assert.That(FileLine(statBasic, "File type"), Is.EqualTo("BASIC Program"));
                Assert.That(FileLine(statBasic, "Data type"), Is.EqualTo("Binary"));
                Decb(out string statData, "fstat", wslImage + ",DATA.DAT");
                Assert.That(FileLine(statData, "File type"), Is.EqualTo("Data"));
                Assert.That(FileLine(statData, "Data type"), Is.EqualTo("ASCII"));
                Decb(out string statText, "fstat", wslImage + ",TEXT.TXT");
                Assert.That(FileLine(statText, "File type"), Is.EqualTo("Text Editor Source"));
                Assert.That(FileLine(statText, "Data type"), Is.EqualTo("ASCII"));

                Decb(out string freeText, "free", wslImage);
                Assert.That(freeText, Does.Contain("Free granules: " + (fs.GetBlocksFree() / 9) + " "));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void Toolshed_SeesOverwriteDeleteAndRename()
        {
            RequireToolshed();
            string folder = NewTempFolder();
            try
            {
                byte[] image = BlankImage();
                DecbFileSystem fs = Open(image, false);
                fs.WriteFile("KEEP.BIN", Pattern(4000, 1));
                fs.WriteFile("OVER.BIN", Pattern(9000, 2));
                fs.WriteFile("GONE.BIN", Pattern(300, 3));
                fs.WriteFile("OLDNAME.BIN", Pattern(777, 4));
                fs.WriteFile("OVER.BIN", Pattern(123, 5));
                fs.DeleteFile("GONE.BIN");
                fs.Rename("OLDNAME.BIN", "NEWNAME.BIN");

                string imagePath = Path.Combine(folder, "w.dsk");
                File.WriteAllBytes(imagePath, image);
                string wslImage = ToWslPath(imagePath);

                Assert.That(Decb(out string dir, "dir", wslImage), Is.EqualTo(0), dir);
                Assert.That(dir, Does.Contain("KEEP"));
                Assert.That(dir, Does.Contain("OVER"));
                Assert.That(dir, Does.Contain("NEWNAME"));
                Assert.That(dir, Does.Not.Contain("GONE"));
                Assert.That(dir, Does.Not.Contain("OLDNAME"));

                string overOut = Path.Combine(folder, "over.bin");
                Assert.That(Decb(out string copyOutput, "copy", "-b", wslImage + ",OVER.BIN", ToWslPath(overOut)), Is.EqualTo(0), copyOutput);
                Assert.That(File.ReadAllBytes(overOut), Is.EqualTo(Pattern(123, 5)));

                string newOut = Path.Combine(folder, "new.bin");
                Assert.That(Decb(out copyOutput, "copy", "-b", wslImage + ",NEWNAME.BIN", ToWslPath(newOut)), Is.EqualTo(0), copyOutput);
                Assert.That(File.ReadAllBytes(newOut), Is.EqualTo(Pattern(777, 4)));

                Decb(out string freeText, "free", wslImage);
                Assert.That(freeText, Does.Contain("Free granules: " + (fs.GetBlocksFree() / 9) + " "));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void WeReadAndChangeADiskThatToolshedMade()
        {
            RequireToolshed();
            string folder = NewTempFolder();
            try
            {
                string imagePath = Path.Combine(folder, "t.dsk");
                string wslImage = ToWslPath(imagePath);
                Assert.That(Decb(out string text, "dskini", wslImage), Is.EqualTo(0), text);

                int[] sizes = { 0, 1, 256, 257, 2305, 12345 };
                for (int i = 0; i < sizes.Length; i++)
                {
                    string source = Path.Combine(folder, "in" + i + ".bin");
                    File.WriteAllBytes(source, Pattern(sizes[i], 10 + i));
                    Assert.That(Decb(out text, "copy", "-2", "-b", ToWslPath(source), wslImage + ",T" + i + ".BIN"), Is.EqualTo(0), text);
                }

                string textSource = Path.Combine(folder, "in.txt");
                File.WriteAllBytes(textSource, new byte[] { 72, 73, 13 });
                Assert.That(Decb(out text, "copy", "-3", "-a", ToWslPath(textSource), wslImage + ",NOTE.TXT"), Is.EqualTo(0), text);

                byte[] image = File.ReadAllBytes(imagePath);
                DecbFileSystem fs = Open(image, false);
                Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(sizes.Length + 1));
                for (int i = 0; i < sizes.Length; i++)
                {
                    Assert.That(fs.ReadFile("T" + i + ".BIN"), Is.EqualTo(Pattern(sizes[i], 10 + i)), "T" + i);
                }

                Assert.That(fs.ReadFile("NOTE.TXT"), Is.EqualTo(new byte[] { 72, 73, 13 }));
                Assert.That(image[EntryOffset(sizes.Length) + 11], Is.EqualTo(3));
                Assert.That(image[EntryOffset(sizes.Length) + 12], Is.EqualTo(0xFF));

                // Change the disk with our code on top of the Toolshed one, then let Toolshed read it again.
                fs.DeleteFile("T2.BIN");
                fs.WriteFile("NEW.BIN", Pattern(5555, 99));
                fs.Rename("T1.BIN", "RENAMED.BIN");
                File.WriteAllBytes(imagePath, image);

                string back = Path.Combine(folder, "back.bin");
                Assert.That(Decb(out text, "copy", "-b", wslImage + ",NEW.BIN", ToWslPath(back)), Is.EqualTo(0), text);
                Assert.That(File.ReadAllBytes(back), Is.EqualTo(Pattern(5555, 99)));
                Assert.That(Decb(out text, "copy", "-b", wslImage + ",RENAMED.BIN", ToWslPath(back)), Is.EqualTo(0), text);
                Assert.That(File.ReadAllBytes(back), Is.EqualTo(Pattern(1, 11)));
                Assert.That(Decb(out text, "copy", "-b", wslImage + ",T5.BIN", ToWslPath(back)), Is.EqualTo(0), text);
                Assert.That(File.ReadAllBytes(back), Is.EqualTo(Pattern(12345, 15)));
                Assert.That(Decb(out string dir, "dir", wslImage), Is.EqualTo(0), dir);
                Assert.That(dir, Does.Not.Contain("T2 "));
                Decb(out string freeText, "free", wslImage);
                Assert.That(freeText, Does.Contain("Free granules: " + (fs.GetBlocksFree() / 9) + " "));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }
}
