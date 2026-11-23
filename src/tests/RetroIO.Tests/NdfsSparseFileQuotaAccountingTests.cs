//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Ndfs.Creation;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tests
{
    [TestFixture]
    public class NdfsSparseFileQuotaAccountingTests
    {
        private const int PageSize = 2048;
        private string _testDir = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "RetroFS_NdfsSparseQuotaTests_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_testDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }

        private string MakeImage(string name)
        {
            string path = Path.Combine(_testDir, name);
            NdfsImageCreator.CreateImageFromTemplate(
                path,
                NdfsImageTemplate.Winchester74MB,
                directoryName: "QUOTA-TEST",
                bootLoaderType: NdfsBootLoaderType.Winchester);
            return path;
        }

        private static byte[] MakePattern(int bytes)
        {
            byte[] data = new byte[bytes];
            for (int i = 0; i < bytes; i++)
            {
                data[i] = (byte)((i % 251) + 1); // 1..251, never 0
            }
            return data;
        }

        /// <summary>
        /// Properties that ask for every all-zero page of <paramref name="data"/> to be a hole, as a
        /// .xat side file of a sparse file does. A plain write stores every page.
        /// </summary>
        /// <param name="data">
        /// The file's bytes.
        /// </param>
        /// <returns>
        /// The property bag with the hole list.
        /// </returns>
        private static System.Collections.Generic.Dictionary<string, object> HolesAt(byte[] data)
        {
            System.Collections.Generic.List<int> holes = new System.Collections.Generic.List<int>();
            int pages = (data.Length + PageSize - 1) / PageSize;
            for (int p = 0; p < pages; p++)
            {
                bool zero = true;
                int end = System.Math.Min(data.Length, (p + 1) * PageSize);
                for (int i = p * PageSize; i < end && zero; i++)
                {
                    zero = data[i] == 0;
                }

                if (zero)
                {
                    holes.Add(p);
                }
            }

            return new System.Collections.Generic.Dictionary<string, object>
            {
                { RetroIO.Storage.Ndfs.PropertyBags.NdfsPropertyBagKeys.Holes, holes.ToArray() },
            };
        }

        private static uint PagesUsedFor(NdfsDriver driver, string userName)
        {
            INdfsFileSystem ndfs = driver.GetFileSystem()!;
            var users = ndfs.GetUsers();
            for (int i = 0; i < users.Count; i++)
            {
                if (string.Equals(users[i].UserName, userName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return users[i].PagesUsed;
                }
            }

            Assert.Fail($"User '{userName}' not found");
            return 0;
        }

        [Test]
        public void WriteFile_FullySparse_ChargesZeroPagesUsed()
        {
            string image = MakeImage("all-zero.img");
            byte[] data = new byte[5 * PageSize]; // all zero — fully sparse

            using FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite);
            NdfsDriver driver = new NdfsDriver();
            driver.Mount(stream, readOnly: false);
            driver.CreateDirectory("/ALICE");

            uint before = PagesUsedFor(driver, "ALICE");
            driver.WriteFileWithProperties("/ALICE/ZERO:DAT", data, HolesAt(data));
            uint after = PagesUsedFor(driver, "ALICE");

            // SINTRAN charges a file's index block and the user's first object data page to the user
            // (measured, docs/NDFS-ALLOCATION-RULES.md section 7); the sparse holes themselves are free.
            Assert.That(after - before, Is.EqualTo(2u),
                "a fully-sparse file allocates no real data blocks; its index block and the user's first object page are charged");
        }

        [Test]
        public void WriteFile_MixedSparse_ChargesOnlyRealPages()
        {
            string image = MakeImage("mixed.img");
            byte[] data = new byte[10 * PageSize];
            byte[] pattern = MakePattern(3 * PageSize);
            System.Array.Copy(pattern, 0, data, 0, pattern.Length); // pages 0-2: real
            // pages 3-9 stay zero (7 sparse holes)

            using FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite);
            NdfsDriver driver = new NdfsDriver();
            driver.Mount(stream, readOnly: false);
            driver.CreateDirectory("/BOB");

            uint before = PagesUsedFor(driver, "BOB");
            driver.WriteFileWithProperties("/BOB/MIXED:DAT", data, HolesAt(data));
            uint after = PagesUsedFor(driver, "BOB");

            Assert.That(after - before, Is.EqualTo(3u + 1u + 1u),
                "the 3 real (non-zero) pages, the index block and the user's first object page count toward quota — not the 7 holes");
        }

        [Test]
        public void WriteFile_FullyReal_ChargesExactPageCount_NoIndexOverhead()
        {
            string image = MakeImage("full.img");
            byte[] data = MakePattern(6 * PageSize); // no zero pages at all

            using FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite);
            NdfsDriver driver = new NdfsDriver();
            driver.Mount(stream, readOnly: false);
            driver.CreateDirectory("/CAROL");

            uint before = PagesUsedFor(driver, "CAROL");
            driver.WriteFile("/CAROL/FULL:DAT", data);
            uint after = PagesUsedFor(driver, "CAROL");

            Assert.That(after - before, Is.EqualTo(6u + 1u + 1u),
                "the 6 real data pages plus the index block plus the user's first object page");
        }

        [Test]
        public void DeleteFile_RefundsExactlyTheRealPagesCharged()
        {
            string image = MakeImage("delete.img");
            byte[] data = new byte[10 * PageSize];
            byte[] pattern = MakePattern(4 * PageSize);
            System.Array.Copy(pattern, 0, data, 0, pattern.Length); // 4 real, 6 sparse

            using FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite);
            NdfsDriver driver = new NdfsDriver();
            driver.Mount(stream, readOnly: false);
            driver.CreateDirectory("/DAVE");

            uint before = PagesUsedFor(driver, "DAVE");
            driver.WriteFile("/DAVE/MIXED:DAT", data);
            driver.DeleteFile("/DAVE/MIXED:DAT");
            uint after = PagesUsedFor(driver, "DAVE");

            // Delete refunds the data pages and the index block. The user's object data page stays
            // charged: SINTRAN never gives it back.
            Assert.That(after, Is.EqualTo(before + 1u), "delete must refund the data pages and the index block");
        }

        [Test]
        public void UpdateExistingFile_AdjustsPagesUsed_ForRealPageDelta()
        {
            // Previously UpdateExistingFile never touched PagesUsed at all — a separate bug fixed
            // alongside the sparse-accounting one. Overwrite a small real file with a much larger
            // real file and confirm quota tracks the new size, not the old one.
            string image = MakeImage("update.img");

            using FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite);
            NdfsDriver driver = new NdfsDriver();
            driver.Mount(stream, readOnly: false);
            driver.CreateDirectory("/EVE");

            uint beforeFirstWrite = PagesUsedFor(driver, "EVE");
            driver.WriteFile("/EVE/GROW:DAT", MakePattern(2 * PageSize));
            uint afterFirstWrite = PagesUsedFor(driver, "EVE");
            Assert.That(afterFirstWrite - beforeFirstWrite, Is.EqualTo(2u + 1u + 1u));

            driver.WriteFile("/EVE/GROW:DAT", MakePattern(8 * PageSize));
            uint afterOverwrite = PagesUsedFor(driver, "EVE");
            Assert.That(afterOverwrite - beforeFirstWrite, Is.EqualTo(8u + 1u + 1u),
                "overwriting with a larger file must charge the NEW real page count, replacing the old");

            driver.WriteFile("/EVE/GROW:DAT", MakePattern(1 * PageSize));
            uint afterShrink = PagesUsedFor(driver, "EVE");
            Assert.That(afterShrink - beforeFirstWrite, Is.EqualTo(1u + 1u + 1u),
                "overwriting with a smaller file must refund down to the new (smaller) real page count");
        }

        [Test]
        public void WriteFile_SubIndexedSparseFile_ChargesOnlyRealPages_NoStructuralOverhead()
        {
            // A file large enough to require SubIndexed (>512 pages) with real data confined to a
            // few pages — quota must not charge for the sub-index block or any group index blocks.
            string image = MakeImage("subindexed-sparse.img");
            byte[] data = new byte[600 * PageSize];
            byte[] pattern = MakePattern(2 * PageSize);
            System.Array.Copy(pattern, 0, data, 0, pattern.Length); // 2 real pages, 598 sparse

            using FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite);
            NdfsDriver driver = new NdfsDriver();
            driver.Mount(stream, readOnly: false);
            driver.CreateDirectory("/FRANK");

            uint before = PagesUsedFor(driver, "FRANK");
            driver.WriteFileWithProperties("/FRANK/BIGSPARSE:DAT", data, HolesAt(data));
            uint after = PagesUsedFor(driver, "FRANK");

            // 2 real pages, the top block and 2 group index blocks (600 pages = 2 groups of 512), and
            // the user's first object page.
            Assert.That(after - before, Is.EqualTo(2u + 3u + 1u),
                "the 2 real pages, the sub-index block and the 2 group index blocks, and the user's first object page count");
        }
    }
}
