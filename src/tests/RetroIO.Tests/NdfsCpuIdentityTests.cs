//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Creation;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tests
{
    /// <summary>
    /// Tests for the SINTRAN CPU number / CPU type reader and writer (<see cref="SintranCpuIdentity"/>
    /// and the NdfsFileSystem entry points). Mirrors norskdata-ndfs <c>ndfs-c/tests/test_sintran_identity.c</c>
    /// case for case: a synthetic system pack is built with the image creator (both symbol lists with
    /// even parity, an RTFIL:DATA with the RT-loader DEFINED SEGMENT NAME records, a SEGFIL0:DATA with a
    /// self-describing segment table and the two DPIT copies, and for the K shape a SINTRAN:DATA holding
    /// the save copy on segment file 4). One case uses segment numbers that are NOT the real 44B/53B,
    /// so the lookup is proven to go by name. Shapes follow the packs measured on 2026-10-29
    /// (NDInsight chapter SINTRAN/OS/28). The real-pack cases run on a COPY of a pack named by an
    /// environment variable, so no image of the user's is ever written.
    /// </summary>
    [TestFixture]
    public class NdfsCpuIdentityTests
    {
        private const int Page = 2048;

        // Fixture shape: logical base 4000B (LOGAD low bits = page 2), 35 pages per copy as on the K
        // pack so the K FCPUN at 105034B (page 32 of the copy) is inside. SEGFIL0 is 80 pages: table
        // at page 5, image copy at page 20. L shape: save copy at page 44 (fits 44+35 = 79). K shape:
        // save copy at page 10 of SINTRAN:DATA (segment file 4).
        private const int TablePage = 5;
        private const int ImageMadr = 20;
        private const int SaveMadrL = 44;
        private const int SaveMadrK = 10;
        private const int Segle = 35;
        private const int Logad = 0x42;        // 000102 octal
        private const int SegfilPages = 80;

        private const int Sysno = 0x829;       // 004051 octal
        private const int Hwinf = 0x82A;       // 004052
        private const int Revle = 0x82F;       // 004057
        private const int FcpunL = 0x90C;      // 004414
        private const int FcpunK = 0x8A1C;     // 105034

        private const string Sym1K = "SYSNO=004051\r\nHWINF=004052\r\nREVLE=004057\r\nINIBU=067172\r\n";
        private const string Sym2K = "FCPUN=105034\r\n33CPU=000156\r\n33CPN=000144\r\nSYSNO=004051\r\n";
        private const string Sym2KNoFcpun = "33CPU=000156\r\n33CPN=000144\r\nSYSNO=004051\r\n";
        private const string Sym1L = "FCPUN=004414\r\nREVLE=004057\r\nSYSNO=004051\r\nHWINF=004052\r\nINIBU=074123\r\n";
        private const string Sym2L = "GCPUN=061434\r\n33CPU=000000\r\n33CPN=000000\r\n";

        private sealed class Shape
        {
            public bool KShape;              // save copy on segment file 4 in SINTRAN:DATA
            public bool WithFcpunSymbol = true;
            public int ImageSegno = 0x24;    // 44B
            public int SaveSegno = 0x2B;     // 53B
            public int SintranDataPages;     // K only; too short = refuse
            public bool WithNameRecords = true;
        }

        private static Shape LShape() => new Shape { KShape = false };
        private static Shape KShape() => new Shape { KShape = true, SintranDataPages = 48 };

        private string _testDir = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "RetroIO_NdfsCpuIdentity_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_testDir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_testDir, true); } catch (IOException) { }
        }

        // ---- fixture builders --------------------------------------------------------------

        internal static void PutWord(byte[] b, long off, int val)
        {
            b[off] = (byte)(val >> 8);
            b[off + 1] = (byte)(val & 0xFF);
        }

        internal static void PutSegEntry(byte[] b, int page, int n, int logad, int segle, int madr, int flag, int sgsta)
        {
            long o = (long)page * Page + n * 16;
            PutWord(b, o + 4, logad);
            PutWord(b, o + 6, segle);
            PutWord(b, o + 8, madr);
            PutWord(b, o + 10, flag);
            PutWord(b, o + 12, sgsta);
        }

        /// <summary>
        /// Packs a name the way the RT-loader does: SIXBIT, right-justified base 64.
        /// </summary>
        internal static void PutNameRecord(byte[] b, long off, string name, int segno)
        {
            ulong n = 0;
            for (int i = 0; i < name.Length; i++) n = (n << 6) | ((ulong)name[i] & 0x3F);
            PutWord(b, off + 0, (int)((n >> 32) & 0xFFFF));
            PutWord(b, off + 2, (int)((n >> 16) & 0xFFFF));
            PutWord(b, off + 4, (int)(n & 0xFFFF));
            PutWord(b, off + 6, segno);
            PutWord(b, off + 8, 0);
            PutWord(b, off + 10, 0x15);   // 25B
            PutWord(b, off + 12, segno);
            PutWord(b, off + 14, 0);
        }

        private static void PutDpitWords(byte[] buf, long baseOff, int fcpunAddr, int sysno, int type, int revle, int fcpun)
        {
            PutWord(buf, baseOff + (Sysno - 0x800) * 2, sysno);
            PutWord(buf, baseOff + (Hwinf + 2 - 0x800) * 2, type);
            PutWord(buf, baseOff + (Revle - 0x800) * 2, revle);
            PutWord(buf, baseOff + (fcpunAddr - 0x800) * 2, fcpun);
        }

        internal static void WriteTextFile(NdfsFileSystem fs, string path, string text, bool parity = true)
        {
            byte[] data = Encoding.ASCII.GetBytes(text);
            if (parity) SintranCpuIdentity.SetParity(data);
            fs.WriteFile(path, data);
        }

        /// <summary>
        /// Opens an image file and mounts it. GenericBlockDevice buffers writes in memory and writes them
        /// back on Flush() or Dispose(), so a writer must call <see cref="GenericBlockDevice.Flush"/> on
        /// <paramref name="device"/> before the stream goes away. The library's own write entry points
        /// flush themselves; raw <c>WriteFile</c> calls in a fixture do not.
        /// </summary>
        internal static NdfsFileSystem Open(string imagePath, bool readOnly, out FileStream stream, out GenericBlockDevice device)
        {
            stream = new FileStream(imagePath, FileMode.Open, readOnly ? FileAccess.Read : FileAccess.ReadWrite);
            var descriptor = NdfsBlockDeviceDescriptor.Create(stream.Length);
            device = new GenericBlockDevice(descriptor);
            device.Mount(stream, readOnly);
            var fs = new NdfsFileSystem();
            fs.Initialize(device);
            return fs;
        }

        internal static NdfsFileSystem Open(string imagePath, bool readOnly, out FileStream stream)
        {
            return Open(imagePath, readOnly, out stream, out _);
        }

        internal static string CreateSmd75(string dir, string name, ushort systemNumber, ushort flagWord)
        {
            string path = Path.Combine(dir, name);
            NdfsImageCreator.CreateImage(path, new NdfsImageCreationOptions
            {
                Template = NdfsImageTemplate.Smd75MB,
                DirectoryName = "PACK-ONE",
                IncludeExtendedInfo = true,
                SystemNumber = systemNumber,
                FlagWord = flagWord,          // "entered by system 100", as every real pack here
            });
            return path;
        }

        private string BuildPack(Shape sh)
        {
            string path = CreateSmd75(_testDir, "pack.img", 100, 0x8000);
            var fs = Open(path, false, out FileStream stream, out GenericBlockDevice device);
            using (stream)
            {
                int fcpunAddr = sh.KShape ? FcpunK : FcpunL;

                // Segment table: entry 2 self-describing, filler so the page has >= 40 good entries,
                // then the two DPIT copies at the numbers the name records will give.
                var seg = new byte[SegfilPages * Page];
                PutSegEntry(seg, TablePage, 2, 0, 1, TablePage, 1, 0xE000);
                for (int n = 3; n <= 60; n++)
                {
                    if (n != sh.ImageSegno && n != sh.SaveSegno) PutSegEntry(seg, TablePage, n, 0, 1, 1, 1, 0xE000);
                }
                PutSegEntry(seg, TablePage, sh.ImageSegno, Logad, Segle, ImageMadr, 0x49 /* 000111 */, 0xE400 /* 162000 */);
                PutSegEntry(seg, TablePage, sh.SaveSegno, Logad, Segle,
                    sh.KShape ? SaveMadrK : SaveMadrL, sh.KShape ? 0x8049 /* 100111 */ : 0x49, 0xE400);

                PutDpitWords(seg, (long)ImageMadr * Page, fcpunAddr, 21056, 9883, 0x1080 /* 010200 */, 21056);
                if (!sh.KShape) PutDpitWords(seg, (long)SaveMadrL * Page, fcpunAddr, 21056, 9883, 0x1080, 21056);
                fs.WriteFile("SYSTEM/SEGFIL0:DATA", seg);

                if (sh.KShape)
                {
                    var sd = new byte[sh.SintranDataPages * Page];
                    if (sd.Length >= (SaveMadrK + Segle) * Page) PutDpitWords(sd, (long)SaveMadrK * Page, fcpunAddr, 21056, 9883, 0x1080, 21056);
                    fs.WriteFile("SYSTEM/SINTRAN:DATA", sd);
                }

                if (sh.WithNameRecords)
                {
                    // RTFIL:DATA as on the real packs: records at page 0 and 1, other names around.
                    var rtfil = new byte[Page * 2];
                    PutNameRecord(rtfil, 0 * 16, "S3IMAGE", 0x02);
                    PutNameRecord(rtfil, 1 * 16, "S3RTFIL", 0x08);
                    PutNameRecord(rtfil, 2 * 16, "S3DPIT", 0x13);
                    PutNameRecord(rtfil, 3 * 16, "S3ISGT", 0x17);
                    PutNameRecord(rtfil, Page + 0 * 16, "S3PATCH", 0x23);
                    PutNameRecord(rtfil, Page + 1 * 16, "S3IDPIT", sh.ImageSegno);
                    PutNameRecord(rtfil, Page + 2 * 16, "S3SDPIT", sh.SaveSegno);
                    fs.WriteFile("SYSTEM/RTFIL:DATA", rtfil);
                }

                WriteTextFile(fs, "SYSTEM/SYMBOL-1-LIST:SYMB", sh.KShape ? Sym1K : Sym1L);
                WriteTextFile(fs, "SYSTEM/SYMBOL-2-LIST:SYMB", sh.KShape ? (sh.WithFcpunSymbol ? Sym2K : Sym2KNoFcpun) : Sym2L);
                device.Flush();
            }
            return path;
        }

        private static int IndexOf(byte[] hay, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= hay.Length; i++)
            {
                bool same = true;
                for (int k = 0; k < needle.Length; k++)
                {
                    if (hay[i + k] != needle[k]) { same = false; break; }
                }
                if (same) return i;
            }
            return -1;
        }

        // ---- Pure helpers ------------------------------------------------------------------

        [Test]
        public void FindSymbol_LineStartOnlyAndOctal()
        {
            byte[] txt = Encoding.ASCII.GetBytes("SYSNO=004051\r\n   FCPUN=004414\r\n33CPN=000000\r\nX=7\n");
            Assert.That(SintranCpuIdentity.FindSymbol(txt, "SYSNO", out int v, out int off, out int w), Is.True);
            Assert.That(v, Is.EqualTo(0x829));
            Assert.That(off, Is.EqualTo(6));
            Assert.That(w, Is.EqualTo(6));
            Assert.That(SintranCpuIdentity.FindSymbol(txt, "FCPUN", out v, out _, out _), Is.True);
            Assert.That(v, Is.EqualTo(0x90C));
            Assert.That(SintranCpuIdentity.FindSymbol(txt, "33CPN", out v, out _, out _), Is.True);
            Assert.That(v, Is.EqualTo(0));
            // A name that is only the tail of another ("CPN" inside "33CPN") must not match.
            Assert.That(SintranCpuIdentity.FindSymbol(txt, "CPN", out _, out _, out _), Is.False);
            Assert.That(SintranCpuIdentity.FindSymbol(txt, "HWINF", out _, out _, out _), Is.False);
        }

        [Test]
        public void DecodePackedName_SixbitRightJustified()
        {
            // The two worked examples of NDInsight chapter 22 section 6.5.
            Assert.That(SintranCpuIdentity.DecodePackedName(0x0004, 0xF325, 0x31D4), Is.EqualTo("S3ISGT"));
            Assert.That(SintranCpuIdentity.DecodePackedName(0x013C, 0xD250, 0x10C3), Is.EqualTo("S3RTACC"));
            Assert.That(SintranCpuIdentity.DecodePackedName(0, 0, 0), Is.Null);
            // A code outside A-Z / 0-9 (0x3F) is not a name.
            Assert.That(SintranCpuIdentity.DecodePackedName(0, 0, 0x003F), Is.Null);
        }

        [Test]
        public void FindSegmentByName_TypeMustBe25B()
        {
            var buf = new byte[256];
            PutNameRecord(buf, 16, "S3IDPIT", 0x24);
            PutNameRecord(buf, 48, "S3SDPIT", 0x2B);
            // A record with the wrong type must be ignored even with the right name.
            PutNameRecord(buf, 80, "S3DPIT", 0x13);
            PutWord(buf, 80 + 10, 0x14);
            Assert.That(SintranCpuIdentity.FindSegmentByName(buf, "S3IDPIT"), Is.EqualTo(0x24));
            Assert.That(SintranCpuIdentity.FindSegmentByName(buf, "S3SDPIT"), Is.EqualTo(0x2B));
            Assert.That(SintranCpuIdentity.FindSegmentByName(buf, "S3DPIT"), Is.EqualTo(-1));
            Assert.That(SintranCpuIdentity.FindSegmentByName(buf, "S3FS"), Is.EqualTo(-1));
        }

        [Test]
        public void SegmentWordOffset_BaseFromLogad()
        {
            // L pack, S3SDPIT: MADR 64, SEGLE 45, LOGAD 000102 -> SYSNO at 64*2048 + 41*2 = 131154.
            Assert.That(SintranCpuIdentity.SegmentWordOffset(0x42, 45, 64, 0x829), Is.EqualTo(131154L));
            Assert.That(SintranCpuIdentity.SegmentWordOffset(0x42, 45, 64, 0x82C), Is.EqualTo(131160L));
            // Below the base or past the end: -1.
            Assert.That(SintranCpuIdentity.SegmentWordOffset(0x42, 45, 64, 0x7FF), Is.EqualTo(-1L));
            Assert.That(SintranCpuIdentity.SegmentWordOffset(0x42, 45, 64, 0x800 + 45 * 1024), Is.EqualTo(-1L));
            // A segment based elsewhere does not contain 4051B.
            Assert.That(SintranCpuIdentity.SegmentWordOffset(0x4C, 45, 64, 0x829), Is.EqualTo(-1L));
        }

        [Test]
        public void SetParity_EvenParityPerByte()
        {
            // '1' = 0x31 has three 1-bits, so it gets bit 7: 0xB1. '0' = 0x30 has two: unchanged.
            byte[] digits = Encoding.ASCII.GetBytes("01");
            SintranCpuIdentity.SetParity(digits);
            Assert.That(digits[0], Is.EqualTo(0x30));
            Assert.That(digits[1], Is.EqualTo(0xB1));
        }

        // ---- Read --------------------------------------------------------------------------

        [Test]
        public void Read_LShape()
        {
            string path = BuildPack(LShape());
            var fs = Open(path, true, out FileStream stream);
            using (stream)
            {
                SintranCpuIdentityInfo id = fs.ReadSintranCpuIdentity();
                Assert.That(id.Version, Is.EqualTo('L'));
                Assert.That(id.Symbols.Sysno, Is.EqualTo(0x829));
                Assert.That(id.Symbols.Fcpun, Is.EqualTo(0x90C));
                Assert.That(id.Symbols.Cpn33, Is.EqualTo(0));
                Assert.That(id.SegmentTablePage, Is.EqualTo(TablePage));
                Assert.That(id.NamesPath, Is.EqualTo("SYSTEM/RTFIL:DATA"));
                Assert.That(id.Copies[0].SegmentNumber, Is.EqualTo(0x24));
                Assert.That(id.Copies[1].SegmentNumber, Is.EqualTo(0x2B));
                Assert.That(id.Copies[0].Present && id.Copies[0].FileFound, Is.True);
                Assert.That(id.Copies[1].Present && id.Copies[1].FileFound, Is.True);
                Assert.That(id.Copies[1].SegmentFile, Is.EqualTo(0));
                Assert.That(id.Copies[1].FilePath, Is.EqualTo("SYSTEM/SEGFIL0:DATA"));
                Assert.That(id.Copies[0].Sysno, Is.EqualTo(21056));
                Assert.That(id.Copies[0].Fcpun, Is.EqualTo(21056));
                Assert.That(id.Copies[0].CpuType, Is.EqualTo(9883));
                Assert.That(id.Copies[0].Revle, Is.EqualTo(0x1080));
                Assert.That(id.Copies[1].Sysno, Is.EqualTo(21056));
                Assert.That(id.DirectoryExtValid, Is.True);
                Assert.That(id.DirectoryEntered, Is.True);
                Assert.That(id.DirectorySystemNumber, Is.EqualTo(100));
            }
        }

        [Test]
        public void Read_KShape_SaveCopyInSintranData()
        {
            string path = BuildPack(KShape());
            var fs = Open(path, true, out FileStream stream);
            using (stream)
            {
                SintranCpuIdentityInfo id = fs.ReadSintranCpuIdentity();
                Assert.That(id.Version, Is.EqualTo('K'));
                Assert.That(id.Symbols.Fcpun, Is.EqualTo(FcpunK));
                Assert.That(id.Symbols.Cpn33, Is.EqualTo(100));
                Assert.That(id.Symbols.Cpu33, Is.EqualTo(110));
                Assert.That(id.Copies[1].SegmentFile, Is.EqualTo(4));
                Assert.That(id.Copies[1].FilePath, Is.EqualTo("SYSTEM/SINTRAN:DATA"));
                Assert.That(id.Copies[1].FileFound, Is.True);
                Assert.That(id.Copies[1].Sysno, Is.EqualTo(21056));
                Assert.That(id.Copies[1].Fcpun, Is.EqualTo(21056));
                Assert.That(id.Copies[1].CpuType, Is.EqualTo(9883));
                Assert.That(id.Copies[0].Fcpun, Is.EqualTo(21056));
            }
        }

        [Test]
        public void Read_SegmentsFoundByNameNotNumber()
        {
            var sh = LShape();
            sh.ImageSegno = 0x25;   // not the real 44B/53B: only the name records say where
            sh.SaveSegno = 0x2C;
            string path = BuildPack(sh);
            var fs = Open(path, true, out FileStream stream);
            using (stream)
            {
                SintranCpuIdentityInfo id = fs.ReadSintranCpuIdentity();
                Assert.That(id.Copies[0].SegmentNumber, Is.EqualTo(0x25));
                Assert.That(id.Copies[1].SegmentNumber, Is.EqualTo(0x2C));
                Assert.That(id.Copies[0].Sysno, Is.EqualTo(21056));
                Assert.That(id.Copies[1].Sysno, Is.EqualTo(21056));
            }
        }

        [Test]
        public void Read_NamesFromSegfilWhenRtfilMissing()
        {
            var sh = LShape();
            sh.WithNameRecords = false;
            string path = BuildPack(sh);
            var fs = Open(path, false, out FileStream stream);
            using (stream)
            {
                // No RTFIL:DATA and no records anywhere: refuse.
                var ex = Assert.Throws<SintranCpuIdentityException>(() => fs.ReadSintranCpuIdentity());
                Assert.That(ex!.Identity, Is.Not.Null);
                Assert.That(ex.Identity!.Copies[0].SegmentNumber, Is.EqualTo(-1));

                // Put the loader table copy inside SEGFIL0 (page 60, as the S3RTFIL segment does).
                byte[] seg = fs.ReadFile("SYSTEM/SEGFIL0:DATA");
                PutNameRecord(seg, 60L * Page, "S3IDPIT", 0x24);
                PutNameRecord(seg, 60L * Page + 16, "S3SDPIT", 0x2B);
                fs.WriteFile("SYSTEM/SEGFIL0:DATA", seg);

                SintranCpuIdentityInfo id = fs.ReadSintranCpuIdentity();
                Assert.That(id.Copies[0].SegmentNumber, Is.EqualTo(0x24));
                Assert.That(id.Copies[1].Sysno, Is.EqualTo(21056));
                Assert.That(id.NamesPath, Is.EqualTo("SEGFIL0 (loader table copy)"));
            }
        }

        [Test]
        public void Read_RefusesWithoutFcpunSymbol()
        {
            var sh = KShape();
            sh.WithFcpunSymbol = false;
            string path = BuildPack(sh);
            var fs = Open(path, true, out FileStream stream);
            using (stream)
            {
                var ex = Assert.Throws<SintranCpuIdentityException>(() => fs.ReadSintranCpuIdentity());
                Assert.That(ex!.Identity!.Symbols.Sysno, Is.EqualTo(0x829));   // what was found is still reported
                Assert.That(ex.Identity.Symbols.Fcpun, Is.EqualTo(-1));
            }
        }

        [Test]
        public void Read_NotASystemPack()
        {
            string path = Path.Combine(_testDir, "floppy.img");
            NdfsImageCreator.CreateImageFromTemplate(path, NdfsImageTemplate.Floppy12MB, "FLOPPY");
            var fs = Open(path, true, out FileStream stream);
            using (stream)
            {
                Assert.Throws<SintranCpuIdentityException>(() => fs.ReadSintranCpuIdentity());
            }
        }

        // ---- Write -------------------------------------------------------------------------

        [Test]
        public void Write_LShape_BothCopiesSymbolsAndStamp()
        {
            string path = BuildPack(LShape());
            var fs = Open(path, false, out FileStream stream);
            using (stream)
            {
                SintranCpuIdentityInfo after = fs.WriteSintranCpuIdentity(21238, 9883, keepStamp: false);
                Assert.That(after.Copies[0].Sysno, Is.EqualTo(21238));
                Assert.That(after.Copies[0].Fcpun, Is.EqualTo(21238));
                Assert.That(after.Copies[1].Sysno, Is.EqualTo(21238));
                Assert.That(after.Copies[1].Fcpun, Is.EqualTo(21238));
                Assert.That(after.Copies[1].CpuType, Is.EqualTo(9883));
                Assert.That(after.Copies[1].Revle, Is.EqualTo(0x1080));     // untouched
                Assert.That(after.Symbols.Cpn33, Is.EqualTo(21238));
                Assert.That(after.Symbols.Cpu33, Is.EqualTo(9883));
                Assert.That(after.DirectoryEntered, Is.False);
                Assert.That(after.DirectorySystemNumber, Is.EqualTo(0));
                Assert.That(after.DirectoryExtValid, Is.True);              // checksum rewritten

                // The symbol list kept its line shape, "33CPN=051366", and the new digits carry even
                // parity like the rest of the file ('1' = 0x31 has three 1-bits, so 0xB1).
                byte[] sym2 = fs.ReadFile("SYSTEM/SYMBOL-2-LIST:SYMB");
                byte[] plain = SintranCpuIdentity.StripParity(sym2);
                int at = IndexOf(plain, Encoding.ASCII.GetBytes("33CPN=051366"));
                Assert.That(at, Is.GreaterThanOrEqualTo(0));
                byte[] expect = Encoding.ASCII.GetBytes("051366");
                SintranCpuIdentity.SetParity(expect);
                for (int i = 0; i < 6; i++) Assert.That(sym2[at + 6 + i], Is.EqualTo(expect[i]));
                Assert.That(IndexOf(plain, Encoding.ASCII.GetBytes("33CPU=023233\r\n")), Is.GreaterThanOrEqualTo(0));
            }

            // Reopened from disk: the stamp really went to page 0 with a valid checksum.
            var again = Open(path, true, out FileStream stream2);
            using (stream2)
            {
                var mb = again.GetMasterBlock();
                Assert.That(mb.ExtValid, Is.True);
                Assert.That(mb.IsDirectoryEntered, Is.False);
                Assert.That(mb.ExtLastSystemNumber, Is.EqualTo(0u));
            }
        }

        [Test]
        public void Write_TypeOptionalAndKeepStamp()
        {
            string path = BuildPack(LShape());
            var fs = Open(path, false, out FileStream stream);
            using (stream)
            {
                SintranCpuIdentityInfo after = fs.WriteSintranCpuIdentity(7, null, keepStamp: true);
                Assert.That(after.Copies[0].Sysno, Is.EqualTo(7));
                Assert.That(after.Copies[0].CpuType, Is.EqualTo(9883));     // kept
                Assert.That(after.Symbols.Cpu33, Is.EqualTo(0));            // kept
                Assert.That(after.DirectoryEntered, Is.True);               // kept
                Assert.That(after.DirectorySystemNumber, Is.EqualTo(100));
            }
        }

        [Test]
        public void Write_KShape_WritesSintranData()
        {
            string path = BuildPack(KShape());
            var fs = Open(path, false, out FileStream stream);
            using (stream)
            {
                SintranCpuIdentityInfo after = fs.WriteSintranCpuIdentity(21238, null, keepStamp: false);
                Assert.That(after.Copies[0].Sysno, Is.EqualTo(21238));
                Assert.That(after.Copies[0].Fcpun, Is.EqualTo(21238));
                Assert.That(after.Copies[1].Sysno, Is.EqualTo(21238));
                Assert.That(after.Copies[1].Fcpun, Is.EqualTo(21238));
                Assert.That(after.Copies[1].CpuType, Is.EqualTo(9883));
                Assert.That(after.Symbols.Cpn33, Is.EqualTo(21238));
                Assert.That(after.Symbols.Cpu33, Is.EqualTo(110));          // type not given: kept
                // The save copy really went into SINTRAN:DATA page 10.
                byte[] sd = fs.ReadFile("SYSTEM/SINTRAN:DATA");
                long o = SaveMadrK * Page + (Sysno - 0x800) * 2;
                Assert.That((sd[o] << 8) | sd[o + 1], Is.EqualTo(21238));
            }
        }

        [Test]
        public void Write_RefusesWhenSaveFileTooShort()
        {
            var sh = KShape();
            sh.SintranDataPages = 16;   // SYSNO (page 10) is inside, FCPUN (page 10 + 32) is not
            string path = BuildPack(sh);
            var fs = Open(path, false, out FileStream stream);
            using (stream)
            {
                SintranCpuIdentityInfo id = fs.ReadSintranCpuIdentity();
                Assert.That(id.Copies[1].Present, Is.True);
                Assert.That(id.Copies[1].FileFound, Is.True);
                Assert.That(id.Copies[1].SysnoOffset, Is.GreaterThanOrEqualTo(0));
                Assert.That(id.Copies[1].FcpunOffset, Is.EqualTo(-1));
                Assert.That(id.Copies[1].Fcpun, Is.EqualTo(-1));
                Assert.Throws<SintranCpuIdentityException>(() => fs.WriteSintranCpuIdentity(21238, null, keepStamp: false));
                id = fs.ReadSintranCpuIdentity();
                Assert.That(id.Copies[0].Sysno, Is.EqualTo(21056));         // nothing changed
                Assert.That(id.DirectoryEntered, Is.True);
            }
        }

        [Test]
        public void Write_RejectsBadValues()
        {
            string path = BuildPack(LShape());
            var fs = Open(path, false, out FileStream stream);
            using (stream)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => fs.WriteSintranCpuIdentity(-1, null, false));
                Assert.Throws<ArgumentOutOfRangeException>(() => fs.WriteSintranCpuIdentity(65536, null, false));
                Assert.Throws<ArgumentOutOfRangeException>(() => fs.WriteSintranCpuIdentity(1, 65536, false));
                // Nothing was written.
                Assert.That(fs.ReadSintranCpuIdentity().Copies[0].Sysno, Is.EqualTo(21056));
            }
        }

        // ---- Real packs, on a COPY ---------------------------------------------------------
        //
        // The packs live outside the repository. Name a SINTRAN K and an L pack with the environment
        // variables below to run these; each is copied to the temp directory before anything is written.
        //   RETROIO_SINTRAN_K_IMAGE=...\BIGDISK0-K-100.IMG
        //   RETROIO_SINTRAN_L_IMAGE=...\BIGDISK0-L2-100.IMG

        private string? CopyOfEnvImage(string variable)
        {
            string? src = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(src) || !File.Exists(src)) return null;
            string dst = Path.Combine(_testDir, Path.GetFileName(src));
            File.Copy(src, dst);
            return dst;
        }

        [Test]
        public void RealPack_K_ReadAndChangeOnCopy()
        {
            string? path = CopyOfEnvImage("RETROIO_SINTRAN_K_IMAGE");
            if (path == null) { Assert.Ignore("RETROIO_SINTRAN_K_IMAGE not set"); return; }
            var fs = Open(path, false, out FileStream stream);
            using (stream)
            {
                SintranCpuIdentityInfo id = fs.ReadSintranCpuIdentity();
                // Measured with ndtool --cpu-identity on BIGDISK0-K-100 (2026-10-29): SYSNO 004051,
                // FCPUN 105034, both copies 35 pages at MADR 27, save copy on segment file 4 in SINTRAN:DATA.
                Assert.That(id.Version, Is.EqualTo('K'));
                Assert.That(id.Symbols.Fcpun, Is.EqualTo(FcpunK));
                Assert.That(id.Copies[0].SegmentNumber, Is.EqualTo(0x24));
                Assert.That(id.Copies[1].SegmentFile, Is.EqualTo(4));
                Assert.That(id.Copies[1].FilePath, Is.EqualTo("SYSTEM/SINTRAN:DATA"));
                Assert.That(id.Copies[0].Sysno, Is.EqualTo(id.Copies[1].Sysno));

                SintranCpuIdentityInfo after = fs.WriteSintranCpuIdentity(21238, 9883, keepStamp: false);
                Assert.That(after.Copies[0].Sysno, Is.EqualTo(21238));
                Assert.That(after.Copies[1].Fcpun, Is.EqualTo(21238));
                Assert.That(after.Copies[1].CpuType, Is.EqualTo(9883));
                Assert.That(after.Symbols.Cpn33, Is.EqualTo(21238));
                Assert.That(after.DirectoryEntered, Is.False);
            }
        }

        [Test]
        public void RealPack_L_ReadAndChangeOnCopy()
        {
            string? path = CopyOfEnvImage("RETROIO_SINTRAN_L_IMAGE");
            if (path == null) { Assert.Ignore("RETROIO_SINTRAN_L_IMAGE not set"); return; }
            var fs = Open(path, false, out FileStream stream);
            using (stream)
            {
                SintranCpuIdentityInfo id = fs.ReadSintranCpuIdentity();
                // Measured on BIGDISK0-L2-100: FCPUN 004414, both copies 45 pages on segment file 0.
                Assert.That(id.Version, Is.EqualTo('L'));
                Assert.That(id.Symbols.Fcpun, Is.EqualTo(FcpunL));
                Assert.That(id.Copies[1].SegmentFile, Is.EqualTo(0));
                Assert.That(id.Copies[0].Segle, Is.EqualTo(45));

                SintranCpuIdentityInfo after = fs.WriteSintranCpuIdentity(21238, null, keepStamp: false);
                Assert.That(after.Copies[0].Sysno, Is.EqualTo(21238));
                Assert.That(after.Copies[1].Sysno, Is.EqualTo(21238));
                Assert.That(after.DirectorySystemNumber, Is.EqualTo(0));
            }
        }
    }
}
