//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers.Binary;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Local.Images;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Tests for PC hard-disk images kept as raw <c>.img</c>, raw <c>.hdd</c> and fixed <c>.vhd</c>,
    /// and for the converter between raw and fixed VHD.
    /// </summary>
    /// <remarks>
    /// Every image is built by the test in its own temporary folder (an MBR with one FAT16 partition,
    /// from <see cref="PcImageFactory"/>); no real disk image is touched. The VHD footer is written by
    /// the test itself, byte by byte, with the same field values RetroCore's PC/AT hard-disk code
    /// writes (creator "rcl ", host "Wi2k", the spec's checksum), so a disk made there is shown to open
    /// here without the test leaning on RetroIO's own footer writer.
    /// </remarks>
    [TestFixture]
    public class FixedVhdTests
    {
        private string _dir = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "retroio-vhd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }

        // ---------------------------------------------------------------------------------------
        // Opening the three formats
        // ---------------------------------------------------------------------------------------

        [TestCase(".img")]
        [TestCase(".hdd")]
        [TestCase(".vhd")]
        public void EachFormat_ListsPartition_WritesOverwritesReadsAndDeletes(string extension)
        {
            string path = MakeDisk("DISK" + extension, extension == ".vhd");

            using DiskImageVolumeProvider provider = NewProvider();
            provider.Mount("file:///" + path + "#0", "v", readOnly: false);
            UrnContext v = new UrnContext { VolumeId = "v" };

            Assert.That(provider.GetMountInfo("v")!.FileSystemType, Is.EqualTo("FAT16"));
            Assert.That(provider.ListDirectory(v, "/"), Is.Empty);

            byte[] first = Pattern(5000, 1);
            byte[] second = Pattern(3000, 7);
            provider.WriteFile(v, "/HELLO.TXT", first);
            Assert.That(provider.ReadFile(v, "/HELLO.TXT"), Is.EqualTo(first));
            provider.WriteFile(v, "/HELLO.TXT", second);
            Assert.That(provider.ReadFile(v, "/HELLO.TXT"), Is.EqualTo(second));
            provider.WriteFile(v, "/GONE.BIN", first);
            provider.DeleteFile(v, "/GONE.BIN");
            provider.Unmount("v");

            // Mount again: what was written is on the file.
            provider.Mount("file:///" + path + "#0", "w", readOnly: true);
            UrnContext w = new UrnContext { VolumeId = "w" };
            DirectoryEntry[] entries = provider.ListDirectory(w, "/");
            Assert.That(entries.Length, Is.EqualTo(1));
            Assert.That(provider.ReadFile(w, "/HELLO.TXT"), Is.EqualTo(second));
        }

        [Test]
        public void Vhd_PartitionTableIsListed()
        {
            string path = MakeDisk("DISK.vhd", vhd: true);
            Assert.That(Partitions.PartitionDetectionService.DetectMbrPartitions(path).Count, Is.EqualTo(1));

            using Stream disk = DiskImageFile.Open(path, FileAccess.Read, FileShare.Read);
            PcFloppyDriver driver = new PcFloppyDriver();
            ((IPartitionProvider)driver).SelectPartition("0");
            driver.Mount(disk, readOnly: true);
            Assert.That(driver.SupportsPartitions(), Is.True);
            Assert.That(driver.ListPartitions().Count, Is.EqualTo(1));
            Assert.That(driver.FileSystemType, Is.EqualTo("FAT16"));
            driver.Dispose();
        }

        [Test]
        public void Vhd_FooterIsByteForByteUnchangedAfterWrites()
        {
            string path = MakeDisk("DISK.vhd", vhd: true);
            byte[] footerBefore = Tail(path, FixedVhd.FooterBytes);

            using (DiskImageVolumeProvider provider = NewProvider())
            {
                provider.Mount("file:///" + path + "#0", "v", readOnly: false);
                UrnContext v = new UrnContext { VolumeId = "v" };
                for (int i = 0; i < 20; i++)
                {
                    provider.WriteFile(v, "/F" + i + ".BIN", Pattern(20000, i));
                }
                provider.Unmount("v");
            }

            Assert.That(Tail(path, FixedVhd.FooterBytes), Is.EqualTo(footerBefore));
        }

        [Test]
        public void Vhd_StreamEndsBeforeFooter_LastSectorWritable_PastItRefused()
        {
            string path = MakeDisk("DISK.vhd", vhd: true);
            long fileBytes = new FileInfo(path).Length;
            byte[] footerBefore = Tail(path, FixedVhd.FooterBytes);

            using (Stream disk = DiskImageFile.Open(path, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.That(disk.Length, Is.EqualTo(fileBytes - FixedVhd.FooterBytes));
                disk.Position = disk.Length - 512;
                disk.Write(Pattern(512, 3), 0, 512);
                Assert.Throws<IOException>(() => disk.Write(new byte[1], 0, 1));
            }

            Assert.That(new FileInfo(path).Length, Is.EqualTo(fileBytes));
            Assert.That(Tail(path, FixedVhd.FooterBytes), Is.EqualTo(footerBefore));
        }

        [Test]
        public void Vhd_RecognisedByFooterNotByName()
        {
            // A fixed VHD under the name .img still opens without its footer.
            string path = MakeDisk("REALLYVHD.img", vhd: true);
            using Stream disk = DiskImageFile.Open(path, FileAccess.Read, FileShare.Read);
            Assert.That(disk.Length, Is.EqualTo(new FileInfo(path).Length - FixedVhd.FooterBytes));
        }

        [Test]
        public void RawImage_OpensWhole()
        {
            string path = MakeDisk("DISK.img", vhd: false);
            using Stream disk = DiskImageFile.Open(path, FileAccess.Read, FileShare.Read);
            Assert.That(disk.Length, Is.EqualTo(new FileInfo(path).Length));
        }

        [Test]
        public void Probe_ReadsRetroCoreStyleFooter()
        {
            string path = MakeDisk("DISK.vhd", vhd: true);
            using FileStream file = File.OpenRead(path);
            Assert.That(FixedVhd.Probe(file, out FixedVhdFooter footer, out string? problem), Is.EqualTo(VhdProbeResult.Fixed), problem);
            Assert.That(footer.DiskBytes, Is.EqualTo(file.Length - 512));
            Assert.That(footer.Geometry.Cylinders, Is.EqualTo(TestCylinders));
            Assert.That(footer.Geometry.Heads, Is.EqualTo(TestHeads));
            Assert.That(footer.Geometry.SectorsPerTrack, Is.EqualTo(TestSectors));
        }

        [Test]
        public void BadCookie_IsARawImage()
        {
            string path = MakeDisk("DISK.vhd", vhd: true);
            PatchFooterByte(path, 7, (byte)'y', fixChecksum: true);   // "conectiy"
            using FileStream file = File.OpenRead(path);
            Assert.That(FixedVhd.Probe(file, out _, out _), Is.EqualTo(VhdProbeResult.NotVhd));
        }

        [Test]
        public void BadChecksum_IsRefusedByName()
        {
            string path = MakeDisk("DISK.vhd", vhd: true);
            PatchFooterByte(path, 200, 0x55, fixChecksum: false);
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => DiskImageFile.Open(path, FileAccess.Read, FileShare.Read).Dispose())!;
            Assert.That(ex.Message, Does.Contain("checksum"));
        }

        [TestCase(3u, "dynamic")]
        [TestCase(4u, "differencing")]
        public void NonFixedVhd_IsRefusedByName(uint diskType, string word)
        {
            string path = MakeDisk("DISK.vhd", vhd: true);
            PatchFooterByte(path, 63, (byte)diskType, fixChecksum: true);
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => DiskImageFile.Open(path, FileAccess.Read, FileShare.Read).Dispose())!;
            Assert.That(ex.Message, Does.Contain(word));
        }

        [Test]
        public void NonFixedVhd_MountIsRefusedWithTheReason()
        {
            string path = MakeDisk("DISK.vhd", vhd: true);
            PatchFooterByte(path, 63, 3, fixChecksum: true);
            using DiskImageVolumeProvider provider = NewProvider();
            IOException ex = Assert.Throws<IOException>(() => provider.Mount("file:///" + path + "#0", "v", readOnly: true))!;
            Assert.That(ex.Message, Does.Contain("dynamic"));
        }

        [Test]
        public void BuildFooter_MatchesHandBuiltFooterExceptCreatorTimeAndId()
        {
            // RetroIO's writer and the test's hand-built footer agree on every field the spec fixes.
            byte[] mine = FixedVhd.BuildFooter(TestDiskBytes, TestCylinders, TestHeads, TestSectors, Guid.Empty, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            byte[] spec = HandBuiltFooter(TestDiskBytes, TestCylinders, TestHeads, TestSectors);
            for (int i = 0; i < 512; i++)
            {
                bool varies = (i >= 24 && i < 32) || (i >= 64 && i < 84);   // time, creator, checksum, id
                if (!varies)
                {
                    Assert.That(mine[i], Is.EqualTo(spec[i]), "footer byte " + i);
                }
            }
            Assert.That(BinaryPrimitives.ReadUInt32BigEndian(mine.AsSpan(64)), Is.EqualTo(SpecChecksum(mine)));
        }

        [Test]
        public void ComputeGeometry_FollowsSpecAppendix()
        {
            // 20 MB = 40960 sectors: 17 sectors, 4 heads, 602 cylinders by the appendix.
            DiskGeometry g = FixedVhd.ComputeGeometry(40960);
            Assert.That(g.ToString(), Is.EqualTo("602/4/17"));
        }

        // ---------------------------------------------------------------------------------------
        // Converter
        // ---------------------------------------------------------------------------------------

        [Test]
        public void Convert_RawToVhdToRaw_IsByteIdentical()
        {
            string raw = MakeDisk("DISK.img", vhd: false);
            string vhd = Path.Combine(_dir, "OUT.vhd");
            string back = Path.Combine(_dir, "BACK.hdd");

            Assert.That(DiskImageConverter.Convert(raw, vhd, null), Is.True);
            Assert.That(DiskImageConverter.Convert(vhd, back, null), Is.False);

            Assert.That(File.ReadAllBytes(back), Is.EqualTo(File.ReadAllBytes(raw)));
        }

        [Test]
        public void Convert_RawToVhd_FooterChecksOut()
        {
            string raw = MakeDisk("DISK.img", vhd: false);
            string vhd = Path.Combine(_dir, "OUT.vhd");
            DiskImageConverter.RawToFixedVhd(raw, vhd, null);

            byte[] tail = Tail(vhd, 512);
            Assert.That(System.Text.Encoding.ASCII.GetString(tail, 0, 8), Is.EqualTo("conectix"));
            Assert.That(BinaryPrimitives.ReadUInt32BigEndian(tail.AsSpan(64)), Is.EqualTo(SpecChecksum(tail)));
            Assert.That(BinaryPrimitives.ReadUInt32BigEndian(tail.AsSpan(60)), Is.EqualTo(2u));
            Assert.That(BinaryPrimitives.ReadUInt64BigEndian(tail.AsSpan(48)), Is.EqualTo((ulong)new FileInfo(raw).Length));
            Assert.That(BinaryPrimitives.ReadUInt64BigEndian(tail.AsSpan(40)), Is.EqualTo((ulong)new FileInfo(raw).Length));
            // Geometry read from the partition table PcImageFactory wrote.
            Assert.That(DiskImageConverter.TryDetectGeometry(raw, out DiskGeometry g), Is.True);
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(tail.AsSpan(56)), Is.EqualTo((ushort)g.Cylinders));
            Assert.That(tail[58], Is.EqualTo((byte)g.Heads));
            Assert.That(tail[59], Is.EqualTo((byte)g.SectorsPerTrack));
            Assert.That(File.ReadAllBytes(vhd).AsSpan(0, (int)new FileInfo(raw).Length).SequenceEqual(File.ReadAllBytes(raw)), Is.True);
        }

        [Test]
        public void Convert_FileWrittenBefore_ReadsBackAfter()
        {
            string raw = MakeDisk("DISK.img", vhd: false);
            byte[] data = Pattern(12345, 9);
            using (DiskImageVolumeProvider provider = NewProvider())
            {
                provider.Mount("file:///" + raw + "#0", "v", readOnly: false);
                provider.WriteFile(new UrnContext { VolumeId = "v" }, "/KEEP.DAT", data);
                provider.Unmount("v");
            }

            string vhd = Path.Combine(_dir, "OUT.vhd");
            DiskImageConverter.RawToFixedVhd(raw, vhd, null);
            using (DiskImageVolumeProvider provider = NewProvider())
            {
                provider.Mount("file:///" + vhd + "#0", "w", readOnly: true);
                Assert.That(provider.ReadFile(new UrnContext { VolumeId = "w" }, "/KEEP.DAT"), Is.EqualTo(data));
            }
        }

        [Test]
        public void Convert_GivenGeometry_IsRecorded()
        {
            string raw = MakeDisk("DISK.img", vhd: false);
            string vhd = Path.Combine(_dir, "OUT.vhd");
            DiskImageConverter.RawToFixedVhd(raw, vhd, new DiskGeometry(100, 8, 33));
            Assert.That(DiskImageConverter.TryDetectGeometry(vhd, out DiskGeometry g), Is.True);
            Assert.That(g.ToString(), Is.EqualTo("100/8/33"));
        }

        [Test]
        public void Convert_RefusesExistingTarget_AndLeavesItAlone()
        {
            string raw = MakeDisk("DISK.img", vhd: false);
            string target = Path.Combine(_dir, "THERE.vhd");
            File.WriteAllBytes(target, new byte[] { 1, 2, 3 });
            IOException ex = Assert.Throws<IOException>(() => DiskImageConverter.Convert(raw, target, null))!;
            Assert.That(ex.Message, Does.Contain("always writes a new file"));
            Assert.That(File.ReadAllBytes(target), Is.EqualTo(new byte[] { 1, 2, 3 }));
        }

        [Test]
        public void Convert_RefusesSourceThatIsNotWholeSectors()
        {
            string bad = Path.Combine(_dir, "BAD.img");
            File.WriteAllBytes(bad, new byte[1000]);
            string target = Path.Combine(_dir, "OUT.vhd");
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => DiskImageConverter.Convert(bad, target, null))!;
            Assert.That(ex.Message, Does.Contain("512-byte sectors"));
            Assert.That(File.Exists(target), Is.False);
        }

        [Test]
        public void Convert_RefusesDamagedVhd()
        {
            string vhd = MakeDisk("DISK.vhd", vhd: true);
            PatchFooterByte(vhd, 200, 0x55, fixChecksum: false);
            string target = Path.Combine(_dir, "OUT.img");
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => DiskImageConverter.Convert(vhd, target, null))!;
            Assert.That(ex.Message, Does.Contain("checksum"));
            Assert.That(File.Exists(target), Is.False);
        }

        [Test]
        public void Convert_RawWithoutGeometry_AsksForIt()
        {
            string blank = Path.Combine(_dir, "BLANK.img");
            File.WriteAllBytes(blank, new byte[512 * 100]);
            string target = Path.Combine(_dir, "OUT.vhd");
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => DiskImageConverter.RawToFixedVhd(blank, target, null))!;
            Assert.That(ex.Message, Does.Contain("cylinders, heads and sectors"));
            Assert.That(File.Exists(target), Is.False);
        }

        [Test]
        public void TryDetectGeometry_FromFatBootSector()
        {
            // A DOS 1.44 MB boot sector: jump, 512 bytes per sector, 18 sectors per track, 2 heads.
            byte[] floppy = new byte[512];
            floppy[0] = 0xEB;
            floppy[1] = 0x3C;
            floppy[2] = 0x90;
            floppy[12] = 2;     // 512 bytes per sector
            floppy[24] = 18;
            floppy[26] = 2;
            floppy[510] = 0x55;
            floppy[511] = 0xAA;
            Assert.That(DiskImageConverter.TryDetectGeometry(floppy, 2880, out DiskGeometry g), Is.True);
            Assert.That(g.ToString(), Is.EqualTo("80/2/18"));
        }

        [Test]
        public void TryDetectGeometry_FromPartitionTable()
        {
            byte[] sector = new byte[512];
            sector[510] = 0x55;
            sector[511] = 0xAA;
            sector[446 + 4] = 0x06;     // FAT16
            sector[446 + 5] = 15;       // end head 15 -> 16 heads
            sector[446 + 6] = 63;       // end sector 63
            Assert.That(DiskImageConverter.TryDetectGeometry(sector, 16 * 63 * 300, out DiskGeometry g), Is.True);
            Assert.That(g.ToString(), Is.EqualTo("300/16/63"));
        }

        // ---------------------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------------------

        // The geometry the hand-built footer records (PcImageFactory's 20 MB AT disk).
        private const int TestCylinders = 615;
        private const int TestHeads = 4;
        private const int TestSectors = 17;
        private static readonly long TestDiskBytes = PcImageFactory.CreateBlankHardDisk(PcHddPreset.At20MB).Length;

        private static DiskImageVolumeProvider NewProvider()
        {
            DriverFactory factory = new DriverFactory();
            factory.Register(new PcFloppyDriverProvider());
            return new DiskImageVolumeProvider(factory);
        }

        private string MakeDisk(string name, bool vhd)
        {
            byte[] disk = PcImageFactory.CreateBlankHardDisk(PcHddPreset.At20MB, "TESTHDD");
            string path = Path.Combine(_dir, name);
            using FileStream fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            fs.Write(disk, 0, disk.Length);
            if (vhd)
            {
                byte[] footer = HandBuiltFooter(disk.Length, TestCylinders, TestHeads, TestSectors);
                fs.Write(footer, 0, footer.Length);
            }
            return path;
        }

        // The footer RetroCore's PC/AT code writes, built here field by field from the spec.
        private static byte[] HandBuiltFooter(long diskBytes, int cylinders, int heads, int sectors)
        {
            byte[] f = new byte[512];
            System.Text.Encoding.ASCII.GetBytes("conectix").CopyTo(f, 0);
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(8), 0x00000002);
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(12), 0x00010000);
            BinaryPrimitives.WriteUInt64BigEndian(f.AsSpan(16), ulong.MaxValue);
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(24), 600000000);
            System.Text.Encoding.ASCII.GetBytes("rcl ").CopyTo(f, 28);
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(32), 0x00010000);
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(36), 0x5769326B);
            BinaryPrimitives.WriteUInt64BigEndian(f.AsSpan(40), (ulong)diskBytes);
            BinaryPrimitives.WriteUInt64BigEndian(f.AsSpan(48), (ulong)diskBytes);
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(56), (ushort)cylinders);
            f[58] = (byte)heads;
            f[59] = (byte)sectors;
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(60), 2);
            Guid.NewGuid().TryWriteBytes(f.AsSpan(68, 16), bigEndian: true, out _);
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(64), SpecChecksum(f));
            return f;
        }

        // The spec's checksum, written out again here so the test does not use the code it checks.
        private static uint SpecChecksum(byte[] footer)
        {
            uint sum = 0;
            for (int i = 0; i < footer.Length; i++)
            {
                if (i < 64 || i >= 68)
                {
                    sum += footer[i];
                }
            }
            return ~sum;
        }

        private static void PatchFooterByte(string path, int offset, byte value, bool fixChecksum)
        {
            byte[] all = File.ReadAllBytes(path);
            int start = all.Length - 512;
            all[start + offset] = value;
            if (fixChecksum)
            {
                byte[] f = new byte[512];
                Array.Copy(all, start, f, 0, 512);
                BinaryPrimitives.WriteUInt32BigEndian(all.AsSpan(start + 64), SpecChecksum(f));
            }
            File.WriteAllBytes(path, all);
        }

        private static byte[] Tail(string path, int count)
        {
            byte[] all = File.ReadAllBytes(path);
            byte[] t = new byte[count];
            Array.Copy(all, all.Length - count, t, 0, count);
            return t;
        }

        private static byte[] Pattern(int length, int seed)
        {
            byte[] b = new byte[length];
            for (int i = 0; i < length; i++)
            {
                b[i] = (byte)(i * 31 + seed);
            }
            return b;
        }
    }
}
