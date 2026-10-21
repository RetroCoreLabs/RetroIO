//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// Tests of the OS-9 (RBF) write members on a copy of the real NitrOS-9 disk NITR09.DSK.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every test works on a copy in a temporary folder; the original image is never opened for writing.
    /// The expected sector contents come from the NitrOS-9 sources (rbf.asm, defs/rbf.d) and from the layout of the directories on the real disk.
    /// </para>
    /// <para>
    /// The tests marked with Toolshed hand the changed image to the Toolshed program os9 (run with wsl.exe in the Ubuntu distribution) and
    /// read the result back with it: os9 dcheck (the file system checker), os9 dir and os9 copy. Toolshed is a separate program written by other people,
    /// so it is the independent check. Those tests skip when wsl.exe or os9 is not available.
    /// </para>
    /// <para>
    /// The real disk has 1440 sectors, 970 used (the first 18 sectors plus the files) and 470 free as one block at sectors 970 to 1439.
    /// Toolshed dcheck reports 16 sectors that are marked used but are in no file: sectors 2 to 17, the rest of the reserved first track. A change that
    /// loses a sector or marks one wrongly changes that number, so every Toolshed test checks it.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class Os9RbfWriteTests
    {
        private const int SectorSize = 256;
        private static readonly DateTime FixedTime = new DateTime(2026, 10, 21, 12, 34, 0);

        private string _folder = string.Empty;
        private string _imagePath = string.Empty;
        private byte[] _original = Array.Empty<byte>();
        private FileStream? _stream;
        private GenericBlockDevice? _device;
        private Os9RbfFileSystem? _fs;

        [SetUp]
        public void SetUp()
        {
            string source = ImagePath("dsk", "NITR09.DSK");
            if (!File.Exists(source))
            {
                Assert.Ignore("Test image not found: NITR09.DSK");
            }

            _folder = Path.Combine(Path.GetTempPath(), "os9write-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            _imagePath = Path.Combine(_folder, "work.dsk");
            File.Copy(source, _imagePath);
            _original = File.ReadAllBytes(_imagePath);
            Mount(false);
        }

        [TearDown]
        public void TearDown()
        {
            Unmount();
            if (_folder.Length > 0 && Directory.Exists(_folder))
            {
                Directory.Delete(_folder, true);
            }
        }

        private static string ImagePath(string folder, string name)
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable("RETROIO_DRAGON_TEST_IMAGES");
            string root = !string.IsNullOrEmpty(fromEnvironment)
                ? fromEnvironment
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "RetroFS", "test-images");
            return Path.Combine(root, folder, name);
        }

        private void Mount(bool readOnly)
        {
            _stream = new FileStream(_imagePath, FileMode.Open, readOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.ReadWrite);
            _device = new GenericBlockDevice(BlockDeviceDescriptor.DragonVDK_DS40);
            _device.Mount(_stream, readOnly);
            _fs = new Os9RbfFileSystem();
            _fs.Initialize(_device);
            _fs.Clock = () => FixedTime;
        }

        private void Unmount()
        {
            _device?.Dispose();
            _stream?.Dispose();
            _device = null;
            _stream = null;
            _fs = null;
        }

        // Makes sure the changes are in the image file, then reads the whole file.
        private byte[] Image()
        {
            _device!.Flush();
            using (FileStream stream = new FileStream(_imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
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

        // ---- raw readers (written for the tests, not shared with the production code) ----

        private static uint U24(byte[] image, int offset)
        {
            return (uint)((image[offset] << 16) | (image[offset + 1] << 8) | image[offset + 2]);
        }

        private static int U16(byte[] image, int offset)
        {
            return (image[offset] << 8) | image[offset + 1];
        }

        private static long U32(byte[] image, int offset)
        {
            return ((long)image[offset] << 24) | ((long)image[offset + 1] << 16) | ((long)image[offset + 2] << 8) | image[offset + 3];
        }

        private static byte[] Sector(byte[] image, uint lsn)
        {
            byte[] sector = new byte[SectorSize];
            Buffer.BlockCopy(image, (int)lsn * SectorSize, sector, 0, SectorSize);
            return sector;
        }

        // The segments of a descriptor as pairs (first sector, count).
        private static List<uint[]> Segments(byte[] descriptor)
        {
            List<uint[]> list = new List<uint[]>();
            for (int i = 0; i < 48; i++)
            {
                int at = 16 + (i * 5);
                int count = U16(descriptor, at + 3);
                if (count == 0)
                {
                    break;
                }

                list.Add(new uint[] { U24(descriptor, at), (uint)count });
            }

            return list;
        }

        // The bytes of a file from its descriptor, cut at the size in the descriptor.
        private static byte[] FileBytes(byte[] image, byte[] descriptor)
        {
            long size = U32(descriptor, 9);
            byte[] result = new byte[size];
            long done = 0;
            List<uint[]> segments = Segments(descriptor);
            for (int s = 0; s < segments.Count; s++)
            {
                long take = Math.Min((long)segments[s][1] * SectorSize, size - done);
                if (take > 0)
                {
                    Buffer.BlockCopy(image, (int)segments[s][0] * SectorSize, result, (int)done, (int)take);
                    done += take;
                }
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

        // Finds the descriptor sector of a path by walking the raw sectors from the root descriptor named in sector 0. Gives 0 when not found.
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

        // Byte offset in the file of the live entry with this name, or -1.
        private static int RawEntryOffset(byte[] image, string directory, string name)
        {
            byte[] data = FileBytes(image, Sector(image, RawFind(image, directory)));
            for (int o = 0; o + 32 <= data.Length; o += 32)
            {
                if (data[o] != 0 && string.Equals(EntryName(data, o), name, StringComparison.OrdinalIgnoreCase))
                {
                    return o;
                }
            }

            return -1;
        }

        private static bool MapBit(byte[] image, uint sector)
        {
            return (image[SectorSize + (int)(sector >> 3)] & (0x80 >> (int)(sector & 7))) != 0;
        }

        private static int MapBitsSet(byte[] image)
        {
            int count = 0;
            for (int bit = 0; bit < 1440; bit++)
            {
                if (MapBit(image, (uint)bit))
                {
                    count++;
                }
            }

            return count;
        }

        private static byte[] MapBytes(byte[] image)
        {
            byte[] map = new byte[180];
            Buffer.BlockCopy(image, SectorSize, map, 0, 180);
            return map;
        }

        // ---- Toolshed ----

        private sealed class ToolshedResult
        {
            public string Text = string.Empty;
        }

        private static string WslPath(string windowsPath)
        {
            string full = Path.GetFullPath(windowsPath);
            return "/mnt/" + char.ToLowerInvariant(full[0]) + full.Substring(2).Replace('\\', '/');
        }

        // Runs "os9 <arguments>" in WSL Ubuntu and returns everything it printed. Skips the test when WSL or os9 is missing.
        private static string Os9(params string[] arguments)
        {
            ProcessStartInfo info = new ProcessStartInfo("wsl.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            info.ArgumentList.Add("-d");
            info.ArgumentList.Add("Ubuntu");
            info.ArgumentList.Add("--exec");
            info.ArgumentList.Add("os9");
            for (int i = 0; i < arguments.Length; i++)
            {
                info.ArgumentList.Add(arguments[i]);
            }

            Process? process;
            try
            {
                process = Process.Start(info);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Ignore("wsl.exe is not available, so Toolshed cannot be run");
                return string.Empty;
            }

            Process started = process!;
            using (started)
            {
                System.Threading.Tasks.Task<string> error = started.StandardError.ReadToEndAsync();
                string output = started.StandardOutput.ReadToEnd();
                if (!started.WaitForExit(120000))
                {
                    started.Kill(true);
                    Assert.Fail("os9 did not finish in 120 seconds");
                }

                string errors = error.Result;
                string all = output + errors;
                if (all.Contains("execvpe(os9) failed") || all.Contains("Command 'os9' not found"))
                {
                    Assert.Ignore("Toolshed os9 is not installed in WSL Ubuntu");
                }

                return all;
            }
        }

        // The image path in the form "<image>,<path inside the image>" that os9 takes.
        private string Spec(string insidePath)
        {
            return WslPath(_imagePath) + "," + insidePath;
        }

        // Runs os9 dcheck and checks the file structure: intact, no sector in a file but missing from the map, no sector used twice, and exactly
        // the 16 reserved sectors (2 to 17) marked used without being in a file.
        private void AssertToolshedClean(int directories, int files)
        {
            Image();
            string text = Os9("dcheck", WslPath(_imagePath));
            Assert.That(text, Does.Contain("file structure is intact"), text);
            Assert.That(text, Does.Contain("0 clusters in file structure but not in allocation map"), text);
            Assert.That(text, Does.Contain("0 previously allocated cluster found"), text);
            Assert.That(text, Does.Contain("0 bad file decriptor sector"), text);
            Assert.That(text, Does.Contain("16 clusters in allocation map but not in file structure"), text);
            Assert.That(text, Does.Contain(directories + " directories"), text);
            Assert.That(text, Does.Contain(files + " files"), text);
        }

        // Copies a file out of the image with os9 copy and gives its bytes.
        private byte[] ToolshedCopyOut(string insidePath)
        {
            Image();
            string target = Path.Combine(_folder, "out-" + Guid.NewGuid().ToString("N"));
            string text = Os9("copy", Spec(insidePath), WslPath(target));
            Assert.That(File.Exists(target), Is.True, "os9 copy gave no file: " + text);
            return File.ReadAllBytes(target);
        }

        // Runs os9 dir -e and gives (name, byte count, attributes) for every line of the listing.
        private List<string[]> ToolshedDir(string insidePath)
        {
            Image();
            string text = Os9("dir", "-e", Spec(insidePath));
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

            Assert.That(text, Does.Not.Contain("dir: error"), text);
            return rows;
        }

        // ---- the reader against Toolshed, on the untouched disk ----

        [Test]
        public void Toolshed_ListingsOfTheUntouchedDisk_EqualOurListings()
        {
            string[] directories = { "/", "/SYS", "/DEFS", "/CMDS" };
            for (int d = 0; d < directories.Length; d++)
            {
                DirectoryEntry[] ours = _fs!.ListDirectory(directories[d]);
                List<string[]> theirs = ToolshedDir(directories[d]);

                Assert.That(theirs.Count, Is.EqualTo(ours.Length), directories[d]);
                for (int i = 0; i < ours.Length; i++)
                {
                    Assert.That(theirs[i][0], Is.EqualTo(ours[i].Name), directories[d] + " name " + i);
                    Assert.That(long.Parse(theirs[i][1]), Is.EqualTo(ours[i].Metadata.Size), directories[d] + " size of " + ours[i].Name);
                    Assert.That(theirs[i][2], Is.EqualTo(ours[i].Metadata.Attributes), directories[d] + " attributes of " + ours[i].Name);
                }
            }
        }

        [Test]
        public void Toolshed_FreeSectorsAndStructureOfTheUntouchedDisk_AgreeWithUs()
        {
            Image();
            string free = Os9("free", WslPath(_imagePath));
            Assert.That(free, Does.Contain("470 Free sectors"), free);
            Assert.That(_fs!.GetBlocksFree(), Is.EqualTo(470));

            AssertToolshedClean(4, 83);
        }

        // ---- WriteFile ----

        [Test]
        public void WriteFile_NewFile_WritesDescriptorDataMapAndEntryAsRbfDoes()
        {
            byte[] data = Pattern(300, 1);

            _fs!.WriteFile("/hello", data);

            byte[] image = Image();
            uint fd = RawFind(image, "/hello");
            Assert.That(fd, Is.EqualTo(970u), "the descriptor takes the first free sector");
            byte[] descriptor = Sector(image, fd);
            Assert.That(descriptor[0], Is.EqualTo(0x0B), "attributes: owner read and write, public read");
            Assert.That(U16(descriptor, 1), Is.EqualTo(0), "owner");
            Assert.That(descriptor[8], Is.EqualTo(1), "link count");
            Assert.That(U32(descriptor, 9), Is.EqualTo(300), "size");
            Assert.That(new[] { descriptor[3], descriptor[4], descriptor[5], descriptor[6], descriptor[7] }, Is.EqualTo(new byte[] { 126, 10, 21, 12, 34 }), "date last modified");
            Assert.That(new[] { descriptor[13], descriptor[14], descriptor[15] }, Is.EqualTo(new byte[] { 126, 10, 21 }), "creation date");
            List<uint[]> segments = Segments(descriptor);
            Assert.That(segments.Count, Is.EqualTo(1));
            Assert.That(segments[0][0], Is.EqualTo(971u));
            Assert.That(segments[0][1], Is.EqualTo(2u));
            Assert.That(FileBytes(image, descriptor), Is.EqualTo(data));
            Assert.That(Sector(image, 972)[44], Is.EqualTo(0), "the unused rest of the last sector is zero");

            Assert.That(MapBit(image, 970) && MapBit(image, 971) && MapBit(image, 972), Is.True);
            Assert.That(MapBit(image, 973), Is.False);
            Assert.That(MapBitsSet(image), Is.EqualTo(973));
            Assert.That(_fs.GetBlocksFree(), Is.EqualTo(467));
        }

        [Test]
        public void WriteFile_NewFile_AddsAnEntryAtTheEndOfTheRootDirectory()
        {
            _fs!.WriteFile("/hello", Pattern(10, 2));

            byte[] image = Image();
            byte[] rootDescriptor = Sector(image, 18);
            Assert.That(U32(rootDescriptor, 9), Is.EqualTo(256), "the directory grows by one entry of 32 bytes (224 + 32)");
            Assert.That(Segments(rootDescriptor).Count, Is.EqualTo(1), "the 8 sectors it already has are enough");
            Assert.That(new[] { rootDescriptor[3], rootDescriptor[4], rootDescriptor[5], rootDescriptor[6], rootDescriptor[7] }, Is.EqualTo(new byte[] { 126, 10, 21, 12, 34 }), "the directory was changed ");

            byte[] entry = new byte[32];
            Buffer.BlockCopy(image, 19 * SectorSize + 224, entry, 0, 32);
            byte[] expected = new byte[32];
            expected[0] = (byte)'h';
            expected[1] = (byte)'e';
            expected[2] = (byte)'l';
            expected[3] = (byte)'l';
            expected[4] = (byte)('o' | 0x80);
            expected[29] = 0;
            expected[30] = 0x03;
            expected[31] = 0xCA;
            Assert.That(entry, Is.EqualTo(expected), "name with bit 7 on the last character, zeros, then the descriptor sector 970 = 0x0003CA");
        }

        [Test]
        public void WriteFile_Toolshed_ReadsBackTheNewFileAndTheStructureIsClean()
        {
            byte[] data = Pattern(1000, 3);

            _fs!.WriteFile("/hello", data);

            Assert.That(ToolshedCopyOut("/hello"), Is.EqualTo(data));
            List<string[]> root = ToolshedDir("/");
            Assert.That(root.Count, Is.EqualTo(6));
            Assert.That(root[5][0], Is.EqualTo("hello"));
            Assert.That(root[5][1], Is.EqualTo("1000"));
            Assert.That(root[5][2], Is.EqualTo("----r-wr"));
            AssertToolshedClean(4, 84);
        }

        [Test]
        public void WriteFile_ExistingFile_KeepsItsDescriptorAndFreesTheOldSectors()
        {
            byte[] image0 = Image();
            uint fd = RawFind(image0, "/startup");
            byte[] before = Sector(image0, fd);
            byte[] data = Pattern(1500, 4);

            _fs!.WriteFile("/startup", data);

            byte[] image = Image();
            Assert.That(RawFind(image, "/startup"), Is.EqualTo(fd), "same descriptor sector");
            byte[] after = Sector(image, fd);
            Assert.That(after[0], Is.EqualTo(before[0]), "attributes kept");
            Assert.That(new[] { after[13], after[14], after[15] }, Is.EqualTo(new[] { before[13], before[14], before[15] }), "creation date kept");
            Assert.That(new[] { after[3], after[4], after[5], after[6], after[7] }, Is.EqualTo(new byte[] { 126, 10, 21, 12, 34 }), "modified date updated");
            Assert.That(U32(after, 9), Is.EqualTo(1500));
            Assert.That(FileBytes(image, after), Is.EqualTo(data));
            Assert.That(_fs.ReadFile("/startup"), Is.EqualTo(data));

            // The old data sector (one sector) is free again, the new data needs 6 sectors: net 5 more used.
            Assert.That(_fs.GetBlocksFree(), Is.EqualTo(470 - 6 + 1));
            Assert.That(ToolshedCopyOut("/startup"), Is.EqualTo(data));
            AssertToolshedClean(4, 83);
        }

        [Test]
        public void WriteFile_ExistingFileMadeSmaller_ReleasesTheTail()
        {
            byte[] data = Pattern(100, 5);

            _fs!.WriteFile("/CMDS/asm", data);

            Assert.That(_fs.GetBlocksFree(), Is.EqualTo(470 + 27 - 1));
            Assert.That(ToolshedCopyOut("/CMDS/asm"), Is.EqualTo(data));
            AssertToolshedClean(4, 83);
        }

        [Test]
        public void WriteFile_EmptyFile_HasADescriptorAndNoSegments()
        {
            _fs!.WriteFile("/empty", Array.Empty<byte>());

            byte[] image = Image();
            byte[] descriptor = Sector(image, RawFind(image, "/empty"));
            Assert.That(U32(descriptor, 9), Is.EqualTo(0));
            Assert.That(Segments(descriptor).Count, Is.EqualTo(0));
            Assert.That(_fs.GetBlocksFree(), Is.EqualTo(469));
            Assert.That(_fs.ReadFile("/empty"), Is.Empty);
            Assert.That(ToolshedCopyOut("/empty"), Is.Empty);
            AssertToolshedClean(4, 84);
        }

        [Test]
        public void WriteFile_FillsTheDiskExactly_AndThenReportsFull()
        {
            byte[] data = Pattern(469 * SectorSize, 6);

            _fs!.WriteFile("/big", data);

            Assert.That(_fs.GetBlocksFree(), Is.EqualTo(0));
            Assert.That(ToolshedCopyOut("/big"), Is.EqualTo(data));
            AssertToolshedClean(4, 84);

            byte[] full = Image();
            Assert.Throws<IOException>(() => _fs.WriteFile("/more", new byte[1]));
            Assert.That(Image(), Is.EqualTo(full), "a failed write changes nothing");
        }

        [Test]
        public void WriteFile_TooBigForTheDisk_ChangesNothing()
        {
            byte[] before = Image();

            IOException? ex = Assert.Throws<IOException>(() => _fs!.WriteFile("/huge", new byte[470 * SectorSize]));

            Assert.That(ex!.Message, Does.Contain("full"));
            Assert.That(Image(), Is.EqualTo(before));
            Assert.That(_fs!.GetBlocksFree(), Is.EqualTo(470));
        }

        [Test]
        public void WriteFile_InAFragmentedDisk_UsesSeveralSegments()
        {
            // Take all but 5 free sectors, then free two files of 47 and 40 sectors. The largest hole is 47, so 85 sectors need two segments.
            _fs!.WriteFile("/big", Pattern(465 * SectorSize, 7));
            _fs.DeleteFile("/CMDS/config");
            _fs.DeleteFile("/CMDS/dcheck");
            byte[] data = Pattern(85 * SectorSize, 8);

            _fs.WriteFile("/frag", data);

            byte[] image = Image();
            List<uint[]> segments = Segments(Sector(image, RawFind(image, "/frag")));
            Assert.That(segments.Count, Is.GreaterThanOrEqualTo(2), "no single hole holds 85 sectors");
            Assert.That(ToolshedCopyOut("/frag"), Is.EqualTo(data));
            Assert.That(ToolshedCopyOut("/big"), Is.EqualTo(Pattern(465 * SectorSize, 7)));
            AssertToolshedClean(4, 83);
        }

        [Test]
        public void WriteFile_FileThatNeedsMoreThan48Segments_IsReportedAndChangesNothing()
        {
            // Write 200 small files (two sectors each: descriptor and data), then delete every second one. That leaves 100 holes of two sectors.
            for (int i = 0; i < 200; i++)
            {
                _fs!.WriteFile("/f" + i, new byte[10]);
            }

            for (int i = 0; i < 200; i += 2)
            {
                _fs!.DeleteFile("/f" + i);
            }

            // Ask for one sector fewer than all the free sectors. Only the holes and the tail can hold it, and the holes alone are more than 48 segments.
            int free = _fs!.GetBlocksFree();
            byte[] before = Image();
            IOException? ex = Assert.Throws<IOException>(() => _fs.WriteFile("/frag", new byte[(free - 1) * SectorSize]));

            Assert.That(ex!.Message, Does.Contain("segment"));
            Assert.That(Image(), Is.EqualTo(before));
        }

        [Test]
        public void WriteFile_IntoANewSubDirectory_AndNested_ReadBackByToolshed()
        {
            byte[] data = Pattern(777, 9);
            _fs!.CreateDirectory("/NEW");
            _fs.CreateDirectory("/NEW/SUB");

            _fs.WriteFile("/NEW/SUB/f.txt", data);

            Assert.That(_fs.ReadFile("/new/sub/F.TXT"), Is.EqualTo(data));
            Assert.That(ToolshedCopyOut("/NEW/SUB/f.txt"), Is.EqualTo(data));
            AssertToolshedClean(6, 84);
        }

        [Test]
        public void WriteFile_Errors_AreTheRightKind()
        {
            Assert.Throws<IOException>(() => _fs!.WriteFile("/CMDS", new byte[1]), "a directory is not a file");
            Assert.Throws<DirectoryNotFoundException>(() => _fs!.WriteFile("/NOSUCH/x", new byte[1]));
            Assert.Throws<DirectoryNotFoundException>(() => _fs!.WriteFile("/startup/x", new byte[1]), "a file is not a directory");
            Assert.Throws<IOException>(() => _fs!.WriteFile("/", new byte[1]));
            Assert.Throws<ArgumentNullException>(() => _fs!.WriteFile("/x", null!));
            Assert.That(Image(), Is.EqualTo(_original), "none of that changed the disk");
        }

        [TestCase("has space")]
        [TestCase("")]
        [TestCase("a%b")]
        [TestCase("-dash")]
        [TestCase("..x")]
        [TestCase("123456789012345678901234567890")]
        public void WriteFile_IllegalNames_AreRefused(string name)
        {
            Assert.Throws<IOException>(() => _fs!.WriteFile("/" + name, new byte[1]));
            Assert.That(Image(), Is.EqualTo(_original));
        }

        [TestCase("a")]
        [TestCase("_x")]
        [TestCase("$sys")]
        [TestCase(".hidden")]
        [TestCase("a-b.c_d$")]
        [TestCase("12345678901234567890123456789")]
        public void WriteFile_LegalNames_AreAccepted(string name)
        {
            _fs!.WriteFile("/" + name, new byte[3]);

            Assert.That(_fs.ReadFile("/" + name), Is.EqualTo(new byte[3]));
            Assert.That(ToolshedCopyOut("/" + name), Is.EqualTo(new byte[3]));
        }

        [Test]
        public void WriteFile_NameOf29Characters_FillsTheNameFieldWithNoZeroByte()
        {
            string name = "12345678901234567890123456789";

            _fs!.WriteFile("/" + name, new byte[1]);

            byte[] image = Image();
            Assert.That(image[19 * SectorSize + 224 + 28], Is.EqualTo((byte)('9' | 0x80)));
            Assert.That(_fs.ListDirectory("/")[5].Name, Is.EqualTo(name));
        }

        [Test]
        public void WriteFile_OnAReadOnlyDevice_IsRefused()
        {
            Unmount();
            Mount(true);

            Assert.Throws<IOException>(() => _fs!.WriteFile("/x", new byte[1]));
            Assert.Throws<IOException>(() => _fs!.DeleteFile("/startup"));
            Assert.Throws<IOException>(() => _fs!.CreateDirectory("/D"));
            Assert.Throws<IOException>(() => _fs!.DeleteDirectory("/SYS"));
            Assert.Throws<IOException>(() => _fs!.Rename("/startup", "/y"));
            Assert.That(Image(), Is.EqualTo(_original));
        }

        [Test]
        public void WriteFile_OnAVolumeWithTwoSectorsPerBit_IsRefusedNotGuessed()
        {
            byte[] changed = (byte[])_original.Clone();
            changed[7] = 2;
            Unmount();
            File.WriteAllBytes(_imagePath, changed);
            Mount(false);

            Assert.Throws<NotSupportedException>(() => _fs!.WriteFile("/x", new byte[1]));
            Assert.Throws<NotSupportedException>(() => _fs!.CreateDirectory("/D"));
        }

        // ---- many files in one directory ----

        [Test]
        public void WriteFile_ManyFiles_GrowTheDirectoryByAnotherSegment()
        {
            _fs!.CreateDirectory("/MANY");
            for (int i = 0; i < 70; i++)
            {
                _fs.WriteFile("/MANY/file" + i, new byte[] { (byte)i });
            }

            byte[] image = Image();
            byte[] descriptor = Sector(image, RawFind(image, "/MANY"));
            Assert.That(U32(descriptor, 9), Is.EqualTo((2 + 70) * 32), "72 entries");
            long sectors = 0;
            List<uint[]> segments = Segments(descriptor);
            for (int s = 0; s < segments.Count; s++)
            {
                sectors += segments[s][1];
            }

            Assert.That(sectors, Is.EqualTo(16), "8 sectors held 64 entries, one more segment of 8 holds the other 8");
            Assert.That(segments.Count, Is.EqualTo(2));
            Assert.That(_fs.ListDirectory("/MANY").Length, Is.EqualTo(70));

            List<string[]> rows = ToolshedDir("/MANY");
            Assert.That(rows.Count, Is.EqualTo(70));
            Assert.That(ToolshedCopyOut("/MANY/file69"), Is.EqualTo(new byte[] { 69 }));
            AssertToolshedClean(5, 83 + 70);
        }

        // ---- DeleteFile ----

        [Test]
        public void DeleteFile_FreesSegmentsAndDescriptorAndMarksTheEntryFree()
        {
            byte[] image0 = Image();
            uint fd = RawFind(image0, "/CMDS/asm");
            int entryOffset = RawEntryOffset(image0, "/CMDS", "asm");
            Assert.That(entryOffset, Is.GreaterThan(0));

            _fs!.DeleteFile("/CMDS/asm");

            byte[] image = Image();
            Assert.That(MapBit(image, fd), Is.False, "descriptor sector free");
            for (uint s = 104; s < 104 + 27; s++)
            {
                Assert.That(MapBit(image, s), Is.False, "data sector " + s);
            }

            Assert.That(MapBitsSet(image), Is.EqualTo(970 - 28));
            byte[] cmds = Sector(image, 85);
            int sector = (int)(U24(cmds, 16) + (uint)(entryOffset / SectorSize));
            Assert.That(image[sector * SectorSize + (entryOffset % SectorSize)], Is.EqualTo(0), "first byte of the entry is zero");
            Assert.That(image[sector * SectorSize + (entryOffset % SectorSize) + 1], Is.EqualTo(image0[sector * SectorSize + (entryOffset % SectorSize) + 1]), "RBF clears only the first byte");

            byte[] freed = Sector(image, fd);
            Assert.That(U32(freed, 9), Is.EqualTo(0), "size zero");
            Assert.That(Segments(freed).Count, Is.EqualTo(0), "no segments");
            Assert.That(U32(cmds, 9), Is.EqualTo(2336), "the directory does not shrink");
            Assert.That(new[] { cmds[3], cmds[4], cmds[5], cmds[6], cmds[7] }, Is.EqualTo(new byte[] { 126, 10, 21, 12, 34 }), "the directory was changed");

            Assert.That(_fs.GetBlocksFree(), Is.EqualTo(470 + 28));
            Assert.That(_fs.ListDirectory("/CMDS").Length, Is.EqualTo(70));
            Assert.Throws<FileNotFoundException>(() => _fs.ReadFile("/CMDS/asm"));
            AssertToolshedClean(4, 82);
            Assert.That(ToolshedDir("/CMDS").Count, Is.EqualTo(70));
        }

        [Test]
        public void DeleteFile_ThenWrite_ReusesTheFreeSlotAndTheFreeSectors()
        {
            byte[] image0 = Image();
            int entryOffset = RawEntryOffset(image0, "/CMDS", "asm");

            _fs!.DeleteFile("/CMDS/asm");
            _fs.WriteFile("/CMDS/zzz", Pattern(50, 10));

            byte[] image = Image();
            Assert.That(RawEntryOffset(image, "/CMDS", "zzz"), Is.EqualTo(entryOffset), "the first free slot is taken");
            Assert.That(U32(Sector(image, 85), 9), Is.EqualTo(2336), "the directory did not grow");
            Assert.That(RawFind(image, "/CMDS/zzz"), Is.EqualTo(103u), "the first free sector is the old descriptor sector");
            Assert.That(ToolshedCopyOut("/CMDS/zzz"), Is.EqualTo(Pattern(50, 10)));
            AssertToolshedClean(4, 83);
        }

        [Test]
        public void DeleteFile_FileWithTwoLinks_OnlyCountsTheLinkDown()
        {
            // No call here makes a second link, but a disk made by other software can have one. RBF decrements the count and frees nothing until it is zero.
            Unmount();
            byte[] changed = (byte[])_original.Clone();
            changed[(103 * SectorSize) + 8] = 2;
            File.WriteAllBytes(_imagePath, changed);
            Mount(false);

            _fs!.DeleteFile("/CMDS/asm");

            byte[] image = Image();
            Assert.That(Sector(image, 103)[8], Is.EqualTo(1), "link count went down by one");
            Assert.That(U32(Sector(image, 103), 9), Is.EqualTo(6912), "the file is still there");
            Assert.That(MapBit(image, 103) && MapBit(image, 104) && MapBit(image, 130), Is.True, "nothing was freed");
            Assert.That(RawEntryOffset(image, "/CMDS", "asm"), Is.EqualTo(-1), "the entry is gone");
            Assert.That(_fs.GetBlocksFree(), Is.EqualTo(470));
        }

        [Test]
        public void CreateDirectory_WhenFewerSectorsAreFreeThanTheAllocationSize_UsesOneSectorAndLeaksNothing()
        {
            // Leave 3 free sectors. The directory needs a descriptor (1) and would like 8; it gets 1.
            _fs!.WriteFile("/big", Pattern(466 * SectorSize, 16));
            Assert.That(_fs.GetBlocksFree(), Is.EqualTo(3));

            _fs.CreateDirectory("/NEW");

            byte[] image = Image();
            byte[] descriptor = Sector(image, RawFind(image, "/NEW"));
            List<uint[]> segments = Segments(descriptor);
            Assert.That(segments.Count, Is.EqualTo(1));
            Assert.That(segments[0][1], Is.EqualTo(1u));
            Assert.That(_fs.GetBlocksFree(), Is.EqualTo(1), "the failed try for 8 sectors gave its sectors back");
            AssertToolshedClean(5, 84);
        }

        [Test]
        public void DeleteFile_Errors_AreTheRightKind()
        {
            Assert.Throws<FileNotFoundException>(() => _fs!.DeleteFile("/NOSUCH"));
            Assert.Throws<FileNotFoundException>(() => _fs!.DeleteFile("/CMDS/."));
            Assert.Throws<IOException>(() => _fs!.DeleteFile("/CMDS"), "a directory is deleted with DeleteDirectory");
            Assert.That(Image(), Is.EqualTo(_original));
        }

        // ---- CreateDirectory / DeleteDirectory ----

        [Test]
        public void CreateDirectory_WritesTheDescriptorAndTheTwoStartEntriesAsMakdirDoes()
        {
            _fs!.CreateDirectory("/NEW");

            byte[] image = Image();
            uint fd = RawFind(image, "/NEW");
            Assert.That(fd, Is.EqualTo(970u));
            byte[] descriptor = Sector(image, fd);
            Assert.That(descriptor[0], Is.EqualTo(0xBF), "directory, owner and public read, write, execute");
            Assert.That(descriptor[8], Is.EqualTo(1));
            Assert.That(U32(descriptor, 9), Is.EqualTo(64));
            List<uint[]> segments = Segments(descriptor);
            Assert.That(segments.Count, Is.EqualTo(1));
            Assert.That(segments[0][0], Is.EqualTo(971u));
            Assert.That(segments[0][1], Is.EqualTo(8u), "the segment allocation size from sector 0, as the other directories on the disk");

            byte[] first = Sector(image, 971);
            byte[] expected = new byte[SectorSize];
            expected[0] = 0x2E;
            expected[1] = 0xAE;
            expected[31] = 0x12;
            expected[32] = 0xAE;
            expected[63] = 0xCA;
            expected[62] = 0x03;
            Assert.That(first, Is.EqualTo(expected), "'..' to the parent (sector 18), '.' to itself (sector 970)");
            for (uint s = 972; s < 979; s++)
            {
                Assert.That(Sector(image, s), Is.EqualTo(new byte[SectorSize]), "sector " + s);
            }

            Assert.That(MapBitsSet(image), Is.EqualTo(970 + 9));
            Assert.That(_fs.ListDirectory("/NEW"), Is.Empty);
            Assert.That(_fs.GetMetadata("/NEW")!.IsDirectory, Is.True);
            Assert.That(_fs.GetMetadata("/NEW")!.Attributes, Is.EqualTo("d-ewrewr"));
        }

        [Test]
        public void CreateDirectory_Toolshed_ListsItAndTheStructureIsClean()
        {
            _fs!.CreateDirectory("/NEW");
            _fs.WriteFile("/NEW/inside", Pattern(10, 11));

            List<string[]> root = ToolshedDir("/");
            Assert.That(root.Count, Is.EqualTo(6));
            Assert.That(root[5][0], Is.EqualTo("NEW"));
            Assert.That(root[5][1], Is.EqualTo("96"), "64 bytes at the start plus the entry of the file written inside");
            Assert.That(root[5][2], Is.EqualTo("d-ewrewr"));
            Assert.That(ToolshedDir("/NEW").Count, Is.EqualTo(1));
            Assert.That(ToolshedCopyOut("/NEW/inside"), Is.EqualTo(Pattern(10, 11)));
            AssertToolshedClean(5, 84);
        }

        [Test]
        public void CreateDirectory_Errors_AreTheRightKind()
        {
            Assert.Throws<IOException>(() => _fs!.CreateDirectory("/CMDS"), "exists");
            Assert.Throws<IOException>(() => _fs!.CreateDirectory("/startup"), "a file has that name");
            Assert.Throws<IOException>(() => _fs!.CreateDirectory("/bad name"));
            Assert.Throws<DirectoryNotFoundException>(() => _fs!.CreateDirectory("/NOSUCH/D"));
            Assert.That(Image(), Is.EqualTo(_original));
        }

        [Test]
        public void DeleteDirectory_Empty_GivesBackExactlyWhatCreateTook()
        {
            byte[] mapBefore = MapBytes(Image());

            _fs!.CreateDirectory("/NEW");
            _fs.DeleteDirectory("/NEW");

            byte[] image = Image();
            Assert.That(MapBytes(image), Is.EqualTo(mapBefore), "the allocation map is as it was");
            Assert.That(_fs.GetBlocksFree(), Is.EqualTo(470));
            Assert.That(RawFind(image, "/NEW"), Is.EqualTo(0u));
            AssertToolshedClean(4, 83);
        }

        [Test]
        public void DeleteDirectory_NotEmpty_Root_AndFile_AreRefused()
        {
            Assert.Throws<IOException>(() => _fs!.DeleteDirectory("/CMDS"), "not empty");
            Assert.Throws<IOException>(() => _fs!.DeleteDirectory("/"), "root");
            Assert.Throws<DirectoryNotFoundException>(() => _fs!.DeleteDirectory("/startup"), "a file");
            Assert.Throws<DirectoryNotFoundException>(() => _fs!.DeleteDirectory("/NOSUCH"));
            Assert.That(Image(), Is.EqualTo(_original));
        }

        [Test]
        public void DeleteDirectory_AfterItsFilesAreDeleted_Works()
        {
            _fs!.CreateDirectory("/NEW");
            _fs.WriteFile("/NEW/a", new byte[5]);
            Assert.Throws<IOException>(() => _fs.DeleteDirectory("/NEW"));

            _fs.DeleteFile("/NEW/a");
            _fs.DeleteDirectory("/NEW");

            Assert.That(_fs.GetBlocksFree(), Is.EqualTo(470));
            AssertToolshedClean(4, 83);
        }

        // ---- Rename ----

        [Test]
        public void Rename_InTheSameDirectory_ChangesOnlyTheNameField()
        {
            byte[] image0 = Image();
            uint fd = RawFind(image0, "/startup");
            byte[] descriptorBefore = Sector(image0, fd);

            _fs!.Rename("/startup", "/boot");

            byte[] image = Image();
            Assert.That(RawFind(image, "/boot"), Is.EqualTo(fd));
            Assert.That(RawFind(image, "/startup"), Is.EqualTo(0u));
            Assert.That(Sector(image, fd), Is.EqualTo(descriptorBefore), "the descriptor is not touched");
            Assert.That(MapBytes(image), Is.EqualTo(MapBytes(image0)), "the allocation map is not touched");

            int offset = RawEntryOffset(image, "/", "boot");
            byte[] root = FileBytes(image, Sector(image, 18));
            Assert.That(root[offset + 3], Is.EqualTo((byte)(0x74 | 0x80)));
            for (int i = 4; i < 29; i++)
            {
                Assert.That(root[offset + i], Is.EqualTo(0), "no byte of the old, longer name is left at " + i);
            }

            Assert.That(ToolshedCopyOut("/boot"), Is.EqualTo(_fs.ReadFile("/boot")));
            List<string[]> rows = ToolshedDir("/");
            Assert.That(rows[4][0], Is.EqualTo("boot"));
            AssertToolshedClean(4, 83);
        }

        [Test]
        public void Rename_ADirectory_KeepsItsContent()
        {
            _fs!.Rename("/SYS", "/SYSTEM");

            Assert.That(_fs.ListDirectory("/SYSTEM").Length, Is.EqualTo(4));
            Assert.Throws<FileNotFoundException>(() => _fs.ListDirectory("/SYS"));
            Assert.That(ToolshedDir("/SYSTEM").Count, Is.EqualTo(4));
            AssertToolshedClean(4, 83);
        }

        [Test]
        public void Rename_OnlyTheCaseChanges_IsAllowed()
        {
            _fs!.Rename("/startup", "/STARTUP");

            Assert.That(_fs.ListDirectory("/")[4].Name, Is.EqualTo("STARTUP"));
            AssertToolshedClean(4, 83);
        }

        [Test]
        public void Rename_Errors_AreTheRightKind()
        {
            Assert.Throws<FileNotFoundException>(() => _fs!.Rename("/NOSUCH", "/x"));
            Assert.Throws<IOException>(() => _fs!.Rename("/startup", "/CMDS"), "the new name is taken by a directory");
            Assert.Throws<IOException>(() => _fs!.Rename("/startup", "/os9boot"), "the new name is taken (case ignored)");
            Assert.Throws<IOException>(() => _fs!.Rename("/startup", "/bad name"));
            Assert.Throws<IOException>(() => _fs!.Rename("/startup", "/.."));
            Assert.Throws<DirectoryNotFoundException>(() => _fs!.Rename("/startup", "/NOSUCH/x"));
            Assert.Throws<FileNotFoundException>(() => _fs!.Rename("/CMDS/.", "/x"));
            Assert.That(Image(), Is.EqualTo(_original));
        }

        [Test]
        public void Rename_MoveAFileToAnotherDirectory_MovesOnlyTheEntry()
        {
            byte[] image0 = Image();
            uint fd = RawFind(image0, "/startup");

            _fs!.Rename("/startup", "/SYS/startup2");

            byte[] image = Image();
            Assert.That(RawFind(image, "/SYS/startup2"), Is.EqualTo(fd));
            Assert.That(RawFind(image, "/startup"), Is.EqualTo(0u));
            Assert.That(MapBytes(image), Is.EqualTo(MapBytes(image0)));
            Assert.That(ToolshedCopyOut("/SYS/startup2"), Is.EqualTo(_fs.ReadFile("/SYS/startup2")));
            Assert.That(ToolshedDir("/SYS").Count, Is.EqualTo(5));
            AssertToolshedClean(4, 83);
        }

        [Test]
        public void Rename_MoveADirectory_PointsItsParentEntryAtTheNewParent()
        {
            _fs!.Rename("/DEFS", "/CMDS/DEFS");

            byte[] image = Image();
            uint defs = RawFind(image, "/CMDS/DEFS");
            byte[] data = FileBytes(image, Sector(image, defs));
            Assert.That(EntryName(data, 0), Is.EqualTo(".."));
            Assert.That(U24(data, 29), Is.EqualTo(85u), "the parent is now CMDS (descriptor sector 85)");
            Assert.That(_fs.ListDirectory("/CMDS/DEFS").Length, Is.EqualTo(6));
            Assert.That(ToolshedDir("/CMDS/DEFS").Count, Is.EqualTo(6));
            AssertToolshedClean(4, 83);
        }

        [Test]
        public void Rename_MoveADirectoryIntoItself_IsRefused()
        {
            Assert.Throws<IOException>(() => _fs!.Rename("/CMDS", "/CMDS/x"));
            Assert.Throws<IOException>(() => _fs!.Rename("/CMDS", "/CMDS/./x"));
            _fs!.CreateDirectory("/CMDS/sub");
            byte[] before = Image();
            Assert.Throws<IOException>(() => _fs.Rename("/CMDS", "/CMDS/sub/x"));
            Assert.That(Image(), Is.EqualTo(before));
        }

        [Test]
        public void Rename_MoveIntoAFullDirectory_GrowsIt()
        {
            _fs!.CreateDirectory("/D");
            for (int i = 0; i < 62; i++)
            {
                _fs.WriteFile("/D/f" + i, new byte[1]);
            }

            // 64 entries fill the 8 sectors. One more must grow the directory.
            _fs.Rename("/startup", "/D/startup");

            Assert.That(_fs.ListDirectory("/D").Length, Is.EqualTo(63));
            Assert.That(ToolshedDir("/D").Count, Is.EqualTo(63));
            AssertToolshedClean(5, 83 + 62);
        }

        // ---- everything together ----

        [Test]
        public void Mixed_ManyChanges_LeaveAnImageToolshedCallsIntact()
        {
            _fs!.CreateDirectory("/work");
            _fs.CreateDirectory("/work/a");
            _fs.WriteFile("/work/a/one", Pattern(5000, 12));
            _fs.WriteFile("/work/two", Pattern(300, 13));
            _fs.Rename("/work/two", "/work/a/two");
            _fs.DeleteFile("/CMDS/config");
            _fs.WriteFile("/work/a/one", Pattern(900, 14));
            _fs.Rename("/work/a", "/b");
            _fs.WriteFile("/b/three", Pattern(2500, 15));
            _fs.DeleteFile("/b/two");
            _fs.CreateDirectory("/tmp");
            _fs.DeleteDirectory("/tmp");

            Assert.That(ToolshedCopyOut("/b/one"), Is.EqualTo(Pattern(900, 14)));
            Assert.That(ToolshedCopyOut("/b/three"), Is.EqualTo(Pattern(2500, 15)));
            AssertToolshedClean(6, 83 - 1 + 2);

            // Open the changed image again with a new file system object: our own reader reads what we wrote.
            Unmount();
            Mount(true);
            Assert.That(_fs!.ReadFile("/b/one"), Is.EqualTo(Pattern(900, 14)));
            Assert.That(_fs.ListDirectory("/work").Length, Is.EqualTo(0));
        }
    }
}
