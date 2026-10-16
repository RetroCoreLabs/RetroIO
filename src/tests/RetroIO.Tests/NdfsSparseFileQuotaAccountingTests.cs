//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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
            driver.WriteFile("/ALICE/ZERO:DAT", data);
            uint after = PagesUsedFor(driver, "ALICE");

            Assert.That(after, Is.EqualTo(before),
                "a fully-sparse file allocates no real data blocks and no index block is charged to quota");
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
            driver.WriteFile("/BOB/MIXED:DAT", data);
            uint after = PagesUsedFor(driver, "BOB");

            Assert.That(after - before, Is.EqualTo(3u),
                "only the 3 real (non-zero) pages should count toward quota — not the 7 holes, and not the index block");
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

            Assert.That(after - before, Is.EqualTo(6u),
                "exactly the 6 real data pages — the index block must not add +1 to quota");
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

            Assert.That(after, Is.EqualTo(before), "delete must refund exactly what create charged");
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
            Assert.That(afterFirstWrite - beforeFirstWrite, Is.EqualTo(2u));

            driver.WriteFile("/EVE/GROW:DAT", MakePattern(8 * PageSize));
            uint afterOverwrite = PagesUsedFor(driver, "EVE");
            Assert.That(afterOverwrite - beforeFirstWrite, Is.EqualTo(8u),
                "overwriting with a larger file must charge the NEW real page count, replacing the old");

            driver.WriteFile("/EVE/GROW:DAT", MakePattern(1 * PageSize));
            uint afterShrink = PagesUsedFor(driver, "EVE");
            Assert.That(afterShrink - beforeFirstWrite, Is.EqualTo(1u),
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
            driver.WriteFile("/FRANK/BIGSPARSE:DAT", data);
            uint after = PagesUsedFor(driver, "FRANK");

            Assert.That(after - before, Is.EqualTo(2u),
                "only the 2 real pages count — none of the sub-index block or group index blocks this file needed");
        }
    }
}
