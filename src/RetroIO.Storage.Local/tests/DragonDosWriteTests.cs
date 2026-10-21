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
    /// Tests the read and write side of <c>DragonDosFileSystem</c> against the raw bytes of the image.
    /// </summary>
    /// <remarks>
    /// The expected bytes are written out by hand from the DragonDOS 1.2 source (dosdefs.asm, ddos12.asm: FindFSNinU, ExtendFile, DOSDeleteFile,
    /// DOSRename, FindEmptyDir, DOSSyncDir), see the remarks of <see cref="DragonDosFileSystem"/> and <see cref="DragonDosTestDisk"/>. Every
    /// test works on a disk built in memory. The same disks were also read by the real DragonDOS ROM in the XRoar emulator (see the report in
    /// the commit), which is not part of the unit tests because it needs the ROM files.
    /// </remarks>
    [TestFixture]
    public class DragonDosWriteTests
    {
        private const int Tracks = 40;

        private static byte[] Flushed(GenericBlockDevice device, byte[] image)
        {
            device.Flush();
            return image;
        }

        private static void AssertBackupEqualsPrimary(byte[] image, int heads)
        {
            Assert.That(DragonDosTestDisk.Track(image, heads, 16), Is.EqualTo(DragonDosTestDisk.Track(image, heads, 20)),
                "the backup directory track 16 must be a copy of track 20");
        }

        // ------------------------------------------------------------------ blank disk

        [Test]
        public void BlankDisk_ListsNothing_AndHasTheFreeSectorsOfTheFormat()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out _);

            Assert.That(fs.IsDragonDosVolume, Is.True);
            Assert.That(fs.ListDirectory("/"), Is.Empty);
            Assert.That(fs.GetBlocksFree(), Is.EqualTo((40 * 18) - 36), "720 sectors minus the 18 of track 20 and the 18 of backup track 16");
        }

        // ------------------------------------------------------------------ create and read back

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(255)]
        [TestCase(256)]
        [TestCase(257)]
        [TestCase(512)]
        [TestCase(513)]
        [TestCase(10000)]
        public void WriteFile_ThenReadFile_ReturnsTheSameBytes(int length)
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out GenericBlockDevice device);
            byte[] data = DragonDosTestDisk.Pattern(length, 7);

            fs.WriteFile("/DATA.BIN", data);

            Assert.That(fs.ReadFile("DATA.BIN"), Is.EqualTo(data));
            Assert.That(fs.GetMetadata("DATA.BIN")!.Size, Is.EqualTo(length));

            // Read it again through a new file system on the flushed bytes.
            device.Flush();
            DragonDosFileSystem again = DragonDosTestDisk.Mount(image, Tracks, 1, out _);
            Assert.That(again.ReadFile("/DATA.BIN"), Is.EqualTo(data));
            AssertBackupEqualsPrimary(image, 1);
        }

        [Test]
        public void WriteFile_FillsTheDirectoryEntryAsTheFormatSays()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out GenericBlockDevice device);

            fs.WriteFile("hello.bin", DragonDosTestDisk.Pattern(513, 1));
            Flushed(device, image);

            byte[] entry = DragonDosTestDisk.Entry(image, 1, 0, false);
            Assert.That(entry[0], Is.EqualTo(0), "attribute byte of a new file");
            Assert.That(entry[1], Is.EqualTo((byte)'H'));
            Assert.That(entry[5], Is.EqualTo((byte)'O'));
            Assert.That(entry[6], Is.EqualTo(0), "the name is padded with zero bytes");
            Assert.That(entry[8], Is.EqualTo(0));
            Assert.That(entry[9], Is.EqualTo((byte)'B'));
            Assert.That(entry[11], Is.EqualTo((byte)'N'));
            Assert.That(entry[14], Is.EqualTo(3), "first extent: 3 sectors for 513 bytes");
            Assert.That(entry[24], Is.EqualTo(1), "one byte in the last sector");
            int lsn = (entry[12] << 8) | entry[13];
            Assert.That(lsn, Is.GreaterThanOrEqualTo(378), "the data goes after the directory track (LSN 360 to 377)");
            Assert.That(DragonDosTestDisk.Entry(image, 1, 1, false)[0], Is.EqualTo(0x89), "the next entry is still a free end of directory entry");
        }

        [TestCase(512, 0)]
        [TestCase(1, 1)]
        [TestCase(255, 255)]
        [TestCase(256, 0)]
        public void WriteFile_BytesInLastSector_ZeroMeansFullSector(int length, int expected)
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out GenericBlockDevice device);

            fs.WriteFile("A", DragonDosTestDisk.Pattern(length, 3));
            Flushed(device, image);

            Assert.That(DragonDosTestDisk.Entry(image, 1, 0, false)[24], Is.EqualTo(expected));
            Assert.That(fs.ReadFile("A").Length, Is.EqualTo(length));
        }

        [Test]
        public void WriteFile_MarksTheSectorsUsedInTheBam_AndTheFreeCountFollows()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out GenericBlockDevice device);
            int before = fs.GetBlocksFree();

            fs.WriteFile("A.BIN", DragonDosTestDisk.Pattern(5 * 256, 0));
            Flushed(device, image);

            byte[] entry = DragonDosTestDisk.Entry(image, 1, 0, false);
            int lsn = (entry[12] << 8) | entry[13];
            for (int s = 0; s < 5; s++)
            {
                Assert.That(DragonDosTestDisk.IsFreeInImage(image, 1, lsn + s), Is.False, "LSN " + (lsn + s) + " must be used");
            }

            Assert.That(DragonDosTestDisk.IsFreeInImage(image, 1, lsn + 5), Is.True);
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(before - 5));
            AssertBackupEqualsPrimary(image, 1);
        }

        [Test]
        public void WriteFile_TwoFiles_DoNotOverlap()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out _);
            byte[] a = DragonDosTestDisk.Pattern(3000, 1);
            byte[] b = DragonDosTestDisk.Pattern(5000, 2);

            fs.WriteFile("A.BIN", a);
            fs.WriteFile("B.BIN", b);

            Assert.That(fs.ReadFile("A.BIN"), Is.EqualTo(a));
            Assert.That(fs.ReadFile("B.BIN"), Is.EqualTo(b));
            Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(2));
        }

        // ------------------------------------------------------------------ overwrite

        [Test]
        public void WriteFile_Overwrite_ReplacesTheContent_AndFreesTheOldSectors()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out GenericBlockDevice device);
            int blank = fs.GetBlocksFree();

            fs.WriteFile("A.BIN", DragonDosTestDisk.Pattern(10 * 256, 1));
            byte[] small = DragonDosTestDisk.Pattern(300, 9);
            fs.WriteFile("A.BIN", small);
            Flushed(device, image);

            Assert.That(fs.ReadFile("A.BIN"), Is.EqualTo(small));
            Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(1), "still one file");
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(blank - 2), "only the two sectors of the new content are used");
            AssertBackupEqualsPrimary(image, 1);
        }

        [Test]
        public void WriteFile_OverwriteWithLargerFile_Works()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out _);

            fs.WriteFile("A.BIN", DragonDosTestDisk.Pattern(100, 1));
            byte[] big = DragonDosTestDisk.Pattern(20000, 2);
            fs.WriteFile("A.BIN", big);

            Assert.That(fs.ReadFile("A.BIN"), Is.EqualTo(big));
        }

        // ------------------------------------------------------------------ delete

        [Test]
        public void DeleteFile_MarksTheEntry81_SetsTheBamBits_AndKeepsTheName()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out GenericBlockDevice device);
            int blank = fs.GetBlocksFree();
            fs.WriteFile("GONE.TXT", DragonDosTestDisk.Pattern(900, 4));
            fs.WriteFile("KEEP.TXT", DragonDosTestDisk.Pattern(100, 5));
            Flushed(device, image);
            byte[] first = DragonDosTestDisk.Entry(image, 1, 0, false);
            int lsn = (first[12] << 8) | first[13];

            fs.DeleteFile("GONE.TXT");
            Flushed(device, image);

            byte[] entry = DragonDosTestDisk.Entry(image, 1, 0, false);
            Assert.That(entry[0], Is.EqualTo(0x81), "deleted entry: bit 7 and the continuation bit (AttrAfterDel)");
            Assert.That(entry[1], Is.EqualTo((byte)'G'), "the name stays, as the DOS leaves it");
            for (int s = 0; s < 4; s++)
            {
                Assert.That(DragonDosTestDisk.IsFreeInImage(image, 1, lsn + s), Is.True, "LSN " + (lsn + s) + " must be free again");
            }

            Assert.That(fs.GetBlocksFree(), Is.EqualTo(blank - 1), "only KEEP.TXT (1 sector) is left");
            Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(1));
            Assert.That(() => fs.ReadFile("GONE.TXT"), Throws.InstanceOf<FileNotFoundException>());
            AssertBackupEqualsPrimary(image, 1);
        }

        [Test]
        public void DeleteFile_ThenWrite_ReusesTheDeletedEntry()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out GenericBlockDevice device);
            fs.WriteFile("A", new byte[] { 1 });
            fs.WriteFile("B", new byte[] { 2 });

            fs.DeleteFile("A");
            fs.WriteFile("C", new byte[] { 3 });
            Flushed(device, image);

            Assert.That(DragonDosTestDisk.Entry(image, 1, 0, false)[1], Is.EqualTo((byte)'C'), "C takes the deleted slot 0");
            Assert.That(DragonDosTestDisk.Entry(image, 1, 2, false)[0], Is.EqualTo(0x89));
            Assert.That(fs.ReadFile("B"), Is.EqualTo(new byte[] { 2 }));
            Assert.That(fs.ReadFile("C"), Is.EqualTo(new byte[] { 3 }));
        }

        [Test]
        public void DeleteFile_Missing_ThrowsFileNotFound()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out _);

            Assert.That(() => fs.DeleteFile("NOPE.BIN"), Throws.InstanceOf<FileNotFoundException>());
        }

        // ------------------------------------------------------------------ rename

        [Test]
        public void Rename_ChangesOnlyTheNameBytes()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out GenericBlockDevice device);
            byte[] data = DragonDosTestDisk.Pattern(700, 6);
            fs.WriteFile("OLD.BAS", data);
            Flushed(device, image);
            byte[] before = DragonDosTestDisk.Entry(image, 1, 0, false);

            fs.Rename("OLD.BAS", "/new.bin");
            Flushed(device, image);

            byte[] after = DragonDosTestDisk.Entry(image, 1, 0, false);
            Assert.That(after[0], Is.EqualTo(before[0]));
            Assert.That(after[1], Is.EqualTo((byte)'N'));
            Assert.That(after[9], Is.EqualTo((byte)'B'));
            for (int i = 12; i < 25; i++)
            {
                Assert.That(after[i], Is.EqualTo(before[i]), "byte " + i + " (extents, last bytes) must not change");
            }

            Assert.That(fs.ReadFile("NEW.BIN"), Is.EqualTo(data));
            Assert.That(() => fs.ReadFile("OLD.BAS"), Throws.InstanceOf<FileNotFoundException>());
            AssertBackupEqualsPrimary(image, 1);
        }

        [Test]
        public void Rename_ToAnExistingName_ThrowsIOException_AndChangesNothing()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out GenericBlockDevice device);
            fs.WriteFile("A.BIN", new byte[] { 1 });
            fs.WriteFile("B.BIN", new byte[] { 2 });
            byte[] before = (byte[])Flushed(device, image).Clone();

            Assert.That(() => fs.Rename("A.BIN", "b.bin"), Throws.InstanceOf<IOException>());
            Assert.That(Flushed(device, image), Is.EqualTo(before));
        }

        [Test]
        public void Rename_Missing_ThrowsFileNotFound()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out _);

            Assert.That(() => fs.Rename("NOPE", "OTHER"), Throws.InstanceOf<FileNotFoundException>());
        }

        // ------------------------------------------------------------------ names

        [TestCase("")]
        [TestCase("/")]
        [TestCase("TOOLONGNAME.BIN")]
        [TestCase("NAME.LONG")]
        [TestCase("A B.BIN")]
        [TestCase("A_B.BIN")]
        [TestCase("A.B.C")]
        [TestCase(".BIN")]
        public void WriteFile_BadName_ThrowsArgumentException_AndWritesNothing(string name)
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out GenericBlockDevice device);
            byte[] before = (byte[])Flushed(device, image).Clone();

            Assert.That(() => fs.WriteFile(name, new byte[] { 1 }), Throws.InstanceOf<ArgumentException>());
            Assert.That(Flushed(device, image), Is.EqualTo(before));
        }

        [Test]
        public void WriteFile_NameWithoutExtensionAndLowerCase_IsStoredUpperCase()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out GenericBlockDevice device);

            fs.WriteFile("readme-1", new byte[] { 5 });
            Flushed(device, image);

            byte[] entry = DragonDosTestDisk.Entry(image, 1, 0, false);
            Assert.That(entry[1], Is.EqualTo((byte)'R'));
            Assert.That(entry[8], Is.EqualTo((byte)'1'));
            Assert.That(entry[9], Is.EqualTo(0));
            Assert.That(fs.ReadFile("README-1"), Is.EqualTo(new byte[] { 5 }));
            Assert.That(fs.ListDirectory("/")[0].Name, Is.EqualTo("README-1"));
        }

        // ------------------------------------------------------------------ refusals

        [Test]
        public void WriteFile_DiskFull_ThrowsIOException_AndLeavesTheDiskAsItWas()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out GenericBlockDevice device);
            fs.WriteFile("KEEP", DragonDosTestDisk.Pattern(1000, 1));
            byte[] before = (byte[])Flushed(device, image).Clone();
            int free = fs.GetBlocksFree();

            Assert.That(() => fs.WriteFile("BIG", new byte[(free + 1) * 256]), Throws.InstanceOf<IOException>());
            Assert.That(Flushed(device, image), Is.EqualTo(before));

            // The largest file that fits does fit, and uses every free sector.
            fs.WriteFile("BIG", DragonDosTestDisk.Pattern(free * 256, 2));
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(0));
            Assert.That(fs.ReadFile("BIG"), Is.EqualTo(DragonDosTestDisk.Pattern(free * 256, 2)));
        }

        [Test]
        public void WriteFile_OverwriteThatDoesNotFit_KeepsTheOldFile()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out GenericBlockDevice device);
            byte[] old = DragonDosTestDisk.Pattern(2000, 1);
            fs.WriteFile("OLD", old);
            byte[] before = (byte[])Flushed(device, image).Clone();

            Assert.That(() => fs.WriteFile("OLD", new byte[700 * 256]), Throws.InstanceOf<IOException>());

            Assert.That(Flushed(device, image), Is.EqualTo(before));
            Assert.That(fs.ReadFile("OLD"), Is.EqualTo(old));
        }

        [Test]
        public void WriteFile_DirectoryFull_ThrowsIOException_AfterExactly160Files()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out GenericBlockDevice device);

            for (int i = 0; i < 160; i++)
            {
                fs.WriteFile("F" + i.ToString("D3"), new byte[] { (byte)i });
            }

            byte[] before = (byte[])Flushed(device, image).Clone();
            Assert.That(() => fs.WriteFile("ONEMORE", new byte[] { 1 }), Throws.InstanceOf<IOException>().With.Message.Contains("directory"));
            Assert.That(Flushed(device, image), Is.EqualTo(before));
            Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(160));
            Assert.That(fs.ReadFile("F159"), Is.EqualTo(new byte[] { 159 }));
        }

        [Test]
        public void Write_OnReadOnlyDevice_ThrowsIOException()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out GenericBlockDevice device, readOnly: true);
            byte[] before = (byte[])image.Clone();

            Assert.That(() => fs.WriteFile("A", new byte[] { 1 }), Throws.InstanceOf<IOException>().With.Message.Contains("read-only"));
            Assert.That(() => fs.DeleteFile("A"), Throws.InstanceOf<IOException>().With.Message.Contains("read-only"));
            Assert.That(() => fs.Rename("A", "B"), Throws.InstanceOf<IOException>().With.Message.Contains("read-only"));
            device.Flush();
            Assert.That(image, Is.EqualTo(before));
        }

        [Test]
        public void WriteProtectedFile_CannotBeOverwrittenDeletedOrRenamed()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            int dirLsn = 20 * 18;
            // Entry 0 written by hand: attribute 02H (write protected), name LOCK.BIN, one extent of one sector at LSN 400.
            int at = ((dirLsn + 2) * 256);
            image[at] = 0x02;
            byte[] name = System.Text.Encoding.ASCII.GetBytes("LOCK\0\0\0\0BIN");
            Array.Copy(name, 0, image, at + 1, name.Length);
            image[at + 12] = 0x01;
            image[at + 13] = 0x90;
            image[at + 14] = 1;
            image[at + 24] = 0;
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out _);

            Assert.That(() => fs.WriteFile("LOCK.BIN", new byte[] { 1 }), Throws.InstanceOf<IOException>().With.Message.Contains("protected"));
            Assert.That(() => fs.DeleteFile("LOCK.BIN"), Throws.InstanceOf<IOException>().With.Message.Contains("protected"));
            Assert.That(() => fs.Rename("LOCK.BIN", "OTHER"), Throws.InstanceOf<IOException>().With.Message.Contains("protected"));
        }

        [Test]
        public void DiskWithoutDragonDosBam_IsNotAVolume_AndRefusesReadsAndWrites()
        {
            byte[] image = new byte[Tracks * 18 * 256];
            image[0] = 0x4F;
            image[1] = 0x1F;
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out _);

            Assert.That(fs.IsDragonDosVolume, Is.False);
            Assert.That(fs.VolumeLabel, Is.EqualTo("VDK DISK"));
            Assert.That(() => fs.ListDirectory("/"), Throws.InstanceOf<InvalidDataException>());
            Assert.That(() => fs.ReadFile("A"), Throws.InstanceOf<InvalidDataException>());
            Assert.That(() => fs.WriteFile("A", new byte[] { 1 }), Throws.InstanceOf<InvalidDataException>());
        }

        [Test]
        public void ListDirectory_StopsAtTheFirstEndOfDirectoryEntry()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            int at = (20 * 18 + 2) * 256;
            // Entry 0: a file. Entry 1: end of directory (89H from the format). Entry 2: a file that the DOS DIR never reaches.
            WriteFileEntry(image, at, "SEEN", "BIN", 400, 1, 0);
            WriteFileEntry(image, at + (2 * 25), "HIDDEN", "BIN", 401, 1, 0);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out _);

            DirectoryEntry[] entries = fs.ListDirectory("/");

            Assert.That(entries.Length, Is.EqualTo(1));
            Assert.That(entries[0].Name, Is.EqualTo("SEEN.BIN"));
        }

        // ------------------------------------------------------------------ more than four extents

        private static void WriteFileEntry(byte[] image, int offset, string name, string ext, int lsn, int count, int lastBytes)
        {
            Array.Clear(image, offset, 25);
            byte[] n = System.Text.Encoding.ASCII.GetBytes(name);
            byte[] e = System.Text.Encoding.ASCII.GetBytes(ext);
            Array.Copy(n, 0, image, offset + 1, n.Length);
            Array.Copy(e, 0, image, offset + 9, e.Length);
            image[offset + 12] = (byte)(lsn >> 8);
            image[offset + 13] = (byte)lsn;
            image[offset + 14] = (byte)count;
            image[offset + 24] = (byte)lastBytes;
        }

        // A disk with 40 one sector files F00 to F39 in consecutive sectors, then every second file deleted, so that the free sectors are single.
        private static DragonDosFileSystem FragmentedDisk(out byte[] image, out GenericBlockDevice device)
        {
            image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out device);
            for (int i = 0; i < 40; i++)
            {
                fs.WriteFile("F" + i.ToString("D2"), DragonDosTestDisk.Pattern(256, i));
            }

            for (int i = 1; i < 40; i += 2)
            {
                fs.DeleteFile("F" + i.ToString("D2"));
            }

            return fs;
        }

        [Test]
        public void WriteFile_With20Extents_UsesThreeContinuationEntries_AndReadsBack()
        {
            DragonDosFileSystem fs = FragmentedDisk(out byte[] image, out GenericBlockDevice device);
            byte[] data = DragonDosTestDisk.Pattern((19 * 256) + 100, 11);

            fs.WriteFile("BIGFILE.DAT", data);
            Flushed(device, image);

            // The new file takes the lowest deleted entries: head 1, continuations 3, 5 and 7.
            byte[] head = DragonDosTestDisk.Entry(image, 1, 1, false);
            Assert.That(head[0], Is.EqualTo(0x20), "file entry, continued");
            Assert.That(head[24], Is.EqualTo(3), "the next entry of the file");
            byte[] c1 = DragonDosTestDisk.Entry(image, 1, 3, false);
            byte[] c2 = DragonDosTestDisk.Entry(image, 1, 5, false);
            byte[] c3 = DragonDosTestDisk.Entry(image, 1, 7, false);
            Assert.That(c1[0], Is.EqualTo(0x21), "continuation entry, continued");
            Assert.That(c1[24], Is.EqualTo(5));
            Assert.That(c2[0], Is.EqualTo(0x21));
            Assert.That(c2[24], Is.EqualTo(7));
            Assert.That(c3[0], Is.EqualTo(0x01), "the last continuation entry is not continued");
            Assert.That(c3[24], Is.EqualTo(100), "the bytes in the last sector are in the LAST entry of the chain");

            // 20 extents of one sector: 4 in the head, 7 + 7 + 2 in the continuation entries.
            Assert.That(head[14], Is.EqualTo(1));
            Assert.That(head[21 + 2], Is.EqualTo(1), "fourth extent of the head");
            Assert.That(c1[1 + 2], Is.EqualTo(1), "first extent of a continuation entry");
            Assert.That(c1[19 + 2], Is.EqualTo(1), "seventh extent of a continuation entry");
            Assert.That(c3[1 + 2], Is.EqualTo(1));
            Assert.That(c3[4 + 2], Is.EqualTo(1));
            Assert.That(c3[7 + 2], Is.EqualTo(0), "only 2 extents in the last continuation entry");

            Assert.That(fs.ReadFile("BIGFILE.DAT"), Is.EqualTo(data));
            Assert.That(fs.GetMetadata("BIGFILE.DAT")!.Size, Is.EqualTo(data.Length));
            Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(21), "20 original files and the new one; continuation entries are not files");
            AssertBackupEqualsPrimary(image, 1);

            // The other files are untouched.
            Assert.That(fs.ReadFile("F00"), Is.EqualTo(DragonDosTestDisk.Pattern(256, 0)));
            Assert.That(fs.ReadFile("F38"), Is.EqualTo(DragonDosTestDisk.Pattern(256, 38)));
        }

        [Test]
        public void DeleteFile_WithContinuationEntries_MarksEveryEntry81_AndFreesEveryExtent()
        {
            DragonDosFileSystem fs = FragmentedDisk(out byte[] image, out GenericBlockDevice device);
            int freeBefore = fs.GetBlocksFree();
            fs.WriteFile("BIGFILE.DAT", DragonDosTestDisk.Pattern(19 * 256, 11));

            fs.DeleteFile("BIGFILE.DAT");
            Flushed(device, image);

            Assert.That(DragonDosTestDisk.Entry(image, 1, 1, false)[0], Is.EqualTo(0x81));
            Assert.That(DragonDosTestDisk.Entry(image, 1, 3, false)[0], Is.EqualTo(0x81));
            Assert.That(DragonDosTestDisk.Entry(image, 1, 5, false)[0], Is.EqualTo(0x81));
            Assert.That(DragonDosTestDisk.Entry(image, 1, 7, false)[0], Is.EqualTo(0x81));
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(freeBefore), "all 19 sectors are free again");
            Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(20));
            AssertBackupEqualsPrimary(image, 1);
        }

        [Test]
        public void WriteFile_With5Extents_UsesOneContinuationEntry()
        {
            DragonDosFileSystem fs = FragmentedDisk(out byte[] image, out GenericBlockDevice device);
            byte[] data = DragonDosTestDisk.Pattern(5 * 256, 3);

            fs.WriteFile("FIVE.DAT", data);
            Flushed(device, image);

            Assert.That(DragonDosTestDisk.Entry(image, 1, 1, false)[0], Is.EqualTo(0x20));
            Assert.That(DragonDosTestDisk.Entry(image, 1, 1, false)[24], Is.EqualTo(3));
            Assert.That(DragonDosTestDisk.Entry(image, 1, 3, false)[0], Is.EqualTo(0x01));
            Assert.That(DragonDosTestDisk.Entry(image, 1, 3, false)[24], Is.EqualTo(0), "a full last sector");
            Assert.That(fs.ReadFile("FIVE.DAT"), Is.EqualTo(data));
        }

        [Test]
        public void WriteFile_FourExtentsOnly_NeedsNoContinuationEntry()
        {
            DragonDosFileSystem fs = FragmentedDisk(out byte[] image, out GenericBlockDevice device);

            fs.WriteFile("FOUR.DAT", DragonDosTestDisk.Pattern(4 * 256, 3));
            Flushed(device, image);

            Assert.That(DragonDosTestDisk.Entry(image, 1, 1, false)[0], Is.EqualTo(0));
            Assert.That(DragonDosTestDisk.Entry(image, 1, 3, false)[0], Is.EqualTo(0x81), "entry 3 is still the deleted entry of F03");
        }

        [Test]
        public void WriteFile_Over255Sectors_SplitsIntoExtentsOfAtMost255()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out GenericBlockDevice device);
            byte[] data = DragonDosTestDisk.Pattern(300 * 256, 1);

            fs.WriteFile("BIG", data);
            Flushed(device, image);

            byte[] entry = DragonDosTestDisk.Entry(image, 1, 0, false);
            Assert.That(entry[14], Is.EqualTo(255), "the count byte holds 255 at most");
            Assert.That(entry[17], Is.EqualTo(45), "the rest, 45 sectors, in a second extent");
            Assert.That(fs.ReadFile("BIG"), Is.EqualTo(data));
        }

        // ------------------------------------------------------------------ the reader, on entries written by hand from the spec

        [Test]
        public void ReadFile_FollowsAContinuationEntry_AndTakesTheLastBytesFromTheLastEntry()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            int at = (20 * 18 + 2) * 256;
            // Head entry 0: SPEC.BIN, attribute 20H (continued), three extents, the next entry is entry 2.
            Array.Clear(image, at, 25);
            image[at] = 0x20;
            Array.Copy(System.Text.Encoding.ASCII.GetBytes("SPEC"), 0, image, at + 1, 4);
            Array.Copy(System.Text.Encoding.ASCII.GetBytes("BIN"), 0, image, at + 9, 3);
            SetExtent(image, at + 12, 400, 2);
            SetExtent(image, at + 15, 100, 1);
            SetExtent(image, at + 18, 500, 3);
            image[at + 24] = 2;
            // Entry 1 stays free (89H). Entry 2: continuation entry, attribute 01H, two extents, 16 bytes in the last sector.
            int cont = at + (2 * 25);
            Array.Clear(image, cont, 25);
            image[cont] = 0x01;
            SetExtent(image, cont + 1, 600, 2);
            SetExtent(image, cont + 4, 700, 1);
            image[cont + 24] = 16;
            // Fill the nine sectors with a pattern that tells them apart.
            int[] lsns = { 400, 401, 100, 500, 501, 502, 600, 601, 700 };
            for (int i = 0; i < lsns.Length; i++)
            {
                for (int b = 0; b < 256; b++)
                {
                    image[(lsns[i] * 256) + b] = (byte)(i + 1);
                }
            }

            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out _);
            byte[] data = fs.ReadFile("SPEC.BIN");

            Assert.That(data.Length, Is.EqualTo((8 * 256) + 16), "nine sectors, the last with 16 bytes (from the last entry, not the head)");
            for (int i = 0; i < 9; i++)
            {
                Assert.That(data[i * 256], Is.EqualTo((byte)(i + 1)), "sector " + i + " of the file");
            }

            Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(1));
            Assert.That(fs.ListDirectory("/")[0].Metadata.Size, Is.EqualTo((8 * 256) + 16));
        }

        private static void SetExtent(byte[] image, int at, int lsn, int count)
        {
            image[at] = (byte)(lsn >> 8);
            image[at + 1] = (byte)lsn;
            image[at + 2] = (byte)count;
        }

        [Test]
        public void ReadFile_ContinuationLinkToANonContinuationEntry_ThrowsInvalidData()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 1);
            int at = (20 * 18 + 2) * 256;
            WriteFileEntry(image, at, "BAD", "BIN", 400, 1, 1);
            image[at] = 0x20;
            image[at + 24] = 0;

            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 1, out _);

            Assert.That(() => fs.ListDirectory("/"), Throws.InstanceOf<InvalidDataException>());
        }

        // ------------------------------------------------------------------ double sided

        [Test]
        public void DoubleSidedDisk_WriteDeleteRename_UseTheTrackOf36Sectors_AndKeepTheBackup()
        {
            byte[] image = DragonDosTestDisk.Format(Tracks, 2);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, Tracks, 2, out GenericBlockDevice device);
            byte[] a = DragonDosTestDisk.Pattern(9000, 1);
            byte[] b = DragonDosTestDisk.Pattern(300, 2);

            fs.WriteFile("A.BIN", a);
            fs.WriteFile("B.BIN", b);
            fs.Rename("B.BIN", "C.BIN");
            fs.DeleteFile("A.BIN");
            fs.WriteFile("D.BIN", a);
            Flushed(device, image);

            Assert.That(fs.ReadFile("C.BIN"), Is.EqualTo(b));
            Assert.That(fs.ReadFile("D.BIN"), Is.EqualTo(a));
            AssertBackupEqualsPrimary(image, 2);
            byte[] entry = DragonDosTestDisk.Entry(image, 2, 0, false);
            int lsn = (entry[12] << 8) | entry[13];
            Assert.That(lsn, Is.GreaterThanOrEqualTo((20 * 36) + 36), "data after the directory track of 36 sectors");
        }

        [Test]
        public void EightyTrackDisk_UsesBothBamSectors()
        {
            byte[] image = DragonDosTestDisk.Format(80, 2);
            DragonDosFileSystem fs = DragonDosTestDisk.Mount(image, 80, 2, out GenericBlockDevice device);
            int free = fs.GetBlocksFree();

            // Fill the first 1440 sectors so that the next file has to use the second BAM sector.
            byte[] big = DragonDosTestDisk.Pattern(1300 * 256, 1);
            fs.WriteFile("FIRST", big);
            fs.WriteFile("SECOND", DragonDosTestDisk.Pattern(600 * 256, 2));
            Flushed(device, image);

            Assert.That(fs.ReadFile("FIRST"), Is.EqualTo(big));
            Assert.That(fs.ReadFile("SECOND"), Is.EqualTo(DragonDosTestDisk.Pattern(600 * 256, 2)));
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(free - 1900));
            AssertBackupEqualsPrimary(image, 2);
        }
    }
}
