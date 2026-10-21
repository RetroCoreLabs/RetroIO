//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.FileSystems;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// The OS-9 (RBF) reader and writer on real NitrOS-9 disks and on volumes made by Toolshed, checked against Toolshed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The images are in the os9 folder of the test-image folder (the RetroFS test-image folder, or the folder in RETROIO_DRAGON_TEST_IMAGES).
    /// Every test works on a copy in a temporary folder; a test whose image is missing is skipped with the name of the image, and the Toolshed
    /// parts are skipped when WSL Ubuntu or Toolshed os9 is not there.
    /// </para>
    /// <para>
    /// The NOS9 images are the distribution disks of NitrOS-9 3.3.0 from the project's snapshot page (nitros9.sourceforge.io/snapshot);
    /// uucpbb21_6809.dsk is the UUCPbb 2.1 package from the same page; the built images were made from the NitrOS-9 source tree with its own
    /// recipes (coco/floppy, coco/dw, coco3/dw, coco3_6309/40d); the toolshed images are empty volumes made by Toolshed 2.2 os9 format
    /// (-l40000; -l20000 -c4; -t80 -ds -c2). The NitrOS-9 build lays its disks out with Toolshed too, so their files were placed by Toolshed,
    /// not by RBF.
    /// </para>
    /// <para>
    /// The DriveWire images (dw in the name) say in sector 0 that the volume has 524268 sectors, but the file stops after the last sector in
    /// use. On a device of the file's own size the reader refuses it, because the volume is larger than the device. The tests use the copy as
    /// it is, not padded: the device is the whole volume through a PaddedSubStream, the sectors behind the file read as zero, and a write grows
    /// the file only as far as the last sector written, as Toolshed does when it writes to such a file.
    /// </para>
    /// <para>
    /// The exact sector numbers expected in the layout tests are worked out in the comments from the rules of rbf.asm (FatScan, Create, L0D07,
    /// L0EFE, ClrFBits) and the free space of the untouched image, which is listed there too.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class Os9RbfRealDiskTests
    {
        private const int SectorSize = 256;
        private static readonly DateTime FixedTime = new DateTime(2026, 10, 21, 12, 34, 0);

        /// <summary>
        /// Every image the reader is compared with Toolshed on.
        /// </summary>
        /// <returns>
        /// One case per image.
        /// </returns>
        public static IEnumerable<TestCaseData> AllImages()
        {
            string[] names =
            {
                "NOS9_6809_L1_v030300_coco1_40d_1.dsk",
                "NOS9_6809_L1_v030300_coco1_80d.dsk",
                "NOS9_6809_L1_v030300_coco1_dw.dsk",
                "NOS9_6809_L2_v030300_coco3_40d_1.dsk",
                "NOS9_6309_L2_v030300_coco3_80d.dsk",
                "NOS9_6809_L3_v030300_coco3_80d.dsk",
                "NOS9_6809_L2_v030300_coco3_cocosdc.dsk",
                "NOS9_6809_L1_v030300_d64_40s_1.dsk",
                "NOS9_6809_L1_v030300_d64_80d.dsk",
                "NOS9_6809_L1_v030300_dalpha_80d.dsk",
                "uucpbb21_6809.dsk",
                "built_l1_coco_40d.dsk",
                "built_l1_coco_80d.dsk",
                "built_l1_coco_dw.dsk",
                "built_l2_coco3_6309.dsk",
                "built_l2_coco3_dw.dsk",
                "toolshed_80ds_c2.dsk",
                "toolshed_hd_20000_c4.dsk",
                "toolshed_hd_40000.dsk"
            };

            for (int i = 0; i < names.Length; i++)
            {
                yield return new TestCaseData(names[i]).SetArgDisplayNames(names[i]);
            }
        }

        private string _folder = string.Empty;
        private readonly List<IDisposable> _open = new List<IDisposable>();

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "os9real-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _open.Count - 1; i >= 0; i--)
            {
                _open[i].Dispose();
            }

            _open.Clear();
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, true);
            }
        }

        // ---- images ----

        private static string ImagePath(string name)
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable("RETROIO_DRAGON_TEST_IMAGES");
            string root = !string.IsNullOrEmpty(fromEnvironment)
                ? fromEnvironment
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "RetroFS", "test-images");
            return Path.Combine(root, "os9", name);
        }

        // Copies the image into the temporary folder, as it is (see the class remarks). Skips the test when the image is missing.
        private string CopyOf(string name)
        {
            string source = ImagePath(name);
            if (!File.Exists(source))
            {
                Assert.Ignore("Test image not found: os9/" + name);
            }

            string copy = Path.Combine(_folder, name);
            File.Copy(source, copy);
            return copy;
        }

        // Mounts the image as the whole volume sector 0 describes. A file shorter than its volume (the DriveWire images) is shown through a
        // PaddedSubStream: the sectors behind the file read as zero and a write grows the file only as far as the sector written.
        private Os9RbfFileSystem Mount(string path, bool readOnly)
        {
            long fileBytes = new FileInfo(path).Length;
            FileStream stream = new FileStream(path, FileMode.Open, readOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.ReadWrite);
            byte[] id = new byte[SectorSize];
            stream.ReadExactly(id, 0, SectorSize);
            long sectors = Math.Max(fileBytes / SectorSize, U24(id, 0));
            Stream data = fileBytes < sectors * SectorSize
                ? new RetroIO.Storage.Local.Streams.PaddedSubStream(stream, 0, fileBytes, sectors * SectorSize)
                : stream;
            GenericBlockDevice device = new GenericBlockDevice(Descriptor(sectors));
            device.Mount(data, readOnly);
            _open.Add(stream);
            _open.Add(device);
            Os9RbfFileSystem fs = new Os9RbfFileSystem();
            fs.Initialize(device);
            fs.Clock = () => FixedTime;
            return fs;
        }

        private static BlockDeviceDescriptor Descriptor(long sectors)
        {
            return new BlockDeviceDescriptor
            {
                Name = "OS-9 image",
                BlockSize = SectorSize,
                AddressMode = BlockAddressMode.LBA,
                Cylinders = (int)sectors,
                Heads = 1,
                UniformSectorsPerTrack = 1,
                TotalBlocks = (uint)sectors,
                ExpectedImageSize = sectors * SectorSize,
                SupportsVariableGeometry = false
            };
        }

        private void Unmount()
        {
            for (int i = _open.Count - 1; i >= 0; i--)
            {
                _open[i].Dispose();
            }

            _open.Clear();
        }

        private static byte[] Read(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] all = new byte[stream.Length];
                stream.ReadExactly(all, 0, all.Length);
                return all;
            }
        }

        private static byte[] Pattern(int length, int seed)
        {
            byte[] data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (byte)((i * 7) + (i / 251) + seed);
            }

            return data;
        }

        // ---- raw readers, written for the tests and not shared with the code under test ----

        private static uint U24(byte[] data, int offset)
        {
            return (uint)((data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2]);
        }

        private static int U16(byte[] data, int offset)
        {
            return (data[offset] << 8) | data[offset + 1];
        }

        private static long U32(byte[] data, int offset)
        {
            return ((long)data[offset] << 24) | ((long)data[offset + 1] << 16) | ((long)data[offset + 2] << 8) | data[offset + 3];
        }

        private static byte[] Sector(byte[] image, long lsn)
        {
            byte[] sector = new byte[SectorSize];
            Buffer.BlockCopy(image, (int)(lsn * SectorSize), sector, 0, SectorSize);
            return sector;
        }

        private static List<uint[]> Segments(byte[] descriptor)
        {
            List<uint[]> list = new List<uint[]>();
            for (int i = 0; i < 48; i++)
            {
                int count = U16(descriptor, 16 + (i * 5) + 3);
                if (count == 0)
                {
                    break;
                }

                list.Add(new uint[] { U24(descriptor, 16 + (i * 5)), (uint)count });
            }

            return list;
        }

        private static byte[] FileBytes(byte[] image, byte[] descriptor)
        {
            long size = U32(descriptor, 9);
            byte[] result = new byte[size];
            long done = 0;
            List<uint[]> segments = Segments(descriptor);
            for (int s = 0; s < segments.Count && done < size; s++)
            {
                long take = Math.Min((long)segments[s][1] * SectorSize, size - done);
                Buffer.BlockCopy(image, (int)(segments[s][0] * SectorSize), result, (int)done, (int)take);
                done += take;
            }

            return result;
        }

        private static string EntryName(byte[] data, int offset)
        {
            StringBuilder name = new StringBuilder();
            for (int i = 0; i < 29; i++)
            {
                byte b = data[offset + i];
                name.Append((char)(b & 0x7F));
                if ((b & 0x80) != 0)
                {
                    break;
                }
            }

            return name.ToString();
        }

        private static uint RawFind(byte[] image, string path)
        {
            uint fd = U24(image, 8);
            string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int p = 0; p < parts.Length; p++)
            {
                byte[] data = FileBytes(image, Sector(image, fd));
                uint next = 0;
                for (int o = 0; o + 32 <= data.Length; o += 32)
                {
                    if (data[o] != 0 && string.Equals(EntryName(data, o), parts[p], StringComparison.OrdinalIgnoreCase))
                    {
                        next = U24(data, o + 29);
                        break;
                    }
                }

                if (next == 0)
                {
                    return 0;
                }

                fd = next;
            }

            return fd;
        }

        // A bit of the allocation map: bit n is cluster n.
        private static bool MapBit(byte[] image, long cluster)
        {
            return (image[SectorSize + (int)(cluster >> 3)] & (0x80 >> (int)(cluster & 7))) != 0;
        }

        private static void SetMapBit(byte[] image, long cluster)
        {
            image[SectorSize + (int)(cluster >> 3)] |= (byte)(0x80 >> (int)(cluster & 7));
        }

        // ---- Toolshed ----

        // Runs a bash script in WSL Ubuntu and gives what it printed; skips the test when WSL, Ubuntu or Toolshed is not there.
        private string Bash(string script)
        {
            string scriptPath = Path.Combine(_folder, "script-" + Guid.NewGuid().ToString("N") + ".sh");
            File.WriteAllText(scriptPath, "command -v os9 >/dev/null || echo 'Command '\\''os9'\\'' not found'\n" + script);
            string? text = StressToolshed.Run("bash", StressToolshed.WslPath(scriptPath));
            if (text == null || text.Replace("\0", string.Empty).Contains("Command 'os9' not found"))
            {
                Assert.Ignore("Toolshed os9 is not available through WSL Ubuntu");
            }

            return text!;
        }

        private static string Quote(string text)
        {
            return "'" + text.Replace("'", "'\\''") + "'";
        }

        private sealed class DcheckResult
        {
            public string Text = string.Empty;
            public long Unreferenced;
            public long Directories;
            public long Files;
            public long Free;
        }

        // os9 dcheck and os9 free on the image. dcheck must find the structure intact, no cluster in two files, none in a file but free in the
        // map, and no bad descriptor.
        private DcheckResult Dcheck(string image)
        {
            string wsl = Quote(StressToolshed.WslPath(image));
            string text = Bash("os9 dcheck " + wsl + " | grep -v '^Logical sector'\necho '@@FREE'\nos9 free " + wsl + "\n");
            Assert.That(text, Does.Contain("file structure is intact"), text);
            Assert.That(text, Does.Contain("0 previously allocated cluster found"), text);
            Assert.That(text, Does.Contain("0 clusters in file structure but not in allocation map"), text);
            Assert.That(text, Does.Contain("0 bad file decriptor sector"), text);

            DcheckResult result = new DcheckResult { Text = text };
            result.Unreferenced = StressToolshed.NumberBefore(text, " clusters in allocation map but not in file structure");
            result.Directories = StressToolshed.NumberBefore(text, " director");
            result.Files = StressToolshed.NumberBefore(text, " files");
            result.Free = StressToolshed.NumberBefore(text, " Free sectors");
            return result;
        }

        // Every directory and file of a volume, read through the code under test, depth first in directory order.
        private static void Collect(Os9RbfFileSystem fs, string path, List<string> directories, List<string> files)
        {
            directories.Add(path);
            DirectoryEntry[] entries = fs.ListDirectory(path);
            for (int i = 0; i < entries.Length; i++)
            {
                string child = path == "/" ? "/" + entries[i].Name : path + "/" + entries[i].Name;
                if (entries[i].Metadata.IsDirectory)
                {
                    Collect(fs, child, directories, files);
                }
                else
                {
                    files.Add(child);
                }
            }
        }

        // The rows of an os9 dir -e listing: name, byte count, attributes.
        private static List<string[]> DirRows(string text)
        {
            List<string[]> rows = new List<string[]>();
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Match m = Regex.Match(lines[i], @"^\s*\d+\.\d+\s+\d{4}/\d\d/\d\d\s+\d{4}\s+(\S+)\s+([0-9A-Fa-f]+)\s+(\d+)\s+(.+?)\s*$");
                if (m.Success)
                {
                    rows.Add(new[] { m.Groups[4].Value, m.Groups[3].Value, m.Groups[1].Value });
                }
            }

            return rows;
        }

        // Compares every directory listing and every file of the volume with what Toolshed reads (os9 dir -e and os9 copy, in one WSL call),
        // then the free sectors and the counts of dcheck.
        private void CompareWithToolshed(string image, Os9RbfFileSystem fs)
        {
            List<string> directories = new List<string>();
            List<string> files = new List<string>();
            Collect(fs, "/", directories, files);

            string wsl = StressToolshed.WslPath(image);
            string outFolder = Path.Combine(_folder, "out-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outFolder);
            StringBuilder script = new StringBuilder();
            for (int d = 0; d < directories.Count; d++)
            {
                script.Append("echo '@@DIR ").Append(d).Append("'\nos9 dir -e ").Append(Quote(wsl + "," + directories[d])).Append('\n');
            }

            for (int f = 0; f < files.Count; f++)
            {
                script.Append("os9 copy ").Append(Quote(wsl + "," + files[f])).Append(' ').Append(Quote(StressToolshed.WslPath(Path.Combine(outFolder, f + ".bin")))).Append('\n');
            }

            script.Append("echo '@@END'\n");
            string text = Bash(script.ToString());

            for (int d = 0; d < directories.Count; d++)
            {
                int start = text.IndexOf("@@DIR " + d + "\n", StringComparison.Ordinal);
                int end = text.IndexOf(d + 1 < directories.Count ? "@@DIR " + (d + 1) + "\n" : "@@END", start + 1, StringComparison.Ordinal);
                Assert.That(start, Is.GreaterThanOrEqualTo(0), text);
                string section = text.Substring(start, end - start);
                Assert.That(section, Does.Not.Contain("dir: error"), section);
                List<string[]> theirs = DirRows(section);
                DirectoryEntry[] ours = fs.ListDirectory(directories[d]);
                Assert.That(theirs.Count, Is.EqualTo(ours.Length), directories[d] + "\n" + section);
                for (int i = 0; i < ours.Length; i++)
                {
                    Assert.That(theirs[i][0], Is.EqualTo(ours[i].Name), directories[d] + " entry " + i);
                    Assert.That(long.Parse(theirs[i][1]), Is.EqualTo(ours[i].Metadata.Size), directories[d] + " size of " + ours[i].Name);
                    Assert.That(theirs[i][2], Is.EqualTo(ours[i].Metadata.Attributes), directories[d] + " attributes of " + ours[i].Name);
                }
            }

            for (int f = 0; f < files.Count; f++)
            {
                string copied = Path.Combine(outFolder, f + ".bin");
                Assert.That(File.Exists(copied), Is.True, "os9 copy gave no file for " + files[f]);
                Assert.That(File.ReadAllBytes(copied), Is.EqualTo(fs.ReadFile(files[f])), "the bytes of " + files[f]);
            }

            DcheckResult check = Dcheck(image);
            Assert.That(check.Free, Is.EqualTo(fs.GetBlocksFree()), "free sectors\n" + check.Text);
            Assert.That(check.Directories, Is.EqualTo(directories.Count), "directories\n" + check.Text);
            Assert.That(check.Files, Is.EqualTo(files.Count), "files\n" + check.Text);
        }

        // ---- the reader ----

        [TestCaseSource(nameof(AllImages))]
        public void Reader_EveryDirectoryFileAndTheFreeSpace_AgreeWithToolshed(string name)
        {
            string image = CopyOf(name);
            Os9RbfFileSystem fs = Mount(image, true);

            CompareWithToolshed(image, fs);
        }

        [TestCase("NOS9_6809_L1_v030300_coco1_dw.dsk")]
        [TestCase("built_l2_coco3_dw.dsk")]
        public void Reader_DriveWireImageShorterThanItsVolume_IsRefusedAsItIs(string name)
        {
            // The file stops after the last sector in use; sector 0 says 524268 sectors. The reader does not take a device shorter than the volume.
            string source = ImagePath(name);
            if (!File.Exists(source))
            {
                Assert.Ignore("Test image not found: os9/" + name);
            }

            string copy = Path.Combine(_folder, name);
            File.Copy(source, copy);
            long sectors = new FileInfo(copy).Length / SectorSize;
            Assert.That(sectors, Is.LessThan(524268));

            FileStream stream = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            GenericBlockDevice device = new GenericBlockDevice(Descriptor(sectors));
            device.Mount(stream, true);
            _open.Add(stream);
            _open.Add(device);
            Os9RbfFileSystem fs = new Os9RbfFileSystem();

            Assert.That(fs.CanHandle(device), Is.False);
            InvalidDataException? ex = Assert.Throws<InvalidDataException>(() => fs.Initialize(device));
            Assert.That(ex!.Message, Does.Contain("larger than the device"));
        }

        // ---- writing on every image ----

        [TestCaseSource(nameof(AllImages))]
        public void Write_CreateOverwriteRenameMoveAndDelete_ToolshedFindsTheVolumeIntactAfterEveryChange(string name)
        {
            string image = CopyOf(name);
            Os9RbfFileSystem fs = Mount(image, false);
            DcheckResult before = Dcheck(image);
            Assert.That(before.Free, Is.EqualTo(fs.GetBlocksFree()));
            long rootSectorsBefore = RootSectors(Read(image));
            int free = fs.GetBlocksFree();

            // The bigger steps only where there is room: d64_40s_1 has 36 free sectors, coco3_40d_1 has 40.
            bool roomy = free >= 400;
            List<string> made = new List<string>();
            List<byte[]> contents = new List<byte[]>();

            void Check(string step)
            {
                DcheckResult after = Dcheck(image);
                Assert.That(after.Unreferenced, Is.EqualTo(before.Unreferenced), step + ": a cluster was lost or a reserved one freed\n" + after.Text);
                Assert.That(after.Free, Is.EqualTo(fs.GetBlocksFree()), step + ": free sectors\n" + after.Text);
            }

            void Write(string path, byte[] data)
            {
                fs.WriteFile(path, data);
                int at = made.IndexOf(path);
                if (at < 0)
                {
                    made.Add(path);
                    contents.Add(data);
                }
                else
                {
                    contents[at] = data;
                }

                Check("write " + path + " with " + data.Length + " bytes");
            }

            fs.CreateDirectory("/RIO");
            Check("make /RIO");
            Write("/RIO/empty", Array.Empty<byte>());
            Write("/RIO/one", Pattern(1, 1));
            Write("/RIO/s255", Pattern(255, 2));
            if (roomy)
            {
                Write("/RIO/s256", Pattern(256, 3));
                Write("/RIO/s257", Pattern(257, 4));
                Write("/RIO/s5000", Pattern(5000, 5));
                Write("/big", Pattern(Math.Min(70000, (free - 200) * SectorSize), 6));
                Write("/RIO/s5000", Pattern(300, 7));
                Write("/RIO/s255", Pattern(9000, 8));
                fs.Rename("/RIO/s256", "/RIO/renamed");
                made[made.IndexOf("/RIO/s256")] = "/RIO/renamed";
                Check("rename");
                fs.Rename("/RIO/s257", "/moved");
                made[made.IndexOf("/RIO/s257")] = "/moved";
                Check("move");
                fs.CreateDirectory("/RIO/SUB");
                fs.Rename("/RIO/SUB", "/SUB2");
                Check("move a directory");
                fs.DeleteDirectory("/SUB2");
                Check("delete a directory");
            }

            fs.DeleteFile("/RIO/one");
            contents.RemoveAt(made.IndexOf("/RIO/one"));
            made.Remove("/RIO/one");
            Check("delete");

            // Toolshed reads back every file the code wrote.
            for (int i = 0; i < made.Count; i++)
            {
                Assert.That(fs.ReadFile(made[i]), Is.EqualTo(contents[i]), made[i]);
            }

            CompareWithToolshed(image, fs);

            // Delete what was made: the free space comes back, less the sectors the root directory grew by (a directory never shrinks).
            for (int i = 0; i < made.Count; i++)
            {
                fs.DeleteFile(made[i]);
            }

            fs.DeleteDirectory("/RIO");
            Check("delete everything that was made");
            long rootGrowth = RootSectors(Read(image)) - rootSectorsBefore;
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(free - rootGrowth));
        }

        private static long RootSectors(byte[] image)
        {
            List<uint[]> segments = Segments(Sector(image, U24(image, 8)));
            long sectors = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                sectors += segments[i][1];
            }

            return sectors;
        }

        // ---- clusters of more than one sector ----

        [Test]
        public void TwoSectorClusters_NewFile_IsLaidOutAsCreateWriteAndCloseLayItOut()
        {
            // NOS9_6809_L1_v030300_d64_80d.dsk: 2880 sectors, 2 sectors per bit, 180 map bytes (one map sector), segment allocation size 8.
            // Free: clusters 720 to 1439, one run (sectors 1440 to 2879); the root directory has room for another entry.
            // Create: FatScan for 1 sector wants (2 - 1 + 1) >> 1 = 1 cluster: cluster 720. The descriptor is sector 1440; the rest of the cluster,
            // sector 1441, becomes the first segment (1441, 1).
            // Write of 300 bytes: 2 sectors are needed and 1 is there, so 1 is missing; below the allocation size, so 8 are asked: (1 + 8) >> 1 = 4
            // clusters, 721 to 724, sectors 1442 to 1449. They start where the first segment ends, in the same map sector, so L0D07 joins them:
            // (1441, 9).
            // Close: the byte at 300 is in logical sector 1; the segment ends at logical sector 9, so 9 - 1 - 1 (the sector holding byte 300 is
            // kept) = 7 sectors could go, rounded down to whole clusters: 6. ClrFBits releases sectors 1444 to 1449 (clusters 722 to 724) and the
            // segment becomes (1441, 3). The file holds clusters 720 and 721.
            string image = CopyOf("NOS9_6809_L1_v030300_d64_80d.dsk");
            byte[] original = Read(image);
            Os9RbfFileSystem fs = Mount(image, false);
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(1440));
            byte[] data = Pattern(300, 21);

            fs.WriteFile("/c2file", data);

            byte[] after = Read(image);
            uint fd = RawFind(after, "/c2file");
            Assert.That(fd, Is.EqualTo(1440u));
            byte[] descriptor = Sector(after, fd);
            List<uint[]> segments = Segments(descriptor);
            Assert.That(segments.Count, Is.EqualTo(1));
            Assert.That(segments[0], Is.EqualTo(new uint[] { 1441, 3 }));
            Assert.That(U32(descriptor, 9), Is.EqualTo(300));
            Assert.That(MapBit(after, 720) && MapBit(after, 721), Is.True);
            Assert.That(MapBit(after, 722) || MapBit(after, 723) || MapBit(after, 724), Is.False);
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(1436));
            Assert.That(fs.ReadFile("/c2file"), Is.EqualTo(data));

            DcheckResult check = Dcheck(image);
            Assert.That(check.Unreferenced, Is.EqualTo(16), "the same as on the untouched disk");
            Assert.That(check.Free, Is.EqualTo(1436));

            // Delete: L0EFE to size 0 keeps the sector of the first segment that rounds to the descriptor's cluster ((3) rounded down to
            // whole clusters is 2: sectors 1442 and 1443 go), then ClrFBits releases the descriptor sector plus the 1 left ("ldd <$13,x /
            // addd #1"): cluster 720. The map is as it was.
            fs.DeleteFile("/c2file");

            byte[] deleted = Read(image);
            byte[] released = Sector(deleted, 1440);
            Assert.That(Segments(released).Count, Is.EqualTo(1));
            Assert.That(Segments(released)[0], Is.EqualTo(new uint[] { 1441, 1 }), "the first segment keeps the sector of the descriptor's cluster");
            Assert.That(U32(released, 9), Is.EqualTo(0));
            Assert.That(released[8], Is.EqualTo(0));
            byte[] mapBefore = new byte[180];
            byte[] mapAfter = new byte[180];
            Buffer.BlockCopy(original, SectorSize, mapBefore, 0, 180);
            Buffer.BlockCopy(deleted, SectorSize, mapAfter, 0, 180);
            Assert.That(mapAfter, Is.EqualTo(mapBefore));
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(1440));
            Assert.That(Dcheck(image).Unreferenced, Is.EqualTo(16));
        }

        [Test]
        public void TwoSectorClusters_NewDirectory_LivesInTheRestOfTheDescriptorsCluster()
        {
            // toolshed_80ds_c2.dsk: 2 sectors per bit, first free cluster 6. MakDir's Create takes cluster 6: descriptor 12, first segment
            // (13, 1). 64 bytes need 1 sector, which that segment holds, so L0C6F asks for nothing more. The parent has room.
            string image = CopyOf("toolshed_80ds_c2.dsk");
            Os9RbfFileSystem fs = Mount(image, false);
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(2868));

            fs.CreateDirectory("/NEW");

            byte[] after = Read(image);
            uint fd = RawFind(after, "/NEW");
            Assert.That(fd, Is.EqualTo(12u));
            byte[] descriptor = Sector(after, fd);
            Assert.That(descriptor[0], Is.EqualTo(0xBF));
            Assert.That(U32(descriptor, 9), Is.EqualTo(64));
            Assert.That(Segments(descriptor)[0], Is.EqualTo(new uint[] { 13, 1 }));
            Assert.That(Segments(descriptor).Count, Is.EqualTo(1));
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(2866));
            Assert.That(EntryName(Sector(after, 13), 0), Is.EqualTo(".."));
            Assert.That(EntryName(Sector(after, 13), 32), Is.EqualTo("."));

            fs.WriteFile("/NEW/f", Pattern(700, 22));
            Assert.That(fs.ReadFile("/NEW/f"), Is.EqualTo(Pattern(700, 22)));
            DcheckResult check = Dcheck(image);
            Assert.That(check.Unreferenced, Is.EqualTo(0));
            Assert.That(check.Directories, Is.EqualTo(2));
            Assert.That(check.Files, Is.EqualTo(1));
        }

        [Test]
        public void FourSectorClusters_ARequestAbove2048Clusters_IsSplitPerMapSectorAndNotJoinedAcrossIt()
        {
            // toolshed_hd_20000_c4.dsk: 20000 sectors, 4 sectors per bit, 625 map bytes (map sectors 1 and 2 whole, sector 3 has 113 bytes),
            // segment allocation size 8. Free clusters: 4 to 2047 (map sector 1), 2048 to 4095 (sector 2), 4096 to 4999 (sector 3).
            // Create: cluster 4, descriptor sector 16, first segment (17, 3). V.MapSct = 0.
            // Write of 9000 sectors: 8997 are missing. FatScan: (3 + 8997) >> 2 = 2250 clusters, cut to 2048. Map sector 1 has 2043 free
            // (5 to 2047), too few; map sector 2 has 2048: clusters 2048 to 4095, sectors 8192 to 16383. Not next to (17, 3): a new segment.
            // V.MapSct = 1. Then 805 sectors are missing: (3 + 805) >> 2 = 202 clusters, from map sector 2 (full) on: map sector 3, clusters
            // 4096 to 4297, sectors 16384 to 17191. That starts where the last segment ends, but the high bits of the two sector numbers differ
            // ((8192 >> 8) xor (16384 >> 8)) >> 3 = 12, masked with not(4 - 1) still 12), so L0D07 does not join them.
            // Close: 9003 data sectors for 9000 sectors of data; nothing to release ((8195 + 808 - 9000) rounded down to 4 is 0).
            string image = CopyOf("toolshed_hd_20000_c4.dsk");
            Os9RbfFileSystem fs = Mount(image, false);
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(19984));
            byte[] data = Pattern(9000 * SectorSize, 23);

            fs.WriteFile("/huge", data);

            byte[] after = Read(image);
            uint fd = RawFind(after, "/huge");
            Assert.That(fd, Is.EqualTo(16u));
            List<uint[]> segments = Segments(Sector(after, fd));
            Assert.That(segments.Count, Is.EqualTo(3));
            Assert.That(segments[0], Is.EqualTo(new uint[] { 17, 3 }));
            Assert.That(segments[1], Is.EqualTo(new uint[] { 8192, 8192 }));
            Assert.That(segments[2], Is.EqualTo(new uint[] { 16384, 808 }));
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(19984 - (4 + 8192 + 808)));
            Assert.That(fs.ReadFile("/huge"), Is.EqualTo(data));

            // V.MapSct was 2 (map sector 3), not below the 2 whole map sectors, so the next search starts again at map sector 1: the next
            // descriptor is cluster 5, sector 20.
            fs.WriteFile("/next", new byte[] { 1 });
            Assert.That(RawFind(Read(image), "/next"), Is.EqualTo(20u));

            DcheckResult check = Dcheck(image);
            Assert.That(check.Unreferenced, Is.EqualTo(0));
            Assert.That(check.Free, Is.EqualTo(fs.GetBlocksFree()));

            fs.DeleteFile("/huge");
            fs.DeleteFile("/next");
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(19984));
            Assert.That(Dcheck(image).Unreferenced, Is.EqualTo(0));
        }

        // ---- many map sectors ----

        [Test]
        public void HardDisk_FatScanStartsAtTheMapSectorOfTheLastAllocation_AndSplitsAtMapSectors()
        {
            // toolshed_hd_40000.dsk: 40000 sectors, 1 sector per bit, 5000 map bytes (19 whole map sectors), allocation size 8. Free: 30 to 2047
            // in map sector 1, every bit of map sectors 2 to 19, and 38912 to 39999 in map sector 20.
            // /a, 100 bytes: descriptor 30 (map sector 1, V.MapSct = 0); 8 asked for the data: 31 to 38; Close keeps 31 and releases 32 to 38.
            // /b, 2048 sectors: descriptor 32 (search from map sector 1); data wants 2048: map sector 1 has 33 to 2047 (2015), too few; map
            // sector 2: 2048 to 4095. V.MapSct = 1.
            // /c, 1 byte: the descriptor search starts at map sector 2, which is full, and takes 4096 in map sector 3, not 33 in map sector 1.
            // V.MapSct = 2. Data: 4097 to 4104 asked, 4097 kept.
            // /d, 5000 sectors: descriptor 4098; data wants 2048: map sector 3 has 4099 to 6143 (2045), too few; map sector 4: 6144 to 8191
            // (V.MapSct = 3); then 2048 more from map sector 4 on: map sector 5, 8192 to 10239 (V.MapSct = 4); then 904 from map sector 5 on: map
            // sector 6, 10240 to 11143. Each starts where the one before ends, but in another map sector, so they stay three segments.
            string image = CopyOf("toolshed_hd_40000.dsk");
            Os9RbfFileSystem fs = Mount(image, false);
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(39970));

            fs.WriteFile("/a", Pattern(100, 31));
            fs.WriteFile("/b", Pattern(2048 * SectorSize, 32));

            // A write that cannot fit moves V.MapSct while it searches, but it fails, so V.MapSct must be what it was (map sector 2) for /c.
            byte[] beforeFailure = Read(image);
            Assert.Throws<IOException>(() => fs.WriteFile("/toobig", new byte[39970 * SectorSize]));
            Assert.That(Read(image), Is.EqualTo(beforeFailure));

            fs.WriteFile("/c", new byte[] { 7 });
            fs.WriteFile("/d", Pattern(5000 * SectorSize, 33));

            byte[] after = Read(image);
            Assert.That(RawFind(after, "/a"), Is.EqualTo(30u));
            Assert.That(Segments(Sector(after, 30))[0], Is.EqualTo(new uint[] { 31, 1 }));
            Assert.That(RawFind(after, "/b"), Is.EqualTo(32u));
            Assert.That(Segments(Sector(after, 32))[0], Is.EqualTo(new uint[] { 2048, 2048 }));
            Assert.That(RawFind(after, "/c"), Is.EqualTo(4096u), "the search starts at the map sector of the last allocation (V.MapSct)");
            Assert.That(Segments(Sector(after, 4096))[0], Is.EqualTo(new uint[] { 4097, 1 }));
            Assert.That(RawFind(after, "/d"), Is.EqualTo(4098u));
            List<uint[]> d = Segments(Sector(after, 4098));
            Assert.That(d.Count, Is.EqualTo(3), "a segment is not joined across a map sector");
            Assert.That(d[0], Is.EqualTo(new uint[] { 6144, 2048 }));
            Assert.That(d[1], Is.EqualTo(new uint[] { 8192, 2048 }));
            Assert.That(d[2], Is.EqualTo(new uint[] { 10240, 904 }));
            Assert.That(fs.ReadFile("/d"), Is.EqualTo(Pattern(5000 * SectorSize, 33)));
            Assert.That(MapBit(after, 33) && MapBit(after, 34), Is.False, "33 to 2047 stayed free");

            DcheckResult check = Dcheck(image);
            Assert.That(check.Unreferenced, Is.EqualTo(0));
            Assert.That(check.Files, Is.EqualTo(4));
            Assert.That(check.Free, Is.EqualTo(39970 - 2 - 2049 - 2 - 5001));

            // ClrFBits releases each segment inside its own map sector.
            fs.DeleteFile("/d");
            fs.DeleteFile("/b");
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(39970 - 4));
            Assert.That(Dcheck(image).Unreferenced, Is.EqualTo(0));
        }

        [Test]
        public void Create_TakesTheDescriptorBeforeTheDirectoryGrows()
        {
            // rbf.asm Create calls FatScan for the descriptor first, then adds the directory entry (which can make the directory grow), and the
            // data comes after that with the write. toolshed_hd_40000.dsk, free from sector 30 on:
            // /D: descriptor 30, its 8 sectors 31 to 38. Each 1-byte file /D/fN then takes a descriptor and one data sector (8 asked, 7 given back
            // at Close), so the 62 files take 39 to 162 and /D holds 64 entries in its 8 sectors. The next file gets descriptor 163; the
            // directory grows by 8 (164 to 171, a second segment, as it does not start where (31, 8) ends); the data takes 172.
            string image = CopyOf("toolshed_hd_40000.dsk");
            Os9RbfFileSystem fs = Mount(image, false);
            fs.CreateDirectory("/D");
            for (int i = 0; i < 62; i++)
            {
                fs.WriteFile("/D/f" + i, new byte[] { (byte)i });
            }

            byte[] full = Read(image);
            Assert.That(U32(Sector(full, 30), 9), Is.EqualTo(64 * 32));
            Assert.That(RawFind(full, "/D/f61"), Is.EqualTo(161u));

            fs.WriteFile("/D/last", new byte[] { 99 });

            byte[] after = Read(image);
            Assert.That(RawFind(after, "/D/last"), Is.EqualTo(163u));
            List<uint[]> directory = Segments(Sector(after, 30));
            Assert.That(directory.Count, Is.EqualTo(2));
            Assert.That(directory[0], Is.EqualTo(new uint[] { 31, 8 }));
            Assert.That(directory[1], Is.EqualTo(new uint[] { 164, 8 }));
            Assert.That(Segments(Sector(after, 163))[0], Is.EqualTo(new uint[] { 172, 1 }));
            Assert.That(Dcheck(image).Unreferenced, Is.EqualTo(0));
        }

        [Test]
        public void TwoSectorClusters_ClrFBitsRoundsTheFirstSectorUpToACluster()
        {
            // ClrFBits adds DD.BIT - 1 to the first sector before it turns it into a cluster, and turns the count into clusters rounding down,
            // so a run that starts inside a cluster does not release that cluster. A first segment that starts after the descriptor and holds
            // an even number of sectors is not what RBF makes, but other software can. Here /x on toolshed_80ds_c2.dsk (descriptor 12, cluster 6)
            // gets the first segment (13, 2) by hand: sector 13 is in cluster 6, sector 14 in cluster 7.
            // Delete: L0EFE to 0 releases the whole segment: ClrFBits(13, 2) is cluster (13 + 1) >> 1 = 7, 2 >> 1 = 1 cluster; the count left is 0.
            // Then ClrFBits(12, 0 + 1) is 1 >> 1 = 0 clusters: nothing. Cluster 6 stays marked used, as RBF leaves it; cluster 7 is free.
            string image = CopyOf("toolshed_80ds_c2.dsk");
            Os9RbfFileSystem fs = Mount(image, false);
            fs.WriteFile("/x", Pattern(300, 71));
            Unmount();

            byte[] raw = Read(image);
            uint fd = RawFind(raw, "/x");
            Assert.That(fd, Is.EqualTo(12u));
            Assert.That(Segments(Sector(raw, fd))[0], Is.EqualTo(new uint[] { 13, 3 }));
            raw[(fd * SectorSize) + 16 + 4] = 2;
            File.WriteAllBytes(image, raw);
            fs = Mount(image, false);

            fs.DeleteFile("/x");

            byte[] after = Read(image);
            Assert.That(MapBit(after, 6), Is.True, "the descriptor's cluster is not released");
            Assert.That(MapBit(after, 7), Is.False);
        }

        [Test]
        public void HardDisk_UucpPackage_WriteAcrossItsMapSectorsAndDelete_ToolshedAgrees()
        {
            // uucpbb21_6809.dsk: 10000 sectors, 1250 map bytes (map sectors 1 to 4 whole, sector 5 has 226 bytes), allocation size 8.
            // Free: 4264 to 6143 (map sector 3), 6144 to 8191 (map sector 4), 8192 to 9999 (map sector 5).
            // /big, 3000 sectors: descriptor 4264 (V.MapSct = 2); the data wants 2048: map sector 3 has 4265 to 6143 (1879), too few; map
            // sector 4 has 6144 to 8191 (V.MapSct = 3); then 952 from map sector 4 on: map sector 5, 8192 to 9143 (V.MapSct = 4). Two segments.
            // V.MapSct = 4 is not below the 4 whole map sectors, so the next search starts at map sector 1 again: /small gets descriptor 4265.
            string image = CopyOf("uucpbb21_6809.dsk");
            Os9RbfFileSystem fs = Mount(image, false);
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(5736));
            byte[] big = Pattern(3000 * SectorSize, 41);

            fs.WriteFile("/big", big);
            fs.WriteFile("/small", Pattern(10, 42));

            byte[] after = Read(image);
            Assert.That(RawFind(after, "/big"), Is.EqualTo(4264u));
            List<uint[]> segments = Segments(Sector(after, 4264));
            Assert.That(segments.Count, Is.EqualTo(2));
            Assert.That(segments[0], Is.EqualTo(new uint[] { 6144, 2048 }));
            Assert.That(segments[1], Is.EqualTo(new uint[] { 8192, 952 }));
            Assert.That(RawFind(after, "/small"), Is.EqualTo(4265u));
            Assert.That(fs.ReadFile("/big"), Is.EqualTo(big));

            DcheckResult check = Dcheck(image);
            Assert.That(check.Unreferenced, Is.EqualTo(0));
            Assert.That(check.Free, Is.EqualTo(5736 - 3001 - 2));
            CompareWithToolshed(image, fs);

            fs.DeleteFile("/big");
            fs.DeleteFile("/small");
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(5736));
            Assert.That(Dcheck(image).Unreferenced, Is.EqualTo(0));
        }

        [Test]
        public void HardDisk_AFileWhoseSegmentCrossesAMapSector_IsNotReleased()
        {
            // /UUCP/CMDS_WIN/expire on uucpbb21_6809.dsk (put there by other software) has the segment 2010 to 2064, which runs from map
            // sector 1 into map sector 2. ClrFBits reads one map sector and clears the bits there with F$DelBit, so RBF would leave 2048 to
            // 2064 marked used (and clear bits in memory past its buffer). The code refuses before it changes anything.
            string image = CopyOf("uucpbb21_6809.dsk");
            Os9RbfFileSystem fs = Mount(image, false);
            byte[] before = Read(image);

            IOException? delete = Assert.Throws<IOException>(() => fs.DeleteFile("/UUCP/CMDS_WIN/expire"));
            Assert.That(delete!.Message, Does.Contain("crosses an allocation map sector"));
            IOException? overwrite = Assert.Throws<IOException>(() => fs.WriteFile("/UUCP/CMDS_WIN/expire", new byte[1]));
            Assert.That(overwrite!.Message, Does.Contain("crosses an allocation map sector"));
            Assert.That(Read(image), Is.EqualTo(before));
        }

        [Test]
        public void DriveWireVolume_TheLastOf256MapSectorsIsNeverSearched()
        {
            // built_l2_coco3_dw.dsk: 524268 sectors, 65534 map bytes, so 255 whole map sectors and a 256th with 254 bytes. FatScan keeps the
            // map sector number in one byte (rbf.asm L0E34 "incb"): after sector 255 it becomes 0, which is V.ResBit (0), so it is skipped,
            // and the search ends. The 2028 sectors of map sector 256 (522240 to 524267) are counted free but never handed out.
            // Here every bit of map sectors 1 to 255 is set by hand, so only map sector 256 has free bits.
            string image = CopyOf("built_l2_coco3_dw.dsk");
            byte[] raw = Read(image);
            for (int i = 0; i < 255 * SectorSize; i++)
            {
                raw[SectorSize + i] = 0xFF;
            }

            File.WriteAllBytes(image, raw);
            Os9RbfFileSystem fs = Mount(image, false);
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(524268 - 522240));

            IOException? ex = Assert.Throws<IOException>(() => fs.WriteFile("/x", new byte[1]));
            Assert.That(ex!.Message, Does.Contain("full"));
            Assert.That(Read(image), Is.EqualTo(raw));
        }

        // ---- the segment list ----

        [Test]
        public void SegmentList_AFileGrowsTo47SegmentsAndNotTo48()
        {
            // NOS9_6809_L1_v030300_coco1_80d.dsk, with the map set by hand so that the only free sectors are single sectors with a used one between
            // them. L0D07 fills at most 47 slots (it stops looking one slot before the end of the list), and no two of these sectors can join.
            // A file of n data sectors needs 1 sector for the descriptor and n segments.
            string image = CopyOf("NOS9_6809_L1_v030300_coco1_80d.dsk");
            byte[] raw = Read(image);
            int total = 2880;
            for (int c = 0; c < total; c++)
            {
                SetMapBit(raw, c);
            }

            // 49 free sectors: 1900, 1902, ..., 1996.
            for (int k = 0; k < 49; k++)
            {
                int c = 1900 + (2 * k);
                raw[SectorSize + (c >> 3)] &= (byte)~(0x80 >> (c & 7));
            }

            File.WriteAllBytes(image, raw);
            Os9RbfFileSystem fs = Mount(image, false);
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(49));

            IOException? ex = Assert.Throws<IOException>(() => fs.WriteFile("/frag48", Pattern(48 * SectorSize, 51)));
            Assert.That(ex!.Message, Does.Contain("segment"));
            Assert.That(Read(image), Is.EqualTo(raw), "a refused write changes nothing");

            fs.WriteFile("/frag47", Pattern(47 * SectorSize, 52));
            byte[] after = Read(image);
            uint fd = RawFind(after, "/frag47");
            Assert.That(fd, Is.EqualTo(1900u));
            Assert.That(Segments(Sector(after, fd)).Count, Is.EqualTo(47));
            Assert.That(fs.ReadFile("/frag47"), Is.EqualTo(Pattern(47 * SectorSize, 52)));
            Assert.That(fs.GetBlocksFree(), Is.EqualTo(1));
        }

        // ---- what RBF leaves in sectors ----

        [Test]
        public void WriteFile_TheRestOfThePartLastSector_KeepsWhatTheSectorHeld()
        {
            // RBF reads a sector before it writes part of it (rbf.asm Read4D9), so the bytes after the end of the data keep what was there.
            // toolshed_hd_40000.dsk: the new file's descriptor is 30 and its data sector 31. Sector 31 is filled with 0x5A by hand first.
            string image = CopyOf("toolshed_hd_40000.dsk");
            byte[] raw = Read(image);
            for (int i = 0; i < SectorSize; i++)
            {
                raw[(31 * SectorSize) + i] = 0x5A;
            }

            File.WriteAllBytes(image, raw);
            Os9RbfFileSystem fs = Mount(image, false);

            fs.WriteFile("/t", Pattern(10, 61));

            byte[] after = Read(image);
            Assert.That(Segments(Sector(after, 30))[0], Is.EqualTo(new uint[] { 31, 1 }));
            byte[] sector = Sector(after, 31);
            for (int i = 0; i < 10; i++)
            {
                Assert.That(sector[i], Is.EqualTo(Pattern(10, 61)[i]));
            }

            for (int i = 10; i < SectorSize; i++)
            {
                Assert.That(sector[i], Is.EqualTo(0x5A), "byte " + i);
            }
        }

        // ---- owner and permissions (rbf.asm ChkAttrs and FindFile) ----

        // A copy of NOS9_6809_L1_v030300_coco1_40d_1.dsk with the attribute and owner bytes of one descriptor changed by hand.
        private string PermissionImage(string path, int attributes, int owner)
        {
            string image = CopyOf("NOS9_6809_L1_v030300_coco1_40d_1.dsk");
            byte[] raw = Read(image);
            uint fd = RawFind(raw, path);
            Assert.That(fd, Is.Not.EqualTo(0u), path);
            if (attributes >= 0)
            {
                raw[(fd * SectorSize) + 0] = (byte)attributes;
            }

            if (owner >= 0)
            {
                raw[(fd * SectorSize) + 1] = (byte)(owner >> 8);
                raw[(fd * SectorSize) + 2] = (byte)owner;
            }

            File.WriteAllBytes(image, raw);
            return image;
        }

        [Test]
        public void Permissions_AnotherUser_MayNotDeleteOrWriteAFileWithoutPublicWrite()
        {
            // startup: attributes 0x0B (owner read and write, public read), owner 0. User 5 is not the owner, so the write bit is looked for
            // among the public bits (shifted left by 3: 0x10), which are not set: E$FNA.
            string image = PermissionImage("/startup", 0x0B, 0);
            Os9RbfFileSystem fs = Mount(image, false);
            byte[] before = Read(image);
            fs.User = 5;

            IOException? delete = Assert.Throws<IOException>(() => fs.DeleteFile("/startup"));
            Assert.That(delete!.Message, Does.Contain("E$FNA"));
            Assert.Throws<IOException>(() => fs.WriteFile("/startup", new byte[1]));
            Assert.Throws<IOException>(() => fs.Rename("/startup", "/boot"));
            Assert.That(Read(image), Is.EqualTo(before));
            Assert.That(fs.ReadFile("/startup").Length, Is.GreaterThan(0), "reading is not checked");

            fs.User = 0;
            fs.DeleteFile("/startup");
            Assert.That(RawFind(Read(image), "/startup"), Is.EqualTo(0u));
        }

        [Test]
        public void Permissions_TheOwner_MayDeleteWithTheOwnerBits()
        {
            string image = PermissionImage("/startup", 0x0B, 5);
            Os9RbfFileSystem fs = Mount(image, false);
            fs.User = 5;

            fs.DeleteFile("/startup");

            Assert.That(RawFind(Read(image), "/startup"), Is.EqualTo(0u));
        }

        [Test]
        public void Permissions_TheSuperUser_StillNeedsTheOwnerWriteBit()
        {
            // ChkAttrs treats user 0 as the owner but still requires the owner bits: a file with only owner read (0x01) cannot be deleted.
            string image = PermissionImage("/startup", 0x01, 0);
            Os9RbfFileSystem fs = Mount(image, false);
            byte[] before = Read(image);

            Assert.Throws<IOException>(() => fs.DeleteFile("/startup"));
            Assert.Throws<IOException>(() => fs.WriteFile("/startup", new byte[1]));
            Assert.That(Read(image), Is.EqualTo(before));
        }

        [Test]
        public void Permissions_EveryDirectoryOnThePath_NeedsWrite()
        {
            // FindFile checks each directory before it searches it, with the mode of the call plus the directory bit. CMDS made 0x83 (directory,
            // owner read and write, no public bits): user 5 may not create, write or delete in it; user 0 may.
            string image = PermissionImage("/CMDS", 0x83, 0);
            Os9RbfFileSystem fs = Mount(image, false);
            byte[] before = Read(image);
            fs.User = 5;

            Assert.Throws<IOException>(() => fs.WriteFile("/CMDS/new", new byte[1]));
            Assert.Throws<IOException>(() => fs.DeleteFile("/CMDS/dir"));
            Assert.Throws<IOException>(() => fs.CreateDirectory("/CMDS/D"));
            Assert.That(Read(image), Is.EqualTo(before));

            fs.User = 0;
            fs.WriteFile("/CMDS/new", new byte[1]);
            Assert.That(RawFind(Read(image), "/CMDS/new"), Is.Not.EqualTo(0u));
        }

        [Test]
        public void Permissions_TheRootDirectoryIsCheckedToo()
        {
            // The root directory is the first one FindFile checks for a path that starts with the device name. Root made 0x81 (directory, owner
            // read only): not even user 0 may create a file in it, since the owner write bit is clear.
            string image = PermissionImage("/", 0x81, 0);
            Os9RbfFileSystem fs = Mount(image, false);
            byte[] before = Read(image);

            Assert.Throws<IOException>(() => fs.WriteFile("/new", new byte[1]));
            Assert.Throws<IOException>(() => fs.WriteFile("/CMDS/new", new byte[1]));
            Assert.That(Read(image), Is.EqualTo(before));
        }

        [Test]
        public void Permissions_ANewFileBelongsToTheUser()
        {
            // Create stores P$User in FD.OWN. The root directory (0xBF) gives public write.
            string image = CopyOf("NOS9_6809_L1_v030300_coco1_40d_1.dsk");
            Os9RbfFileSystem fs = Mount(image, false);
            fs.User = 0x0107;

            fs.WriteFile("/mine", new byte[3]);
            fs.CreateDirectory("/MYDIR");

            byte[] after = Read(image);
            Assert.That(U16(Sector(after, RawFind(after, "/mine")), 1), Is.EqualTo(0x0107));
            Assert.That(U16(Sector(after, RawFind(after, "/MYDIR")), 1), Is.EqualTo(0x0107));

            // The owner may delete it without the super user; another user may not, though the directory lets everybody write.
            fs.User = 9;
            Assert.Throws<IOException>(() => fs.DeleteFile("/mine"));
            fs.User = 0x0107;
            fs.DeleteFile("/mine");
        }

        [Test]
        public void Permissions_DeleteDirectory_NeedsTheOwnerOrTheSuperUser()
        {
            // deldir runs "attr -d", and attr refuses unless the user is the owner or the super user (attr.asm: F$ID, compare with FD.OWN).
            // The directory gives everybody read and write (0xBF), so only that check stops user 5.
            string image = CopyOf("NOS9_6809_L1_v030300_coco1_40d_1.dsk");
            Os9RbfFileSystem fs = Mount(image, false);
            fs.CreateDirectory("/EMPTY");
            byte[] before = Read(image);
            fs.User = 5;

            IOException? ex = Assert.Throws<IOException>(() => fs.DeleteDirectory("/EMPTY"));
            Assert.That(ex!.Message, Does.Contain("owner"));
            Assert.That(Read(image), Is.EqualTo(before));

            fs.User = 0;
            fs.DeleteDirectory("/EMPTY");
        }

        [Test]
        public void DeleteDirectory_ClearsTheDirectoryBitAndStampsTheDirectoriesDeldirChangesInto()
        {
            // deldir changes into every directory of the path in read and write mode (I$ChgDir sets the date last modified), attr -d clears
            // the directory bit and its open in update mode sets the date, then I$Delete releases it.
            string image = CopyOf("NOS9_6809_L1_v030300_coco1_40d_1.dsk");
            Os9RbfFileSystem fs = Mount(image, false);
            fs.CreateDirectory("/A");
            fs.CreateDirectory("/A/B");
            uint a = RawFind(Read(image), "/A");
            uint b = RawFind(Read(image), "/A/B");
            fs.Clock = () => new DateTime(2027, 3, 4, 5, 6, 0);

            fs.DeleteDirectory("/A/B");

            byte[] after = Read(image);
            byte[] expectedDate = { 127, 3, 4, 5, 6 };
            byte[] rootDescriptor = Sector(after, U24(after, 8));
            Assert.That(new[] { rootDescriptor[3], rootDescriptor[4], rootDescriptor[5], rootDescriptor[6], rootDescriptor[7] }, Is.EqualTo(expectedDate), "root");
            byte[] aDescriptor = Sector(after, a);
            Assert.That(new[] { aDescriptor[3], aDescriptor[4], aDescriptor[5], aDescriptor[6], aDescriptor[7] }, Is.EqualTo(expectedDate), "A");
            byte[] bDescriptor = Sector(after, b);
            Assert.That(bDescriptor[0], Is.EqualTo(0x3F), "attr -d took the directory bit away");
            Assert.That(new[] { bDescriptor[3], bDescriptor[4], bDescriptor[5], bDescriptor[6], bDescriptor[7] }, Is.EqualTo(expectedDate), "B");
            Assert.That(bDescriptor[8], Is.EqualTo(0));
            Assert.That(RawFind(after, "/A/B"), Is.EqualTo(0u));
        }
    }
}
