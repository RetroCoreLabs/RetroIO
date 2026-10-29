//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Ndfs.Creation;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tests
{
    /// <summary>
    /// Tests for the SINTRAN segment lister / exporter / importer. Mirrors norskdata-ndfs
    /// <c>ndfs-c/tests/test_sintran_segments.c</c>: a synthetic pack with a contiguous SEGFIL0:DATA holding
    /// a self-describing segment table, a DPIT image copy whose kernel tables DBLST / ABLPAGE / MASSNO
    /// point at the real disk pages of SEGFIL0 and of a second contiguous segment file SEGFIL1:DATA, an
    /// RTFIL:DATA with the name records, and a symbol list with BLST / ABLPA / MASSN / INIBU. Shapes
    /// follow the K and L packs measured on 2026-10-29.
    /// </summary>
    [TestFixture]
    public class NdfsSegmentsTests
    {
        private const int Page = 2048;
        private const int TablePage = 5;
        private const int Segfil0Pages = 64;
        private const int Segfil1Pages = 16;

        // The L07 symbol values.
        private const int BlstSym = 0x813;     // 004023
        private const int AblpaSym = 0x931;    // 004461
        private const int MassnSym = 0x921;    // 004441

        private const int SegIdpit = 0x24;     // 44B
        private const int SegSdpit = 0x2B;     // 53B
        private const int SegFs = 0x06;        // a code segment on segment file 0
        private const int SegUser = 0x41;      // 101B: a user segment on segment file 1
        private const int SegMemory = 0x13;    // 23B: S3DPIT, memory only
        private const int SegSave4 = 0x2C;     // 54B: a save segment on segment file 4 (absolute)
        private const int SegImage = 0x02;     // S3IMAGE: file 0, MADR 0, real pages

        private const int IdpitMadr = 20, SdpitMadr = 40, FsMadr = 10, UserMadr = 2, Save4Madr = 30;
        private const int DpitLogad = 0x42;    // base page 2 = 4000B

        private string _testDir = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "RetroIO_NdfsSegments_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_testDir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_testDir, true); } catch (IOException) { }
        }

        private static uint FirstBlockOf(NdfsFileSystem fs, string path)
        {
            var entries = fs.GetObjectEntries();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                string full = $"{e.UserName}/{e.ObjectName}:{(e.Type ?? string.Empty).TrimEnd('\x27', ' ')}";
                if (string.Equals(full, path, StringComparison.OrdinalIgnoreCase)) return e.FilePointer!.BlockID;
            }
            Assert.Fail($"{path} not found");
            return 0;
        }

        /// <summary>
        /// Builds the pack; returns its path and where the two segment files landed.
        /// </summary>
        private string BuildPack(bool withSymbols, out uint sf0, out uint sf1)
        {
            string path = NdfsCpuIdentityTests.CreateSmd75(_testDir, "segs.img", 100, 0x8000);
            var fs = NdfsCpuIdentityTests.Open(path, false, out FileStream stream, out RetroIO.Storage.Local.BlockDevices.GenericBlockDevice device);
            using (stream)
            {
                // Both segment files contiguous, as SINTRAN requires (@ALLOCATE-FILE).
                fs.CreateContiguousFile("SYSTEM/SEGFIL0:DATA", Segfil0Pages);
                fs.CreateContiguousFile("SYSTEM/SEGFIL1:DATA", Segfil1Pages);
                sf0 = FirstBlockOf(fs, "SYSTEM/SEGFIL0:DATA");
                sf1 = FirstBlockOf(fs, "SYSTEM/SEGFIL1:DATA");

                var seg = new byte[Segfil0Pages * Page];
                var seg1 = new byte[Segfil1Pages * Page];

                // Segment table: self-describing entry 027 (S3ISGT on the real packs), filler, then the
                // real entries; entry 2 is S3IMAGE.
                NdfsCpuIdentityTests.PutSegEntry(seg, TablePage, 0x17, 0, 1, TablePage, 1, 0xE000);
                for (int n = 3; n <= 60; n++)
                {
                    if (n == SegIdpit || n == SegSdpit || n == SegFs || n == SegMemory || n == SegSave4 || n == SegImage || n == 0x17) continue;
                    NdfsCpuIdentityTests.PutSegEntry(seg, TablePage, n, 0, 1, 1, 1, 0xE000);
                }
                NdfsCpuIdentityTests.PutSegEntry(seg, TablePage, SegFs, 0x16 /* 000026 */, 4, FsMadr, 0x49, 0xE400);
                NdfsCpuIdentityTests.PutSegEntry(seg, TablePage, SegMemory, DpitLogad, 0, 0, 0x49, 0xE400);
                NdfsCpuIdentityTests.PutSegEntry(seg, TablePage, SegImage, 0, 2, 0, 0x49, 0xE400);
                NdfsCpuIdentityTests.PutSegEntry(seg, TablePage, SegIdpit, DpitLogad, 4, IdpitMadr, 0x49, 0xE400);
                NdfsCpuIdentityTests.PutSegEntry(seg, TablePage, SegSdpit, DpitLogad, 4, SdpitMadr, 0x49, 0xE400);
                NdfsCpuIdentityTests.PutSegEntry(seg, TablePage, SegSave4, 0x18 /* 000030 */, 2, Save4Madr, 0x8049 /* 100111 */, 0xE400);
                NdfsCpuIdentityTests.PutSegEntry(seg, TablePage, SegUser, 0x30 /* 000060 */, 3, UserMadr, 0x2049 /* 020111 */, 0xE400);

                // Recognisable content: every page of a segment carries its number and page index.
                for (int p = 0; p < 4; p++) NdfsCpuIdentityTests.PutWord(seg, (long)(FsMadr + p) * Page, 0x5300 | p);
                for (int p = 0; p < 2; p++) NdfsCpuIdentityTests.PutWord(seg, (long)p * Page, 0x0200 | p);   // S3IMAGE at MADR 0
                for (int p = 0; p < 4; p++) NdfsCpuIdentityTests.PutWord(seg, (long)(IdpitMadr + p) * Page, 0x4400 | p);
                for (int p = 0; p < 4; p++) NdfsCpuIdentityTests.PutWord(seg, (long)(SdpitMadr + p) * Page, 0x5300 | p);
                for (int p = 0; p < 3; p++) NdfsCpuIdentityTests.PutWord(seg1, (long)(UserMadr + p) * Page, 0xA100 | p);

                // The kernel tables in the DPIT image copy (base 4000B): DBLST in blocks, 2 per page.
                long baseOff = (long)IdpitMadr * Page;
                uint dblst0 = sf0 * 2, dblst1 = sf1 * 2;
                NdfsCpuIdentityTests.PutWord(seg, baseOff + (BlstSym + 0 - 0x800) * 2, (int)(dblst0 >> 16));
                NdfsCpuIdentityTests.PutWord(seg, baseOff + (BlstSym + 1 - 0x800) * 2, (int)(dblst0 & 0xFFFF));
                NdfsCpuIdentityTests.PutWord(seg, baseOff + (BlstSym + 2 - 0x800) * 2, (int)(dblst1 >> 16));
                NdfsCpuIdentityTests.PutWord(seg, baseOff + (BlstSym + 3 - 0x800) * 2, (int)(dblst1 & 0xFFFF));
                // segment file 2: defined but on another disk unit
                NdfsCpuIdentityTests.PutWord(seg, baseOff + (BlstSym + 5 - 0x800) * 2, 2000);
                NdfsCpuIdentityTests.PutWord(seg, baseOff + (AblpaSym + 0 - 0x800) * 2, 2);
                NdfsCpuIdentityTests.PutWord(seg, baseOff + (AblpaSym + 1 - 0x800) * 2, 2);
                NdfsCpuIdentityTests.PutWord(seg, baseOff + (MassnSym + 0 - 0x800) * 2, 0x240 /* 01100 */);
                NdfsCpuIdentityTests.PutWord(seg, baseOff + (MassnSym + 1 - 0x800) * 2, 0x240);
                NdfsCpuIdentityTests.PutWord(seg, baseOff + (MassnSym + 2 - 0x800) * 2, 0x340 /* 01500 */);
                NdfsCpuIdentityTests.PutWord(seg, baseOff + (0x829 - 0x800) * 2, 100);                      // SYSNO, for show

                fs.WriteFile("SYSTEM/SEGFIL0:DATA", seg);
                fs.WriteFile("SYSTEM/SEGFIL1:DATA", seg1);

                // Segment file 4 is absolute: disk page 1 + MADR. Page 31 and 32 of a fresh 75 MB pack
                // are free, so writing them raw harms nothing.
                var page = new byte[Page];
                NdfsCpuIdentityTests.PutWord(page, 0, 0x5400);
                fs.WriteDiskPage((uint)(1 + Save4Madr), page);
                NdfsCpuIdentityTests.PutWord(page, 0, 0x5401);
                fs.WriteDiskPage((uint)(2 + Save4Madr), page);

                var rtfil = new byte[Page];
                NdfsCpuIdentityTests.PutNameRecord(rtfil, 0 * 16, "S3IMAGE", SegImage);
                NdfsCpuIdentityTests.PutNameRecord(rtfil, 1 * 16, "S3FS", SegFs);
                NdfsCpuIdentityTests.PutNameRecord(rtfil, 2 * 16, "S3DPIT", SegMemory);
                NdfsCpuIdentityTests.PutNameRecord(rtfil, 3 * 16, "S3IDPIT", SegIdpit);
                NdfsCpuIdentityTests.PutNameRecord(rtfil, 4 * 16, "S3SDPIT", SegSdpit);
                NdfsCpuIdentityTests.PutNameRecord(rtfil, 5 * 16, "S3SPRMA", SegSave4);
                NdfsCpuIdentityTests.PutNameRecord(rtfil, 6 * 16, "USERSEG", SegUser);
                fs.WriteFile("SYSTEM/RTFIL:DATA", rtfil);

                if (withSymbols)
                {
                    NdfsCpuIdentityTests.WriteTextFile(fs, "SYSTEM/SYMBOL-1-LIST:SYMB",
                        "BLST=004023\r\nDBLST=004023\r\nMASSN=004441\r\nMASSU=004451\r\nABLPA=004461\r\nSYSNO=004051\r\nINIBU=074123\r\n");
                }
                device.Flush();
            }
            return path;
        }

        private static int Word(byte[] d, int o) => (d[o] << 8) | d[o + 1];

        [Test]
        public void CollectNames_FirstRecordWins()
        {
            var buf = new byte[128];
            NdfsCpuIdentityTests.PutNameRecord(buf, 0, "S3FS", SegFs);
            NdfsCpuIdentityTests.PutNameRecord(buf, 16, "S3SDPIT", SegSdpit);
            NdfsCpuIdentityTests.PutNameRecord(buf, 32, "S3SDPIT", SegSave4);     // a second record for a name: first wins
            var names = SintranCpuIdentity.CollectSegmentNames(buf);
            Assert.That(names.Count, Is.EqualTo(3));
            Assert.That(names[SegFs], Is.EqualTo("S3FS"));
            Assert.That(names[SegSdpit], Is.EqualTo("S3SDPIT"));
            Assert.That(names[SegSave4], Is.EqualTo("S3SDPIT"));
            Assert.That(names.ContainsKey(1), Is.False);
        }

        [Test]
        public void RawPageApi_RoundTripAndBounds()
        {
            string path = Path.Combine(_testDir, "floppy.img");
            NdfsImageCreator.CreateImageFromTemplate(path, NdfsImageTemplate.Floppy12MB, "FLOPPY");
            var fs = NdfsCpuIdentityTests.Open(path, false, out FileStream stream);
            using (stream)
            {
                uint total = fs.TotalPages;
                Assert.That(total, Is.GreaterThan(100u));   // the floppy template, whatever its exact size
                var page = new byte[Page];
                Array.Fill(page, (byte)0x5A);
                fs.WriteDiskPage(100, page);
                Assert.That(fs.ReadDiskPage(100), Is.EqualTo(page));
                Assert.Throws<ArgumentOutOfRangeException>(() => fs.ReadDiskPage(total));
                Assert.Throws<ArgumentOutOfRangeException>(() => fs.WriteDiskPage(total, page));
                Assert.Throws<ArgumentException>(() => fs.WriteDiskPage(100, new byte[5]));
            }
        }

        [Test]
        public void ListSegments_FromKernelTables()
        {
            string path = BuildPack(true, out uint sf0, out uint sf1);
            var fs = NdfsCpuIdentityTests.Open(path, true, out FileStream stream);
            using (stream)
            {
                SintranSegmentList l = fs.ListSintranSegments();
                Assert.That(l.Version, Is.EqualTo('L'));
                Assert.That(l.SegmentTablePage, Is.EqualTo(TablePage));
                Assert.That(l.HaveSymbols, Is.True);
                Assert.That(l.NamesPath, Is.EqualTo("SYSTEM/RTFIL:DATA"));

                // Segment files from the kernel tables.
                Assert.That(l.SegmentFiles[0].Defined, Is.True);
                Assert.That(l.SegmentFiles[0].BasePage, Is.EqualTo((long)sf0));
                Assert.That(l.SegmentFiles[0].FilePath, Is.EqualTo("SYSTEM/SEGFIL0:DATA"));
                Assert.That(l.SegmentFiles[1].Defined, Is.True);
                Assert.That(l.SegmentFiles[1].BasePage, Is.EqualTo((long)sf1));
                Assert.That(l.SegmentFiles[1].FilePath, Is.EqualTo("SYSTEM/SEGFIL1:DATA"));
                Assert.That(l.SegmentFiles[2].Defined, Is.True);
                Assert.That(l.SegmentFiles[2].OnThisDisk, Is.False);
                Assert.That(l.SegmentFiles[3].Defined, Is.False);
                Assert.That(l.SegmentFiles[4].Defined, Is.True);
                Assert.That(l.SegmentFiles[4].BasePage, Is.EqualTo(1L));

                // The segments.
                int i = NdfsFileSystem.FindSintranSegment(l, "S3FS");
                Assert.That(i, Is.GreaterThanOrEqualTo(0));
                Assert.That(l.Segments[i].Number, Is.EqualTo(SegFs));
                Assert.That(l.Segments[i].DiskPage, Is.EqualTo((long)sf0 + 10));
                Assert.That(l.Segments[i].Segle, Is.EqualTo(4));
                Assert.That(l.Segments[i].SegmentFile, Is.EqualTo(0));
                i = NdfsFileSystem.FindSintranSegment(l, "userseg");                 // case-insensitive
                Assert.That(i, Is.GreaterThanOrEqualTo(0));
                Assert.That(l.Segments[i].SegmentFile, Is.EqualTo(1));
                Assert.That(l.Segments[i].DiskPage, Is.EqualTo((long)sf1 + 2));
                i = NdfsFileSystem.FindSintranSegment(l, "S3DPIT");
                Assert.That(i, Is.GreaterThanOrEqualTo(0));
                Assert.That(l.Segments[i].MemoryOnly, Is.True);
                Assert.That(l.Segments[i].DiskPage, Is.EqualTo(-1L));
                i = NdfsFileSystem.FindSintranSegment(l, "S3IMAGE");                 // MADR 0 is the first page of file 0
                Assert.That(i, Is.GreaterThanOrEqualTo(0));
                Assert.That(l.Segments[i].MemoryOnly, Is.False);
                Assert.That(l.Segments[i].DiskPage, Is.EqualTo((long)sf0));
                i = NdfsFileSystem.FindSintranSegment(l, "S3SPRMA");
                Assert.That(i, Is.GreaterThanOrEqualTo(0));
                Assert.That(l.Segments[i].SegmentFile, Is.EqualTo(4));
                Assert.That(l.Segments[i].DiskPage, Is.EqualTo(31L));
                // By number: octal with B, decimal without.
                Assert.That(NdfsFileSystem.FindSintranSegment(l, "53B"), Is.EqualTo(NdfsFileSystem.FindSintranSegment(l, "S3SDPIT")));
                Assert.That(NdfsFileSystem.FindSintranSegment(l, "43"), Is.EqualTo(NdfsFileSystem.FindSintranSegment(l, "S3SDPIT")));
                Assert.That(NdfsFileSystem.FindSintranSegment(l, "NOSUCH"), Is.EqualTo(-1));
                Assert.That(NdfsFileSystem.FindSintranSegment(l, "60B"), Is.GreaterThanOrEqualTo(0));   // filler entry 060B: listed, unnamed
            }
        }

        [Test]
        public void ListSegments_WithoutSymbolsFallsBackToSegfil0File()
        {
            string path = BuildPack(false, out uint sf0, out _);
            var fs = NdfsCpuIdentityTests.Open(path, true, out FileStream stream);
            using (stream)
            {
                SintranSegmentList l = fs.ListSintranSegments();
                Assert.That(l.HaveSymbols, Is.False);
                Assert.That(l.Version, Is.EqualTo('?'));
                Assert.That(l.SegmentFiles[0].Defined, Is.True);
                Assert.That(l.SegmentFiles[0].BasePage, Is.EqualTo((long)sf0));
                Assert.That(l.SegmentFiles[1].Defined, Is.False);
                Assert.That(l.SegmentFiles[4].Defined, Is.False);
                int i = NdfsFileSystem.FindSintranSegment(l, "S3FS");
                Assert.That(l.Segments[i].DiskPage, Is.EqualTo((long)sf0 + 10));
                i = NdfsFileSystem.FindSintranSegment(l, "USERSEG");
                Assert.That(l.Segments[i].DiskPage, Is.EqualTo(-1L));    // segment file 1 unknown
            }
        }

        [Test]
        public void Export_MatchesFileBytes()
        {
            string path = BuildPack(true, out _, out _);
            var fs = NdfsCpuIdentityTests.Open(path, true, out FileStream stream);
            using (stream)
            {
                SintranSegmentList l = fs.ListSintranSegments();

                // S3FS: 4 pages at SEGFIL0 page 10 - must equal the file's bytes.
                int i = NdfsFileSystem.FindSintranSegment(l, "S3FS");
                byte[] data = fs.ExportSintranSegment(l, i);
                Assert.That(data.Length, Is.EqualTo(4 * Page));
                byte[] file = fs.ReadFile("SYSTEM/SEGFIL0:DATA");
                for (int k = 0; k < data.Length; k++) Assert.That(data[k], Is.EqualTo(file[10 * Page + k]));
                Assert.That(Word(data, 2 * Page), Is.EqualTo(0x5302));

                // USERSEG on segment file 1.
                i = NdfsFileSystem.FindSintranSegment(l, "USERSEG");
                data = fs.ExportSintranSegment(l, i);
                Assert.That(data.Length, Is.EqualTo(3 * Page));
                Assert.That(Word(data, Page), Is.EqualTo(0xA101));

                // The absolute save segment on file 4.
                i = NdfsFileSystem.FindSintranSegment(l, "S3SPRMA");
                data = fs.ExportSintranSegment(l, i);
                Assert.That(Word(data, 0), Is.EqualTo(0x5400));
                Assert.That(Word(data, Page), Is.EqualTo(0x5401));

                // S3IMAGE at MADR 0: the first pages of SEGFIL0.
                i = NdfsFileSystem.FindSintranSegment(l, "S3IMAGE");
                data = fs.ExportSintranSegment(l, i);
                Assert.That(Word(data, Page), Is.EqualTo(0x0201));

                // Memory-only: refused.
                i = NdfsFileSystem.FindSintranSegment(l, "S3DPIT");
                Assert.Throws<SintranCpuIdentityException>(() => fs.ExportSintranSegment(l, i));
            }
        }

        [Test]
        public void Import_RoundTripAndRefusals()
        {
            string path = BuildPack(true, out _, out _);
            var fs = NdfsCpuIdentityTests.Open(path, false, out FileStream stream);
            using (stream)
            {
                SintranSegmentList l = fs.ListSintranSegments();
                int i = NdfsFileSystem.FindSintranSegment(l, "S3FS");

                // Modify the export and write it back; the file view must show it.
                byte[] data = fs.ExportSintranSegment(l, i);
                data[100] = 0xDE;
                data[101] = 0xAD;
                data[3 * Page + 7] = 0x77;
                fs.ImportSintranSegment(l, i, data);
                Assert.That(fs.ExportSintranSegment(l, i), Is.EqualTo(data));
                byte[] back = fs.ReadFile("SYSTEM/SEGFIL0:DATA");
                Assert.That(back[10 * Page + 100], Is.EqualTo(0xDE));
                Assert.That(back[13 * Page + 7], Is.EqualTo(0x77));

                // A shorter import keeps the tail of the last page.
                data[0] = 0x11;
                data[1] = 0x22;
                fs.ImportSintranSegment(l, i, new ReadOnlySpan<byte>(data, 0, 10));
                back = fs.ExportSintranSegment(l, i);
                Assert.That(back[0], Is.EqualTo(0x11));
                Assert.That(back[100], Is.EqualTo(0xDE));     // untouched tail of page 0

                // Too long: refused, nothing written.
                var big = new byte[5 * Page];
                Array.Fill(big, (byte)0xEE);
                Assert.Throws<ArgumentOutOfRangeException>(() => fs.ImportSintranSegment(l, i, big));
                Assert.That(fs.ExportSintranSegment(l, i)[0], Is.EqualTo(0x11));

                // Memory-only: refused.
                i = NdfsFileSystem.FindSintranSegment(l, "S3DPIT");
                Assert.Throws<SintranCpuIdentityException>(() => fs.ImportSintranSegment(l, i, new ReadOnlySpan<byte>(big, 0, 10)));
            }
        }
    }
}
