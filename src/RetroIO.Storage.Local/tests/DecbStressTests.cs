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
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.FileSystems;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Long random runs of create, overwrite, read, delete and rename on a CoCo DECB disk, checked against a model of what the disk should hold
    /// after every single call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only DECB layout <c>DecbFileSystem</c> accepts is 35 tracks, one side, 18 sectors of 256 bytes (its <c>CanHandle</c> asks for exactly
    /// that, and it has 68 granules built in). Toolshed can make 40 and 80 track DECB disks (<c>decb dskini -4</c> and <c>-8</c>), but the code
    /// does not take them, so they are not run here.
    /// </para>
    /// <para>
    /// After every call the whole image is read back from the block device and checked byte by byte, without using the code under test:
    /// every directory slot against the model, every granule chain (length, end marker, sectors in the last granule, bytes in the last sector,
    /// the data itself), no granule in two chains, and every granule that is in no chain marked free (a lost granule fails at once). Then the
    /// code's own answers are checked: the listing in directory order, every file read back, and the free space, which must equal 68 granules
    /// minus the granules the model's files need. Every message names the seed and the call number, so a failure can be run again.
    /// </para>
    /// <para>
    /// The run is split in rounds. Each round fills the disk to the last granule once and then asks for one more (which must fail and leave
    /// every byte as it was), and ends by deleting every file. The memory still held after each round, and the time each call took, are
    /// compared between the early and the late rounds.
    /// </para>
    /// <para>
    /// These runs stay at the file system level: the file system sits on a plain <c>GenericBlockDevice</c>, with no Dragon driver and no
    /// container format in between. The same disks through the JVC, VDK and DMK drivers are run by <c>DragonStressTests</c>, so driver
    /// variants belong there and not here.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class DecbStressTests
    {
        [Test]
        public void Decb35Track_500RandomCalls_EveryCheckHoldsAfterEveryCall()
        {
            DecbStressRun run = new DecbStressRun(20261021, 5, 100, false);
            run.Execute();
        }

        [Test]
        [Explicit("Long run, about a minute: start it by hand")]
        [Category("Stress")]
        public void Decb35Track_5000RandomCalls_EveryCheckHoldsAfterEveryCall()
        {
            DecbStressRun run = new DecbStressRun(4711, 50, 100, true);
            run.Execute();
        }
    }

    /// <summary>
    /// One seeded stress run on a fresh DECB disk image in a temporary file.
    /// </summary>
    internal sealed class DecbStressRun
    {
        private const int SectorSize = 256;
        private const int SectorsPerTrack = 18;
        private const int Tracks = 35;
        private const int TotalSectors = Tracks * SectorsPerTrack;
        private const int Granules = 68;
        private const int GranuleBytes = 2304;
        private const int SectorsPerGranule = 9;
        private const int DirectoryEntries = 72;
        private const int FatOffset = ((17 * SectorsPerTrack) + 1) * SectorSize;
        private const int DirOffset = ((17 * SectorsPerTrack) + 2) * SectorSize;
        private const int Track17Offset = 17 * SectorsPerTrack * SectorSize;

        // Kinds of call, for the timings.
        private const int KindCreate = 0;
        private const int KindOverwrite = 1;
        private const int KindRead = 2;
        private const int KindDelete = 3;
        private const int KindRename = 4;
        private const int KindRefused = 5;
        private static readonly string[] KindNames = { "create", "overwrite", "read", "delete", "rename", "refused" };

        // The names files get. A small set, so that names are taken again after a delete and renames run into existing names.
        private static readonly string[] Extensions = { "BIN", "BAS", "DAT", "TXT", string.Empty, "X", "AB" };

        private sealed class ModelFile
        {
            public string Name = string.Empty;
            public byte[] Data = Array.Empty<byte>();
            public byte Type;
            public byte Flag;
        }

        private readonly int _seed;
        private readonly int _rounds;
        private readonly int _callsPerRound;
        private readonly bool _toolshedEveryRound;
        private readonly Random _random;
        private readonly StressTimings _timings;
        private readonly List<ModelFile?> _slots = new List<ModelFile?>(DirectoryEntries);
        private readonly long[] _memory;
        private readonly string[] _names;
        private readonly byte[][] _probeData;
        private readonly byte[][] _probeOther;
        private readonly byte[][] _ballast;
        private bool _probing;
        private long _probeTicks;

        private string _folder = string.Empty;
        private string _imagePath = string.Empty;
        private byte[] _initial = Array.Empty<byte>();
        private GenericBlockDevice? _device;
        private CountingBlockDevice? _counter;
        private DecbFileSystem? _fs;
        private int _call;
        private string _callText = string.Empty;
        private int _round;
        private readonly byte[] _snapshot = new byte[TotalSectors * SectorSize];
        private readonly byte[] _before = new byte[TotalSectors * SectorSize];
        private readonly StringBuilder _toolshedNotes = new StringBuilder();

        // Counters for the report.
        private int _creates;
        private int _overwrites;
        private int _reads;
        private int _deletes;
        private int _renames;
        private int _refusedFull;
        private int _refusedOther;
        private int _fills;
        private int _toolshedRuns;

        public DecbStressRun(int seed, int rounds, int callsPerRound, bool toolshedEveryRound)
        {
            _seed = seed;
            _rounds = rounds;
            _callsPerRound = callsPerRound;
            _toolshedEveryRound = toolshedEveryRound;
            _random = new Random(seed);
            _timings = new StressTimings(KindNames, rounds * (callsPerRound + 200), rounds);
            _memory = new long[rounds];

            // The probe data and the ballast that brings the disk to the probe's fill levels, made once so the probes allocate nothing.
            int[] sizes = { 0, 1, 256, 2305, 5000, 9000 };
            _probeData = new byte[sizes.Length][];
            _probeOther = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
            {
                _probeData[i] = new byte[sizes[i]];
                _probeOther[i] = new byte[sizes[sizes.Length - 1 - i]];
                _random.NextBytes(_probeData[i]);
                _random.NextBytes(_probeOther[i]);
            }

            // The probe needs at most 18 granules at once (while it overwrites), so the fullest ballast must leave that: 70 % is 47 granules,
            // and 47 + 18 = 65 of 68.
            _ballast = new byte[StressTimings.ProbePercent.Length][];
            for (int level = 0; level < _ballast.Length; level++)
            {
                _ballast[level] = new byte[Granules * StressTimings.ProbePercent[level] / 100 * GranuleBytes];
            }

            // 120 names: a letter and two digits, with one of the extensions.
            _names = new string[120];
            for (int i = 0; i < _names.Length; i++)
            {
                string ext = Extensions[i % Extensions.Length];
                string name = ((char)('A' + (i % 26))).ToString() + (i / 26).ToString() + (i % 10).ToString();
                _names[i] = ext.Length == 0 ? name : name + "." + ext;
            }
        }

        private string Where()
        {
            return "seed " + _seed + ", call " + _call + " (" + _callText + ")";
        }

        public void Execute()
        {
            _folder = Path.Combine(Path.GetTempPath(), "decbstress-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            _imagePath = Path.Combine(_folder, "stress.dsk");
            try
            {
                File.WriteAllBytes(_imagePath, BlankImage());
                Mount();
                _initial = (byte[])Snapshot().Clone();
                Verify();

                // Toolshed on the empty disk first: it checks the blank image, and the first start of a process is paid before any memory is measured.
                ToolshedCheck();
                RunRounds();
            }
            finally
            {
                _device?.Dispose();
                _device = null;
                if (Directory.Exists(_folder))
                {
                    Directory.Delete(_folder, true);
                }
            }
        }

        // A freshly formatted disk as Toolshed dskini makes it: 68 free FAT bytes (0xFF), the rest of the FAT sector zero, the whole directory 0xFF.
        private static byte[] BlankImage()
        {
            byte[] image = new byte[TotalSectors * SectorSize];
            for (int i = 0; i < Granules; i++)
            {
                image[FatOffset + i] = 0xFF;
            }

            for (int i = 0; i < 9 * SectorSize; i++)
            {
                image[DirOffset + i] = 0xFF;
            }

            return image;
        }

        private void Mount()
        {
            BlockDeviceDescriptor descriptor = new BlockDeviceDescriptor
            {
                Name = "CoCo DSK 35T/SS",
                BlockSize = SectorSize,
                AddressMode = BlockAddressMode.LBA,
                Cylinders = Tracks,
                Heads = 1,
                UniformSectorsPerTrack = SectorsPerTrack,
                TotalBlocks = TotalSectors,
                ExpectedImageSize = TotalSectors * SectorSize,
                SupportsVariableGeometry = false
            };

            FileStream stream = new FileStream(_imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            _device = new GenericBlockDevice(descriptor);
            _device.Mount(stream, false);
            _counter = new CountingBlockDevice(_device);
            _fs = new DecbFileSystem();
            Assert.That(_fs.CanHandle(_counter), Is.True, "DecbFileSystem takes the image");
            _fs.Initialize(_counter);
        }

        // Disposes the device, requires that nothing holds the image file open any more, and mounts it again with new objects.
        private void Remount()
        {
            _device!.Dispose();
            _device = null;
            _fs = null;
            Assert.That(StressMemory.CanOpenExclusively(_imagePath, out string reason), Is.True,
                "the image file is still open after the device was disposed: " + reason + ", " + Where());
            Mount();
        }

        // Every sector, read through the block device (the bytes the file system wrote, without its own reader).
        private byte[] Snapshot()
        {
            byte[] image = _snapshot;
            for (uint lba = 0; lba < TotalSectors; lba++)
            {
                _device!.ReadBlock(BlockAddress.FromLBA(lba), image.AsSpan((int)lba * SectorSize, SectorSize));
            }

            return image;
        }

        private static int GranulesFor(int length)
        {
            return Math.Max(1, (length + GranuleBytes - 1) / GranuleBytes);
        }

        private int UsedGranules()
        {
            int used = 0;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i] != null)
                {
                    used += GranulesFor(_slots[i]!.Data.Length);
                }
            }

            return used;
        }

        private int FileCount()
        {
            int count = 0;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i] != null)
                {
                    count++;
                }
            }

            return count;
        }

        private int IndexOf(string name)
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i] != null && string.Equals(_slots[i]!.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        // The first deleted slot or the end marker, as the code and Toolshed pick it. -1 when all 72 are in use.
        private int FirstEmpty()
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i] == null)
                {
                    return i;
                }
            }

            return _slots.Count < DirectoryEntries ? _slots.Count : -1;
        }

        private ModelFile? RandomFile()
        {
            int count = FileCount();
            if (count == 0)
            {
                return null;
            }

            int pick = _random.Next(count);
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i] != null && pick-- == 0)
                {
                    return _slots[i];
                }
            }

            return null;
        }

        // The name as it is passed in: sometimes lower case, sometimes with a leading slash. The disk always holds upper case.
        private string Spelled(string name)
        {
            int how = _random.Next(4);
            if (how == 0)
            {
                return name.ToLowerInvariant();
            }

            return how == 1 ? "/" + name : name;
        }

        private static void DefaultType(string name, out byte type, out byte flag)
        {
            int dot = name.IndexOf('.');
            string ext = dot >= 0 ? name.Substring(dot + 1) : string.Empty;
            type = 2;
            flag = 0x00;
            if (ext == "BAS")
            {
                type = 0;
            }
            else if (ext == "DAT")
            {
                type = 1;
                flag = 0xFF;
            }
            else if (ext == "TXT")
            {
                type = 3;
                flag = 0xFF;
            }
        }

        private byte[] RandomData(int length)
        {
            byte[] data = new byte[length];
            _random.NextBytes(data);
            return data;
        }

        // Sizes that hit every boundary: empty, one byte, around a sector, around a granule, whole granules, and around the free space.
        private int RandomSize(int availableGranules)
        {
            int r = _random.Next(100);
            int k = 2 + _random.Next(3);
            if (r < 8)
                return 0;
            if (r < 14)
                return 1;
            if (r < 20)
                return SectorSize - 1 + _random.Next(3);
            if (r < 30)
                return GranuleBytes - 1 + _random.Next(3);
            if (r < 45)
                return (k * GranuleBytes) - 1 + _random.Next(3);
            if (r < 80)
                return _random.Next(3 * GranuleBytes);
            if (r < 88)
                return availableGranules > 0 ? _random.Next(availableGranules * GranuleBytes) : 1;
            if (r < 94)
                return Math.Max(1, availableGranules * GranuleBytes);
            return (availableGranules * GranuleBytes) + 1;
        }

        private void RunRounds()
        {
            for (_round = 0; _round < _rounds; _round++)
            {
                for (int i = 0; i < _callsPerRound; i++)
                {
                    if (i == _callsPerRound / 2)
                    {
                        FillToFull();
                    }

                    RandomCall();
                }

                bool first = _round == 0;
                bool last = _round == _rounds - 1;
                CheckImageFileMatchesDevice();
                if (last || _toolshedEveryRound)
                {
                    ToolshedCheck();
                }

                if (first || last)
                {
                    // A new mount on the same file must see exactly what the model holds. Done in the first round too, so that the memory taken
                    // by the first use of everything is in the first measurement.
                    _callText = "remount";
                    Remount();
                    Verify();
                }

                DeleteAll();
                RunProbes();
                if (last || _toolshedEveryRound)
                {
                    ToolshedCheck();
                }

                _memory[_round] = StressMemory.Retained();
            }

            _device!.Dispose();
            _device = null;
            _fs = null;
            Assert.That(StressMemory.CanOpenExclusively(_imagePath, out string reason), Is.True, "the image file is still open at the end: " + reason);

            Report();
        }

        private void RandomCall()
        {
            int r = _random.Next(100);
            if (r < 32)
            {
                int index = _random.Next(_names.Length);
                if (IndexOf(_names[index]) >= 0)
                {
                    Overwrite(_slots[IndexOf(_names[index])]!);
                }
                else
                {
                    Create(_names[index]);
                }
            }
            else if (r < 50)
            {
                ModelFile? file = RandomFile();
                if (file != null)
                    Overwrite(file);
                else
                    Create(_names[_random.Next(_names.Length)]);
            }
            else if (r < 62)
            {
                ModelFile? file = RandomFile();
                if (file != null)
                    Read(file);
                else
                    DeleteMissing();
            }
            else if (r < 80)
            {
                ModelFile? file = RandomFile();
                if (file != null)
                    Delete(file);
                else
                    DeleteMissing();
            }
            else if (r < 95)
            {
                ModelFile? file = RandomFile();
                if (file != null)
                    Rename(file);
                else
                    DeleteMissing();
            }
            else
            {
                DeleteMissing();
            }
        }

        private void Begin(string text)
        {
            _call++;
            _callText = text;
        }

        // Keeps the time of one call: in the table of random calls, or, during a probe pass, in the probe's sum.
        private void Record(int kind, int bucket, int round, long ticks)
        {
            if (_probing)
            {
                _probeTicks += ticks;
                return;
            }

            _timings.Add(kind, bucket, round, ticks);
        }

        // The fixed probe: the same 30 calls with the same sizes every time (create six files, read them, overwrite them with other sizes,
        // rename them, delete them). It is run three times at each fill level after every round; the fastest pass counts.
        private void RunProbes()
        {
            for (int level = 0; level < StressTimings.ProbePercent.Length; level++)
            {
                if (_ballast[level].Length > 0)
                {
                    Write("BALLAST.BIN", _ballast[level], false, 0, 0);
                }

                for (int pass = 0; pass < 3; pass++)
                {
                    _probing = true;
                    _probeTicks = 0;
                    long blocks = _counter!.Reads + _counter.Writes;
                    for (int i = 0; i < _probeData.Length; i++)
                    {
                        Write("PRB" + i + ".PRB", _probeData[i], false, 0, 0);
                    }

                    for (int i = 0; i < _probeData.Length; i++)
                    {
                        Read(_slots[IndexOf("PRB" + i + ".PRB")]!);
                    }

                    for (int i = 0; i < _probeData.Length; i++)
                    {
                        Write("PRB" + i + ".PRB", _probeOther[i], false, 0, 0);
                    }

                    for (int i = 0; i < _probeData.Length; i++)
                    {
                        RenameTo(_slots[IndexOf("PRB" + i + ".PRB")]!, "PRQ" + i + ".PRB");
                    }

                    for (int i = 0; i < _probeData.Length; i++)
                    {
                        Delete(_slots[IndexOf("PRQ" + i + ".PRB")]!);
                    }

                    _probing = false;
                    _timings.AddProbe(_round, level, _probeTicks, _counter.Reads + _counter.Writes - blocks);
                }
            }

            Delete(_slots[IndexOf("BALLAST.BIN")]!);
        }

        private int Bucket()
        {
            return StressTimings.BucketOf(UsedGranules(), Granules);
        }

        private void Create(string name)
        {
            int free = Granules - UsedGranules();
            byte[] data = RandomData(RandomSize(free));
            bool typed = _random.Next(5) == 0;
            byte type = (byte)_random.Next(4);
            byte flag = _random.Next(2) == 0 ? (byte)0x00 : (byte)0xFF;
            Write(name, data, typed, type, flag);
        }

        private void Overwrite(ModelFile file)
        {
            int available = Granules - UsedGranules() + GranulesFor(file.Data.Length);
            byte[] data = RandomData(RandomSize(available));
            bool typed = _random.Next(8) == 0;
            byte type = (byte)_random.Next(4);
            byte flag = _random.Next(2) == 0 ? (byte)0x00 : (byte)0xFF;
            Write(file.Name, data, typed, type, flag);
        }

        // Writes a file and checks the outcome against the model: it must work when the granules are there, and must throw and change no byte
        // when they are not.
        private void Write(string name, byte[] data, bool typed, byte type, byte flag)
        {
            int found = IndexOf(name);
            int available = Granules - UsedGranules() + (found >= 0 ? GranulesFor(_slots[found]!.Data.Length) : 0);
            bool fits = GranulesFor(data.Length) <= available;
            Begin((found >= 0 ? "overwrite " : "create ") + name + " with " + data.Length + " bytes" + (typed ? " type " + type + " flag " + flag : string.Empty)
                + (fits ? string.Empty : ", does not fit"));
            if (!fits)
            {
                Buffer.BlockCopy(Snapshot(), 0, _before, 0, _before.Length);
            }

            int bucket = Bucket();
            string spelled = Spelled(name);
            Exception? error = null;
            long start = Stopwatch.GetTimestamp();
            try
            {
                if (typed)
                    _fs!.WriteFile(spelled, data, type, flag);
                else
                    _fs!.WriteFile(spelled, data);
            }
            catch (IOException ex)
            {
                error = ex;
            }

            long ticks = Stopwatch.GetTimestamp() - start;

            if (!fits)
            {
                Record(KindRefused, bucket, _round, ticks);
                Assert.That(error, Is.Not.Null, "a write that needs more granules than are free did not throw, " + Where());
                Assert.That(error!.Message, Does.Contain("full"), Where());
                Assert.That(Snapshot().AsSpan().SequenceEqual(_before), Is.True, "a refused write changed the disk, " + Where());
                _refusedFull++;
                Verify();
                return;
            }

            Record(found >= 0 ? KindOverwrite : KindCreate, bucket, _round, ticks);
            Assert.That(error, Is.Null, "the write failed although " + available + " granules were free, " + Where() + ": " + error?.Message);

            ModelFile written = new ModelFile { Name = name, Data = data };
            if (found >= 0)
            {
                written.Type = typed ? type : _slots[found]!.Type;
                written.Flag = typed ? flag : _slots[found]!.Flag;

                // The code (like Toolshed, which kills and then creates) puts the new entry in the first empty slot when that comes before the old one.
                int slot = FirstEmpty();
                if (slot < 0 || found < slot)
                {
                    slot = found;
                }

                _slots[found] = null;
                Place(slot, written);
                _overwrites++;
            }
            else
            {
                if (typed)
                {
                    written.Type = type;
                    written.Flag = flag;
                }
                else
                {
                    DefaultType(name, out written.Type, out written.Flag);
                }

                Place(FirstEmpty(), written);
                _creates++;
            }

            Verify();
        }

        private void Place(int slot, ModelFile file)
        {
            if (slot == _slots.Count)
                _slots.Add(file);
            else
                _slots[slot] = file;
        }

        private void Read(ModelFile file)
        {
            Begin("read " + file.Name);
            int bucket = Bucket();
            long start = Stopwatch.GetTimestamp();
            byte[] data = _fs!.ReadFile(Spelled(file.Name));
            Record(KindRead, bucket, _round, Stopwatch.GetTimestamp() - start);
            Assert.That(data.AsSpan().SequenceEqual(file.Data), Is.True, "read back differs, " + Where());
            _reads++;
            Verify();
        }

        private void Delete(ModelFile file)
        {
            Begin("delete " + file.Name);
            int bucket = Bucket();
            long start = Stopwatch.GetTimestamp();
            _fs!.DeleteFile(Spelled(file.Name));
            Record(KindDelete, bucket, _round, Stopwatch.GetTimestamp() - start);
            _slots[IndexOf(file.Name)] = null;
            _deletes++;
            Verify();
        }

        // Deleting a name that is not there must throw FileNotFoundException and change nothing.
        private void DeleteMissing()
        {
            string name = _names[_random.Next(_names.Length)];
            if (IndexOf(name) >= 0)
            {
                return;
            }

            Begin("delete missing " + name);
            Buffer.BlockCopy(Snapshot(), 0, _before, 0, _before.Length);
            int bucket = Bucket();
            long start = Stopwatch.GetTimestamp();
            Assert.Throws<FileNotFoundException>(() => _fs!.DeleteFile(name), Where());
            Record(KindRefused, bucket, _round, Stopwatch.GetTimestamp() - start);
            Assert.That(Snapshot().AsSpan().SequenceEqual(_before), Is.True, "a refused delete changed the disk, " + Where());
            _refusedOther++;
            Verify();
        }

        private void Rename(ModelFile file)
        {
            RenameTo(file, _names[_random.Next(_names.Length)]);
        }

        private void RenameTo(ModelFile file, string target)
        {
            bool clash = IndexOf(target) >= 0;
            Begin("rename " + file.Name + " to " + target + (clash ? ", name taken" : string.Empty));
            if (clash)
            {
                Buffer.BlockCopy(Snapshot(), 0, _before, 0, _before.Length);
            }

            int bucket = Bucket();
            Exception? error = null;
            long start = Stopwatch.GetTimestamp();
            try
            {
                _fs!.Rename(Spelled(file.Name), Spelled(target));
            }
            catch (IOException ex)
            {
                error = ex;
            }

            long ticks = Stopwatch.GetTimestamp() - start;
            if (clash)
            {
                Record(KindRefused, bucket, _round, ticks);
                Assert.That(error, Is.Not.Null, "a rename onto an existing name did not throw, " + Where());
                Assert.That(Snapshot().AsSpan().SequenceEqual(_before), Is.True, "a refused rename changed the disk, " + Where());
                _refusedOther++;
            }
            else
            {
                Record(KindRename, bucket, _round, ticks);
                Assert.That(error, Is.Null, "rename failed, " + Where() + ": " + error?.Message);
                file.Name = target;
                _renames++;
            }

            Verify();
        }

        // Fills the disk to the last granule by overwriting one file with exactly the free space plus its own granules, then asks for one granule
        // more in three ways (a new file, an empty new file, the same file one byte bigger). Each must throw and change nothing.
        private void FillToFull()
        {
            _fills++;
            ModelFile? file = RandomFile();
            if (file == null)
            {
                Write(_names[_random.Next(_names.Length)], Array.Empty<byte>(), false, 0, 0);
                file = RandomFile();
            }

            if (file == null)
            {
                return;
            }

            int available = Granules - UsedGranules() + GranulesFor(file.Data.Length);
            int size = (available * GranuleBytes) - _random.Next(SectorSize);
            Write(file.Name, RandomData(size), false, 0, 0);
            Assert.That(_fs!.GetBlocksFree(), Is.EqualTo(0), "the disk is not full after the fill, " + Where());

            string other = _names[_random.Next(_names.Length)];
            if (IndexOf(other) < 0)
            {
                Write(other, new byte[] { 1 }, false, 0, 0);
                Write(other, Array.Empty<byte>(), false, 0, 0);
            }

            Write(file.Name, RandomData((available * GranuleBytes) + 1), false, 0, 0);
        }

        // Deletes every file in a random order and requires the whole disk back: 612 free sectors, every FAT byte free.
        private void DeleteAll()
        {
            while (FileCount() > 0)
            {
                Delete(RandomFile()!);
            }

            Begin("all files deleted");
            Assert.That(_fs!.GetBlocksFree(), Is.EqualTo(Granules * SectorsPerGranule), "free space did not come back to the empty-disk value, " + Where());
            byte[] image = Snapshot();
            for (int g = 0; g < Granules; g++)
            {
                Assert.That(image[FatOffset + g], Is.EqualTo(0xFF), "granule " + g + " is not free after every file was deleted, " + Where());
            }
        }

        // ---- the checks run after every call ----

        private void Verify()
        {
            // The checks below go through the file system too; their blocks are not part of the work being measured.
            _counter!.Paused = true;
            VerifyCore();
            _counter.Paused = false;
        }

        private void VerifyCore()
        {
            byte[] image = Snapshot();

            // Track 17 outside the FAT and directory sectors, and the FAT sector after byte 67, are never written.
            for (int i = Track17Offset; i < Track17Offset + (SectorsPerTrack * SectorSize); i++)
            {
                bool inFat = i >= FatOffset + Granules && i < FatOffset + SectorSize;
                bool outsideBoth = i < FatOffset || i >= DirOffset + (9 * SectorSize);
                if ((inFat || outsideBoth) && image[i] != _initial[i])
                {
                    Assert.Fail("byte " + (i - Track17Offset) + " of track 17 changed (" + _initial[i] + " to " + image[i] + "), " + Where());
                }
            }

            int[] owner = new int[Granules];
            for (int g = 0; g < Granules; g++)
            {
                owner[g] = -1;
            }

            int usedGranules = 0;
            for (int slot = 0; slot < DirectoryEntries; slot++)
            {
                int e = DirOffset + (slot * 32);
                ModelFile? file = slot < _slots.Count ? _slots[slot] : null;
                if (file == null)
                {
                    byte expectedFirst = slot < _slots.Count ? (byte)0x00 : (byte)0xFF;
                    if (image[e] != expectedFirst)
                    {
                        Assert.Fail("directory slot " + slot + " starts with " + image[e] + ", expected " + expectedFirst + ", " + Where());
                    }

                    continue;
                }

                usedGranules += CheckEntry(image, slot, file, owner);
            }

            for (int g = 0; g < Granules; g++)
            {
                if (owner[g] < 0 && image[FatOffset + g] != 0xFF)
                {
                    Assert.Fail("lost granule " + g + ": FAT value " + image[FatOffset + g] + " but no file uses it, " + Where());
                }
            }

            // Free space plus the space the files need is the whole disk.
            int free = _fs!.GetBlocksFree();
            if (free != (Granules - usedGranules) * SectorsPerGranule)
            {
                Assert.Fail("free space is " + free + " sectors, the files need " + usedGranules + " of " + Granules + " granules, so it should be "
                    + ((Granules - usedGranules) * SectorsPerGranule) + ", " + Where());
            }

            DirectoryEntry[] listing = _fs.ListDirectory("/");
            int n = 0;
            for (int slot = 0; slot < _slots.Count; slot++)
            {
                ModelFile? file = _slots[slot];
                if (file == null)
                {
                    continue;
                }

                if (n >= listing.Length || listing[n].Name != file.Name || listing[n].Metadata.Size != file.Data.Length)
                {
                    Assert.Fail("listing entry " + n + " is " + (n < listing.Length ? listing[n].Name + " " + listing[n].Metadata.Size : "missing")
                        + ", expected " + file.Name + " " + file.Data.Length + ", " + Where());
                }

                if (!_fs.ReadFile(file.Name).AsSpan().SequenceEqual(file.Data))
                {
                    Assert.Fail(file.Name + " does not read back as written, " + Where());
                }

                n++;
            }

            Assert.That(listing.Length, Is.EqualTo(n), "the listing has more files than the model, " + Where());
        }

        // Checks one directory entry and its granule chain in the raw image. Gives the number of granules of the chain.
        private int CheckEntry(byte[] image, int slot, ModelFile file, int[] owner)
        {
            int e = DirOffset + (slot * 32);
            int dot = file.Name.IndexOf('.');
            string name = dot >= 0 ? file.Name.Substring(0, dot) : file.Name;
            string ext = dot >= 0 ? file.Name.Substring(dot + 1) : string.Empty;
            string expectedName = name.PadRight(8) + ext.PadRight(3);
            string actualName = Encoding.ASCII.GetString(image, e, 11);
            if (actualName != expectedName || image[e + 11] != file.Type || image[e + 12] != file.Flag)
            {
                Assert.Fail("slot " + slot + " holds '" + actualName + "' type " + image[e + 11] + " flag " + image[e + 12] + ", expected '"
                    + expectedName + "' type " + file.Type + " flag " + file.Flag + ", " + Where());
            }

            int length = file.Data.Length;
            int granules = GranulesFor(length);
            int lastBytes = length == 0 ? 0 : (length % SectorSize == 0 ? SectorSize : length % SectorSize);
            int inLast = length - ((granules - 1) * GranuleBytes);
            int sectorsInLast = Math.Max(1, (inLast + SectorSize - 1) / SectorSize);
            int storedLastBytes = (image[e + 14] << 8) | image[e + 15];
            if (storedLastBytes != lastBytes)
            {
                Assert.Fail(file.Name + ": bytes in the last sector are " + storedLastBytes + ", expected " + lastBytes + ", " + Where());
            }

            int granule = image[e + 13];
            int position = 0;
            for (int j = 0; j < granules; j++)
            {
                if (granule >= Granules)
                {
                    Assert.Fail(file.Name + ": granule " + j + " of the chain is " + granule + ", outside the disk, " + Where());
                }

                if (owner[granule] >= 0)
                {
                    Assert.Fail("granule " + granule + " is used by " + file.Name + " and by the file in slot " + owner[granule] + ", " + Where());
                }

                owner[granule] = slot;
                int value = image[FatOffset + granule];
                bool lastOne = j == granules - 1;
                if (lastOne && value != 0xC0 + sectorsInLast)
                {
                    Assert.Fail(file.Name + ": the chain ends at granule " + granule + " with FAT value " + value + ", expected " + (0xC0 + sectorsInLast)
                        + ", " + Where());
                }

                if (!lastOne && value >= Granules)
                {
                    Assert.Fail(file.Name + ": the chain stops after " + (j + 1) + " granules (FAT value " + value + "), expected " + granules + ", " + Where());
                }

                int track = granule / 2;
                if (track >= 17)
                {
                    track++;
                }

                int offset = ((track * SectorsPerTrack) + ((granule % 2) * SectorsPerGranule)) * SectorSize;
                int take = Math.Min(GranuleBytes, length - position);
                if (take > 0 && !image.AsSpan(offset, take).SequenceEqual(file.Data.AsSpan(position, take)))
                {
                    Assert.Fail(file.Name + ": the data in granule " + granule + " differs from what was written, " + Where());
                }

                position += Math.Max(0, take);
                granule = value;
            }

            return granules;
        }

        private void CheckImageFileMatchesDevice()
        {
            _callText = "image file after round " + _round;
            byte[] file = StressMemory.ReadShared(_imagePath);
            Assert.That(file.AsSpan().SequenceEqual(Snapshot()), Is.True, "the image file is not what the device holds: a write was not flushed, " + Where());
        }

        // ---- Toolshed ----

        private void ToolshedCheck()
        {
            string image = StressToolshed.WslPath(_imagePath);
            string? free = StressToolshed.Run("decb", "free", image);
            if (free == null)
            {
                if (_toolshedNotes.Length == 0)
                {
                    _toolshedNotes.Append("Toolshed decb is not available through WSL Ubuntu, so the independent check was skipped.");
                }

                return;
            }

            _toolshedRuns++;
            _callText = "Toolshed check after round " + _round;
            Assert.That(StressToolshed.NumberAfter(free, "Free granules:"), Is.EqualTo(Granules - UsedGranules()), "decb free: " + free + ", " + Where());

            string? dir = StressToolshed.Run("decb", "dir", image);
            Assert.That(dir, Is.Not.Null);
            string[] lines = dir!.Split('\n');
            List<string> listed = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Length < 14 || line.StartsWith("Directory of", StringComparison.Ordinal))
                {
                    continue;
                }

                string name = line.Substring(0, 8).Trim();
                string ext = line.Substring(9, 3).Trim();
                listed.Add(ext.Length == 0 ? name : name + "." + ext);
            }

            int n = 0;
            for (int slot = 0; slot < _slots.Count; slot++)
            {
                if (_slots[slot] == null)
                {
                    continue;
                }

                Assert.That(n < listed.Count && listed[n] == _slots[slot]!.Name, Is.True, "decb dir line " + n + " is not " + _slots[slot]!.Name + ":\n" + dir + Where());
                n++;
            }

            Assert.That(listed.Count, Is.EqualTo(n), "decb dir lists other files:\n" + dir + Where());

            // Toolshed copies up to three files out, and they must be what was written.
            for (int k = 0; k < 3; k++)
            {
                ModelFile? file = RandomFile();
                if (file == null)
                {
                    break;
                }

                string target = Path.Combine(_folder, "out" + k + ".bin");
                string? copy = StressToolshed.Run("decb", "copy", "-b", image + "," + file.Name, StressToolshed.WslPath(target));
                Assert.That(File.Exists(target), Is.True, "decb copy made no file: " + copy + ", " + Where());
                Assert.That(File.ReadAllBytes(target).AsSpan().SequenceEqual(file.Data), Is.True, "decb copy of " + file.Name + " differs, " + Where());
                File.Delete(target);
            }
        }

        // ---- report and the checks over the whole run ----

        private void Report()
        {
            StringBuilder report = new StringBuilder();
            report.Append("DECB 35 track stress, seed ").Append(_seed).Append(": ").Append(_call).Append(" calls in ").Append(_rounds).Append(" rounds (")
                .Append(_creates).Append(" creates, ").Append(_overwrites).Append(" overwrites, ").Append(_reads).Append(" reads, ")
                .Append(_deletes).Append(" deletes, ").Append(_renames).Append(" renames, ").Append(_refusedFull).Append(" refused as disk full, ")
                .Append(_refusedOther).Append(" other refusals, ").Append(_fills).Append(" fills to the last granule)").AppendLine();
            report.Append("  Toolshed checks run: ").Append(_toolshedRuns).Append(". ").Append(_toolshedNotes.ToString()).AppendLine();
            report.Append("  memory still held after each round (bytes):");
            for (int i = 0; i < _rounds; i++)
            {
                report.Append(' ').Append(_memory[i]);
            }

            report.AppendLine();
            _timings.Describe(report, _rounds);
            _timings.DescribeProbes(report);
            TestContext.Out.WriteLine(report.ToString());

            StressChecks.RequireNoLeak(_memory, report.ToString());
            StressChecks.RequireNoSlowdown(_timings, report.ToString());
        }
    }
}
