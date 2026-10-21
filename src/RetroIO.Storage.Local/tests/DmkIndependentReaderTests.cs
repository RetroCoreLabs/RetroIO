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
using System.Security.Cryptography;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Drivers;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Checks DMK images with dmk2jv3, a DMK reader written by other people, so that our DMK reader and writer are not only checked by our own
    /// CRC routines.
    /// </summary>
    /// <remarks>
    /// <para><b>The tool</b></para>
    /// dmk2jv3 is part of the cw2dmk package by Tim Mann (GPL 2), kept at github.com/qbarnes/cw2dmk; release v4.10.1 was used. It reads the DMK
    /// header, walks the 64 IDAM pointers of every track itself, checks the CRC of every sector header and every data field itself (its own CRC
    /// table and its own start value), and writes each sector it found to a JV3 file with a flag byte that has bit 08H set when either CRC was
    /// wrong. A header CRC error and a missing data mark are also counted in the "total errors" line it prints. These tests read the JV3 file
    /// back (the JV3 layout is simple: 2901 entries of track, sector and flags, a write protect byte, then the sector data in entry order) and
    /// compare every sector with what we expect, and require that no sector has the error flag.
    /// <para><b>Installing it</b></para>
    /// The tool is not in this repository. Either of these works:
    ///  - WSL Ubuntu: download cw2dmk-4.10.1-src.tar.gz from the release page of github.com/qbarnes/cw2dmk (SHA-256
    ///    2a986034b882c3f57e5382a3b90f8514d1eceaa2ef947580c8fe5d71b2f68e4f) into a scratch folder, unpack it, run "make dmk2jv3" there and
    ///    copy the program to /usr/local/bin ("sudo install -m 755 dmk2jv3 /usr/local/bin"). The release also has a ready built
    ///    cw2dmk-4.10.1-linux-x86_64.tar.gz with dmk2jv3 in it.
    ///  - Windows: build the same four source files (dmk2jv3.c, crc.c, dmk.h, jv3.h) with MinGW gcc ("gcc -O2 -std=gnu99 -o dmk2jv3.exe
    ///    dmk2jv3.c") and set the environment variable RETROIO_DMK2JV3 to the full path of dmk2jv3.exe. When that variable is set it is used
    ///    instead of WSL, and a wrong path fails the test rather than skipping it.
    /// Without either, every test here is skipped with a message that says so.
    /// <para><b>Images</b></para>
    /// The written images are built blank by this test (a DMK file in the usual layout, its own CRC routine), then changed through
    /// <see cref="DragonDmkDriver"/>, which rebuilds the changed sectors with <see cref="DmkDiskImage.SetSectorImage"/>. The real images are the four
    /// FEdit DMK disks in the dmk folder of RETROIO_DRAGON_TEST_IMAGES (see <see cref="DmkDiskImageTests"/>); the OS-9 disk is NITR09.DSK in its dsk
    /// folder. A test whose image is missing is skipped.
    /// </remarks>
    [TestFixture]
    public class DmkIndependentReaderTests
    {
        private const int Sector = 256;
        private const int SectorsPerTrack = 18;
        private const int TrackLength = 0x1900;

        // JV3 layout, from jv3.h of cw2dmk.
        private const int Jv3HeaderBytes = 34 * 256;
        private const int Jv3Entries = Jv3HeaderBytes / 3;
        private const int Jv3Density = 0x80;
        private const int Jv3Side = 0x10;
        private const int Jv3Error = 0x08;
        private const int Jv3SizeBits = 0x03;
        private const int Jv3Free = 0xFF;

        // The one message dmk2jv3 may print about a good image: the first JV3 header block holds 2901 sectors, and a disk with more (80 tracks
        // double sided with the doubled pointers of the real images) needs the second block, which some old emulators do not read. That is a
        // limit of the JV3 output, not something wrong with the DMK file.
        private const string Jv3SizeWarning = "[Warning: too many total sectors for some emulators (> 2901)]";

        private static readonly string[] RealImages = { "FEdit.dmk", "FEdit_40_2.dmk", "FEdit_80_1.dmk", "FEdit_80_2.dmk" };
        private static readonly int[] RealCylinders = { 40, 40, 80, 80 };
        private static readonly int[] RealSides = { 1, 2, 1, 2 };

        // MD5 of the decoded sector image of each real disk, found by a separate decoder written from the format notes (the same values as in
        // DmkDiskImageTests). Comparing the tool's sectors with these does not use our DMK reader at all.
        private static readonly string[] RealMd5 =
        {
            "34490d791b96a6365b84d03eb5efc6d7",
            "4acf9a71c1f63358fec596a3829a4401",
            "6b493a629bcc9cd418db6f50e2e65249",
            "e2da755ae9ac408a7a0515ee01c557bb"
        };

        private static bool? _wslHasTool;

        private string _folder = string.Empty;

        /// <summary>
        /// Makes an empty folder for the files of one test.
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "retroio-dmk2jv3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        /// <summary>
        /// Removes the folder of the test.
        /// </summary>
        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, true);
            }
        }

        // ------------------------------------------------------------------ real images

        /// <summary>
        /// The tool reads every sector of a real FEdit disk with valid CRCs, and the sectors it finds hash to what the separate decoder found and
        /// equal what our reader decodes.
        /// </summary>
        /// <param name="index">
        /// Which of the four real images.
        /// </param>
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void RealImage_ToolFindsEverySectorWithValidCrcs(int index)
        {
            string path = RealImage("dmk", RealImages[index]);
            byte[] sectors = ToolSectors(path, RealCylinders[index], RealSides[index]);

            Assert.That(Convert.ToHexString(MD5.HashData(sectors)).ToLowerInvariant(), Is.EqualTo(RealMd5[index]), "the sectors dmk2jv3 found");
            using (FileStream stream = File.OpenRead(path))
            {
                Assert.That(DmkDiskImage.Parse(stream).GetSectorImage(), Is.EqualTo(sectors), "our reader and dmk2jv3 agree on every sector");
            }
        }

        /// <summary>
        /// Files written, overwritten, renamed and deleted on a copy of a real FEdit disk through the DMK driver: the tool then finds every sector
        /// with valid CRCs and with the contents the file system holds, and the files read from the tool's sectors are the files written.
        /// </summary>
        /// <remarks>
        /// About a third of the sectors of the real disks have no A1H sync bytes in front of their data mark, and the track headers list most
        /// sectors twice. Our rebuild keeps both as they are, so this checks the rebuild on the layout a real program wrote.
        /// </remarks>
        /// <param name="index">
        /// Which of the four real images.
        /// </param>
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void RealImage_AfterWritesThroughTheDriver_ToolFindsEverySector(int index)
        {
            string path = Path.Combine(_folder, RealImages[index]);
            File.Copy(RealImage("dmk", RealImages[index]), path);

            WriteAndCheck(path, RealCylinders[index], RealSides[index], false);
        }

        // ------------------------------------------------------------------ images we build and write

        /// <summary>
        /// A blank DragonDOS disk in DMK, changed through the DMK driver: the tool finds every sector with valid CRCs and the contents the file
        /// system holds, and the files read from the tool's sectors are the files written.
        /// </summary>
        /// <param name="cylinders">
        /// 40 or 80.
        /// </param>
        /// <param name="sides">
        /// 1 or 2.
        /// </param>
        [TestCase(40, 1)]
        [TestCase(40, 2)]
        [TestCase(80, 1)]
        [TestCase(80, 2)]
        public void DragonDos_WrittenImage_ToolFindsEverySector(int cylinders, int sides)
        {
            RequireTool();
            string path = Path.Combine(_folder, "dragondos.dmk");
            File.WriteAllBytes(path, BuildDmk(DragonDosTestDisk.Format(cylinders, sides), cylinders, sides));

            WriteAndCheck(path, cylinders, sides, false);
        }

        /// <summary>
        /// A blank DECB disk (35 tracks, one side, the only DECB size) in DMK, changed through the DMK driver, checked the same way.
        /// </summary>
        [Test]
        public void Decb_WrittenImage_ToolFindsEverySector()
        {
            RequireTool();
            string path = Path.Combine(_folder, "decb.dmk");
            File.WriteAllBytes(path, BuildDmk(BlankDecbSectors(), 35, 1));

            WriteAndCheck(path, 35, 1, false);
        }

        /// <summary>
        /// The real NitrOS-9 disk (40 tracks, two sides) put in a DMK file, changed through the DMK driver, checked the same way.
        /// </summary>
        [Test]
        public void Os9_WrittenImage_ToolFindsEverySector()
        {
            byte[] sectors = File.ReadAllBytes(RealImage("dsk", "NITR09.DSK"));
            Assert.That(sectors.Length, Is.EqualTo(40 * 2 * SectorsPerTrack * Sector), "NITR09.DSK is a headerless 40 track double sided JVC file");
            RequireTool();
            string path = Path.Combine(_folder, "os9.dmk");
            File.WriteAllBytes(path, BuildDmk(sectors, 40, 2));

            WriteAndCheck(path, 40, 2, true);
        }

        // ------------------------------------------------------------------ the tool catches damage

        /// <summary>
        /// A data byte changed without its CRC: the tool flags that sector, and only that one. This shows the check above can fail.
        /// </summary>
        [Test]
        public void DamagedData_ToolFlagsThatSector()
        {
            RequireTool();
            byte[] file = BuildDmk(DragonDosTestDisk.Format(40, 1), 40, 1);

            // Cylinder 3, sector 5: the first data byte. BuildDmk puts the data mark 7 + 22 + 12 + 3 bytes after the sector header.
            int block = 16 + (3 * TrackLength);
            int header = block + ((file[block + 8] | (file[block + 9] << 8)) & 0x3FFF);
            Assert.That(file[header + 3], Is.EqualTo(5), "pointer 4 is sector 5");
            file[header + 7 + 22 + 12 + 3 + 1] ^= 0x5A;
            string path = Path.Combine(_folder, "bad-data.dmk");
            File.WriteAllBytes(path, file);

            ToolResult result = RunTool(path);
            int flagged = 0;
            for (int i = 0; i < result.Sectors.Count; i++)
            {
                Jv3Sector s = result.Sectors[i];
                if ((s.Flags & Jv3Error) != 0)
                {
                    flagged++;
                    Assert.That(s.Track, Is.EqualTo(3));
                    Assert.That(s.Number, Is.EqualTo(5));
                }
            }

            Assert.That(flagged, Is.EqualTo(1), "exactly the damaged sector is flagged\n" + result.Output);
        }

        /// <summary>
        /// A sector header changed without its CRC: the tool counts an error and flags the sector.
        /// </summary>
        [Test]
        public void DamagedHeader_ToolReportsAnError()
        {
            RequireTool();
            byte[] file = BuildDmk(DragonDosTestDisk.Format(40, 1), 40, 1);
            int block = 16 + (7 * TrackLength);
            int header = block + ((file[block] | (file[block + 1] << 8)) & 0x3FFF);
            file[header + 5] ^= 0x01;
            string path = Path.Combine(_folder, "bad-header.dmk");
            File.WriteAllBytes(path, file);

            ToolResult result = RunTool(path);

            Assert.That(StressToolshed.NumberBefore(result.Output, " total errors"), Is.EqualTo(1), result.Output);
            Assert.That(result.Output, Does.Contain("[Recording ID CRC error as data CRC error]"));
            int flagged = 0;
            for (int i = 0; i < result.Sectors.Count; i++)
            {
                if ((result.Sectors[i].Flags & Jv3Error) != 0)
                {
                    flagged++;
                    Assert.That(result.Sectors[i].Track, Is.EqualTo(7));
                    Assert.That(result.Sectors[i].Number, Is.EqualTo(1));
                }
            }

            Assert.That(flagged, Is.EqualTo(1));
        }

        // ------------------------------------------------------------------ the work

        // Mounts the DMK file read-write, writes, overwrites, renames and deletes files, reads every sector through the driver (what the file
        // system holds), then runs the tool on the file and compares. Last, the files are read from the tool's sectors with the DSK driver.
        private void WriteAndCheck(string path, int cylinders, int sides, bool os9)
        {
            RequireTool();
            int total = cylinders * sides * SectorsPerTrack;
            byte[] before = File.ReadAllBytes(path);
            byte[] expected = new byte[total * Sector];
            string[] names = new string[6];
            byte[][] contents = new byte[6][];
            int[] sizes = { 300, 2500, 9000, 256, 700, 4100 };
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = os9 ? "dmk" + i.ToString("D2") : "DMK" + i.ToString("D2") + ".BIN";
                contents[i] = DragonDosTestDisk.Pattern(sizes[i], 31 + i);
            }

            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                DragonDmkDriver driver = new DragonDmkDriver();
                driver.Mount(stream, false);
                Assert.That(driver.CanWrite, Is.True, "the DMK image mounts writable");

                for (int i = 0; i < names.Length; i++)
                {
                    driver.WriteFile("/" + names[i], contents[i]);
                }

                // Overwrite file 1 with a longer one, rename file 2, delete file 3.
                contents[1] = DragonDosTestDisk.Pattern(6000, 99);
                driver.WriteFile("/" + names[1], contents[1]);
                string renamed = os9 ? "dmkren" : "DMKREN.TXT";
                driver.RenameFile("/" + names[2], "/" + renamed);
                names[2] = renamed;
                driver.DeleteFile("/" + names[3]);
                names[3] = string.Empty;

                for (int lsn = 0; lsn < total; lsn++)
                {
                    int read = driver.ReadBlock(BlockAddress.FromLBA((uint)lsn), new Span<byte>(expected, lsn * Sector, Sector));
                    Assert.That(read, Is.EqualTo(Sector), "sector " + lsn + " read through the driver");
                }

                driver.Dispose();
            }

            Assert.That(File.ReadAllBytes(path), Is.Not.EqualTo(before), "the writes reached the DMK file");

            byte[] fromTool = ToolSectors(path, cylinders, sides);
            for (int lsn = 0; lsn < total; lsn++)
            {
                if (!new ReadOnlySpan<byte>(fromTool, lsn * Sector, Sector).SequenceEqual(new ReadOnlySpan<byte>(expected, lsn * Sector, Sector)))
                {
                    int track = lsn / SectorsPerTrack;
                    Assert.Fail("dmk2jv3 found other data than the file system holds in cylinder " + (track / sides) + " side " + (track % sides)
                        + " sector " + ((lsn % SectorsPerTrack) + 1));
                }
            }

            // The files, read from the sectors the tool found (behind a JVC header) by the DSK driver.
            byte[] jvc = new byte[5 + fromTool.Length];
            jvc[0] = SectorsPerTrack;
            jvc[1] = (byte)sides;
            jvc[2] = 1;
            jvc[3] = 1;
            Buffer.BlockCopy(fromTool, 0, jvc, 5, fromTool.Length);
            DragonDskDriver dsk = new DragonDskDriver();
            dsk.Mount(new MemoryStream(jvc, false), true);
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].Length > 0)
                {
                    Assert.That(dsk.ReadFile("/" + names[i]), Is.EqualTo(contents[i]), names[i] + " read from the sectors dmk2jv3 found");
                }
            }

            Assert.That(dsk.GetMetadata("/" + (os9 ? "dmk03" : "DMK03.BIN")), Is.Null, "the deleted file is gone");
            dsk.Dispose();
        }

        // Runs the tool and gives the sectors it found in logical order (cylinder, side 0 then side 1, sector 1 to 18). Fails when the tool counts
        // an error, prints anything but the one known JV3 size warning, flags a sector, finds a sector that is not 256 bytes double density, a
        // sector number outside 1 to 18, a sector twice with different data, or misses one.
        private byte[] ToolSectors(string dmkPath, int cylinders, int sides)
        {
            ToolResult result = RunTool(dmkPath);
            string output = result.Output;

            Assert.That(StressToolshed.NumberBefore(output, " total errors"), Is.EqualTo(0), "dmk2jv3 counted errors:\n" + output);
            int at = output.IndexOf('[');
            while (at >= 0)
            {
                int end = output.IndexOf(']', at);
                string message = end > at ? output.Substring(at, end - at + 1) : output.Substring(at);
                Assert.That(message, Is.EqualTo(Jv3SizeWarning), "dmk2jv3 printed a message:\n" + output);
                at = output.IndexOf('[', at + 1);
            }

            int total = cylinders * sides * SectorsPerTrack;
            byte[] sectors = new byte[total * Sector];
            bool[] seen = new bool[total];
            for (int i = 0; i < result.Sectors.Count; i++)
            {
                Jv3Sector s = result.Sectors[i];
                int side = (s.Flags & Jv3Side) != 0 ? 1 : 0;
                string where = "cylinder " + s.Track + " side " + side + " sector " + s.Number;
                Assert.That(s.Flags & Jv3Error, Is.EqualTo(0), "dmk2jv3 found a CRC error (header or data) in " + where + "\n" + output);
                Assert.That(s.Flags & Jv3Density, Is.EqualTo(Jv3Density), where + " is double density");
                Assert.That(s.Data.Length, Is.EqualTo(Sector), where + " is 256 bytes");
                Assert.That(s.Track < cylinders && side < sides && s.Number >= 1 && s.Number <= SectorsPerTrack, Is.True, where + " is on the disk");

                int lsn = (((s.Track * sides) + side) * SectorsPerTrack) + (s.Number - 1);
                if (seen[lsn])
                {
                    // The real images list a sector twice in the track header; both copies must be the same sector.
                    Assert.That(new ReadOnlySpan<byte>(sectors, lsn * Sector, Sector).SequenceEqual(s.Data), Is.True, where + " found twice with different data");
                    continue;
                }

                seen[lsn] = true;
                Buffer.BlockCopy(s.Data, 0, sectors, lsn * Sector, Sector);
            }

            for (int lsn = 0; lsn < total; lsn++)
            {
                int track = lsn / SectorsPerTrack;
                Assert.That(seen[lsn], Is.True, "dmk2jv3 did not find cylinder " + (track / sides) + " side " + (track % sides) + " sector " + ((lsn % SectorsPerTrack) + 1));
            }

            return sectors;
        }

        // ------------------------------------------------------------------ running the tool

        private sealed class Jv3Sector
        {
            public int Track;
            public int Number;
            public int Flags;
            public byte[] Data = Array.Empty<byte>();
        }

        private sealed class ToolResult
        {
            public string Output = string.Empty;
            public List<Jv3Sector> Sectors = new List<Jv3Sector>();
        }

        private static string? NativeTool()
        {
            string? path = Environment.GetEnvironmentVariable("RETROIO_DMK2JV3");
            return string.IsNullOrEmpty(path) ? null : path;
        }

        // Skips the test when the tool is not on this machine. The WSL probe runs the tool with no arguments: it then prints its usage.
        private static void RequireTool()
        {
            string? native = NativeTool();
            if (native != null)
            {
                Assert.That(File.Exists(native), Is.True, "RETROIO_DMK2JV3 is set to " + native + ", which does not exist");
                return;
            }

            // One WSL start is asked once per test run, not once per test.
            if (_wslHasTool == null)
            {
                _wslHasTool = StressToolshed.Run("dmk2jv3") != null;
            }

            if (!_wslHasTool.Value)
            {
                Assert.Ignore("dmk2jv3 (from cw2dmk, github.com/qbarnes/cw2dmk) is not installed in WSL Ubuntu and RETROIO_DMK2JV3 is not set; "
                    + "see the remarks of DmkIndependentReaderTests for how to install it");
            }
        }

        // Converts the DMK file to JV3 with the tool, at verbosity 1 (errors, warnings and the "total errors" line), and reads the JV3 file back.
        private ToolResult RunTool(string dmkPath)
        {
            RequireTool();
            string jv3Path = Path.Combine(_folder, Path.GetFileNameWithoutExtension(dmkPath) + ".jv3");
            if (File.Exists(jv3Path))
            {
                File.Delete(jv3Path);
            }

            string? native = NativeTool();
            string? output = native != null
                ? RunNative(native, "-v", "1", dmkPath, jv3Path)
                : StressToolshed.Run("dmk2jv3", "-v", "1", StressToolshed.WslPath(dmkPath), StressToolshed.WslPath(jv3Path));
            Assert.That(output, Is.Not.Null, "dmk2jv3 could not be started");
            Assert.That(File.Exists(jv3Path), Is.True, "dmk2jv3 wrote no JV3 file:\n" + output);

            ToolResult result = new ToolResult { Output = output! };
            ReadJv3(File.ReadAllBytes(jv3Path), result.Sectors);
            return result;
        }

        private static string RunNative(string program, params string[] arguments)
        {
            ProcessStartInfo info = new ProcessStartInfo(program)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            for (int i = 0; i < arguments.Length; i++)
            {
                info.ArgumentList.Add(arguments[i]);
            }

            using (Process process = Process.Start(info)!)
            {
                System.Threading.Tasks.Task<string> error = process.StandardError.ReadToEndAsync();
                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(120000))
                {
                    // Our own child process: killed by its handle, not by name.
                    process.Kill(true);
                    throw new TimeoutException(program + " did not finish in 120 seconds");
                }

                return output + error.Result;
            }
        }

        // Reads a JV3 file as dmk2jv3 writes it: a header block of 2901 entries (track, sector, flags; FFH in the track means unused) and a write
        // protect byte, then the data of those sectors in entry order. When all 2901 entries are used, a second header block of the same shape
        // follows the data of the first, then the data of its sectors. The size bits of a used entry: 0 = 256, 1 = 128, 2 = 1024, 3 = 512 bytes.
        private static void ReadJv3(byte[] jv3, List<Jv3Sector> sectors)
        {
            int headerAt = 0;
            for (int block = 0; block < 2; block++)
            {
                Assert.That(jv3.Length, Is.GreaterThanOrEqualTo(headerAt + Jv3HeaderBytes), "JV3 header block " + block + " is complete");
                int dataAt = headerAt + Jv3HeaderBytes;
                int used = 0;
                for (int entry = 0; entry < Jv3Entries; entry++)
                {
                    int e = headerAt + (entry * 3);
                    if (jv3[e] == Jv3Free)
                    {
                        break;
                    }

                    int flags = jv3[e + 2];
                    int size;
                    switch (flags & Jv3SizeBits)
                    {
                        case 0:
                            size = 256;
                            break;
                        case 1:
                            size = 128;
                            break;
                        case 2:
                            size = 1024;
                            break;
                        default:
                            size = 512;
                            break;
                    }

                    Assert.That(jv3.Length, Is.GreaterThanOrEqualTo(dataAt + size), "the data of JV3 entry " + entry + " is in the file");
                    byte[] data = new byte[size];
                    Buffer.BlockCopy(jv3, dataAt, data, 0, size);
                    dataAt += size;
                    sectors.Add(new Jv3Sector { Track = jv3[e], Number = jv3[e + 1], Flags = flags, Data = data });
                    used++;
                }

                if (used < Jv3Entries || dataAt >= jv3.Length)
                {
                    return;
                }

                headerAt = dataAt;
            }
        }

        // ------------------------------------------------------------------ images

        private static string RealImage(string folder, string name)
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable("RETROIO_DRAGON_TEST_IMAGES");
            string root = !string.IsNullOrEmpty(fromEnvironment)
                ? fromEnvironment
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "RetroFS", "test-images");
            string path = Path.Combine(root, folder, name);
            if (!File.Exists(path))
            {
                Assert.Ignore("Test image not found: " + name);
            }

            return path;
        }

        // A blank DECB disk: 35 tracks of 18 sectors, the granule table (sector 2 of track 17) all FFH (free) and the directory (sectors 3 to 11)
        // all FFH (never used).
        private static byte[] BlankDecbSectors()
        {
            byte[] image = new byte[35 * SectorsPerTrack * Sector];
            int fat = ((17 * SectorsPerTrack) + 1) * Sector;
            int dir = ((17 * SectorsPerTrack) + 2) * Sector;
            for (int i = 0; i < 68; i++)
            {
                image[fat + i] = 0xFF;
            }

            for (int i = 0; i < 9 * Sector; i++)
            {
                image[dir + i] = 0xFF;
            }

            return image;
        }

        // Builds a double density DMK file from sectors in logical order, track length 1900H. Each sector: 32 bytes 4EH, the sector header (FEH,
        // cylinder, side, sector, size code 1, CRC), 22 bytes 4EH, 12 bytes 00H, three A1H, the data mark FBH, 256 data bytes and the data CRC.
        private static byte[] BuildDmk(byte[] sectors, int cylinders, int sides)
        {
            byte[] file = new byte[16 + (cylinders * sides * TrackLength)];
            file[1] = (byte)cylinders;
            file[2] = TrackLength & 0xFF;
            file[3] = TrackLength >> 8;
            file[4] = sides == 1 ? (byte)0x10 : (byte)0;

            for (int cylinder = 0; cylinder < cylinders; cylinder++)
            {
                for (int side = 0; side < sides; side++)
                {
                    int track = (cylinder * sides) + side;
                    int block = 16 + (track * TrackLength);
                    int pos = 0x80;
                    for (int s = 0; s < SectorsPerTrack; s++)
                    {
                        for (int i = 0; i < 32; i++)
                        {
                            file[block + pos++] = 0x4E;
                        }

                        int pointer = 0x8000 | pos;
                        file[block + (s * 2)] = (byte)(pointer & 0xFF);
                        file[block + (s * 2) + 1] = (byte)(pointer >> 8);

                        int header = block + pos;
                        file[header] = 0xFE;
                        file[header + 1] = (byte)cylinder;
                        file[header + 2] = (byte)side;
                        file[header + 3] = (byte)(s + 1);
                        file[header + 4] = 1;
                        int idCrc = Crc(file, header, 5);
                        file[header + 5] = (byte)(idCrc >> 8);
                        file[header + 6] = (byte)(idCrc & 0xFF);
                        pos += 7;
                        for (int i = 0; i < 22; i++)
                        {
                            file[block + pos++] = 0x4E;
                        }

                        for (int i = 0; i < 12; i++)
                        {
                            file[block + pos++] = 0x00;
                        }

                        for (int i = 0; i < 3; i++)
                        {
                            file[block + pos++] = 0xA1;
                        }

                        int mark = block + pos;
                        file[mark] = 0xFB;
                        Buffer.BlockCopy(sectors, ((track * SectorsPerTrack) + s) * Sector, file, mark + 1, Sector);
                        int dataCrc = Crc(file, mark, 257);
                        file[mark + 257] = (byte)(dataCrc >> 8);
                        file[mark + 258] = (byte)(dataCrc & 0xFF);
                        pos += 259;
                    }

                    while (pos < TrackLength)
                    {
                        file[block + pos++] = 0x4E;
                    }
                }
            }

            return file;
        }

        // Disk controller CRC: polynomial 1021H, start FFFFH, high bit first, over the three A1H sync bytes and then the given bytes.
        private static int Crc(byte[] data, int offset, int length)
        {
            int crc = 0xFFFF;
            for (int i = -3; i < length; i++)
            {
                int value = i < 0 ? 0xA1 : data[offset + i];
                crc ^= value << 8;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
                }
            }

            return crc;
        }
    }
}
