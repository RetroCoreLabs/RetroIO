//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Creation;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.FileSystems;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Tests
{
    /// <summary>
    /// Tests for SINTRAN's page allocation rules and image layout (docs/NDFS-ALLOCATION-RULES.md and
    /// docs/CREATE-DIRECTORY-PLACEMENT.md in the norskdata-ndfs repository).
    /// </summary>
    /// <remarks>
    /// The expected numbers come from real SINTRAN M runs in the nd100x emulator: the bit file address,
    /// the first single page of 12 disk formats, contiguous stacks, index block placement and quota.
    /// </remarks>
    [TestFixture]
    public class NdfsAllocationRulesTests
    {
        private const int PageSize = NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
        private string _testDir = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "RetroFS_NdfsAllocRules_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_testDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }

        /// <summary>
        /// A bit file for a disk of <paramref name="cap"/> pages with the bit file at
        /// <paramref name="ba"/>: page 0 and the bit file pages are marked used.
        /// </summary>
        private static BitFile MakeBitFile(uint cap, uint ba)
        {
            var bf = new BitFile();
            bf.Initialize(cap);
            bf.AllocCeiling = cap;
            bf.BitAddress = ba;
            bf.MarkBlockUsed(0);
            for (uint i = 0; i < (cap + 16383) / 16384; i++)
                bf.MarkBlockUsed(ba + i);
            return bf;
        }

        private static uint TakeHint(BitFile bf)
        {
            uint p = bf.FindFreeHint()!.Value;
            bf.MarkBlockUsed(p);
            return p;
        }

        private static uint TakeBlock(BitFile bf)
        {
            uint p = bf.FindFreeDataBlock()!.Value;
            bf.MarkBlockUsed(p);
            return p;
        }

        // ---- RSBPG hint rule ---------------------------------------------------------------

        [Test]
        public void HintRule_Smd75_GivesTheFourPagesSintranGave()
        {
            BitFile bf = MakeBitFile(36945, 18468);
            Assert.That(new[] { TakeHint(bf), TakeHint(bf), TakeHint(bf), TakeHint(bf) },
                Is.EqualTo(new uint[] { 18479, 18478, 18477, 18476 }));
        }

        [Test]
        public void HintRule_BitFileHoldsTheTopOfTheHomeWord()
        {
            // DISC-288MB: BA 70191 ends in binary 1111, so the four pages sit directly below it.
            BitFile bf = MakeBitFile(140391, 70191);
            Assert.That(new[] { TakeHint(bf), TakeHint(bf), TakeHint(bf), TakeHint(bf) },
                Is.EqualTo(new uint[] { 70190, 70189, 70188, 70187 }));
        }

        [Test]
        public void HintRule_SmallWinchester()
        {
            BitFile bf = MakeBitFile(6912, 3456);
            Assert.That(new[] { TakeHint(bf), TakeHint(bf), TakeHint(bf), TakeHint(bf) },
                Is.EqualTo(new uint[] { 3455, 3454, 3453, 3452 }));
        }

        [Test]
        public void HintRule_SpiralsToTheNeighbouringWords_ReadInCodeNotMeasured()
        {
            BitFile bf = MakeBitFile(36945, 18468);
            for (uint p = 18464; p < 18480; p++)
                bf.MarkBlockUsed(p);
            Assert.That(TakeHint(bf), Is.EqualTo(18480u));   // word+1, lowest bit first
            Assert.That(TakeHint(bf), Is.EqualTo(18481u));
            for (uint p = 18482; p < 18496; p++)
                bf.MarkBlockUsed(p);
            Assert.That(TakeHint(bf), Is.EqualTo(18463u));   // word-1, highest bit first
        }

        [Test]
        public void HintRule_NeverGivesPagesBelowBlock7OrAtTheCapacity()
        {
            BitFile bf = MakeBitFile(100, 50);
            for (uint p = 7; p < 100; p++)
            {
                if (!bf.IsBlockUsed(p))
                    bf.MarkBlockUsed(p);
            }
            Assert.That(bf.FindFreeHint(), Is.Null);
            bf.MarkBlockFree(99);
            Assert.That(bf.FindFreeHint(), Is.EqualTo(99u));
        }

        [Test]
        public void Rules_FallBackToTopDownWithoutABitAddress()
        {
            BitFile bf = MakeBitFile(1000, 500);
            bf.BitAddress = 0;
            uint? top = bf.FindFirstFreeBlock();
            Assert.That(bf.FindFreeHint(), Is.EqualTo(top));
            Assert.That(bf.FindFreeDataBlock(), Is.EqualTo(top));
        }

        // ---- RSPAG block rule ---------------------------------------------------------------

        /// <summary>
        /// First single page on a cold start of 12 real disk formats (measured with SINTRAN M).
        /// </summary>
        [TestCase(18486u, 9243u, 16384u)]      // DISC-38MB
        [TestCase(34765u, 17379u, 32768u)]     // DISC-70MB
        [TestCase(36945u, 18468u, 32768u)]     // DISC-75MB
        [TestCase(69530u, 34758u, 65536u)]     // DISC-140MB-F
        [TestCase(140391u, 70191u, 131072u)]   // DISC-288MB
        [TestCase(220584u, 110286u, 212992u)]  // DISC-450MB
        [TestCase(22032u, 11016u, 16384u)]     // DISC-45MB
        [TestCase(36396u, 18198u, 32768u)]     // DISC-74MB
        [TestCase(6912u, 3456u, 3328u)]        // DISC-14MB
        [TestCase(10728u, 5364u, 5120u)]       // DISC-21MB
        [TestCase(11016u, 5508u, 5376u)]       // DISC-23MB
        [TestCase(13648u, 6822u, 6656u)]       // DISC-28MB
        public void BlockRule_ColdStart_FirstPage(uint cap, uint ba, uint first)
        {
            BitFile bf = MakeBitFile(cap, ba);
            Assert.That(TakeBlock(bf), Is.EqualTo(first));
            Assert.That(TakeBlock(bf), Is.EqualTo(first + 1), "the next call stays in the block");
        }

        [Test]
        public void BlockRule_FullBlockSendsTheSearchToTheNextBlocks()
        {
            BitFile bf = MakeBitFile(36945, 18468);
            for (uint p = 32768; p < 33024; p++)
                bf.MarkBlockUsed(p);                          // cold start block full
            Assert.That(TakeBlock(bf), Is.EqualTo(33024u));   // block + 1
            for (uint p = 33025; p < 33280; p++)
                bf.MarkBlockUsed(p);                          // block + 1 full
            Assert.That(TakeBlock(bf), Is.EqualTo(33280u));   // the cursor moved to block + 1; its next block up
        }

        // ---- File system: placement and quota ------------------------------------------------

        private string MakeSmd75(string name)
        {
            string path = Path.Combine(_testDir, name);
            NdfsImageCreator.CreateImageFromTemplate(path, NdfsImageTemplate.Smd75MB, "ALLOC");
            return path;
        }

        private static uint PagesUsedOf(INdfsFileSystem ndfs, string userName)
        {
            foreach (UserEntry u in ndfs.GetUsers())
            {
                if (string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase))
                    return u.PagesUsed;
            }

            Assert.Fail($"User {userName} not found");
            return 0;
        }

        /// <summary>
        /// Reads the data page numbers of an Indexed file from its index block.
        /// </summary>
        private static List<uint> DataPagesOf(string image, uint indexBlock, int count)
        {
            var result = new List<uint>();
            using var stream = new FileStream(image, FileMode.Open, FileAccess.Read);
            var page = new byte[PageSize];
            stream.Seek((long)indexBlock * PageSize, SeekOrigin.Begin);
            stream.ReadExactly(page, 0, PageSize);
            for (int i = 0; i < count; i++)
                result.Add(BlockPointer.FromBytes(page, i * 4).BlockID);
            return result;
        }

        /// <summary>
        /// The top pointer block of a file, found by object name (the part before the colon).
        /// </summary>
        private static uint FilePointerOf(INdfsFileSystem ndfs, string name, string user)
        {
            string objectName = name.Split(':')[0];
            foreach (ObjectEntry e in ndfs.GetObjectEntries())
            {
                if (string.Equals(e.ObjectName, objectName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.UserName, user, StringComparison.OrdinalIgnoreCase))
                    return e.FilePointer!.BlockID;
            }

            Assert.Fail($"{name} not found");
            return 0;
        }

        [Test]
        public void ContiguousFiles_StackDownwardFromTheDirectorySize()
        {
            string image = MakeSmd75("contig.img");
            using var stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite);
            var driver = new NdfsDriver();
            driver.Mount(stream, readOnly: false);
            INdfsFileSystem ndfs = driver.GetFileSystem()!;

            ndfs.CreateContiguousFile("SYSTEM/C1:DATA", 10);
            ndfs.CreateContiguousFile("SYSTEM/C2:DATA", 20);

            Assert.That(FilePointerOf(ndfs, "C1:DATA", "SYSTEM"), Is.EqualTo(36935u));
            Assert.That(FilePointerOf(ndfs, "C2:DATA", "SYSTEM"), Is.EqualTo(36915u));
            driver.Dispose();
        }

        [Test]
        public void ContiguousFileOfOnePage_UsesTheDataPageRule()
        {
            string image = MakeSmd75("single.img");
            using var stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite);
            var driver = new NdfsDriver();
            driver.Mount(stream, readOnly: false);
            INdfsFileSystem ndfs = driver.GetFileSystem()!;

            ndfs.CreateContiguousFile("SYSTEM/N1:DATA", 1);
            ndfs.CreateContiguousFile("SYSTEM/N2:DATA", 1);

            Assert.That(FilePointerOf(ndfs, "N1:DATA", "SYSTEM"), Is.EqualTo(32768u));
            Assert.That(FilePointerOf(ndfs, "N2:DATA", "SYSTEM"), Is.EqualTo(32769u));
            driver.Dispose();
        }

        [Test]
        public void IndexedFile_IndexBlockByTheHintRule_DataPagesByTheDataPageRule()
        {
            string image = MakeSmd75("indexed.img");
            uint indexBlock;
            using (var stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                var driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                INdfsFileSystem ndfs = driver.GetFileSystem()!;

                ndfs.CreateContiguousFile("SYSTEM/N1:DATA", 1);
                ndfs.CreateContiguousFile("SYSTEM/N2:DATA", 1);
                var data = new byte[3 * PageSize];
                Array.Fill(data, (byte)0x5A);
                ndfs.WriteFile("SYSTEM/N3:DATA", data);

                indexBlock = FilePointerOf(ndfs, "N3:DATA", "SYSTEM");
                driver.Dispose();
            }

            Assert.That(indexBlock, Is.EqualTo(18475u), "index block: first free page going down from the home word");
            Assert.That(DataPagesOf(image, indexBlock, 3), Is.EqualTo(new uint[] { 32770, 32771, 32772 }));
        }

        [Test]
        public void Quota_ChargesDataPagesIndexBlocksAndTheObjectPage()
        {
            string image = MakeSmd75("quota.img");
            using var stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite);
            var driver = new NdfsDriver();
            driver.Mount(stream, readOnly: false);
            INdfsFileSystem ndfs = driver.GetFileSystem()!;

            Assert.That(PagesUsedOf(ndfs, "SYSTEM"), Is.EqualTo(1u), "SYSTEM is charged its object data page");

            var three = new byte[3 * PageSize];
            Array.Fill(three, (byte)0x11);
            ndfs.WriteFile("SYSTEM/Q1:DATA", three);
            Assert.That(PagesUsedOf(ndfs, "SYSTEM"), Is.EqualTo(1u + 3u + 1u), "3 data pages and 1 index block");

            Assert.That(ndfs.AddUser("ALPHA", 50), Is.True);
            Assert.That(PagesUsedOf(ndfs, "ALPHA"), Is.EqualTo(0u));
            var one = new byte[PageSize];
            Array.Fill(one, (byte)0x22);
            ndfs.WriteFile("ALPHA/A1:DATA", one);
            Assert.That(PagesUsedOf(ndfs, "ALPHA"), Is.EqualTo(1u + 1u + 1u), "data page, index block and object page");

            ndfs.DeleteFile("ALPHA/A1:DATA");
            Assert.That(PagesUsedOf(ndfs, "ALPHA"), Is.EqualTo(1u), "the object page stays charged");
            ndfs.DeleteFile("SYSTEM/Q1:DATA");
            Assert.That(PagesUsedOf(ndfs, "SYSTEM"), Is.EqualTo(1u));
            driver.Dispose();
        }

        [Test]
        public void Quota_FileOf600Pages_ChargesTheTopBlockAndTwoGroupIndexBlocks()
        {
            string image = MakeSmd75("big.img");
            using var stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite);
            var driver = new NdfsDriver();
            driver.Mount(stream, readOnly: false);
            INdfsFileSystem ndfs = driver.GetFileSystem()!;

            var data = new byte[600 * PageSize];
            for (int i = 0; i < data.Length; i++)
                data[i] = (byte)((i * 7 + 1) & 0xFF);
            ndfs.WriteFile("SYSTEM/BIGF:DATA", data);

            Assert.That(PagesUsedOf(ndfs, "SYSTEM"), Is.EqualTo(1u + 600u + 3u));
            Assert.That(ndfs.ReadFile("SYSTEM/BIGF:DATA"), Is.EqualTo(data));
            driver.Dispose();
        }

        // ---- Created image layout ------------------------------------------------------------

        private byte[] CreateBytes(NdfsImageCreationOptions options)
        {
            string path = Path.Combine(_testDir, Path.GetRandomFileName() + ".img");
            NdfsImageCreator.CreateImage(path, options);
            return File.ReadAllBytes(path);
        }

        private static uint Ptr(byte[] img, int off)
        {
            int o = 0x7E0 + off;
            return (uint)(((img[o] << 24) | (img[o + 1] << 16) | (img[o + 2] << 8) | img[o + 3]) & 0x3FFFFFFF);
        }

        private static int RawBit(byte[] img, uint bitFile, uint page)
        {
            return (img[bitFile * PageSize + ((page >> 3) ^ 1)] >> (int)(page & 7)) & 1;
        }

        [Test]
        public void Created_DefaultPointersOfEveryTemplate()
        {
            var expected = new (NdfsImageTemplate T, uint Obj, uint Usr, uint Bit)[]
            {
                (NdfsImageTemplate.Floppy360KB, 76, 78, 72),
                (NdfsImageTemplate.Floppy12MB, 316, 318, 306),
                (NdfsImageTemplate.Smd75MB, 18476, 18478, 18468),
                (NdfsImageTemplate.Winchester74MB, 18204, 18206, 18198),
                (NdfsImageTemplate.Scsi126MB, 30506, 30508, 30510),
            };

            foreach (var e in expected)
            {
                byte[] img = CreateBytes(new NdfsImageCreationOptions { Template = e.T, DirectoryName = "RULES" });
                Assert.That((Ptr(img, 0x10), Ptr(img, 0x14), Ptr(img, 0x18)), Is.EqualTo((e.Obj, e.Usr, e.Bit)), e.T.ToString());
            }
        }

        [Test]
        public void Created_Scsi126_HasTheRealLastPageAndLength()
        {
            byte[] img = CreateBytes(new NdfsImageCreationOptions { Template = NdfsImageTemplate.Scsi126MB, DirectoryName = "SCSI" });
            Assert.That(img.Length, Is.EqualTo(64656 * PageSize));
            byte[] last = new byte[PageSize];
            Array.Copy(img, 64655L * PageSize, last, 0, PageSize);
            Assert.That(last, Is.EqualTo(NdfsBootBlobsForTest.ScsiLastPage));
        }

        [Test]
        public void Created_BitFileAnswers()
        {
            byte[] img = CreateBytes(new NdfsImageCreationOptions { Template = NdfsImageTemplate.Smd75MB, BitFileAtEndOfDisc = true });
            Assert.That((Ptr(img, 0x18), Ptr(img, 0x14), Ptr(img, 0x10)), Is.EqualTo((36942u, 36940u, 36938u)),
                "end of disc: capacity minus the 3 bit file pages");

            // A typed address on a custom image: BA 16384, hint 16383 OR 15 = 16383.
            img = CreateBytes(new NdfsImageCreationOptions
            {
                Template = NdfsImageTemplate.Custom,
                CustomNdfsPages = 36945,
                DevicePages = 38400,
                ControllerType = NdfsControllerType.Smd,
                BitFileAddress = 16384
            });
            Assert.That((Ptr(img, 0x18), Ptr(img, 0x14), Ptr(img, 0x10)), Is.EqualTo((16384u, 16382u, 16380u)));
        }

        [Test]
        public void Created_UnreservedPagesFormula()
        {
            // capacity - 1 - bit file pages - 2 - 1 - the SYSTEM quota: 36945-1-3-2-1-18472
            byte[] img = CreateBytes(new NdfsImageCreationOptions { Template = NdfsImageTemplate.Smd75MB });
            Assert.That(Ptr(img, 0x1C), Is.EqualTo(18466u));
        }

        [Test]
        public void Created_PagesAboveTheCapacityAreMarkedUsedUpToTheNext256()
        {
            byte[] img = CreateBytes(new NdfsImageCreationOptions { Template = NdfsImageTemplate.Smd75MB });
            uint ba = Ptr(img, 0x18);
            Assert.That(RawBit(img, ba, 36945), Is.EqualTo(1));
            Assert.That(RawBit(img, ba, 37119), Is.EqualTo(1));
            Assert.That(RawBit(img, ba, 37120), Is.EqualTo(0));
            Assert.That(RawBit(img, ba, 0), Is.EqualTo(1));
            Assert.That(RawBit(img, ba, 1), Is.EqualTo(0));
        }

        [Test]
        public void Created_FloppyHasNoExtendedInfo_AndHardDiskHasTheCapacity()
        {
            byte[] floppy = CreateBytes(new NdfsImageCreationOptions { Template = NdfsImageTemplate.Floppy12MB });
            for (int i = 0x7D0; i < 0x7E0; i++)
                Assert.That(floppy[i], Is.EqualTo(0), $"byte {i:X}");

            byte[] smd = CreateBytes(new NdfsImageCreationOptions { Template = NdfsImageTemplate.Smd75MB });
            Assert.That((smd[0x7DC] << 24) | (smd[0x7DD] << 16) | (smd[0x7DE] << 8) | smd[0x7DF], Is.EqualTo(36945));
        }

        [Test]
        public void Created_SystemUserIsThere_WithTheSystemQuotaAndTheObjectPageCharged()
        {
            string path = Path.Combine(_testDir, "sys.img");
            NdfsImageCreator.CreateImageFromTemplate(path, NdfsImageTemplate.Smd75MB, "SYS");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            var driver = new NdfsDriver();
            driver.Mount(stream, readOnly: true);
            UserEntry system = driver.GetFileSystem()!.GetUsers()[0];
            Assert.That(system.UserName, Is.EqualTo("SYSTEM"));
            Assert.That(system.PagesReserved, Is.EqualTo(18472u));
            Assert.That(system.PagesUsed, Is.EqualTo(1u));
            Assert.That(system.DefaultFileAccess, Is.EqualTo((ushort)0x04FF));
            driver.Dispose();
        }

        /// <summary>
        /// Reads the Unreserved Pages field of the master block straight from the image on disk.
        /// </summary>
        private static uint OnDiskUnreserved(string image)
        {
            byte[] img = File.ReadAllBytes(image);
            return (uint)((img[0x7FC] << 24) | (img[0x7FD] << 16) | (img[0x7FE] << 8) | img[0x7FF]);
        }

        [Test]
        public void UnreservedPages_IsTheQuotaPool_FilesDoNotChangeIt_QuotaChangesDo()
        {
            string image = MakeSmd75("pool.img");
            uint baseValue;
            using (var stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                var driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                INdfsFileSystem ndfs = driver.GetFileSystem()!;
                ndfs.WriteFile("SYSTEM/A:DATA", new byte[] { 1, 2, 3 });
                driver.Dispose();
            }
            baseValue = OnDiskUnreserved(image);
            Assert.That(baseValue, Is.EqualTo(18466u), "a file does not change the pool");

            using (var stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                var driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                INdfsFileSystem ndfs = driver.GetFileSystem()!;
                Assert.That(ndfs.AddUser("ALPHA", 100), Is.True);
                driver.Dispose();
            }
            Assert.That(OnDiskUnreserved(image), Is.EqualTo(baseValue - 100));

            using (var stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                var driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                INdfsFileSystem ndfs = driver.GetFileSystem()!;
                int index = -1;
                foreach (UserEntry u in ndfs.GetUsers())
                {
                    if (u.UserName == "ALPHA")
                        index = u.UserIndex;
                }

                Assert.That(ndfs.UpdateUserQuota(index, 300), Is.True);
                driver.Dispose();
            }
            Assert.That(OnDiskUnreserved(image), Is.EqualTo(baseValue - 300));
        }

        // ---- Disk types ----------------------------------------------------------------------

        [Test]
        public void DiskTypes_Table()
        {
            Assert.That(NdfsDiskTypes.All.Count, Is.EqualTo(40));
            Assert.That(NdfsDiskTypes.Find("DISC-75MB")!.Pages, Is.EqualTo(36945u));
            Assert.That(NdfsDiskTypes.Find("disc-74mb")!.Pages, Is.EqualTo(36396u));
            Assert.That(NdfsDiskTypes.Find("DISC-450MB-F")!.Pages, Is.EqualTo(220584u));
            Assert.That(NdfsDiskTypes.Find("DISC-14MB")!.Pages, Is.EqualTo(6912u));
            Assert.That(NdfsDiskTypes.Find("DISC-90MB")!.Pages, Is.EqualTo(7389u));
            Assert.That(NdfsDiskTypes.Find("DISC-SCSI-1"), Is.Null);
            Assert.That(NdfsDiskTypes.Find("DISC-75MB-1"), Is.Null);
            Assert.That(NdfsDiskTypes.Find(""), Is.Null);
            Assert.That(NdfsDiskTypes.Find("DISC-74MB")!.Family, Is.EqualTo(NdfsDiskFamily.Winchester));
            Assert.That(NdfsDiskTypes.Find("DISC-75MB")!.Family, Is.EqualTo(NdfsDiskFamily.Smd));
            Assert.That(NdfsDiskTypes.Find("DISC-74MB")!.Tested, Is.True);
            Assert.That(NdfsDiskTypes.Find("DISC-30MB")!.Tested, Is.False);
            Assert.That(NdfsDiskTypes.Find("DISC-10MB")!.Pages, Is.EqualTo(0u), "valid name, size not known");
        }

        [Test]
        public void DiskTypes_LayoutLengths()
        {
            Assert.That(NdfsDiskTypes.Find("DISC-288MB-R")!.LayoutPages, Is.EqualTo(140733u));
            Assert.That(NdfsDiskTypes.Find("DISC-288MB-F")!.LayoutPages, Is.EqualTo(153576u));
            Assert.That(NdfsDiskTypes.Find("DISC-450MB-F")!.LayoutPages, Is.EqualTo(230364u));
            Assert.That(NdfsDiskTypes.Find("DISC-450MB-N")!.LayoutPages, Is.EqualTo(223820u));
            Assert.That(NdfsDiskTypes.Find("DISC-75MB")!.LayoutPages, Is.EqualTo(37035u));
            Assert.That(NdfsDiskTypes.Find("DISC-30MB")!.LayoutPages, Is.EqualTo(0u));
        }

        [Test]
        public void DiskTypes_CreateFromName_UsesTheFullLayoutLength()
        {
            byte[] img = CreateBytes(NdfsImageCreationOptions.ForDiskType("DISC-288MB-F", "BIG"));
            Assert.That(img.Length, Is.EqualTo(153576 * PageSize));
            Assert.That((Ptr(img, 0x10), Ptr(img, 0x14), Ptr(img, 0x18)), Is.EqualTo((70187u, 70189u, 70191u)));
            Assert.That((img[0x7DC] << 24) | (img[0x7DD] << 16) | (img[0x7DE] << 8) | img[0x7DF], Is.EqualTo(140391));
        }

        [Test]
        public void DiskTypes_UnknownNamesAndNamesWithoutASizeAreRefused()
        {
            Assert.Throws<ArgumentException>(() => NdfsImageCreationOptions.ForDiskType("DISC-NOPE"));
            Assert.Throws<ArgumentException>(() => NdfsImageCreationOptions.ForDiskType("DISC-10MB"));
        }
    }

    /// <summary>
    /// Gives the tests access to the internal boot blobs.
    /// </summary>
    internal static class NdfsBootBlobsForTest
    {
        /// <summary>
        /// The last page of the real SCSI disk.
        /// </summary>
        public static byte[] ScsiLastPage => Convert.FromBase64String(ReadScsiLastPageBase64());

        private static string ReadScsiLastPageBase64()
        {
            // The blob class is internal to RetroIO.Storage.Ndfs; read it by reflection so the test needs no
            // InternalsVisibleTo change.
            Type t = typeof(NdfsImageCreator).Assembly.GetType("RetroIO.Storage.Ndfs.Creation.NdfsBootBlobs")!;
            object value = t.GetProperty("ScsiLastPage")!.GetValue(null)!;
            return Convert.ToBase64String((byte[])value);
        }
    }
}
