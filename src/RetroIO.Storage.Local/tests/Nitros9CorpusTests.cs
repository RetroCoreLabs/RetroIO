//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Local.FileSystems;
using RetroIO.Storage.Local.Streams;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Reads every NitrOS-9 disk image in a folder with RetroIO and compares what it finds with Toolshed, image by image.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The images are the NitrOS-9 project snapshot (nitros9.sourceforge.io/snapshot). They are not in this repository. Set
    /// RETROIO_NITROS9_IMAGES to the folder that holds them; the test is skipped when it is not set. The report goes to the folder named by
    /// RETROIO_NITROS9_REPORT, or to a folder called analysis next to the image folder. RETROIO_NITROS9_FILTER, when set, limits the run to the
    /// images whose file name contains that text. When a file nitros9-images.json lies next to the image folder, its sha256 and page details
    /// are put in the report.
    /// </para>
    /// <para>
    /// For each image: sector 0 is decoded, the image is mounted the way the application mounts it (the driver factory with the Dragon and
    /// CoCo drivers), and when that fails it is mounted on a plain block device so the file system itself can still be checked. The whole tree
    /// is listed and every file read. Toolshed (os9 or decb in WSL Ubuntu, a separate program written by other people) then lists the tree,
    /// counts the free space, checks the volume and copies every file out, and both sides are compared field by field and byte by byte.
    /// Toolshed always works on a copy of the image inside WSL, and the original file is never opened for writing.
    /// </para>
    /// <para>
    /// The test fails when any image is not read the way Toolshed reads it, and the report lists every difference.
    /// </para>
    /// </remarks>
    [TestFixture]
    [Category("Corpus")]
    public class Nitros9CorpusTests
    {
        private const int SectorSize = 256;

        // Most entries one tree walk takes, so a directory that points back at one of its parents cannot run forever.
        private const int MaxEntries = 100000;

        // Deepest directory level the walk goes to, for the same reason.
        private const int MaxDepth = 32;

        // Most lines of one kind kept in the report; the full Toolshed output is in the toolshed folder of the report.
        private const int MaxListed = 50;

        /// <summary>
        /// Every image in RETROIO_NITROS9_IMAGES is read by RetroIO the way Toolshed reads it.
        /// </summary>
        [Test]
        [Explicit("Reads a folder of disk images that is not in the repository and runs Toolshed for each; takes several minutes")]
        public void EveryImage_IsReadTheWayToolshedReadsIt()
        {
            string? imageFolder = Environment.GetEnvironmentVariable("RETROIO_NITROS9_IMAGES");
            if (string.IsNullOrEmpty(imageFolder))
            {
                Assert.Ignore("RETROIO_NITROS9_IMAGES is not set, so there are no images to read");
            }

            if (!Directory.Exists(imageFolder))
            {
                Assert.Ignore("RETROIO_NITROS9_IMAGES names a folder that does not exist: " + imageFolder);
            }

            string? tools = StressToolshed.RunWithTimeout("bash", 60000, "-c", "command -v os9 && command -v decb && command -v timeout");
            if (tools == null || !tools.Contains("os9", StringComparison.Ordinal) || !tools.Contains("decb", StringComparison.Ordinal))
            {
                Assert.Ignore("Toolshed os9 and decb are not available through WSL Ubuntu: " + tools);
            }

            string parent = Path.GetDirectoryName(Path.GetFullPath(imageFolder!).TrimEnd(Path.DirectorySeparatorChar)) ?? imageFolder!;
            string? reportSetting = Environment.GetEnvironmentVariable("RETROIO_NITROS9_REPORT");
            string reportFolder = string.IsNullOrEmpty(reportSetting) ? Path.Combine(parent, "analysis") : reportSetting!;
            string rawFolder = Path.Combine(reportFolder, "toolshed");
            Directory.CreateDirectory(rawFolder);

            Dictionary<string, IndexEntry> index = ReadIndex(Path.Combine(parent, "nitros9-images.json"));
            string? filter = Environment.GetEnvironmentVariable("RETROIO_NITROS9_FILTER");

            string[] files = Directory.GetFiles(imageFolder!);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            string work = Path.Combine(Path.GetTempPath(), "retroio-nitros9-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            List<ImageRecord> records = new List<ImageRecord>();
            try
            {
                for (int i = 0; i < files.Length; i++)
                {
                    string name = Path.GetFileName(files[i]);
                    if (!string.IsNullOrEmpty(filter) && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    ImageRecord record = AnalyseImage(files[i], index, work, rawFolder);
                    records.Add(record);
                    TestContext.Progress.WriteLine((records.Count).ToString(CultureInfo.InvariantCulture).PadLeft(4) + " " + record.Outcome.PadRight(28) + " " + name);
                }
            }
            finally
            {
                Directory.Delete(work, true);
            }

            string summary = Summarise(records);
            JsonSerializerOptions options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(Path.Combine(reportFolder, "nitros9-analysis.json"), JsonSerializer.Serialize(records, options));
            File.WriteAllText(Path.Combine(reportFolder, "nitros9-summary.txt"), summary);
            TestContext.Out.WriteLine(summary);

            int notMatching = 0;
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].Outcome != Outcomes.Match)
                {
                    notMatching++;
                }
            }

            Assert.That(notMatching, Is.EqualTo(0), notMatching + " of " + records.Count + " images are not read the way Toolshed reads them\n" + summary);
        }

        // ---- one image ----

        private static ImageRecord AnalyseImage(string path, Dictionary<string, IndexEntry> index, string work, string rawFolder)
        {
            string name = Path.GetFileName(path);
            byte[] image = File.ReadAllBytes(path);
            ImageRecord record = new ImageRecord { File = name, SizeBytes = image.Length };
            record.Sha256 = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
            if (index.TryGetValue(name, out IndexEntry? listed))
            {
                record.IndexSha256Matches = string.Equals(listed.Sha256, record.Sha256, StringComparison.OrdinalIgnoreCase);
                record.PageName = listed.PageName;
                record.PageFormat = listed.PageFormat;
            }

            record.Geometry = DecodeSector0(image);

            // Everything below works on a copy, so nothing can change the original.
            string copy = Path.Combine(work, name);
            File.WriteAllBytes(copy, image);

            StringBuilder raw = new StringBuilder();
            using (Mounted? mounted = Mount(copy, image, record))
            {
                if (mounted != null)
                {
                    WalkOurTree(mounted, record);
                }

                RunToolshed(copy, mounted, record, work, raw);
            }

            File.WriteAllText(Path.Combine(rawFolder, name + ".txt"), raw.ToString());
            File.Delete(copy);

            // Proof that the original file was not changed: it still hashes to what it did when it was read.
            string after = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if (after != record.Sha256)
            {
                record.Errors.Add("the original image changed while it was analysed");
            }

            record.Outcome = DecideOutcome(record);
            return record;
        }

        // ---- sector 0 ----

        // Field offsets of the RBF identification sector (NitrOS-9 defs/rbf.d): DD.TOT 0 (3 bytes), DD.TKS 3, DD.MAP 4 (2), DD.BIT 6 (2),
        // DD.DIR 8 (3), DD.OWN 11 (2), DD.ATT 13, DD.DSK 14 (2), DD.FMT 16, DD.SPT 17 (2), DD.RES 19 (2), DD.BT 21 (3), DD.BSZ 24 (2),
        // DD.DAT 26 (5), DD.NAM 31 (32, last character has bit 7 set).
        private static Geometry DecodeSector0(byte[] image)
        {
            Geometry g = new Geometry();
            g.FileSectors = image.Length / SectorSize;
            g.BytesAfterLastWholeSector = image.Length % SectorSize;
            if (image.Length < SectorSize)
            {
                return g;
            }

            g.TotalSectors = (image[0] << 16) | (image[1] << 8) | image[2];
            g.TrackSize = image[3];
            g.MapBytes = (image[4] << 8) | image[5];
            g.SectorsPerCluster = (image[6] << 8) | image[7];
            g.RootDirectoryLsn = (image[8] << 16) | (image[9] << 8) | image[10];
            g.Owner = (image[11] << 8) | image[12];
            g.Attributes = image[13];
            g.DiskId = (image[14] << 8) | image[15];
            g.Format = image[16];
            g.FormatText = ((image[16] & 4) != 0 ? "96 TPI" : "48 TPI") + ", " + ((image[16] & 2) != 0 ? "double density" : "single density")
                + ", " + ((image[16] & 1) != 0 ? "double sided" : "single sided");
            g.SectorsPerTrack = (image[17] << 8) | image[18];
            g.BootLsn = (image[21] << 16) | (image[22] << 8) | image[23];
            g.BootSize = (image[24] << 8) | image[25];
            g.Created = image[26] + 1900 + "/" + image[27].ToString("D2", CultureInfo.InvariantCulture) + "/" + image[28].ToString("D2", CultureInfo.InvariantCulture)
                + " " + image[29].ToString("D2", CultureInfo.InvariantCulture) + image[30].ToString("D2", CultureInfo.InvariantCulture);
            StringBuilder volume = new StringBuilder();
            for (int i = 31; i < 63; i++)
            {
                if (i == 31 && image[i] == 0)
                {
                    break;
                }

                volume.Append((char)(image[i] & 0x7F));
                if ((image[i] & 0x80) != 0)
                {
                    break;
                }
            }

            g.VolumeName = volume.ToString();
            g.SectorsMissingFromFile = g.TotalSectors > g.FileSectors ? g.TotalSectors - g.FileSectors : 0;
            g.SectorsBeyondVolume = g.FileSectors > g.TotalSectors ? g.FileSectors - g.TotalSectors : 0;
            if (g.TrackSize > 0)
            {
                g.TracksFromTotal = (double)g.TotalSectors / g.TrackSize;
            }

            // Sectors the allocation map marks as used although the file does not hold them. Zero means the short file lost nothing in use.
            long mapEnd = SectorSize + (long)g.MapBytes;
            if (g.MapBytes > 0 && g.SectorsPerCluster > 0 && mapEnd <= image.Length && g.SectorsMissingFromFile > 0)
            {
                long used = 0;
                for (long lsn = g.FileSectors; lsn < g.TotalSectors; lsn++)
                {
                    long bit = lsn / g.SectorsPerCluster;
                    if (bit / 8 < g.MapBytes && (image[SectorSize + (bit / 8)] & (0x80 >> (int)(bit % 8))) != 0)
                    {
                        used++;
                    }
                }

                g.UsedSectorsMissingFromFile = used;
            }

            return g;
        }

        // ---- mounting ----

        private static Mounted? Mount(string copy, byte[] image, ImageRecord record)
        {
            // 1. The way the application does it: the driver factory picks a driver from the name, size and first bytes.
            DriverFactory factory = new DriverFactory();
            factory.Register(new DragonVdkDriverProvider());
            factory.Register(new DragonDskDriverProvider());
            factory.Register(new DragonDmkDriverProvider());
            Dictionary<string, float> confidences = factory.GetDetectionConfidences(copy);
            StringBuilder scores = new StringBuilder();
            Dictionary<string, float>.Enumerator e = confidences.GetEnumerator();
            while (e.MoveNext())
            {
                scores.Append(e.Current.Key).Append('=').Append(e.Current.Value.ToString("0.00", CultureInfo.InvariantCulture)).Append(' ');
            }

            e.Dispose();
            record.Mount.FactoryScores = scores.ToString().Trim();

            IFileSystemDriver? chosen = factory.Detect(copy);
            record.Mount.FactoryDriver = chosen == null ? "none (no provider reached 0.5)" : chosen.GetType().Name;
            if (chosen != null)
            {
                Mounted? viaFactory = TryDriver(chosen, copy, "driver factory: " + chosen.GetType().Name, record);
                if (viaFactory != null)
                {
                    record.Mount.Mounted = true;
                    record.Mount.ByApplicationPath = true;
                    return viaFactory;
                }
            }

            // 2. Each Dragon and CoCo driver directly, in case the factory picked the wrong one.
            IFileSystemDriver[] drivers = { new DragonDskDriver(), new DragonVdkDriver(), new DragonDmkDriver() };
            for (int i = 0; i < drivers.Length; i++)
            {
                if (chosen != null && drivers[i].GetType() == chosen.GetType())
                {
                    continue;
                }

                Mounted? direct = TryDriver(drivers[i], copy, "driver: " + drivers[i].GetType().Name, record);
                if (direct != null)
                {
                    record.Mount.Mounted = true;
                    return direct;
                }
            }

            // 3. A plain block device, so the file system can be checked even when no driver takes the image. When sector 0 says the volume
            //    has more sectors than the file holds, the missing sectors read as zero bytes (what "os9 format" without -e leaves out).
            int fileSectors = image.Length / SectorSize;
            int deviceSectors = Math.Max(fileSectors, record.Geometry.TotalSectors);
            // The sector counts are in the geometry and in DeviceSectors, so the text stays the same for every image mounted this way.
            string way = deviceSectors > fileSectors
                ? "plain block device of DD.TOT sectors, the sectors the file does not hold read as zero"
                : "plain block device of the file's sectors";
            IBlockFileSystem[] systems = { new Os9RbfFileSystem(), new DecbFileSystem() };
            for (int i = 0; i < systems.Length; i++)
            {
                Mounted? plain = TryPlain(systems[i], copy, fileSectors, deviceSectors, way, record);
                if (plain != null)
                {
                    record.Mount.Mounted = true;
                    return plain;
                }
            }

            return null;
        }

        private static Mounted? TryDriver(IFileSystemDriver driver, string copy, string way, ImageRecord record)
        {
            FileStream stream = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                driver.Mount(stream, true);
                record.Mount.Attempts.Add(new Attempt { Way = way, Result = "mounted as " + driver.FileSystemType });
                record.Mount.Way = way;
                record.Mount.FileSystem = driver.FileSystemType;
                IBlockDevice? device = driver is DragonDskDriver dsk ? dsk.GetBlockDevice() : null;
                record.Mount.DeviceSectors = device != null ? (long)device.TotalBlocks : -1;
                return new Mounted(driver, null, stream);
            }
            catch (Exception ex)
            {
                record.Mount.Attempts.Add(new Attempt { Way = way, Result = ex.GetType().Name + ": " + ex.Message });
                driver.Dispose();
                stream.Dispose();
                return null;
            }
        }

        private static Mounted? TryPlain(IBlockFileSystem fs, string copy, int fileSectors, int deviceSectors, string way, ImageRecord record)
        {
            FileStream stream = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                Stream data = new PaddedSubStream(stream, 0, (long)fileSectors * SectorSize, (long)deviceSectors * SectorSize);
                BlockDeviceDescriptor descriptor = new BlockDeviceDescriptor
                {
                    Name = "plain " + deviceSectors + " sectors",
                    BlockSize = SectorSize,
                    AddressMode = BlockAddressMode.LBA,
                    Cylinders = 1,
                    Heads = 1,
                    UniformSectorsPerTrack = 1,
                    TotalBlocks = (uint)deviceSectors,
                    ExpectedImageSize = (long)deviceSectors * SectorSize
                };

                // DECB needs the 35 track layout to find its directory track, so it is offered the CoCo geometry when the size fits.
                if (fs is DecbFileSystem && deviceSectors == 630)
                {
                    descriptor.Cylinders = 35;
                    descriptor.UniformSectorsPerTrack = 18;
                }

                GenericBlockDevice device = new GenericBlockDevice(descriptor);
                device.Mount(data, true);
                string full = way + ", " + fs.FileSystemType;
                if (!fs.CanHandle(device))
                {
                    record.Mount.Attempts.Add(new Attempt { Way = full, Result = "the file system does not recognise the volume" });
                    device.Dispose();
                    return null;
                }

                fs.Initialize(device);
                record.Mount.Attempts.Add(new Attempt { Way = full, Result = "mounted as " + fs.FileSystemType });
                record.Mount.Way = way;
                record.Mount.FileSystem = fs.FileSystemType;
                record.Mount.DeviceSectors = deviceSectors;
                return new Mounted(null, fs, stream);
            }
            catch (Exception ex)
            {
                record.Mount.Attempts.Add(new Attempt { Way = way + ", " + fs.FileSystemType, Result = ex.GetType().Name + ": " + ex.Message });
                stream.Dispose();
                return null;
            }
        }

        // ---- our side ----

        private static void WalkOurTree(Mounted mounted, ImageRecord record)
        {
            List<string> pending = new List<string> { "/" };
            List<int> depths = new List<int> { 0 };
            record.Ours.Directories = 0;
            int entries = 0;
            while (pending.Count > 0)
            {
                string dir = pending[pending.Count - 1];
                int depth = depths[depths.Count - 1];
                pending.RemoveAt(pending.Count - 1);
                depths.RemoveAt(depths.Count - 1);
                record.Ours.Directories++;

                DirectoryEntry[] list;
                try
                {
                    list = mounted.ListDirectory(dir);
                }
                catch (Exception ex)
                {
                    record.Errors.Add("ours: listing " + dir + ": " + ex.GetType().Name + ": " + ex.Message);
                    continue;
                }

                for (int i = 0; i < list.Length; i++)
                {
                    if (++entries > MaxEntries)
                    {
                        record.Errors.Add("ours: more than " + MaxEntries + " entries, the walk stopped");
                        return;
                    }

                    FileMetadata m = list[i].Metadata;
                    string path = dir == "/" ? "/" + list[i].Name : dir + "/" + list[i].Name;
                    OurEntry entry = new OurEntry
                    {
                        IsDirectory = m.IsDirectory,
                        Size = m.Size,
                        Attributes = m.Attributes ?? string.Empty,
                        Modified = m.ModifiedTime.HasValue ? m.ModifiedTime.Value.ToString("yyyy/MM/dd HHmm", CultureInfo.InvariantCulture) : "no valid date"
                    };

                    if (record.OurEntries.ContainsKey(path))
                    {
                        record.Errors.Add("ours: the name " + path + " is in its directory more than once");
                        continue;
                    }

                    record.OurEntries[path] = entry;
                    if (m.IsDirectory)
                    {
                        if (depth + 1 > MaxDepth)
                        {
                            record.Errors.Add("ours: " + path + " is deeper than " + MaxDepth + " levels, not followed");
                            continue;
                        }

                        pending.Add(path);
                        depths.Add(depth + 1);
                        continue;
                    }

                    record.Ours.Files++;
                    try
                    {
                        entry.Data = mounted.ReadFile(path);
                        record.Ours.FilesRead++;
                        record.Ours.Bytes += entry.Data.Length;
                    }
                    catch (Exception ex)
                    {
                        record.Ours.ReadErrors++;
                        record.Errors.Add("ours: reading " + path + ": " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }

            try
            {
                record.Ours.FreeSectors = mounted.FreeSectors();
            }
            catch (Exception ex)
            {
                record.Errors.Add("ours: free space: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---- Toolshed's side ----

        private static void RunToolshed(string copy, Mounted? mounted, ImageRecord record, string work, StringBuilder raw)
        {
            bool decb = mounted != null && record.Mount.FileSystem == "DECB";
            if (mounted != null && record.Mount.FileSystem != "OS-9" && !decb)
            {
                record.Toolshed.Notes.Add("Toolshed has no reader for " + record.Mount.FileSystem + ", so nothing was compared");
                return;
            }

            string image = StressToolshed.WslPath(copy);
            StringBuilder script = new StringBuilder();
            script.Append("exec 2>&1\n");
            script.Append("W=$(mktemp -d /tmp/retroio-nitros9.XXXXXX)\n");
            script.Append("cp ").Append(Quote(image)).Append(" \"$W/img\"\n");
            if (decb || mounted == null)
            {
                Section(script, "decbdir", "timeout 60 decb dir \"$W/img,\"");
                Section(script, "decbfree", "timeout 60 decb free \"$W/img\"");
            }

            if (!decb)
            {
                Section(script, "dir", "timeout 120 os9 dir -e -r -a \"$W/img,\"");
                Section(script, "free", "timeout 60 os9 free \"$W/img\"");
                Section(script, "dcheck", "timeout 180 os9 dcheck \"$W/img\"");
                Section(script, "id", "timeout 60 os9 id \"$W/img\"");
            }

            script.Append("rm -rf \"$W\"\n");
            string? first = RunScript(script.ToString(), work, 600000, record);
            raw.Append(first);
            if (first == null)
            {
                return;
            }

            Dictionary<string, string> sections = Sections(first, record);
            if (decb)
            {
                ParseDecb(sections, record);
            }
            else
            {
                ParseOs9(sections, record);
            }

            if (mounted == null)
            {
                return;
            }

            CopyAndCompare(image, decb, record, work, raw);
            Compare(record, decb);
        }

        private static void Section(StringBuilder script, string name, string command)
        {
            script.Append("echo '@@@BEGIN ").Append(name).Append("'\n");
            script.Append(command).Append('\n');
            script.Append("echo \"@@@END ").Append(name).Append(" $?\"\n");
        }

        private static string Quote(string text)
        {
            return "'" + text.Replace("'", "'\\''") + "'";
        }

        private static string? RunScript(string script, string work, int timeoutMilliseconds, ImageRecord record)
        {
            string file = Path.Combine(work, "toolshed-" + Guid.NewGuid().ToString("N") + ".sh");
            File.WriteAllText(file, script.Replace("\r\n", "\n"), new UTF8Encoding(false));
            try
            {
                string? output = StressToolshed.RunWithTimeout("bash", timeoutMilliseconds, StressToolshed.WslPath(file));
                if (output == null)
                {
                    record.Toolshed.Errors.Add("WSL Ubuntu or bash could not be started");
                }

                return output;
            }
            catch (TimeoutException ex)
            {
                record.Toolshed.Errors.Add(ex.Message);
                return null;
            }
            finally
            {
                File.Delete(file);
            }
        }

        // Splits the script output at the @@@BEGIN and @@@END markers. A section whose command did not end with status 0 is noted.
        private static Dictionary<string, string> Sections(string output, ImageRecord record)
        {
            Dictionary<string, string> sections = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] lines = output.Replace("\r", string.Empty).Split('\n');
            string? current = null;
            StringBuilder body = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("@@@BEGIN ", StringComparison.Ordinal))
                {
                    current = line.Substring(9);
                    body.Clear();
                    continue;
                }

                if (line.StartsWith("@@@END ", StringComparison.Ordinal) && current != null)
                {
                    sections[current] = body.ToString();
                    string status = line.Substring(line.LastIndexOf(' ') + 1);
                    if (status != "0")
                    {
                        record.Toolshed.Errors.Add(current + " ended with status " + status + (status == "124" ? " (killed by the timeout)" : string.Empty)
                            + ": " + Shorten(body.ToString()));
                    }

                    current = null;
                    continue;
                }

                if (current != null)
                {
                    body.Append(line).Append('\n');
                }
            }

            return sections;
        }

        private static string Shorten(string text)
        {
            string t = text.Trim();
            return t.Length > 400 ? t.Substring(0, 200) + " ... " + t.Substring(t.Length - 200) : t;
        }

        private static void ParseOs9(Dictionary<string, string> sections, ImageRecord record)
        {
            if (sections.TryGetValue("dir", out string? dir))
            {
                ParseOs9Dir(dir, record);
            }

            if (sections.TryGetValue("free", out string? free))
            {
                record.Toolshed.FreeSectors = StressToolshed.NumberBefore(free, "Free sectors");
            }

            if (sections.TryGetValue("dcheck", out string? dcheck))
            {
                ParseDcheck(dcheck, record);
            }
        }

        // The output of "os9 dir -e -r -a": a header "Directory of <image>,.<path>" for each directory, then one line per entry:
        // owner, date and time, attributes, descriptor sector (hex), size, name (Toolshed os9/os9dir.c, the printf calls of the listing loop).
        private static void ParseOs9Dir(string text, ImageRecord record)
        {
            string[] lines = text.Split('\n');
            string? dir = null;
            record.Toolshed.Directories = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int header = line.IndexOf("Directory of ", StringComparison.Ordinal);
                if (header >= 0)
                {
                    int comma = line.IndexOf(",.", header, StringComparison.Ordinal);
                    string rest = comma < 0 ? string.Empty : line.Substring(comma + 2);
                    dir = rest.Length == 0 ? "/" : rest;
                    record.Toolshed.Directories++;
                    continue;
                }

                if (dir == null || line.Length < 40 || line.StartsWith(" Owner", StringComparison.Ordinal) || line.StartsWith("-------", StringComparison.Ordinal))
                {
                    continue;
                }

                // "  0.0     2021/09/19 2344   ------wr       B     22929 OS9Boot": six words (owner, date, time, attributes, descriptor
                // sector, size), then one space and the name, which is taken whole.
                string[] parts = new string[6];
                int at = 0;
                for (int w = 0; w < 6; w++)
                {
                    while (at < line.Length && line[at] == ' ')
                    {
                        at++;
                    }

                    int start = at;
                    while (at < line.Length && line[at] != ' ')
                    {
                        at++;
                    }

                    parts[w] = line.Substring(start, at - start);
                }

                if (parts[5].Length == 0 || at + 1 > line.Length)
                {
                    record.Toolshed.Notes.Add("dir line not understood: " + line);
                    continue;
                }

                string name = line.Substring(at + 1);
                if (name == "." || name == "..")
                {
                    continue;
                }

                string path = dir == "/" ? "/" + name : dir + "/" + name;
                ToolshedEntry entry = new ToolshedEntry
                {
                    Owner = parts[0],
                    Modified = parts[1] + " " + parts[2],
                    Attributes = parts[3],
                    DescriptorLsn = parts[4],
                    Size = long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out long size) ? size : -1,
                    IsDirectory = parts[3].StartsWith("d", StringComparison.Ordinal)
                };

                if (record.ToolshedEntries.ContainsKey(path))
                {
                    record.Toolshed.Notes.Add("Toolshed lists " + path + " more than once");
                    continue;
                }

                record.ToolshedEntries[path] = entry;
                if (!entry.IsDirectory)
                {
                    record.Toolshed.Files++;
                }
            }
        }

        private static void ParseDcheck(string text, ImageRecord record)
        {
            // dcheck prints "1 directory" and "1 file" in the singular, so both spellings are looked for.
            record.Toolshed.DcheckDirectories = StressToolshed.NumberBefore(text, " directories\n");
            if (record.Toolshed.DcheckDirectories < 0)
            {
                record.Toolshed.DcheckDirectories = StressToolshed.NumberBefore(text, " directory\n");
            }

            record.Toolshed.DcheckFiles = StressToolshed.NumberBefore(text, " files\n");
            if (record.Toolshed.DcheckFiles < 0)
            {
                record.Toolshed.DcheckFiles = StressToolshed.NumberBefore(text, " file\n");
            }
            record.Toolshed.DcheckIntact = text.Contains("file structure is intact", StringComparison.Ordinal);
            string[] lines = text.Split('\n');
            int problems = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                // The four count lines at the end ("0 previously allocated cluster found", "... in file structure but not in allocation map",
                // "... in allocation map but not in file structure", "0 bad file decriptor sector") are kept whole.
                bool countLine = char.IsDigit(line[0]) && (line.Contains("previously allocated", StringComparison.Ordinal)
                    || line.Contains("but not in", StringComparison.Ordinal) || line.Contains("bad file", StringComparison.Ordinal));
                if (countLine)
                {
                    record.Toolshed.DcheckCounts.Add(line);
                    continue;
                }

                // Every other line that is not part of the fixed header or the closing lines is a problem dcheck reports; they are counted
                // and the first ones kept.
                bool known = line.StartsWith("Volume - ", StringComparison.Ordinal) || line.Contains("bytes in allocation map", StringComparison.Ordinal)
                    || line.Contains("per cluster", StringComparison.Ordinal) || line.Contains("total sectors on media", StringComparison.Ordinal)
                    || line.Contains("is start of root directory", StringComparison.Ordinal) || line.StartsWith("Building secondary", StringComparison.Ordinal)
                    || line.StartsWith("Comparing primary", StringComparison.Ordinal) || line.Contains("file structure is intact", StringComparison.Ordinal)
                    || line.EndsWith(" directories", StringComparison.Ordinal) || line.EndsWith(" files", StringComparison.Ordinal)
                    || line.EndsWith(" directory", StringComparison.Ordinal) || line.EndsWith(" file", StringComparison.Ordinal);
                if (!known)
                {
                    problems++;
                    if (record.Toolshed.DcheckProblems.Count < MaxListed)
                    {
                        record.Toolshed.DcheckProblems.Add(line);
                    }
                }
            }

            record.Toolshed.DcheckProblemLines = problems;
        }

        // "decb dir" prints one line per file: name (8), extension (3), type, A or B, granules. "decb free" prints "Free granules: N".
        private static void ParseDecb(Dictionary<string, string> sections, ImageRecord record)
        {
            record.Toolshed.Directories = 1;
            if (sections.TryGetValue("decbdir", out string? dir))
            {
                string[] lines = dir.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.Length < 14 || line.StartsWith("Directory of", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string name = line.Substring(0, 8).Trim();
                    string ext = line.Substring(9, 3).Trim();
                    string path = "/" + (ext.Length == 0 ? name : name + "." + ext);
                    record.ToolshedEntries[path] = new ToolshedEntry { Size = -1, Attributes = line.Substring(12).Trim() };
                    record.Toolshed.Files++;
                }
            }

            if (sections.TryGetValue("decbfree", out string? free))
            {
                long granules = StressToolshed.NumberAfter(free, "Free granules:");
                record.Toolshed.FreeSectors = granules < 0 ? -1 : granules * 9;
            }
        }

        // Copies every file either side knows out with Toolshed, then compares the bytes with what our reader gave.
        private static void CopyAndCompare(string image, bool decb, ImageRecord record, string work, StringBuilder raw)
        {
            List<string> paths = new List<string>();
            Dictionary<string, OurEntry>.Enumerator ours = record.OurEntries.GetEnumerator();
            while (ours.MoveNext())
            {
                if (!ours.Current.Value.IsDirectory)
                {
                    paths.Add(ours.Current.Key);
                }
            }

            ours.Dispose();
            Dictionary<string, ToolshedEntry>.Enumerator theirs = record.ToolshedEntries.GetEnumerator();
            while (theirs.MoveNext())
            {
                if (!theirs.Current.Value.IsDirectory && !record.OurEntries.ContainsKey(theirs.Current.Key))
                {
                    paths.Add(theirs.Current.Key);
                }
            }

            theirs.Dispose();
            if (paths.Count == 0)
            {
                return;
            }

            string outFolder = Path.Combine(work, "out-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outFolder);
            try
            {
                StringBuilder script = new StringBuilder();
                script.Append("exec 2>&1\n");
                script.Append("W=$(mktemp -d /tmp/retroio-nitros9.XXXXXX)\n");
                script.Append("cp ").Append(Quote(image)).Append(" \"$W/img\"\n");
                script.Append("mkdir \"$W/out\"\n");
                for (int i = 0; i < paths.Count; i++)
                {
                    string source = decb ? paths[i].Substring(1) : paths[i];
                    string program = decb ? "decb copy -b" : "os9 copy";
                    // "$W/img" and the quoted ",<path>" touch, so the shell joins them into one argument: <image>,<path>.
                    script.Append("timeout 60 ").Append(program).Append(" \"$W/img\"").Append(Quote("," + source))
                        .Append(" \"$W/out/f").Append(i).Append("\"\n");
                    script.Append("echo \"@@@COPY ").Append(i).Append(" $?\"\n");
                }

                script.Append("cp -r \"$W/out/.\" ").Append(Quote(StressToolshed.WslPath(outFolder))).Append('\n');
                script.Append("rm -rf \"$W\"\n");
                string? output = RunScript(script.ToString(), work, 120000 + (paths.Count * 61000), record);
                raw.Append("\n@@@COPIES\n").Append(output);
                if (output == null)
                {
                    return;
                }

                string[] lines = output.Replace("\r", string.Empty).Split('\n');
                StringBuilder message = new StringBuilder();
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].StartsWith("@@@COPY ", StringComparison.Ordinal))
                    {
                        message.Append(lines[i]).Append(' ');
                        continue;
                    }

                    string[] parts = lines[i].Split(' ');
                    int n = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    if (parts[2] != "0")
                    {
                        record.Toolshed.CopyFailures++;
                        AddMismatch(record, paths[n], "toolshed copy", "-", "status " + parts[2] + ": " + Shorten(message.ToString()));
                    }

                    message.Clear();
                }

                for (int i = 0; i < paths.Count; i++)
                {
                    string target = Path.Combine(outFolder, "f" + i.ToString(CultureInfo.InvariantCulture));
                    if (!File.Exists(target))
                    {
                        continue;
                    }

                    byte[] theirBytes = File.ReadAllBytes(target);
                    record.Toolshed.FilesCopied++;
                    if (!record.OurEntries.TryGetValue(paths[i], out OurEntry? our) || our.Data == null)
                    {
                        continue;
                    }

                    if (theirBytes.AsSpan().SequenceEqual(our.Data))
                    {
                        record.Toolshed.FilesEqual++;
                        continue;
                    }

                    int first = 0;
                    int shorter = Math.Min(theirBytes.Length, our.Data.Length);
                    while (first < shorter && theirBytes[first] == our.Data[first])
                    {
                        first++;
                    }

                    AddMismatch(record, paths[i], "content", our.Data.Length + " bytes", theirBytes.Length + " bytes, first difference at byte " + first);
                }
            }
            finally
            {
                Directory.Delete(outFolder, true);
            }
        }

        // ---- the comparison ----

        private static void Compare(ImageRecord record, bool decb)
        {
            Dictionary<string, OurEntry>.Enumerator ours = record.OurEntries.GetEnumerator();
            while (ours.MoveNext())
            {
                string path = ours.Current.Key;
                OurEntry our = ours.Current.Value;
                if (!record.ToolshedEntries.TryGetValue(path, out ToolshedEntry? their))
                {
                    AddMismatch(record, path, "exists", "listed", "not listed");
                    continue;
                }

                if (decb)
                {
                    continue;
                }

                if (our.IsDirectory != their.IsDirectory)
                {
                    AddMismatch(record, path, "directory", our.IsDirectory.ToString(), their.IsDirectory.ToString());
                }

                if (our.Size != their.Size)
                {
                    AddMismatch(record, path, "size", our.Size.ToString(CultureInfo.InvariantCulture), their.Size.ToString(CultureInfo.InvariantCulture));
                }

                if (our.Attributes != their.Attributes)
                {
                    AddMismatch(record, path, "attributes", our.Attributes, their.Attributes);
                }

                if (our.Modified != their.Modified)
                {
                    AddMismatch(record, path, "modified", our.Modified, their.Modified);
                }
            }

            ours.Dispose();
            Dictionary<string, ToolshedEntry>.Enumerator theirs = record.ToolshedEntries.GetEnumerator();
            while (theirs.MoveNext())
            {
                if (!record.OurEntries.ContainsKey(theirs.Current.Key))
                {
                    AddMismatch(record, theirs.Current.Key, "exists", "not listed", "listed");
                }
            }

            theirs.Dispose();
            if (record.Ours.FreeSectors != record.Toolshed.FreeSectors)
            {
                AddMismatch(record, "/", "free sectors", record.Ours.FreeSectors.ToString(CultureInfo.InvariantCulture), record.Toolshed.FreeSectors.ToString(CultureInfo.InvariantCulture));
            }

            if (!decb && record.Toolshed.DcheckFiles >= 0 && record.Ours.Files != record.Toolshed.DcheckFiles)
            {
                AddMismatch(record, "/", "file count (dcheck)", record.Ours.Files.ToString(CultureInfo.InvariantCulture), record.Toolshed.DcheckFiles.ToString(CultureInfo.InvariantCulture));
            }

            if (!decb && record.Toolshed.DcheckDirectories >= 0 && record.Ours.Directories != record.Toolshed.DcheckDirectories)
            {
                AddMismatch(record, "/", "directory count (dcheck)", record.Ours.Directories.ToString(CultureInfo.InvariantCulture), record.Toolshed.DcheckDirectories.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void AddMismatch(ImageRecord record, string path, string field, string ours, string toolshed)
        {
            record.MismatchCount++;
            if (record.Mismatches.Count < MaxListed * 4)
            {
                record.Mismatches.Add(new Mismatch { Path = path, Field = field, Ours = ours, Toolshed = toolshed });
            }
        }

        private static string DecideOutcome(ImageRecord record)
        {
            if (!record.Mount.Mounted)
            {
                return Outcomes.NotMounted;
            }

            if (record.Mount.FileSystem != "OS-9" && record.Mount.FileSystem != "DECB")
            {
                return Outcomes.NoToolshedReader;
            }

            if (record.Toolshed.Directories == 0 && record.Toolshed.Errors.Count > 0)
            {
                return Outcomes.ToolshedFailed;
            }

            bool clean = record.MismatchCount == 0 && record.Errors.Count == 0 && record.Toolshed.Errors.Count == 0;
            if (!clean)
            {
                return record.Ours.ReadErrors > 0 || record.Errors.Count > 0 ? Outcomes.ReadErrors : Outcomes.Mismatch;
            }

            return record.Mount.ByApplicationPath ? Outcomes.Match : Outcomes.MatchOnPlainDeviceOnly;
        }

        // ---- the summary ----

        private static string Summarise(List<ImageRecord> records)
        {
            StringBuilder s = new StringBuilder();
            s.Append("NitrOS-9 image analysis, ").Append(records.Count).Append(" images, ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).AppendLine();
            s.AppendLine();
            s.AppendLine("Outcome per image:");
            AppendCounts(s, records, 0);
            s.AppendLine();
            s.AppendLine("How each image was mounted (way, file system):");
            AppendCounts(s, records, 1);
            s.AppendLine();
            s.AppendLine("Why the application path (driver factory, then each driver) did not mount it, first refusal:");
            AppendCounts(s, records, 2);
            s.AppendLine();
            s.AppendLine("Mismatch fields (images that have at least one):");
            AppendCounts(s, records, 3);
            s.AppendLine();
            s.AppendLine("Toolshed errors (first one per image):");
            AppendCounts(s, records, 4);
            s.AppendLine();
            s.AppendLine("Our errors (first one per image, numbers left in):");
            AppendCounts(s, records, 5);
            s.AppendLine();
            s.AppendLine("Images by outcome:");
            Dictionary<string, List<string>> byOutcome = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            for (int i = 0; i < records.Count; i++)
            {
                if (!byOutcome.TryGetValue(records[i].Outcome, out List<string>? names))
                {
                    names = new List<string>();
                    byOutcome[records[i].Outcome] = names;
                }

                names.Add(records[i].File);
            }

            Dictionary<string, List<string>>.Enumerator e = byOutcome.GetEnumerator();
            while (e.MoveNext())
            {
                s.Append("  ").Append(e.Current.Key).Append(" (").Append(e.Current.Value.Count).Append("): ").AppendLine(string.Join(" ", e.Current.Value));
            }

            e.Dispose();
            return s.ToString();
        }

        private static void AppendCounts(StringBuilder s, List<ImageRecord> records, int kind)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < records.Count; i++)
            {
                ImageRecord r = records[i];
                List<string> keys = new List<string>();
                switch (kind)
                {
                    case 0:
                        keys.Add(r.Outcome);
                        break;
                    case 1:
                        keys.Add(r.Mount.Mounted ? r.Mount.Way + ", " + r.Mount.FileSystem : "not mounted");
                        break;
                    case 2:
                        if (!r.Mount.ByApplicationPath && r.Mount.Attempts.Count > 0)
                        {
                            keys.Add(r.Mount.Attempts[0].Way + ": " + r.Mount.Attempts[0].Result);
                        }

                        break;
                    case 3:
                        for (int k = 0; k < r.Mismatches.Count; k++)
                        {
                            if (!keys.Contains(r.Mismatches[k].Field))
                            {
                                keys.Add(r.Mismatches[k].Field);
                            }
                        }

                        break;
                    case 4:
                        if (r.Toolshed.Errors.Count > 0)
                        {
                            keys.Add(Shorten(r.Toolshed.Errors[0]).Replace(r.File, "<image>"));
                        }

                        break;
                    default:
                        if (r.Errors.Count > 0)
                        {
                            keys.Add(r.Errors[0]);
                        }

                        break;
                }

                for (int k = 0; k < keys.Count; k++)
                {
                    // Numbers (track counts, sector numbers, paths with digits) are folded to # so the same cause is counted once.
                    string key = kind >= 2 ? FoldNumbers(keys[k]) : keys[k];
                    counts.TryGetValue(key, out int n);
                    counts[key] = n + 1;
                }
            }

            List<KeyValuePair<string, int>> sorted = new List<KeyValuePair<string, int>>(counts);
            sorted.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
            for (int i = 0; i < sorted.Count; i++)
            {
                s.Append("  ").Append(sorted[i].Value.ToString(CultureInfo.InvariantCulture).PadLeft(4)).Append("  ").AppendLine(sorted[i].Key);
            }
        }

        private static string FoldNumbers(string text)
        {
            StringBuilder folded = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsDigit(text[i]))
                {
                    if (folded.Length == 0 || folded[folded.Length - 1] != '#')
                    {
                        folded.Append('#');
                    }

                    continue;
                }

                folded.Append(text[i]);
            }

            return folded.ToString();
        }

        // ---- the index next to the image folder ----

        private static Dictionary<string, IndexEntry> ReadIndex(string path)
        {
            Dictionary<string, IndexEntry> index = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path))
            {
                return index;
            }

            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                JsonElement all = doc.RootElement;
                int n = all.GetArrayLength();
                for (int i = 0; i < n; i++)
                {
                    JsonElement item = all[i];
                    string file = item.TryGetProperty("file", out JsonElement f) ? f.GetString() ?? string.Empty : string.Empty;
                    IndexEntry entry = new IndexEntry
                    {
                        Sha256 = item.TryGetProperty("sha256", out JsonElement h) ? h.GetString() ?? string.Empty : string.Empty
                    };
                    if (item.TryGetProperty("pageInfo", out JsonElement page) && page.ValueKind == JsonValueKind.Object)
                    {
                        entry.PageName = page.TryGetProperty("Name", out JsonElement pn) ? pn.GetString() : null;
                        entry.PageFormat = page.TryGetProperty("Format", out JsonElement pf) ? pf.GetString() : null;
                    }

                    index[Path.GetFileName(file)] = entry;
                }
            }

            return index;
        }

        // ---- the records written to the report ----

        private static class Outcomes
        {
            public const string Match = "match";
            public const string MatchOnPlainDeviceOnly = "match-only-on-plain-device";
            public const string Mismatch = "mismatch";
            public const string ReadErrors = "read-errors";
            public const string NotMounted = "not-mounted";
            public const string ToolshedFailed = "toolshed-failed";
            public const string NoToolshedReader = "no-toolshed-reader";
        }

        private sealed class IndexEntry
        {
            public string Sha256 { get; set; } = string.Empty;

            public string? PageName { get; set; }

            public string? PageFormat { get; set; }
        }

        /// <summary>
        /// What was found for one image. Public properties only, because the report is written from them.
        /// </summary>
        public sealed class ImageRecord
        {
            /// <summary>
            /// Gets or sets the file name of the image.
            /// </summary>
            public string File { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the outcome, one of the names in the summary.
            /// </summary>
            public string Outcome { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the size of the file in bytes.
            /// </summary>
            public long SizeBytes { get; set; }

            /// <summary>
            /// Gets or sets the sha256 of the file.
            /// </summary>
            public string Sha256 { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets whether the sha256 is the one in the index, or null when the index does not list the file.
            /// </summary>
            public bool? IndexSha256Matches { get; set; }

            /// <summary>
            /// Gets or sets the name the snapshot page gives the image.
            /// </summary>
            public string? PageName { get; set; }

            /// <summary>
            /// Gets or sets the format the snapshot page gives the image.
            /// </summary>
            public string? PageFormat { get; set; }

            /// <summary>
            /// Gets or sets the fields of sector 0.
            /// </summary>
            public Geometry Geometry { get; set; } = new Geometry();

            /// <summary>
            /// Gets or sets how the image was mounted.
            /// </summary>
            public MountRecord Mount { get; set; } = new MountRecord();

            /// <summary>
            /// Gets or sets what our reader found.
            /// </summary>
            public Counts Ours { get; set; } = new Counts();

            /// <summary>
            /// Gets or sets what Toolshed found.
            /// </summary>
            public ToolshedRecord Toolshed { get; set; } = new ToolshedRecord();

            /// <summary>
            /// Gets or sets the number of differences, which can be more than the ones listed.
            /// </summary>
            public int MismatchCount { get; set; }

            /// <summary>
            /// Gets the differences between our reader and Toolshed.
            /// </summary>
            public List<Mismatch> Mismatches { get; } = new List<Mismatch>();

            /// <summary>
            /// Gets the errors and exceptions our reader gave.
            /// </summary>
            public List<string> Errors { get; } = new List<string>();

            /// <summary>
            /// Gets the entries our reader listed, by path. Not written to the report.
            /// </summary>
            [System.Text.Json.Serialization.JsonIgnore]
            public Dictionary<string, OurEntry> OurEntries { get; } = new Dictionary<string, OurEntry>(StringComparer.Ordinal);

            /// <summary>
            /// Gets the entries Toolshed listed, by path. Not written to the report.
            /// </summary>
            [System.Text.Json.Serialization.JsonIgnore]
            public Dictionary<string, ToolshedEntry> ToolshedEntries { get; } = new Dictionary<string, ToolshedEntry>(StringComparer.Ordinal);
        }

        /// <summary>
        /// The fields of the OS-9 identification sector (sector 0), and how they fit the file.
        /// </summary>
        public sealed class Geometry
        {
            /// <summary>
            /// Gets or sets DD.TOT, the sectors in the volume.
            /// </summary>
            public int TotalSectors { get; set; }

            /// <summary>
            /// Gets or sets DD.TKS, the track size in sectors.
            /// </summary>
            public int TrackSize { get; set; }

            /// <summary>
            /// Gets or sets DD.MAP, the bytes in the allocation map.
            /// </summary>
            public int MapBytes { get; set; }

            /// <summary>
            /// Gets or sets DD.BIT, the sectors per map bit (cluster).
            /// </summary>
            public int SectorsPerCluster { get; set; }

            /// <summary>
            /// Gets or sets DD.DIR, the sector of the root directory descriptor.
            /// </summary>
            public int RootDirectoryLsn { get; set; }

            /// <summary>
            /// Gets or sets DD.OWN, the owner.
            /// </summary>
            public int Owner { get; set; }

            /// <summary>
            /// Gets or sets DD.ATT, the disk attributes.
            /// </summary>
            public int Attributes { get; set; }

            /// <summary>
            /// Gets or sets DD.DSK, the disk id.
            /// </summary>
            public int DiskId { get; set; }

            /// <summary>
            /// Gets or sets DD.FMT, the disk format byte.
            /// </summary>
            public int Format { get; set; }

            /// <summary>
            /// Gets or sets DD.FMT in words: bit 0 sides, bit 1 density, bit 2 tracks per inch (as Toolshed os9 id shows them).
            /// </summary>
            public string FormatText { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets DD.SPT, the sectors per track.
            /// </summary>
            public int SectorsPerTrack { get; set; }

            /// <summary>
            /// Gets or sets DD.BT, the first sector of the boot file.
            /// </summary>
            public int BootLsn { get; set; }

            /// <summary>
            /// Gets or sets DD.BSZ, the size of the boot file.
            /// </summary>
            public int BootSize { get; set; }

            /// <summary>
            /// Gets or sets DD.DAT, the creation date, as yyyy/mm/dd hhmm.
            /// </summary>
            public string Created { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets DD.NAM, the volume name.
            /// </summary>
            public string VolumeName { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the whole sectors in the file.
            /// </summary>
            public int FileSectors { get; set; }

            /// <summary>
            /// Gets or sets the bytes after the last whole sector, which would be a header or a cut sector.
            /// </summary>
            public int BytesAfterLastWholeSector { get; set; }

            /// <summary>
            /// Gets or sets DD.TOT divided by DD.TKS.
            /// </summary>
            public double TracksFromTotal { get; set; }

            /// <summary>
            /// Gets or sets how many sectors of the volume the file does not hold.
            /// </summary>
            public int SectorsMissingFromFile { get; set; }

            /// <summary>
            /// Gets or sets how many sectors the file holds after the end of the volume.
            /// </summary>
            public int SectorsBeyondVolume { get; set; }

            /// <summary>
            /// Gets or sets how many of the sectors the file does not hold are marked used in the allocation map.
            /// </summary>
            public long UsedSectorsMissingFromFile { get; set; }
        }

        /// <summary>
        /// How the image was mounted.
        /// </summary>
        public sealed class MountRecord
        {
            /// <summary>
            /// Gets or sets whether anything mounted it.
            /// </summary>
            public bool Mounted { get; set; }

            /// <summary>
            /// Gets or sets whether the driver factory, the way the application opens an image, mounted it.
            /// </summary>
            public bool ByApplicationPath { get; set; }

            /// <summary>
            /// Gets or sets the scores the providers gave the image.
            /// </summary>
            public string FactoryScores { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the driver the factory picked.
            /// </summary>
            public string FactoryDriver { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the way that mounted it.
            /// </summary>
            public string Way { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the file system that was mounted.
            /// </summary>
            public string FileSystem { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the sectors of the block device, or -1 when the driver does not show it.
            /// </summary>
            public long DeviceSectors { get; set; }

            /// <summary>
            /// Gets every way that was tried, with its result or error.
            /// </summary>
            public List<Attempt> Attempts { get; } = new List<Attempt>();
        }

        /// <summary>
        /// One way of mounting and what came of it.
        /// </summary>
        public sealed class Attempt
        {
            /// <summary>
            /// Gets or sets the way.
            /// </summary>
            public string Way { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the result or the exception.
            /// </summary>
            public string Result { get; set; } = string.Empty;
        }

        /// <summary>
        /// What our reader found.
        /// </summary>
        public sealed class Counts
        {
            /// <summary>
            /// Gets or sets the directories, the root included.
            /// </summary>
            public int Directories { get; set; }

            /// <summary>
            /// Gets or sets the files.
            /// </summary>
            public int Files { get; set; }

            /// <summary>
            /// Gets or sets the files that could be read.
            /// </summary>
            public int FilesRead { get; set; }

            /// <summary>
            /// Gets or sets the files that could not be read.
            /// </summary>
            public int ReadErrors { get; set; }

            /// <summary>
            /// Gets or sets the bytes read.
            /// </summary>
            public long Bytes { get; set; }

            /// <summary>
            /// Gets or sets the free sectors.
            /// </summary>
            public long FreeSectors { get; set; } = -1;
        }

        /// <summary>
        /// What Toolshed found.
        /// </summary>
        public sealed class ToolshedRecord
        {
            /// <summary>
            /// Gets or sets the directories "dir -r" listed, the root included.
            /// </summary>
            public int Directories { get; set; }

            /// <summary>
            /// Gets or sets the files "dir -r" listed.
            /// </summary>
            public int Files { get; set; }

            /// <summary>
            /// Gets or sets the free sectors "free" gave (granules times 9 for DECB).
            /// </summary>
            public long FreeSectors { get; set; } = -1;

            /// <summary>
            /// Gets or sets the directories dcheck counted, or -1.
            /// </summary>
            public long DcheckDirectories { get; set; } = -1;

            /// <summary>
            /// Gets or sets the files dcheck counted, or -1.
            /// </summary>
            public long DcheckFiles { get; set; } = -1;

            /// <summary>
            /// Gets or sets whether dcheck said the file structure is intact.
            /// </summary>
            public bool DcheckIntact { get; set; }

            /// <summary>
            /// Gets the count lines at the end of the dcheck output.
            /// </summary>
            public List<string> DcheckCounts { get; } = new List<string>();

            /// <summary>
            /// Gets or sets the number of problem lines dcheck printed.
            /// </summary>
            public int DcheckProblemLines { get; set; }

            /// <summary>
            /// Gets the first problem lines dcheck printed.
            /// </summary>
            public List<string> DcheckProblems { get; } = new List<string>();

            /// <summary>
            /// Gets or sets the files Toolshed copied out.
            /// </summary>
            public int FilesCopied { get; set; }

            /// <summary>
            /// Gets or sets the files whose Toolshed copy equals what our reader read.
            /// </summary>
            public int FilesEqual { get; set; }

            /// <summary>
            /// Gets or sets the copies that ended with an error.
            /// </summary>
            public int CopyFailures { get; set; }

            /// <summary>
            /// Gets the commands that failed or timed out, with what they printed.
            /// </summary>
            public List<string> Errors { get; } = new List<string>();

            /// <summary>
            /// Gets other remarks about the Toolshed output.
            /// </summary>
            public List<string> Notes { get; } = new List<string>();
        }

        /// <summary>
        /// One difference between our reader and Toolshed.
        /// </summary>
        public sealed class Mismatch
        {
            /// <summary>
            /// Gets or sets the path, or / for the whole volume.
            /// </summary>
            public string Path { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the field that differs.
            /// </summary>
            public string Field { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets our value.
            /// </summary>
            public string Ours { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets Toolshed's value.
            /// </summary>
            public string Toolshed { get; set; } = string.Empty;
        }

        /// <summary>
        /// One entry our reader listed.
        /// </summary>
        public sealed class OurEntry
        {
            /// <summary>
            /// Gets or sets whether it is a directory.
            /// </summary>
            public bool IsDirectory { get; set; }

            /// <summary>
            /// Gets or sets the size.
            /// </summary>
            public long Size { get; set; }

            /// <summary>
            /// Gets or sets the attributes as OS-9 shows them.
            /// </summary>
            public string Attributes { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the date last modified, as yyyy/MM/dd HHmm.
            /// </summary>
            public string Modified { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the bytes read, or null when the file could not be read.
            /// </summary>
            public byte[]? Data { get; set; }
        }

        /// <summary>
        /// One entry Toolshed listed.
        /// </summary>
        public sealed class ToolshedEntry
        {
            /// <summary>
            /// Gets or sets whether it is a directory.
            /// </summary>
            public bool IsDirectory { get; set; }

            /// <summary>
            /// Gets or sets the size, or -1 when the listing does not give one.
            /// </summary>
            public long Size { get; set; }

            /// <summary>
            /// Gets or sets the attributes as printed.
            /// </summary>
            public string Attributes { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the date as printed.
            /// </summary>
            public string Modified { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the owner as printed.
            /// </summary>
            public string Owner { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the descriptor sector as printed (hex).
            /// </summary>
            public string DescriptorLsn { get; set; } = string.Empty;
        }

        // A mounted image: either a driver or a file system on a plain block device, and the stream under it.
        private sealed class Mounted : IDisposable
        {
            private readonly IFileSystemDriver? _driver;
            private readonly IBlockFileSystem? _fs;
            private readonly Stream _stream;

            public Mounted(IFileSystemDriver? driver, IBlockFileSystem? fs, Stream stream)
            {
                _driver = driver;
                _fs = fs;
                _stream = stream;
            }

            public DirectoryEntry[] ListDirectory(string path)
            {
                return _driver != null ? _driver.ListDirectory(path) : _fs!.ListDirectory(path);
            }

            public byte[] ReadFile(string path)
            {
                return _driver != null ? _driver.ReadFile(path) : _fs!.ReadFile(path);
            }

            public long FreeSectors()
            {
                if (_fs != null)
                {
                    return _fs.GetBlocksFree();
                }

                if (_driver is DragonDskDriver dsk)
                {
                    return dsk.GetBlocksFree();
                }

                if (_driver is DragonVdkDriver vdk)
                {
                    return vdk.GetBlocksFree();
                }

                if (_driver is DragonDmkDriver dmk)
                {
                    return dmk.GetBlocksFree();
                }

                return -1;
            }

            public void Dispose()
            {
                _driver?.Dispose();
                _stream.Dispose();
            }
        }
    }
}
