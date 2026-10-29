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
using RetroIO.Storage.Local.Streams;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Long random runs of create, overwrite, read, delete, rename, move, make directory and remove directory on OS-9 (RBF) disks, checked
    /// against a model of what the disk should hold after every single call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Variants: four empty disks built here (CoCo 35 tracks one side, CoCo 40 tracks one side, Dragon 40 tracks two sides with the first track
    /// reserved, CoCo 80 tracks two sides, whose allocation map needs two sectors), a copy of the real NitrOS-9 disk NITR09.DSK, and copies of
    /// disks in the os9 test-image folder (see <c>Os9RbfRealDiskTests</c>): the NitrOS-9 Level 1 disk for the CoCo, 80 tracks; the Dragon 64
    /// disk, 80 tracks, with 2 sectors per map bit; the UUCPbb 2.1 package, 10000 sectors with 5 map sectors; and two empty volumes made by
    /// Toolshed os9 format, 40000 sectors with 20 map sectors, and 20000 sectors with 4 sectors per map bit. The code cannot format a disk, so
    /// the empty disks are built byte for byte the way Toolshed <c>os9 format</c> lays them out (dumped from images it made on 2026-10-21), and
    /// Toolshed <c>os9 dcheck</c> checks each one before the run starts. One more variant is the NitrOS-9 Level 1 DriveWire disk, whose file
    /// is shorter than its volume (sector 0 says 524268 sectors, the file holds far fewer). It is used as it is, not padded: the device is the
    /// whole volume, the sectors behind the file read as zero, and the file grows only as far as the last sector written.
    /// </para>
    /// <para>
    /// Where the outcome of a call depends on the allocation map, the model works it out from the rules of rbf.asm: a new file takes one cluster
    /// for its descriptor, a directory that must grow takes the segment allocation size or, when no free run is that long, the longest free run
    /// (FatScan), and a file's data ends up in whole clusters counted with the descriptor's cluster (Create and L0EFE). On a disk with more than
    /// one whole map sector FatScan starts where it last allocated, which the model does not follow; there the model takes every possible start
    /// and accepts both outcomes when they differ. A file on a real disk whose segment crosses an allocation map sector cannot be released
    /// (RBF clears bits in one map sector only); the model keeps it and expects every delete or overwrite of it to be refused unchanged.
    /// </para>
    /// <para>
    /// After every call the whole image is read back through the block device and walked from the root without the code under test: every
    /// directory slot against the model (free slots, the entries for the directory itself and its parent, names, descriptor sectors), every file
    /// descriptor (type, size, sectors in the segment list) and every byte of data. Every sector of every descriptor and segment is claimed, so a
    /// sector in two files fails at once; then every sector of the allocation map must be set exactly when a file or directory owns it or it was
    /// reserved on the empty disk, so a lost sector or a sector in use but marked free fails at once. Then the code's own answers are checked:
    /// the listing of every directory in slot order, every file read back, and the free space, which must equal the total minus the reserved
    /// sectors minus the sectors the model's files and directories own. Every message names the seed and the call number.
    /// </para>
    /// <para>
    /// The run is split in rounds. Each round fills the disk once and then asks for more (which must fail and leave every byte as it was), and
    /// ends by deleting every file and directory. The memory still held after each round, and the time each call took, are compared between the
    /// early and the late rounds.
    /// </para>
    /// <para>
    /// These runs stay at the file system level: the file system sits on a plain <c>GenericBlockDevice</c>, with no Dragon driver and no
    /// container format in between. The same disks through the JVC, VDK and DMK drivers are run by <c>DragonStressTests</c>, so driver
    /// variants belong there and not here.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class Os9RbfStressTests
    {
        /// <summary>
        /// The disks the runs use.
        /// </summary>
        /// <returns>
        /// One case per disk.
        /// </returns>
        public static IEnumerable<TestCaseData> Variants()
        {
            yield return new TestCaseData("CoCo35SS").SetName("Os9_CoCo35TrackOneSide_500RandomCalls");
            yield return new TestCaseData("CoCo40SS").SetName("Os9_CoCo40TrackOneSide_500RandomCalls");
            yield return new TestCaseData("Dragon40DS").SetName("Os9_Dragon40TrackTwoSides_500RandomCalls");
            yield return new TestCaseData("CoCo80DS").SetName("Os9_CoCo80TrackTwoSides_500RandomCalls");
            yield return new TestCaseData("NITR09").SetName("Os9_RealNitros9Disk_500RandomCalls");
            yield return new TestCaseData("CoCo1L1_80D").SetName("Os9_RealNitros9CoCoLevel1_80Track_500RandomCalls");
            yield return new TestCaseData("D64_80D").SetName("Os9_RealNitros9Dragon64_TwoSectorClusters_500RandomCalls");
            yield return new TestCaseData("UUCPBB").SetName("Os9_RealUucpPackage_FiveMapSectors_500RandomCalls");
            yield return new TestCaseData("HD40000").SetName("Os9_ToolshedHardDisk40000_TwentyMapSectors_500RandomCalls");
            yield return new TestCaseData("HD20000C4").SetName("Os9_ToolshedHardDisk20000_FourSectorClusters_500RandomCalls");
            yield return new TestCaseData("CoCo1L1_DW").SetName("Os9_RealNitros9DriveWire_ShortFile_500RandomCalls");
        }

        /// <summary>
        /// The same disks for the long run.
        /// </summary>
        /// <returns>
        /// One case per disk.
        /// </returns>
        public static IEnumerable<TestCaseData> LongVariants()
        {
            yield return new TestCaseData("CoCo35SS").SetName("Os9_CoCo35TrackOneSide_5000RandomCalls");
            yield return new TestCaseData("CoCo40SS").SetName("Os9_CoCo40TrackOneSide_5000RandomCalls");
            yield return new TestCaseData("Dragon40DS").SetName("Os9_Dragon40TrackTwoSides_5000RandomCalls");
            yield return new TestCaseData("CoCo80DS").SetName("Os9_CoCo80TrackTwoSides_5000RandomCalls");
            yield return new TestCaseData("NITR09").SetName("Os9_RealNitros9Disk_5000RandomCalls");
            yield return new TestCaseData("CoCo1L1_80D").SetName("Os9_RealNitros9CoCoLevel1_80Track_5000RandomCalls");
            yield return new TestCaseData("D64_80D").SetName("Os9_RealNitros9Dragon64_TwoSectorClusters_5000RandomCalls");
            yield return new TestCaseData("UUCPBB").SetName("Os9_RealUucpPackage_FiveMapSectors_5000RandomCalls");
            yield return new TestCaseData("HD40000").SetName("Os9_ToolshedHardDisk40000_TwentyMapSectors_5000RandomCalls");
            yield return new TestCaseData("HD20000C4").SetName("Os9_ToolshedHardDisk20000_FourSectorClusters_5000RandomCalls");
            yield return new TestCaseData("CoCo1L1_DW").SetName("Os9_RealNitros9DriveWire_ShortFile_5000RandomCalls");
        }

        [TestCaseSource(nameof(Variants))]
        public void RandomCalls_EveryCheckHoldsAfterEveryCall(string variant)
        {
            Os9StressRun run = new Os9StressRun(variant, 20261021, 5, 100, false);
            run.Execute();
        }

        [TestCaseSource(nameof(LongVariants))]
        [Explicit("Long run, minutes: start it by hand")]
        [Category("Stress")]
        public void RandomCalls_Long_EveryCheckHoldsAfterEveryCall(string variant)
        {
            Os9StressRun run = new Os9StressRun(variant, 4711, 50, 100, true);
            run.Execute();
        }
    }

    /// <summary>
    /// One seeded stress run on an OS-9 disk image in a temporary file.
    /// </summary>
    internal sealed class Os9StressRun
    {
        private const int SectorSize = 256;
        private const int SectorsPerTrack = 18;
        private const int MaxDirectories = 12;
        private const int MaxDepth = 3;
        private static readonly DateTime FixedTime = new DateTime(2026, 10, 21, 12, 34, 0);

        private const int KindCreate = 0;
        private const int KindOverwrite = 1;
        private const int KindRead = 2;
        private const int KindDelete = 3;
        private const int KindRename = 4;
        private const int KindMove = 5;
        private const int KindMakeDir = 6;
        private const int KindRemoveDir = 7;
        private const int KindRefused = 8;
        private static readonly string[] KindNames = { "create", "overwrite", "read", "delete", "rename", "move", "mkdir", "rmdir", "refused" };

        // One node of the model: a file or a directory.
        private sealed class Node
        {
            public string Name = string.Empty;
            public bool IsDirectory;
            public Node? Parent;
            public byte[] Data = Array.Empty<byte>();
            public int DataSectors;
            public List<Node?> Slots = new List<Node?>();
            public int InitialSlack;
            public int CapacitySectors;
            public uint Fd;
            public int LinkCount = 1;

            // A file from the disk with a segment that crosses an allocation map sector: RBF cannot release it, so every delete or overwrite
            // of it must be refused.
            public bool Pinned;
        }

        // Stand-ins in a directory's slot list for the entries named one dot (the directory itself) and two dots (its parent).
        private static readonly Node Self = new Node { Name = "." };
        private static readonly Node Up = new Node { Name = ".." };

        private readonly string _variant;
        private readonly int _seed;
        private readonly int _rounds;
        private readonly int _callsPerRound;
        private readonly bool _toolshedEveryRound;
        private readonly Random _random;
        private readonly StressTimings _timings;
        private readonly long[] _memory;

        // What StressMemory.WaitForTestHostWarmUp said before the first measurement, for the report.
        private string _hostWarmUp = string.Empty;
        private readonly StringBuilder _notes = new StringBuilder();

        private string _folder = string.Empty;
        private string _imagePath = string.Empty;
        private BlockDeviceDescriptor? _descriptor;
        private GenericBlockDevice? _device;
        private FileStream? _file;

        // Size of one page of the image in BlockDeviceBase, which holds and counts the image in whole pages (see HeldBytes).
        private const int DevicePageSize = 4096;

        // Bytes of the image the block device holds in memory at the end of each round (BlockDeviceBase.HeldBytes).
        private long[] _held = Array.Empty<long>();
        private CountingBlockDevice? _counter;
        private Os9RbfFileSystem? _fs;
        private Node _root = new Node();
        private byte[] _initial = Array.Empty<byte>();
        private byte[] _snapshot = Array.Empty<byte>();
        private byte[] _before = Array.Empty<byte>();
        private int _total;
        private int _mapBytes;
        private int _mapSectors;
        private int _sas;

        // Sectors per allocation map bit (a cluster), its log2, the number of clusters, and the whole map sectors (DD.MAP high byte).
        private int _bit = 1;
        private int _shift;
        private int _clusters;
        private int _mapHigh;

        // Reserved sectors (as they were counted before clusters), and reserved clusters: marked used on the disk before the run but holding
        // no sector of a file or directory.
        private bool[] _reserved = Array.Empty<bool>();
        private int _reservedCount;
        private bool[] _reservedCluster = Array.Empty<bool>();
        private int _reservedClusters;
        private long _toolshedUnreferenced = -1;
        private int _pinnedRefusals;
        private int _maybeCalls;
        private int _emptyFree;
        private int _free;
        private int _call;
        private string _callText = string.Empty;
        private int _round;

        private int _creates;
        private int _overwrites;
        private int _reads;
        private int _deletes;
        private int _renames;
        private int _moves;
        private int _makeDirs;
        private int _removeDirs;
        private int _refusedFull;
        private int _refusedSegments;
        private int _refusedOther;
        private int _fills;
        private int _toolshedRuns;

        public Os9StressRun(string variant, int seed, int rounds, int callsPerRound, bool toolshedEveryRound)
        {
            _variant = variant;
            _seed = seed;
            _rounds = rounds;
            _callsPerRound = callsPerRound;
            _toolshedEveryRound = toolshedEveryRound;
            _random = new Random(seed);
            _timings = new StressTimings(KindNames, rounds * (callsPerRound + 400), rounds);
            _memory = new long[rounds];
            _held = new long[rounds];

            // The probe data, made once so the probes allocate nothing. At most about 110 sectors are in use at once during a probe pass,
            // which fits beside the fullest ballast (70 % of the free space) on the smallest disk (619 free sectors: 433 + 110 = 543).
            int[] sizes = { 0, 1, 256, 1000, 5000, 8000 };
            _probeData = new byte[sizes.Length][];
            _probeOther = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
            {
                _probeData[i] = new byte[sizes[i]];
                _probeOther[i] = new byte[sizes[sizes.Length - 1 - i]];
                _random.NextBytes(_probeData[i]);
                _random.NextBytes(_probeOther[i]);
            }
        }

        private readonly byte[][] _probeData;
        private readonly byte[][] _probeOther;
        private byte[][] _ballast = Array.Empty<byte[]>();
        private bool _probing;
        private long _probeTicks;

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

        // The fixed probe: the same 32 calls with the same sizes every time, all in the root directory (create six files, read them, overwrite
        // them with other sizes, rename them, delete them, make and remove a directory). It is run three times at each fill level after every
        // round; the fastest pass counts.
        private void RunProbes()
        {
            if (_ballast.Length == 0)
            {
                _ballast = new byte[StressTimings.ProbePercent.Length][];
                for (int level = 0; level < _ballast.Length; level++)
                {
                    _ballast[level] = new byte[_emptyFree * StressTimings.ProbePercent[level] / 100 * SectorSize];
                }
            }

            for (int level = 0; level < StressTimings.ProbePercent.Length; level++)
            {
                if (_ballast[level].Length > 0)
                {
                    Node? ballast = Find(_root, "ballast");
                    if (ballast == null)
                        Create(_root, "ballast", _ballast[level]);
                    else
                        Overwrite(ballast, _ballast[level]);
                }

                for (int pass = 0; pass < 3; pass++)
                {
                    _probing = true;
                    _probeTicks = 0;
                    long blocks = _counter!.Reads + _counter.Writes;
                    for (int i = 0; i < _probeData.Length; i++)
                    {
                        Create(_root, "probe" + i, _probeData[i]);
                    }

                    for (int i = 0; i < _probeData.Length; i++)
                    {
                        Read(Find(_root, "probe" + i)!);
                    }

                    for (int i = 0; i < _probeData.Length; i++)
                    {
                        Overwrite(Find(_root, "probe" + i)!, _probeOther[i]);
                    }

                    for (int i = 0; i < _probeData.Length; i++)
                    {
                        RenameTo(Find(_root, "probe" + i)!, "probeq" + i);
                    }

                    for (int i = 0; i < _probeData.Length; i++)
                    {
                        Delete(Find(_root, "probeq" + i)!);
                    }

                    MakeDirectoryAt(_root, "PD");
                    RemoveDirectoryNode(Find(_root, "PD")!);
                    _probing = false;
                    _timings.AddProbe(_round, level, _probeTicks, _counter.Reads + _counter.Writes - blocks);
                }
            }

            Node? last = Find(_root, "ballast");
            if (last != null)
            {
                Delete(last);
            }
        }

        private string Where()
        {
            return _variant + ", seed " + _seed + ", call " + _call + " (" + _callText + ")";
        }

        public void Execute()
        {
            _folder = Path.Combine(Path.GetTempPath(), "os9stress-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            _imagePath = Path.Combine(_folder, "stress.dsk");
            try
            {
                PrepareImage();
                Mount();
                _total = (int)_descriptor!.TotalBlocks;
                _snapshot = new byte[_total * SectorSize];
                _before = new byte[_total * SectorSize];
                _initial = (byte[])Snapshot().Clone();
                LoadModel();
                Verify();
                ToolshedCheck("before the run");
                RunRounds();
            }
            finally
            {
                _device?.Dispose();
                _device = null;
                _file?.Dispose();
                _file = null;
                if (Directory.Exists(_folder))
                {
                    Directory.Delete(_folder, true);
                }
            }
        }

        // ---- the disks ----

        private void PrepareImage()
        {
            if (_variant == "NITR09")
            {
                string? fromEnvironment = Environment.GetEnvironmentVariable("RETROIO_DRAGON_TEST_IMAGES");
                string root = !string.IsNullOrEmpty(fromEnvironment)
                    ? fromEnvironment
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "RetroFS", "test-images");
                string source = Path.Combine(root, "dsk", "NITR09.DSK");
                if (!File.Exists(source))
                {
                    Assert.Ignore("Test image not found: NITR09.DSK");
                }

                File.Copy(source, _imagePath);
                _descriptor = BlockDeviceDescriptor.DragonVDK_DS40;
                return;
            }

            string? realName = RealImageName(_variant);
            if (realName != null)
            {
                string? fromEnvironment = Environment.GetEnvironmentVariable("RETROIO_DRAGON_TEST_IMAGES");
                string root = !string.IsNullOrEmpty(fromEnvironment)
                    ? fromEnvironment
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "RetroFS", "test-images");
                string source = Path.Combine(root, "os9", realName);
                if (!File.Exists(source))
                {
                    Assert.Ignore("Test image not found: os9/" + realName);
                }

                // The copy is used as it is. A DriveWire file is shorter than its volume (sector 0 says 524268 sectors): the device is
                // the whole volume and the sectors behind the file read as zero (see Mount), and the file grows as sectors are written.
                File.Copy(source, _imagePath);
                long sectors = Math.Max(new FileInfo(_imagePath).Length / SectorSize, VolumeSectorsOf(_imagePath));
                _descriptor = new BlockDeviceDescriptor
                {
                    Name = "OS-9 " + _variant,
                    BlockSize = SectorSize,
                    AddressMode = BlockAddressMode.LBA,
                    Cylinders = (int)sectors,
                    Heads = 1,
                    UniformSectorsPerTrack = 1,
                    TotalBlocks = (uint)sectors,
                    ExpectedImageSize = sectors * SectorSize,
                    SupportsVariableGeometry = false
                };
                return;
            }

            int cylinders;
            int heads;
            bool dragon = false;
            switch (_variant)
            {
                case "CoCo35SS":
                    cylinders = 35;
                    heads = 1;
                    break;
                case "CoCo40SS":
                    cylinders = 40;
                    heads = 1;
                    break;
                case "Dragon40DS":
                    cylinders = 40;
                    heads = 2;
                    dragon = true;
                    break;
                case "CoCo80DS":
                    cylinders = 80;
                    heads = 2;
                    break;
                default:
                    throw new ArgumentException("Unknown variant " + _variant);
            }

            byte[] image = BuildEmptyImage(cylinders, heads, dragon);
            File.WriteAllBytes(_imagePath, image);
            _descriptor = new BlockDeviceDescriptor
            {
                Name = "OS-9 " + _variant,
                BlockSize = SectorSize,
                AddressMode = BlockAddressMode.LBA,
                Cylinders = cylinders,
                Heads = heads,
                UniformSectorsPerTrack = SectorsPerTrack,
                TotalBlocks = (uint)(cylinders * heads * SectorsPerTrack),
                ExpectedImageSize = cylinders * heads * SectorsPerTrack * SectorSize,
                SupportsVariableGeometry = false
            };
        }

        // The file in the os9 test-image folder for a variant that runs on a real disk, or null for the other variants.
        private static string? RealImageName(string variant)
        {
            switch (variant)
            {
                case "CoCo1L1_80D":
                    return "NOS9_6809_L1_v030300_coco1_80d.dsk";
                case "D64_80D":
                    return "NOS9_6809_L1_v030300_d64_80d.dsk";
                case "UUCPBB":
                    return "uucpbb21_6809.dsk";
                case "HD40000":
                    return "toolshed_hd_40000.dsk";
                case "HD20000C4":
                    return "toolshed_hd_20000_c4.dsk";
                case "CoCo1L1_DW":
                    return "NOS9_6809_L1_v030300_coco1_dw.dsk";
                default:
                    return null;
            }
        }

        // DD.TOT, the sectors of the volume, from sector 0 of an image file (3 bytes at offset 0, NitrOS-9 defs/rbf.d).
        private static long VolumeSectorsOf(string path)
        {
            byte[] id = new byte[SectorSize];
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.ReadExactly(id, 0, SectorSize);
            }

            return U24(id, 0);
        }

        // An empty OS-9 disk laid out the way Toolshed 2.2 "os9 format -tN [-ss|-ds] [-dr] -e" lays it out. Every value below was read from
        // images that program made on 2026-10-21 (35 tracks one side, 40 one side, 40 two sides, 40 two sides with -dr, 80 two sides):
        //  - sector 0 is the identification sector;
        //  - the allocation map starts at sector 1 and takes as many sectors as it needs (two on the 80 track disk);
        //  - CoCo disks put the root directory descriptor right after the map; Dragon disks (-dr) keep the whole first track (sectors 0 to 17)
        //    and put it at sector 18;
        //  - the root directory gets 8 sectors (the segment allocation size) right after its descriptor;
        //  - map bits past the last sector, and the map bytes past the end of the map in its last sector, are set.
        private static byte[] BuildEmptyImage(int cylinders, int heads, bool dragon)
        {
            int total = cylinders * heads * SectorsPerTrack;
            int mapBytes = (total + 7) / 8;
            int mapSectors = (mapBytes + SectorSize - 1) / SectorSize;
            int rootFd = dragon ? SectorsPerTrack : 1 + mapSectors;
            const int rootSectors = 8;
            byte[] image = new byte[total * SectorSize];

            // Sector 0. The fields the file system reads are named; the others hold what Toolshed writes.
            Put24(image, 0, total);                      // total sectors
            image[3] = SectorsPerTrack;                  // sectors per track
            Put16(image, 4, mapBytes);                   // bytes in the allocation map
            Put16(image, 6, 1);                          // sectors per map bit
            Put24(image, 8, rootFd);                     // root directory descriptor
            image[13] = 0xFF;                            // as Toolshed writes it
            Put16(image, 14, 0x0180);                    // as Toolshed writes it
            image[16] = (byte)(heads == 2 ? 3 : 2);      // disk format: bit 0 set means two sides (Toolshed also sets bit 1)
            Put16(image, 17, SectorsPerTrack);           // as Toolshed writes it
            image[26] = 126;                             // creation date: 2026-10-21 11:05 (year since 1900, month, day, hour, minute)
            image[27] = 10;
            image[28] = 21;
            image[29] = 11;
            image[30] = 5;
            PutName(image, 31, "Stress Disk");           // volume name, bit 7 set on the last character

            // The option area from byte 63, as Toolshed writes it. Byte 77 (63 + 14) is the segment allocation size the file system reads.
            image[63] = 1;
            image[64] = 1;
            image[66] = 0x20;
            image[67] = 1;
            Put16(image, 68, cylinders);
            image[70] = (byte)heads;
            Put16(image, 72, SectorsPerTrack);
            Put16(image, 74, SectorsPerTrack);
            image[76] = 3;
            image[77] = rootSectors;
            image[104] = 1;

            // Allocation map: everything up to the end of the root directory is used.
            int used = rootFd + 1 + rootSectors;
            for (int bit = 0; bit < used; bit++)
            {
                image[SectorSize + (bit >> 3)] |= (byte)(0x80 >> (bit & 7));
            }

            for (int bit = total; bit < mapBytes * 8; bit++)
            {
                image[SectorSize + (bit >> 3)] |= (byte)(0x80 >> (bit & 7));
            }

            for (int i = mapBytes; i < mapSectors * SectorSize; i++)
            {
                image[SectorSize + i] = 0xFF;
            }

            // Root directory descriptor: directory with all permissions, link count 1, 64 bytes (the two dot entries), one segment of 8 sectors.
            int fd = rootFd * SectorSize;
            image[fd] = 0xBF;
            image[fd + 3] = 126;
            image[fd + 4] = 10;
            image[fd + 5] = 21;
            image[fd + 6] = 11;
            image[fd + 7] = 5;
            image[fd + 8] = 1;
            Put24(image, fd + 9 + 1, 64);
            image[fd + 13] = 126;
            image[fd + 14] = 10;
            image[fd + 15] = 21;
            Put24(image, fd + 16, rootFd + 1);
            Put16(image, fd + 19, rootSectors);

            // First root directory sector: ".." and "." both point at the root descriptor.
            int dir = (rootFd + 1) * SectorSize;
            image[dir] = (byte)'.';
            image[dir + 1] = (byte)('.' | 0x80);
            Put24(image, dir + 29, rootFd);
            image[dir + 32] = (byte)('.' | 0x80);
            Put24(image, dir + 32 + 29, rootFd);
            return image;
        }

        private static void Put16(byte[] data, int offset, int value)
        {
            data[offset] = (byte)(value >> 8);
            data[offset + 1] = (byte)value;
        }

        private static void Put24(byte[] data, int offset, int value)
        {
            data[offset] = (byte)(value >> 16);
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)value;
        }

        private static void PutName(byte[] data, int offset, string name)
        {
            for (int i = 0; i < name.Length; i++)
            {
                data[offset + i] = (byte)name[i];
            }

            data[offset + name.Length - 1] |= 0x80;
        }

        private void Mount()
        {
            FileStream stream = new FileStream(_imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            _file = stream;
            long volumeBytes = (long)_descriptor!.TotalBlocks * SectorSize;
            Stream data = stream.Length < volumeBytes
                ? new PaddedSubStream(stream, 0, stream.Length, volumeBytes)
                : stream;
            _device = new GenericBlockDevice(_descriptor!);
            _device.Mount(data, false);
            _counter = new CountingBlockDevice(_device);
            _fs = new Os9RbfFileSystem();
            Assert.That(_fs.CanHandle(_counter), Is.True, "Os9RbfFileSystem takes the image, " + _variant);
            _fs.Initialize(_counter);
            _fs.Clock = () => FixedTime;
        }

        private void Remount()
        {
            _device!.Dispose();
            _device = null;

            // A PaddedSubStream does not close the file under it, so the file is closed here.
            _file?.Dispose();
            _file = null;
            _fs = null;
            Assert.That(StressMemory.CanOpenExclusively(_imagePath, out string reason), Is.True,
                "the image file is still open after the device was disposed: " + reason + ", " + Where());
            Mount();
        }

        private byte[] Snapshot()
        {
            for (uint lba = 0; lba < _total; lba++)
            {
                _device!.ReadBlock(BlockAddress.FromLBA(lba), _snapshot.AsSpan((int)lba * SectorSize, SectorSize));
            }

            return _snapshot;
        }

        // ---- raw reading, written for the test and not shared with the code under test ----

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

        // A bit of the allocation map: bit n is cluster n, which holds sectors n times the sectors per bit and up.
        private static bool ClusterBit(byte[] image, int cluster)
        {
            return (image[SectorSize + (cluster >> 3)] & (0x80 >> (cluster & 7))) != 0;
        }

        // Rounds a number of sectors up to whole clusters.
        private int RoundUp(int sectors)
        {
            return ((sectors + _bit - 1) >> _shift) << _shift;
        }

        // The sectors a file of this length holds in its segments after Close: its data and the descriptor rounded up to whole clusters,
        // less the descriptor (Create puts the rest of the descriptor's cluster into the first segment; L0EFE keeps whole clusters).
        private int FileSectors(int length)
        {
            return RoundUp(1 + SectorsFor(length)) - 1;
        }

        // The clusters a file of this length takes besides its descriptor's cluster.
        private int DataClusters(int length)
        {
            return (RoundUp(1 + SectorsFor(length)) >> _shift) - 1;
        }

        // The bytes of a file whose descriptor starts at fdOffset, cut at the size, and the number of sectors its segments hold.
        private byte[] RawFile(byte[] image, int fdOffset, out int sectors)
        {
            long size = U32(image, fdOffset + 9);
            sectors = 0;
            for (int i = 0; i < 48; i++)
            {
                int count = U16(image, fdOffset + 16 + (i * 5) + 3);
                if (count == 0)
                {
                    break;
                }

                sectors += count;
            }

            byte[] result = new byte[Math.Min(size, (long)sectors * SectorSize)];
            long done = 0;
            for (int i = 0; i < 48 && done < result.Length; i++)
            {
                int at = fdOffset + 16 + (i * 5);
                int count = U16(image, at + 3);
                if (count == 0)
                {
                    break;
                }

                long take = Math.Min((long)count * SectorSize, result.Length - done);
                Buffer.BlockCopy(image, (int)U24(image, at) * SectorSize, result, (int)done, (int)take);
                done += take;
            }

            return result;
        }

        // Reads the tree on the disk into the model before the run: the empty root of a built disk, or the whole real disk.
        private void LoadModel()
        {
            byte[] image = _snapshot;
            Assert.That((int)U24(image, 0), Is.EqualTo(_total), "total sectors in sector 0 against the device");
            _mapBytes = U16(image, 4);
            _mapSectors = (_mapBytes + SectorSize - 1) / SectorSize;
            _mapHigh = (_mapBytes >> 8) & 0xFF;
            _sas = image[77];
            _bit = U16(image, 6);
            Assert.That(_bit & (_bit - 1), Is.EqualTo(0), "sectors per map bit is a power of two");
            _shift = 0;
            while ((1 << _shift) < _bit)
            {
                _shift++;
            }

            _clusters = (_total + _bit - 1) >> _shift;
            Assert.That(_sas, Is.GreaterThan(0));
            uint rootFd = U24(image, 8);
            _root = new Node { Name = "/", IsDirectory = true, Fd = rootFd };
            _root.Parent = _root;
            LoadDirectory(image, _root, rootFd);

            // Reserved clusters: marked used on the disk before the run but holding no sector of a file or directory.
            int[] owner = Walk(image, false);
            _reserved = new bool[_total];
            _reservedCount = 0;
            _reservedCluster = new bool[_clusters];
            _reservedClusters = 0;
            for (int c = 0; c < _clusters; c++)
            {
                if (!ClusterBit(image, c) || ClusterOwned(owner, c))
                {
                    continue;
                }

                _reservedCluster[c] = true;
                _reservedClusters++;
                for (int s = c << _shift; s < Math.Min(_total, (c + 1) << _shift); s++)
                {
                    _reserved[s] = true;
                    _reservedCount++;
                }
            }

            if (_variant == "Dragon40DS" || _variant == "CoCo35SS" || _variant == "CoCo40SS" || _variant == "CoCo80DS")
            {
                // A built disk reserves sector 0 and the map sectors; a Dragon disk the whole first track.
                int expectedReserved = _variant == "Dragon40DS" ? SectorsPerTrack : 1 + _mapSectors;
                Assert.That(_reservedCount, Is.EqualTo(expectedReserved), "reserved sectors of the empty disk");
                for (int s = 0; s < expectedReserved; s++)
                {
                    Assert.That(_reserved[s], Is.True, "sector " + s + " is reserved");
                }
            }

            _rootStartSectors = _root.CapacitySectors;
            _emptyFree = EmptyFree();
        }

        // True when a sector of the cluster belongs to a file or directory.
        private bool ClusterOwned(int[] owner, int cluster)
        {
            for (int s = cluster << _shift; s < Math.Min(_total, (cluster + 1) << _shift); s++)
            {
                if (owner[s] >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        // The free space once everything that can be deleted is deleted: what the root directory, the files that cannot be released and the
        // directories that hold them take stays.
        private int EmptyFree()
        {
            return ((_clusters - _reservedClusters) << _shift) - Staying(_root);
        }

        // The sectors a directory takes, its descriptor included, and what stays below it.
        private static int Staying(Node directory)
        {
            int sectors = 1 + directory.CapacitySectors;
            for (int i = 0; i < directory.Slots.Count; i++)
            {
                Node? slot = directory.Slots[i];
                if (!IsChild(slot))
                {
                    continue;
                }

                if (slot!.IsDirectory)
                {
                    if (HoldsPinned(slot))
                    {
                        sectors += Staying(slot);
                    }
                }
                else if (slot.Pinned)
                {
                    sectors += 1 + slot.DataSectors;
                }
            }

            return sectors;
        }

        private static bool HoldsPinned(Node directory)
        {
            for (int i = 0; i < directory.Slots.Count; i++)
            {
                Node? slot = directory.Slots[i];
                if (!IsChild(slot))
                {
                    continue;
                }

                if (slot!.IsDirectory ? HoldsPinned(slot) : slot.Pinned)
                {
                    return true;
                }
            }

            return false;
        }

        // True when a segment of the descriptor runs from one allocation map sector (2048 clusters) into the next.
        private bool CrossesMapSector(byte[] image, int fdOffset)
        {
            for (int i = 0; i < 48; i++)
            {
                int at = fdOffset + 16 + (i * 5);
                int count = U16(image, at + 3);
                if (count == 0)
                {
                    return false;
                }

                long first = U24(image, at) >> _shift;
                long last = ((long)U24(image, at) + count - 1) >> _shift;
                if (first / 2048 != last / 2048)
                {
                    return true;
                }
            }

            return false;
        }

        private void LoadDirectory(byte[] image, Node directory, uint fd)
        {
            int fdOffset = (int)fd * SectorSize;
            byte[] data = RawFile(image, fdOffset, out int sectors);
            directory.CapacitySectors = sectors;
            directory.InitialSlack = (sectors * SectorSize) - data.Length;
            directory.LinkCount = image[fdOffset + 8];
            for (int o = 0; o + 32 <= data.Length; o += 32)
            {
                if (data[o] == 0)
                {
                    directory.Slots.Add(null);
                    continue;
                }

                string name = EntryName(data, o);
                if (name == ".")
                {
                    directory.Slots.Add(Self);
                    continue;
                }

                if (name == "..")
                {
                    directory.Slots.Add(Up);
                    continue;
                }

                uint childFd = U24(data, o + 29);
                int childOffset = (int)childFd * SectorSize;
                Node child = new Node { Name = name, Parent = directory, Fd = childFd, LinkCount = image[childOffset + 8] };
                child.IsDirectory = (image[childOffset] & 0x80) != 0;
                directory.Slots.Add(child);
                if (child.IsDirectory)
                {
                    LoadDirectory(image, child, childFd);
                }
                else
                {
                    child.Data = RawFile(image, childOffset, out int dataSectors);
                    child.DataSectors = dataSectors;
                    child.Pinned = CrossesMapSector(image, childOffset);
                }
            }
        }

        // ---- model helpers ----

        private static string PathOf(Node node)
        {
            if (node.Parent == node)
            {
                return "/";
            }

            string parent = PathOf(node.Parent!);
            return parent == "/" ? "/" + node.Name : parent + "/" + node.Name;
        }

        private static int Depth(Node node)
        {
            int depth = 0;
            while (node.Parent != node)
            {
                node = node.Parent!;
                depth++;
            }

            return depth;
        }

        private static bool IsChild(Node? slot)
        {
            return slot != null && slot != Self && slot != Up;
        }

        private static Node? Find(Node directory, string name)
        {
            for (int i = 0; i < directory.Slots.Count; i++)
            {
                Node? slot = directory.Slots[i];
                if (IsChild(slot) && string.Equals(slot!.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return slot;
                }
            }

            return null;
        }

        private static void Collect(Node directory, List<Node> files, List<Node> directories)
        {
            directories.Add(directory);
            for (int i = 0; i < directory.Slots.Count; i++)
            {
                Node? slot = directory.Slots[i];
                if (!IsChild(slot))
                {
                    continue;
                }

                if (slot!.IsDirectory)
                    Collect(slot, files, directories);
                else
                    files.Add(slot);
            }
        }

        private Node? RandomFile()
        {
            List<Node> files = new List<Node>();
            Collect(_root, files, new List<Node>());
            return files.Count == 0 ? null : files[_random.Next(files.Count)];
        }

        private Node RandomDirectory()
        {
            List<Node> directories = new List<Node>();
            Collect(_root, new List<Node>(), directories);
            return directories[_random.Next(directories.Count)];
        }

        private int DirectoryCount()
        {
            List<Node> directories = new List<Node>();
            Collect(_root, new List<Node>(), directories);
            return directories.Count;
        }

        private static bool IsInside(Node node, Node directory)
        {
            Node current = directory;
            while (true)
            {
                if (current == node)
                {
                    return true;
                }

                if (current.Parent == current)
                {
                    return false;
                }

                current = current.Parent!;
            }
        }

        // True when adding an entry to the directory makes it take more sectors: no free slot and the next 32 bytes do not fit.
        private static bool NeedsGrowth(Node directory)
        {
            for (int i = 0; i < directory.Slots.Count; i++)
            {
                if (directory.Slots[i] == null)
                {
                    return false;
                }
            }

            return (directory.Slots.Count + 1) * 32 > directory.CapacitySectors * SectorSize;
        }

        private static void AddEntry(Node directory, Node child)
        {
            child.Parent = directory;
            for (int i = 0; i < directory.Slots.Count; i++)
            {
                if (directory.Slots[i] == null)
                {
                    directory.Slots[i] = child;
                    return;
                }
            }

            directory.Slots.Add(child);
        }

        private static void RemoveEntry(Node child)
        {
            Node directory = child.Parent!;
            for (int i = 0; i < directory.Slots.Count; i++)
            {
                if (directory.Slots[i] == child)
                {
                    directory.Slots[i] = null;
                    return;
                }
            }

            throw new InvalidOperationException("model: " + child.Name + " is not in its parent");
        }

        // The clusters a directory gets when it must grow right after a new descriptor took its cluster (rbf.asm Create: FatScan for the
        // descriptor, then the directory's Writ599 and L0C6F). FatScan for the allocation size wants (DD.BIT - 1 + SAS) >> shift clusters and
        // gives that many when some free run in one map sector is that long, otherwise the longest such run. The descriptor takes the first
        // free cluster from the map sector FatScan starts at (V.MapSct), which is always the first one when the map has fewer than two whole
        // sectors; otherwise every start is tried. least and most are the smallest and largest growth over those starts.
        private void GrowthAfterDescriptor(byte[] image, out int least, out int most)
        {
            int want = (_bit - 1 + _sas) >> _shift;
            int starts = _mapHigh <= 1 ? 1 : _mapHigh;
            least = int.MaxValue;
            most = 0;
            for (int start = 0; start < starts; start++)
            {
                int descriptor = FirstFree(image, start);
                int growth = Math.Min(want, LongestRun(image, descriptor));
                least = Math.Min(least, growth);
                most = Math.Max(most, growth);
            }
        }

        private int MapBitsIn(int mapSector)
        {
            return Math.Min(2048, (_mapBytes * 8) - (mapSector * 2048));
        }

        // The first free cluster, searching the map sectors from this one on and round to the start.
        private int FirstFree(byte[] image, int start)
        {
            for (int k = 0; k < _mapSectors; k++)
            {
                int sector = (start + k) % _mapSectors;
                int first = sector * 2048;
                int bits = MapBitsIn(sector);
                for (int c = first; c < first + bits && c < _clusters; c++)
                {
                    if (!ClusterBit(image, c))
                    {
                        return c;
                    }
                }
            }

            return -1;
        }

        // The longest run of free clusters inside one map sector, with the cluster taken counted as used.
        private int LongestRun(byte[] image, int taken)
        {
            int longest = 0;
            for (int sector = 0; sector < _mapSectors; sector++)
            {
                int first = sector * 2048;
                int bits = MapBitsIn(sector);
                int run = 0;
                for (int c = first; c < first + bits && c < _clusters; c++)
                {
                    if (c == taken || ClusterBit(image, c))
                    {
                        run = 0;
                        continue;
                    }

                    run++;
                    longest = Math.Max(longest, run);
                }
            }

            return longest;
        }

        // A path as it is passed in: each name in random case (OS-9 compares names without case), sometimes with a trailing slash.
        private string Spelled(string path)
        {
            StringBuilder spelled = new StringBuilder(path.Length + 1);
            int how = _random.Next(3);
            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];
                if (how == 1)
                    c = char.ToUpperInvariant(c);
                else if (how == 2)
                    c = char.ToLowerInvariant(c);
                spelled.Append(c);
            }

            return spelled.ToString();
        }

        // The path of a new name in a directory: the directory part in random case, the new name exactly as it is to be stored (the code
        // stores the case it is given).
        private string SpelledChild(Node directory, string name)
        {
            string parent = PathOf(directory);
            return parent == "/" ? "/" + name : Spelled(parent) + "/" + name;
        }

        // Names: short ones that repeat (so they are taken again after a delete and renames run into them), and some with the other legal
        // characters. No 29-character name: Toolshed os9 dcheck (2.2 and the current source) ends the name in place by writing a zero byte
        // after it, which for a 29-character name is the top byte of the entry's descriptor sector, so on a volume over 65536 sectors it
        // reads the wrong descriptor and reports errors the image does not have.
        private string RandomName(bool directory)
        {
            int r = _random.Next(19);
            if (r == 0)
            {
                return "x-" + _random.Next(10) + ".y_$";
            }

            return (directory ? "D" : "f") + _random.Next(directory ? 12 : 60);
        }

        private byte[] RandomData(int length)
        {
            byte[] data = new byte[length];
            _random.NextBytes(data);
            return data;
        }

        // Sizes: empty, one byte, around one and two sectors, small and medium, and around all the space there is.
        private int RandomSize(int availableSectors)
        {
            int available = Math.Max(0, availableSectors);
            int r = _random.Next(100);
            if (r < 6)
                return 0;
            if (r < 12)
                return 1;
            if (r < 20)
                return SectorSize - 1 + _random.Next(3);
            if (r < 28)
                return (2 * SectorSize) - 1 + _random.Next(3);
            if (r < 50)
                return _random.Next(4096);
            if (r < 65)
                return _random.Next(16384);
            if (r < 78)
                return available > 1 ? _random.Next(available * SectorSize / 2) : 1;
            if (r < 85)
                return available * SectorSize;
            if (r < 92)
                return Math.Max(0, (available * SectorSize) - _random.Next(SectorSize));
            return (available * SectorSize) + 1;
        }

        private static int SectorsFor(int length)
        {
            return (length + SectorSize - 1) / SectorSize;
        }

        // ---- the run ----

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
                    ToolshedCheck("after round " + _round);
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
                    ToolshedCheck("after everything was deleted in round " + _round);
                }

                if (first)
                {
                    // The test host's one-time step must be behind us before the first measurement (see WaitForTestHostWarmUp).
                    _hostWarmUp = StressMemory.WaitForTestHostWarmUp();
                }

                _held[_round] = _device!.HeldBytes;
                _memory[_round] = StressMemory.Retained();
            }

            _device!.Dispose();
            _device = null;

            // A PaddedSubStream does not close the file under it, so the file is closed here (as in Remount).
            _file?.Dispose();
            _file = null;
            _fs = null;
            Assert.That(StressMemory.CanOpenExclusively(_imagePath, out string reason), Is.True, "the image file is still open at the end: " + reason);

            Report();
        }

        private void RandomCall()
        {
            int r = _random.Next(100);
            if (r < 28)
                WriteSomewhere();
            else if (r < 40)
                OverwriteRandom();
            else if (r < 50)
                ReadRandom();
            else if (r < 64)
                DeleteRandom();
            else if (r < 72)
                RenameRandom();
            else if (r < 78)
                MoveRandom();
            else if (r < 86)
                MakeDirectory();
            else if (r < 94)
                RemoveDirectory();
            else
                RefusedCall();
        }

        private void Begin(string text)
        {
            _call++;
            _callText = text;
        }

        private int Bucket()
        {
            return StressTimings.BucketOf(_emptyFree - _free, _emptyFree);
        }

        private void TakeBefore()
        {
            Buffer.BlockCopy(Snapshot(), 0, _before, 0, _before.Length);
        }

        private void RequireUnchanged(string what)
        {
            if (!Snapshot().AsSpan().SequenceEqual(_before))
            {
                Assert.Fail("a refused " + what + " changed the disk, " + Where());
            }
        }

        // Counts runs of free clusters in the allocation map, with a break at every map sector of 2048 clusters (a segment never crosses one).
        private int FreeRuns()
        {
            byte[] image = Snapshot();
            int runs = 0;
            bool inRun = false;
            for (int c = 0; c < _clusters; c++)
            {
                if (c % 2048 == 0)
                {
                    inRun = false;
                }

                bool free = !ClusterBit(image, c);
                if (free && !inRun)
                {
                    runs++;
                }

                inRun = free;
            }

            return runs;
        }

        // Runs one call that changes the disk. When it is expected to work it must work; when the model says there is not enough space it must
        // throw an IOException that says the disk is full and change nothing; when the model cannot tell (maybe), either is accepted, a refusal
        // only as "full" and unchanged. A refusal because the segment list is full (RBF grows a file to 47 segments) is accepted only when the
        // free space, with the pieces the call releases first (oldSegments), is in at least 48 pieces.
        private bool Attempt(Action call, bool fits, int kind, string what, bool maybe = false, int oldSegments = 0)
        {
            TakeBefore();
            int bucket = Bucket();
            Exception? error = null;
            long start = Stopwatch.GetTimestamp();
            try
            {
                call();
            }
            catch (IOException ex)
            {
                error = ex;
            }

            long ticks = Stopwatch.GetTimestamp() - start;
            if (maybe)
            {
                _maybeCalls++;
            }

            if (error == null)
            {
                if (!fits && !maybe)
                {
                    Assert.Fail("a " + what + " that needs more sectors than are free did not throw, " + Where());
                }

                Record(kind, bucket, _round, ticks);
                return true;
            }

            Record(KindRefused, bucket, _round, ticks);
            RequireUnchanged(what);
            if (error.Message.Contains("segment"))
            {
                int runs = FreeRuns();
                Assert.That(runs + oldSegments, Is.GreaterThanOrEqualTo(48), "the " + what + " was refused for too many segments, but the free space is in only "
                    + runs + " pieces (and the call releases " + oldSegments + " segments first), " + Where());
                _refusedSegments++;
                return false;
            }

            if (!fits || maybe)
            {
                Assert.That(error.Message, Does.Contain("full"), Where());
                _refusedFull++;
                return false;
            }

            Assert.Fail("the " + what + " failed although there was room, " + Where() + ": " + error.Message);
            return false;
        }

        // A refused call on a file the code cannot release: it must throw, name the crossing, and change nothing.
        private void RequirePinnedRefusal(Action call, string what)
        {
            TakeBefore();
            long start = Stopwatch.GetTimestamp();
            IOException? error = Assert.Throws<IOException>(() => call(), what + " of a file whose segment crosses a map sector did not throw, " + Where());
            Record(KindRefused, Bucket(), _round, Stopwatch.GetTimestamp() - start);
            Assert.That(error!.Message, Does.Contain("crosses an allocation map sector"), Where());
            RequireUnchanged(what);
            _pinnedRefusals++;
        }

        // The number of segments in a file's descriptor on the disk.
        private int SegmentCount(Node file)
        {
            byte[] image = Snapshot();
            int at = (int)file.Fd * SectorSize;
            int count = 0;
            while (count < 48 && U16(image, at + 16 + (count * 5) + 3) != 0)
            {
                count++;
            }

            return count;
        }

        private void WriteSomewhere()
        {
            Node directory = RandomDirectory();
            string name = RandomName(false);
            Node? existing = Find(directory, name);
            if (existing != null && existing.IsDirectory)
            {
                string path = PathOf(directory) == "/" ? "/" + name : PathOf(directory) + "/" + name;
                Begin("write over the directory " + path);
                TakeBefore();
                long start = Stopwatch.GetTimestamp();
                Assert.Throws<IOException>(() => _fs!.WriteFile(Spelled(path), new byte[1]), Where());
                Record(KindRefused, Bucket(), _round, Stopwatch.GetTimestamp() - start);
                RequireUnchanged("write over a directory");
                _refusedOther++;
                Verify();
                return;
            }

            if (existing != null)
            {
                Overwrite(existing);
                return;
            }

            byte[] data = RandomData(RandomSize(_free - 1));
            Create(directory, name, data);
        }

        private void Create(Node directory, string name, byte[] data)
        {
            string path = PathOf(directory) == "/" ? "/" + name : PathOf(directory) + "/" + name;
            bool grow = NeedsGrowth(directory);

            // The descriptor's cluster, then (when the directory must grow) the directory's growth, then the data clusters.
            int free = _free >> _shift;
            int data2 = DataClusters(data.Length);
            bool fits;
            bool maybe = false;
            if (!grow)
            {
                fits = free >= 1 + data2;
            }
            else if (free < 2)
            {
                fits = false;
            }
            else
            {
                GrowthAfterDescriptor(Snapshot(), out int least, out int most);
                fits = free - 1 - most >= data2;
                maybe = !fits && free - 1 - least >= data2;
            }

            Begin("create " + path + " with " + data.Length + " bytes" + (grow ? ", the directory grows" : string.Empty)
                + (fits ? string.Empty : maybe ? ", may not fit" : ", does not fit"));
            if (Attempt(() => _fs!.WriteFile(SpelledChild(directory, name), data), fits, KindCreate, "create", maybe))
            {
                Node file = new Node { Name = name, Data = data, DataSectors = FileSectors(data.Length) };
                AddEntry(directory, file);
                _creates++;
            }

            Verify();
        }

        private void OverwriteRandom()
        {
            Node? file = RandomFile();
            if (file == null)
            {
                WriteSomewhere();
                return;
            }

            Overwrite(file);
        }

        private void Overwrite(Node file)
        {
            int available = _free + file.DataSectors;
            byte[] data = RandomData(RandomSize(available));
            Overwrite(file, data);
        }

        private void Overwrite(Node file, byte[] data)
        {
            string path = PathOf(file);
            if (file.Pinned)
            {
                Begin("overwrite " + path + ", which has a segment across a map sector");
                RequirePinnedRefusal(() => _fs!.WriteFile(Spelled(path), data), "overwrite");
                Verify();
                return;
            }

            // SS.Size 0 releases every cluster of the file but the descriptor's; the new data then needs its own clusters.
            int kept = (RoundUp(1 + file.DataSectors) >> _shift) - 1;
            bool fits = (_free >> _shift) + kept >= DataClusters(data.Length);
            Begin("overwrite " + path + " (" + file.Data.Length + " bytes) with " + data.Length + " bytes" + (fits ? string.Empty : ", does not fit"));
            int oldSegments = SegmentCount(file);
            if (Attempt(() => _fs!.WriteFile(Spelled(path), data), fits, KindOverwrite, "overwrite", false, oldSegments))
            {
                file.Data = data;
                file.DataSectors = FileSectors(data.Length);
                _overwrites++;
            }

            Verify();
        }

        private void ReadRandom()
        {
            Node? file = RandomFile();
            if (file == null)
            {
                RefusedCall();
                return;
            }

            Read(file);
        }

        private void Read(Node file)
        {
            string path = PathOf(file);
            Begin("read " + path);
            int bucket = Bucket();
            long start = Stopwatch.GetTimestamp();
            byte[] data = _fs!.ReadFile(Spelled(path));
            Record(KindRead, bucket, _round, Stopwatch.GetTimestamp() - start);
            if (!data.AsSpan().SequenceEqual(file.Data))
            {
                Assert.Fail("read back differs, " + Where());
            }

            _reads++;
            Verify();
        }

        private void DeleteRandom()
        {
            Node? file = RandomFile();
            if (file == null)
            {
                RefusedCall();
                return;
            }

            Delete(file);
        }

        private void Delete(Node file)
        {
            string path = PathOf(file);
            if (file.Pinned)
            {
                Begin("delete " + path + ", which has a segment across a map sector");
                RequirePinnedRefusal(() => _fs!.DeleteFile(Spelled(path)), "delete");
                Verify();
                return;
            }

            Begin("delete " + path);
            int bucket = Bucket();
            long start = Stopwatch.GetTimestamp();
            _fs!.DeleteFile(Spelled(path));
            Record(KindDelete, bucket, _round, Stopwatch.GetTimestamp() - start);
            RemoveEntry(file);
            _deletes++;
            Verify();
        }

        private void RenameRandom()
        {
            List<Node> files = new List<Node>();
            List<Node> directories = new List<Node>();
            Collect(_root, files, directories);
            directories.RemoveAt(0);
            if (files.Count + directories.Count == 0)
            {
                RefusedCall();
                return;
            }

            int pick = _random.Next(files.Count + directories.Count);
            Node node = pick < files.Count ? files[pick] : directories[pick - files.Count];

            // Now and then only the case of the name changes, which is allowed.
            string newName = _random.Next(8) == 0 ? (node.Name.ToUpperInvariant() == node.Name ? node.Name.ToLowerInvariant() : node.Name.ToUpperInvariant())
                : RandomName(node.IsDirectory);
            RenameTo(node, newName);
        }

        private void RenameTo(Node node, string newName)
        {
            Node parent = node.Parent!;
            Node? clash = Find(parent, newName);
            bool refused = clash != null && clash != node;
            string oldPath = PathOf(node);
            string newPath = PathOf(parent) == "/" ? "/" + newName : PathOf(parent) + "/" + newName;
            Begin("rename " + oldPath + " to " + newPath + (refused ? ", name taken" : string.Empty));
            TakeBefore();
            int bucket = Bucket();
            Exception? error = null;
            long start = Stopwatch.GetTimestamp();
            try
            {
                _fs!.Rename(Spelled(oldPath), SpelledChild(parent, newName));
            }
            catch (IOException ex)
            {
                error = ex;
            }

            long ticks = Stopwatch.GetTimestamp() - start;
            if (refused)
            {
                Record(KindRefused, bucket, _round, ticks);
                Assert.That(error, Is.Not.Null, "a rename onto a name that is taken did not throw, " + Where());
                RequireUnchanged("rename");
                _refusedOther++;
            }
            else
            {
                Record(KindRename, bucket, _round, ticks);
                Assert.That(error, Is.Null, "rename failed, " + Where() + ": " + error?.Message);
                node.Name = newName;
                _renames++;
            }

            Verify();
        }

        private void MoveRandom()
        {
            List<Node> files = new List<Node>();
            List<Node> directories = new List<Node>();
            Collect(_root, files, directories);
            directories.RemoveAt(0);
            if (files.Count + directories.Count == 0)
            {
                RefusedCall();
                return;
            }

            int pick = _random.Next(files.Count + directories.Count);
            Node node = pick < files.Count ? files[pick] : directories[pick - files.Count];
            Node target = RandomDirectory();
            if (target == node.Parent)
            {
                RenameRandom();
                return;
            }

            bool intoItself = node.IsDirectory && IsInside(node, target);
            bool clash = Find(target, node.Name) != null;
            bool tooDeep = node.IsDirectory && Depth(target) + 1 > MaxDepth;
            if (tooDeep && !intoItself)
            {
                RenameRandom();
                return;
            }

            bool grow = NeedsGrowth(target);
            bool fits = !grow || _free >= 1;
            string oldPath = PathOf(node);
            string newPath = PathOf(target) == "/" ? "/" + node.Name : PathOf(target) + "/" + node.Name;
            Begin("move " + oldPath + " to " + newPath + (intoItself ? ", into itself" : string.Empty) + (clash ? ", name taken" : string.Empty)
                + (grow ? ", the directory grows" : string.Empty));
            if (intoItself || clash)
            {
                TakeBefore();
                long start = Stopwatch.GetTimestamp();
                Assert.Throws<IOException>(() => _fs!.Rename(Spelled(oldPath), Spelled(newPath)), Where());
                Record(KindRefused, Bucket(), _round, Stopwatch.GetTimestamp() - start);
                RequireUnchanged("move");
                _refusedOther++;
                Verify();
                return;
            }

            if (Attempt(() => _fs!.Rename(Spelled(oldPath), SpelledChild(target, node.Name)), fits, KindMove, "move"))
            {
                RemoveEntry(node);
                AddEntry(target, node);
                _moves++;
            }

            Verify();
        }

        private void MakeDirectory()
        {
            Node parent = RandomDirectory();
            if (DirectoryCount() >= MaxDirectories || Depth(parent) + 1 > MaxDepth)
            {
                RemoveDirectory();
                return;
            }

            MakeDirectoryAt(parent, RandomName(true));
        }

        private void MakeDirectoryAt(Node parent, string name)
        {
            Node? existing = Find(parent, name);
            string path = PathOf(parent) == "/" ? "/" + name : PathOf(parent) + "/" + name;
            if (existing != null)
            {
                Begin("make directory " + path + ", name taken");
                TakeBefore();
                long start = Stopwatch.GetTimestamp();
                Assert.Throws<IOException>(() => _fs!.CreateDirectory(Spelled(path)), Where());
                Record(KindRefused, Bucket(), _round, Stopwatch.GetTimestamp() - start);
                RequireUnchanged("make directory");
                _refusedOther++;
                Verify();
                return;
            }

            // MakDir: the descriptor's cluster, then the parent's growth when it needs one, then room for the 64 bytes of the new directory, which
            // the rest of the descriptor's cluster already gives when a cluster has more than one sector. Each step takes at least one cluster
            // when any is free (FatScan gives the longest run when none is long enough), so the call fits exactly when there are as many free
            // clusters as steps.
            bool grow = NeedsGrowth(parent);
            int steps = 1 + (grow ? 1 : 0) + (_bit == 1 ? 1 : 0);
            bool fits = (_free >> _shift) >= steps;

            Begin("make directory " + path + (grow ? ", the parent grows" : string.Empty) + (fits ? string.Empty : ", does not fit"));
            if (Attempt(() => _fs!.CreateDirectory(SpelledChild(parent, name)), fits, KindMakeDir, "make directory"))
            {
                Node directory = new Node { Name = name, IsDirectory = true };
                directory.Slots.Add(Up);
                directory.Slots.Add(Self);
                directory.InitialSlack = -1;
                AddEntry(parent, directory);
                _makeDirs++;
            }

            Verify();
        }

        private void RemoveDirectory()
        {
            List<Node> directories = new List<Node>();
            Collect(_root, new List<Node>(), directories);
            if (directories.Count < 2)
            {
                MakeDirectoryOrRefuse();
                return;
            }

            RemoveDirectoryNode(directories[1 + _random.Next(directories.Count - 1)]);
        }

        private void RemoveDirectoryNode(Node directory)
        {
            bool empty = true;
            for (int i = 0; i < directory.Slots.Count; i++)
            {
                if (IsChild(directory.Slots[i]))
                {
                    empty = false;
                    break;
                }
            }

            string path = PathOf(directory);
            Begin("remove directory " + path + (empty ? string.Empty : ", not empty"));
            TakeBefore();
            int bucket = Bucket();
            Exception? error = null;
            long start = Stopwatch.GetTimestamp();
            try
            {
                _fs!.DeleteDirectory(Spelled(path));
            }
            catch (IOException ex)
            {
                error = ex;
            }

            long ticks = Stopwatch.GetTimestamp() - start;
            if (!empty)
            {
                Record(KindRefused, bucket, _round, ticks);
                Assert.That(error, Is.Not.Null, "removing a directory that is not empty did not throw, " + Where());
                RequireUnchanged("remove directory");
                _refusedOther++;
            }
            else
            {
                Record(KindRemoveDir, bucket, _round, ticks);
                Assert.That(error, Is.Null, "remove directory failed, " + Where() + ": " + error?.Message);
                RemoveEntry(directory);
                _removeDirs++;
            }

            Verify();
        }

        private void MakeDirectoryOrRefuse()
        {
            if (_random.Next(2) == 0)
            {
                MakeDirectory();
            }
            else
            {
                RefusedCall();
            }
        }

        // Calls that must be refused and change nothing: a missing file, the root directory, a dot name.
        private void RefusedCall()
        {
            int r = _random.Next(4);
            string path = "/nosuch" + _random.Next(1000);
            if (Find(_root, path.Substring(1)) != null)
            {
                return;
            }

            Begin("refused call " + r + " on " + path);
            TakeBefore();
            long start = Stopwatch.GetTimestamp();
            if (r == 0)
                Assert.Throws<FileNotFoundException>(() => _fs!.DeleteFile(path), Where());
            else if (r == 1)
                Assert.Throws<FileNotFoundException>(() => _fs!.ReadFile(path), Where());
            else if (r == 2)
                Assert.Throws<IOException>(() => _fs!.DeleteDirectory("/"), Where());
            else
                Assert.Throws<DirectoryNotFoundException>(() => _fs!.WriteFile(path + "/x", new byte[1]), Where());

            Record(KindRefused, Bucket(), _round, Stopwatch.GetTimestamp() - start);
            RequireUnchanged("call");
            _refusedOther++;
            Verify();
        }

        // Fills the disk: one file is overwritten with all the free space plus its own sectors. When the free space is in too many pieces for
        // one segment list, the size is halved until it goes. When the disk is full, a new file, an empty new file, a new directory and the
        // same file one sector bigger must all be refused without changing a byte.
        private void FillToFull()
        {
            _fills++;
            Node? file = RandomFile();
            if (file == null)
            {
                Create(_root, "fill" + _random.Next(10), Array.Empty<byte>());
                file = RandomFile();
            }

            if (file == null)
            {
                return;
            }

            int available = _free + file.DataSectors;
            int size = Math.Max(0, (available * SectorSize) - _random.Next(SectorSize));
            for (int tries = 0; tries < 8; tries++)
            {
                int refusedBefore = _refusedSegments;
                Overwrite(file, RandomData(size));
                if (_refusedSegments == refusedBefore)
                {
                    break;
                }

                size /= 2;
            }

            if (_free != 0)
            {
                return;
            }

            string extra = "full" + _random.Next(100);
            if (Find(_root, extra) == null)
            {
                Create(_root, extra, new byte[] { 1 });
                Create(_root, extra, Array.Empty<byte>());
            }

            if (DirectoryCount() < MaxDirectories)
            {
                string name = "Dfull" + _random.Next(10);
                if (Find(_root, name) == null)
                {
                    Begin("make directory /" + name + " on a full disk");
                    Attempt(() => _fs!.CreateDirectory("/" + name), false, KindMakeDir, "make directory");
                    Verify();
                }
            }

            Overwrite(file, RandomData((file.DataSectors * SectorSize) + 1));
        }

        // Deletes every file and directory, deepest first, and requires the free space of an empty disk back. The root directory never
        // shrinks, so any sectors it gained during the run stay with it; the slack check in Verify limits how many that can be. Files that
        // cannot be released, and the directories that hold them, stay (with any growth of those directories).
        private void DeleteAll()
        {
            List<Node> files = new List<Node>();
            List<Node> directories = new List<Node>();
            Collect(_root, files, directories);
            while (files.Count > 0)
            {
                int pick = _random.Next(files.Count);
                if (!files[pick].Pinned)
                {
                    Delete(files[pick]);
                }

                files.RemoveAt(pick);
            }

            for (int i = directories.Count - 1; i >= 1; i--)
            {
                Node directory = directories[i];
                if (HoldsPinned(directory))
                {
                    continue;
                }

                string path = PathOf(directory);
                Begin("remove directory " + path + " while emptying the disk");
                long start = Stopwatch.GetTimestamp();
                _fs!.DeleteDirectory(Spelled(path));
                Record(KindRemoveDir, Bucket(), _round, Stopwatch.GetTimestamp() - start);
                RemoveEntry(directory);
                _removeDirs++;
                Verify();
            }

            Begin("everything deleted");
            int rootGrowth = _emptyFree - EmptyFree();
            Assert.That(_fs!.GetBlocksFree(), Is.EqualTo(_emptyFree - rootGrowth),
                "free space after everything was deleted is not the empty-disk value (" + _emptyFree + ") less the " + rootGrowth
                + " sectors the directories that stay grew by, " + Where());
            int rootOnly = _root.CapacitySectors - _rootStartSectors;
            if (rootOnly > _maxRootGrowth)
            {
                _maxRootGrowth = rootOnly;
            }
        }

        // Sectors the root directory held before the run, and the most it grew by.
        private int _rootStartSectors;
        private int _maxRootGrowth;

        // ---- the checks run after every call ----

        // Walks the disk from the root and checks it against the model. Gives, per sector, the index of the node that owns it in the order
        // the walk met them, or -1.
        private int[] Walk(byte[] image, bool check)
        {
            int[] owner = new int[_total];
            for (int s = 0; s < _total; s++)
            {
                owner[s] = -1;
            }

            List<Node> seen = new List<Node>();
            WalkDirectory(image, _root, U24(image, 8), U24(image, 8), owner, seen, check);
            return owner;
        }

        private void Claim(int[] owner, List<Node> seen, Node node, long sector)
        {
            if (sector < 0 || sector >= _total)
            {
                Assert.Fail(PathOf(node) + " uses sector " + sector + ", outside the disk, " + Where());
            }

            if (owner[sector] >= 0)
            {
                Assert.Fail("sector " + sector + " is used by " + PathOf(node) + " and by " + PathOf(seen[owner[sector]]) + ", " + Where());
            }

            owner[sector] = seen.Count - 1;
        }

        private int ClaimDescriptor(byte[] image, Node node, uint fd, int[] owner, List<Node> seen)
        {
            seen.Add(node);
            Claim(owner, seen, node, fd);
            int fdOffset = (int)fd * SectorSize;
            int sectors = 0;
            for (int i = 0; i < 48; i++)
            {
                int at = fdOffset + 16 + (i * 5);
                int count = U16(image, at + 3);
                if (count == 0)
                {
                    break;
                }

                uint start = U24(image, at);
                for (int k = 0; k < count; k++)
                {
                    Claim(owner, seen, node, (long)start + k);
                }

                sectors += count;
            }

            return sectors;
        }

        private void WalkDirectory(byte[] image, Node directory, uint fd, uint parentFd, int[] owner, List<Node> seen, bool check)
        {
            int fdOffset = (int)fd * SectorSize;
            int sectors = ClaimDescriptor(image, directory, fd, owner, seen);
            directory.Fd = fd;
            directory.CapacitySectors = sectors;
            byte[] data = RawFile(image, fdOffset, out _);
            if (check)
            {
                if ((image[fdOffset] & 0x80) == 0)
                {
                    Assert.Fail(PathOf(directory) + " is not a directory on the disk, " + Where());
                }

                if (image[fdOffset + 8] != directory.LinkCount)
                {
                    Assert.Fail(PathOf(directory) + " has link count " + image[fdOffset + 8] + ", expected " + directory.LinkCount + ", " + Where());
                }

                long size = U32(image, fdOffset + 9);
                if (size != directory.Slots.Count * 32)
                {
                    Assert.Fail(PathOf(directory) + " is " + size + " bytes, the model has " + directory.Slots.Count + " entries, " + Where());
                }

                if (directory.InitialSlack < 0)
                {
                    directory.InitialSlack = (sectors * SectorSize) - (int)size;
                }

                // A directory only takes more sectors when an entry does not fit; it may then hold up to one allocation (the allocation size in
                // whole clusters) less one entry spare.
                int slack = (sectors * SectorSize) - (int)size;
                int allocation = ((_bit - 1 + _sas) >> _shift) << _shift;
                if (slack < 0 || slack > Math.Max(directory.InitialSlack, (allocation * SectorSize) - 32))
                {
                    Assert.Fail(PathOf(directory) + " holds " + sectors + " sectors for " + size + " bytes, " + Where());
                }
            }

            for (int i = 0; i < directory.Slots.Count; i++)
            {
                int o = i * 32;
                Node? slot = directory.Slots[i];
                if (slot == null)
                {
                    if (check && data[o] != 0)
                    {
                        Assert.Fail(PathOf(directory) + " slot " + i + " should be free but holds '" + EntryName(data, o) + "', " + Where());
                    }

                    continue;
                }

                string name = EntryName(data, o);
                uint target = U24(data, o + 29);
                if (check && (data[o] == 0 || name != slot.Name))
                {
                    Assert.Fail(PathOf(directory) + " slot " + i + " holds '" + (data[o] == 0 ? "(free)" : name) + "', expected '" + slot.Name + "', " + Where());
                }

                if (slot == Self || slot == Up)
                {
                    uint expectedTarget = slot == Self ? fd : parentFd;
                    if (check && target != expectedTarget)
                    {
                        Assert.Fail(PathOf(directory) + " entry '" + slot.Name + "' points at sector " + target + ", expected " + expectedTarget + ", " + Where());
                    }

                    continue;
                }

                if (slot.IsDirectory)
                {
                    WalkDirectory(image, slot, target, fd, owner, seen, check);
                    continue;
                }

                int childOffset = (int)target * SectorSize;
                int dataSectors = ClaimDescriptor(image, slot, target, owner, seen);
                slot.Fd = target;
                if (!check)
                {
                    continue;
                }

                if ((image[childOffset] & 0x80) != 0 || image[childOffset + 8] != slot.LinkCount)
                {
                    Assert.Fail(PathOf(slot) + " has attributes " + image[childOffset] + " and link count " + image[childOffset + 8] + ", " + Where());
                }

                if (U32(image, childOffset + 9) != slot.Data.Length || dataSectors != slot.DataSectors)
                {
                    Assert.Fail(PathOf(slot) + " is " + U32(image, childOffset + 9) + " bytes in " + dataSectors + " sectors, expected " + slot.Data.Length
                        + " bytes in " + slot.DataSectors + " sectors, " + Where());
                }

                if (!RawFile(image, childOffset, out _).AsSpan().SequenceEqual(slot.Data))
                {
                    Assert.Fail(PathOf(slot) + ": the data on the disk differs from what was written, " + Where());
                }
            }
        }

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

            // Sector 0 never changes, nor do the map bits past the last sector and the map bytes past the end of the map.
            if (!image.AsSpan(0, SectorSize).SequenceEqual(_initial.AsSpan(0, SectorSize)))
            {
                Assert.Fail("sector 0 changed, " + Where());
            }

            for (int i = _clusters; i < _mapSectors * SectorSize * 8; i++)
            {
                int at = SectorSize + (i >> 3);
                int mask = 0x80 >> (i & 7);
                if ((image[at] & mask) != (_initial[at] & mask))
                {
                    Assert.Fail("allocation map bit " + i + " (past the last cluster) changed, " + Where());
                }
            }

            // Every cluster: its bit is set exactly when one of its sectors belongs to a file or directory or it was reserved.
            int[] owner = Walk(image, true);
            int owned = 0;
            int ownedClusters = 0;
            for (int s = 0; s < _total; s++)
            {
                if (owner[s] >= 0)
                {
                    owned++;
                }
            }

            for (int c = 0; c < _clusters; c++)
            {
                bool set = ClusterBit(image, c);
                bool inUse = ClusterOwned(owner, c);
                if (inUse)
                {
                    ownedClusters++;
                }

                if (set && !inUse && !_reservedCluster[c])
                {
                    Assert.Fail("lost cluster " + c + ": marked used in the allocation map but no sector of it is in a file or directory, " + Where());
                }

                if (!set && (inUse || _reservedCluster[c]))
                {
                    Assert.Fail("cluster " + c + " is in use" + (inUse ? string.Empty : " (reserved)") + " but marked free in the allocation map, " + Where());
                }
            }

            // Free space plus the space the model's files and directories own plus the reserved sectors is the whole disk.
            List<Node> files = new List<Node>();
            List<Node> directories = new List<Node>();
            Collect(_root, files, directories);
            int modelOwned = 0;
            for (int i = 0; i < files.Count; i++)
            {
                modelOwned += 1 + files[i].DataSectors;
            }

            for (int i = 0; i < directories.Count; i++)
            {
                modelOwned += 1 + directories[i].CapacitySectors;
            }

            // RBF never makes a segment that crosses an allocation map sector: FatScan searches one map sector at a time, and L0D07 joins a new
            // run to the last segment only when both start in the same map sector (rbf.asm, L0D4E to L0D96). Only a file that came with the disk
            // may have one.
            for (int i = 0; i < files.Count; i++)
            {
                if (!files[i].Pinned && CrossesMapSector(image, (int)files[i].Fd * SectorSize))
                {
                    Assert.Fail(PathOf(files[i]) + " has a segment that crosses an allocation map sector, which RBF never makes, " + Where());
                }
            }

            for (int i = 0; i < directories.Count; i++)
            {
                if (CrossesMapSector(image, (int)directories[i].Fd * SectorSize))
                {
                    Assert.Fail("directory " + PathOf(directories[i]) + " has a segment that crosses an allocation map sector, which RBF never makes, " + Where());
                }
            }

            if (modelOwned != owned)
            {
                Assert.Fail("the walk found " + owned + " sectors in use, the model's files and directories own " + modelOwned + ", " + Where());
            }

            _free = (_clusters - _reservedClusters - ownedClusters) << _shift;
            int free = _fs!.GetBlocksFree();
            if (free != _free)
            {
                Assert.Fail("free space is " + free + " sectors, but (" + _clusters + " clusters - " + _reservedClusters + " reserved - " + ownedClusters
                    + " in use) times " + _bit + " = " + _free + ", " + Where());
            }

            // The code's own view: every listing in slot order, every file read back.
            for (int d = 0; d < directories.Count; d++)
            {
                Node directory = directories[d];
                DirectoryEntry[] listing = _fs.ListDirectory(PathOf(directory));
                int n = 0;
                for (int i = 0; i < directory.Slots.Count; i++)
                {
                    Node? slot = directory.Slots[i];
                    if (!IsChild(slot))
                    {
                        continue;
                    }

                    long size = slot!.IsDirectory ? slot.Slots.Count * 32 : slot.Data.Length;
                    if (n >= listing.Length || listing[n].Name != slot.Name || listing[n].Metadata.IsDirectory != slot.IsDirectory || listing[n].Metadata.Size != size)
                    {
                        Assert.Fail("listing of " + PathOf(directory) + " entry " + n + " is " + (n < listing.Length ? listing[n].Name + " " + listing[n].Metadata.Size : "missing")
                            + ", expected " + slot.Name + " " + size + ", " + Where());
                    }

                    n++;
                }

                if (listing.Length != n)
                {
                    Assert.Fail("listing of " + PathOf(directory) + " has " + listing.Length + " entries, the model " + n + ", " + Where());
                }
            }

            for (int i = 0; i < files.Count; i++)
            {
                if (!_fs.ReadFile(PathOf(files[i])).AsSpan().SequenceEqual(files[i].Data))
                {
                    Assert.Fail(PathOf(files[i]) + " does not read back as written, " + Where());
                }
            }
        }

        private void CheckImageFileMatchesDevice()
        {
            _callText = "image file after round " + _round;
            byte[] file = StressMemory.ReadShared(_imagePath);
            byte[] device = Snapshot();

            // A short file (DriveWire) holds the first part of the volume; every sector behind it must be zero on the device, and the file
            // must end on a whole sector.
            Assert.That(file.Length % SectorSize, Is.EqualTo(0), "the image file is not whole sectors, " + Where());
            Assert.That(file.Length, Is.LessThanOrEqualTo(device.Length), "the image file is longer than the volume, " + Where());
            Assert.That(file.AsSpan().SequenceEqual(device.AsSpan(0, file.Length)), Is.True,
                "the image file is not what the device holds: a write was not flushed, " + Where());
            Assert.That(device.AsSpan(file.Length).IndexOfAnyExcept((byte)0), Is.EqualTo(-1),
                "the device holds data behind the end of the image file: a write was not flushed, " + Where());
        }

        // ---- Toolshed ----

        private void ToolshedCheck(string when)
        {
            string image = StressToolshed.WslPath(_imagePath);
            string? check = StressToolshed.Run("os9", "dcheck", image);
            if (check == null)
            {
                if (_notes.Length == 0)
                {
                    _notes.Append("Toolshed os9 is not available through WSL Ubuntu, so the independent check was skipped.");
                }

                return;
            }

            _toolshedRuns++;
            _callText = "Toolshed check " + when;
            List<Node> files = new List<Node>();
            List<Node> directories = new List<Node>();
            Collect(_root, files, directories);
            Assert.That(check, Does.Contain("file structure is intact"), check + Where());
            Assert.That(check, Does.Contain("0 previously allocated cluster found"), check + Where());
            Assert.That(check, Does.Contain("0 clusters in file structure but not in allocation map"), check + Where());
            Assert.That(check, Does.Contain("0 bad file decriptor sector"), check + Where());

            // dcheck counts neither sector 0 nor the map sectors as reserved, so a built CoCo disk shows 0 here and a built Dragon disk 16 (sectors
            // 2 to 17). On every disk the number dcheck gives before the run must stay the same: a cluster lost or a reserved one released changes it.
            long unreferenced = StressToolshed.NumberBefore(check, " clusters in allocation map but not in file structure");
            if (_toolshedUnreferenced < 0)
            {
                _toolshedUnreferenced = unreferenced;
                if (RealImageName(_variant) == null && _variant != "NITR09")
                {
                    Assert.That(unreferenced, Is.EqualTo(_reservedCount - 1 - _mapSectors), check + Where());
                }
            }

            Assert.That(unreferenced, Is.EqualTo(_toolshedUnreferenced), check + Where());
            Assert.That(StressToolshed.NumberBefore(check, " director"), Is.EqualTo(directories.Count), check + Where());
            Assert.That(StressToolshed.NumberBefore(check, " files"), Is.EqualTo(files.Count), check + Where());

            string? free = StressToolshed.Run("os9", "free", image);
            Assert.That(free, Is.Not.Null);
            Assert.That(StressToolshed.NumberBefore(free!, " Free sectors"), Is.EqualTo(_free), free + Where());

            for (int k = 0; k < 3 && files.Count > 0; k++)
            {
                Node file = files[_random.Next(files.Count)];
                string target = Path.Combine(_folder, "out" + k + ".bin");
                string? copy = StressToolshed.Run("os9", "copy", image + "," + PathOf(file), StressToolshed.WslPath(target));
                Assert.That(File.Exists(target), Is.True, "os9 copy made no file: " + copy + ", " + Where());
                Assert.That(File.ReadAllBytes(target).AsSpan().SequenceEqual(file.Data), Is.True, "os9 copy of " + PathOf(file) + " differs, " + Where());
                File.Delete(target);
            }
        }

        // ---- report ----

        private void Report()
        {
            StringBuilder report = new StringBuilder();
            report.Append("OS-9 stress on ").Append(_variant).Append(" (").Append(_total).Append(" sectors, ").Append(_emptyFree).Append(" free when empty), seed ")
                .Append(_seed).Append(": ").Append(_call).Append(" calls in ").Append(_rounds).Append(" rounds (")
                .Append(_creates).Append(" creates, ").Append(_overwrites).Append(" overwrites, ").Append(_reads).Append(" reads, ")
                .Append(_deletes).Append(" deletes, ").Append(_renames).Append(" renames, ").Append(_moves).Append(" moves, ")
                .Append(_makeDirs).Append(" mkdir, ").Append(_removeDirs).Append(" rmdir, ").Append(_refusedFull).Append(" refused as disk full, ")
                .Append(_refusedSegments).Append(" refused for too many segments, ").Append(_refusedOther).Append(" other refusals, ")
                .Append(_fills).Append(" fills); the root directory grew by up to ").Append(_maxRootGrowth).Append(" sectors").AppendLine();
            report.Append("  ").Append(_bit).Append(" sectors per map bit, ").Append(_mapSectors).Append(" map sectors; ").Append(_pinnedRefusals)
                .Append(" refusals of files with a segment across a map sector; ").Append(_maybeCalls).Append(" creates whose outcome depended on where FatScan started")
                .AppendLine();
            report.Append("  Toolshed checks run: ").Append(_toolshedRuns).Append(". ").Append(_notes.ToString()).AppendLine();
            report.Append("  memory still held after each round (bytes):");
            for (int i = 0; i < _rounds; i++)
            {
                report.Append(' ').Append(_memory[i]);
            }

            report.AppendLine();
            report.Append("  before the first measurement: ").Append(_hostWarmUp).AppendLine();
            report.Append("  image bytes the device held after each round:");
            for (int i = 0; i < _rounds; i++)
            {
                report.Append(' ').Append(_held[i]);
            }

            report.AppendLine();
            _timings.Describe(report, _rounds);
            _timings.DescribeProbes(report);
            TestContext.Out.WriteLine(report.ToString());

            // On a short DriveWire file the device holds only the 4096-byte pages of the image that are in the file or were written
            // (BlockDeviceBase.HeldBytes), so it holds more each time the run writes somewhere new on the volume, up to the whole volume. Those
            // pages are the disk, not memory the file system kept, so the leak check looks at what is held besides them: each page counted at
            // what one 4096-byte array really costs on this runtime. On a full-size image the device holds the same pages in every round.
            long pageCost = StressMemory.ArrayCost(DevicePageSize);
            long[] besidesImage = new long[_rounds];
            for (int i = 0; i < _rounds; i++)
            {
                besidesImage[i] = _memory[i] - ((_held[i] / DevicePageSize) * pageCost);
            }

            StressChecks.RequireNoLeak(besidesImage, report.ToString());
            StressChecks.RequireNoSlowdown(_timings, report.ToString());
        }
    }
}
