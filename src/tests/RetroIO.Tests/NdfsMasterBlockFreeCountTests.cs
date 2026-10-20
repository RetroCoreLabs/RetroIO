//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Creation;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tests
{
    /// <summary>
    /// Regression tests for the "Unreserved Pages" master-block field (offset 0x1C within the
    /// 32-byte master block at <see cref="NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET"/>, page 0).
    /// The field is the pool for user quotas, not a free page count. SINTRAN keeps it as the pages
    /// a directory can hand out (capacity - 1 - bit file pages - 3) minus the sum of the pages
    /// the users have reserved (measured, docs/NDFS-ALLOCATION-RULES.md section 7a in the
    /// norskdata-ndfs repository). Files do not change it; adding, changing and removing users does.
    /// Before this fix the file system set it to the free page count after every write.
    /// </summary>
    [TestFixture]
    public class NdfsMasterBlockFreeCountTests
    {
        private const int PageSize = NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
        private string _testDir = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "RetroFS_NdfsMasterBlockFreeCountTests_" + Path.GetRandomFileName());
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
                directoryName: "MB-FREE-TEST",
                bootLoaderType: NdfsBootLoaderType.Winchester);
            return path;
        }

        /// <summary>
        /// The value SINTRAN keeps in Unreserved Pages: capacity - bit file pages - 4, minus the sum
        /// of the pages the users have reserved.
        /// </summary>
        private static uint ExpectedPool(INdfsFileSystem ndfs)
        {
            long capacity = ndfs.GetMasterBlock().ExtPagesAvailable;
            long bitmapBytes = ((capacity + 7) / 8 + 1) & ~1L;
            long bitmapPages = (bitmapBytes + PageSize - 1) / PageSize;
            long pool = capacity - bitmapPages - 4;
            foreach (var user in ndfs.GetUsers())
                pool -= user.PagesReserved;
            return pool > 0 ? (uint)pool : 0u;
        }

        // Deterministic non-zero fill so sparse-hole logic doesn't turn pages into holes.
        private static byte[] MakePattern(int bytes)
        {
            byte[] data = new byte[bytes];
            for (int i = 0; i < bytes; i++)
            {
                data[i] = (byte)((i % 251) + 1); // 1..251, never 0
            }
            return data;
        }

        // Reads the on-disk "Unreserved Pages" big-endian uint32 directly from the raw image
        // bytes, bypassing the filesystem API entirely, so we're verifying what's actually
        // persisted rather than what's cached in memory.
        private static uint ReadOnDiskUnreservedPages(string imagePath)
        {
            byte[] page0 = new byte[PageSize];
            using (FileStream fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read))
            {
                fs.ReadExactly(page0, 0, PageSize);
            }

            int offset = NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET + 0x1C;
            uint value = ((uint)page0[offset] << 24)
                       | ((uint)page0[offset + 1] << 16)
                       | ((uint)page0[offset + 2] << 8)
                       | page0[offset + 3];
            return value;
        }

        // Reads the Extended Info Block's checksum bytes (word 1750 octal = byte offset 0x07D0,
        // 2 bytes) directly from the raw image, so tests can prove this fix never disturbs it.
        private static byte[] ReadExtendedInfoChecksumBytes(string imagePath)
        {
            byte[] page0 = new byte[PageSize];
            using (FileStream fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read))
            {
                fs.ReadExactly(page0, 0, PageSize);
            }

            byte[] checksum = new byte[2];
            Array.Copy(page0, 0x07D0, checksum, 0, 2);
            return checksum;
        }

        [Test]
        public void CreateFile_OnDiskUnreservedPages_IsTheQuotaPool()
        {
            string image = MakeImage("create.img");

            uint onDiskAfterCreate;
            uint liveFreePagesAfterCreate;

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/ALICE");
                driver.WriteFile("/ALICE/FILE:DAT", MakePattern(10 * PageSize));

                INdfsFileSystem ndfs = driver.GetFileSystem()!;
                liveFreePagesAfterCreate = ExpectedPool(ndfs);

                driver.Dispose(); // flush buffered writes to the underlying stream
            }

            onDiskAfterCreate = ReadOnDiskUnreservedPages(image);

            Assert.That(onDiskAfterCreate, Is.EqualTo(liveFreePagesAfterCreate),
                "On-disk Unreserved Pages must be the quota pool after a file create (capacity minus overhead minus the reserved pages).");
        }

        [Test]
        public void DeleteFile_OnDiskUnreservedPages_DoesNotChangeThePool()
        {
            string image = MakeImage("delete.img");

            uint onDiskAfterCreate;
            uint onDiskAfterDelete;

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/BOB");
                driver.WriteFile("/BOB/FILE:DAT", MakePattern(10 * PageSize));
                driver.Dispose();
            }
            onDiskAfterCreate = ReadOnDiskUnreservedPages(image);

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.DeleteFile("/BOB/FILE:DAT");

                INdfsFileSystem ndfs = driver.GetFileSystem()!;
                uint liveFreePagesAfterDelete = ExpectedPool(ndfs);

                driver.Dispose();

                onDiskAfterDelete = ReadOnDiskUnreservedPages(image);

                Assert.That(onDiskAfterDelete, Is.EqualTo(liveFreePagesAfterDelete),
                    "On-disk Unreserved Pages must still be the quota pool after a file delete.");
            }

            Assert.That(onDiskAfterDelete, Is.EqualTo(onDiskAfterCreate),
                "Files do not change the quota pool: the user's reservation covers them.");
        }

        [Test]
        public void MultipleMutations_OnDiskUnreservedPages_TracksCorrectlyAtEachStep()
        {
            string image = MakeImage("multi.img");

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/CAROL");
                INdfsFileSystem ndfs = driver.GetFileSystem()!;

                // Step 1: create file A.
                driver.WriteFile("/CAROL/A:DAT", MakePattern(20 * PageSize));
                uint liveAfterA = ExpectedPool(ndfs);
                driver.Dispose();
                Assert.That(ReadOnDiskUnreservedPages(image), Is.EqualTo(liveAfterA), "after creating A");
            }

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                INdfsFileSystem ndfs = driver.GetFileSystem()!;

                // Step 2: create file B (on top of A).
                driver.WriteFile("/CAROL/B:DAT", MakePattern(15 * PageSize));
                uint liveAfterB = ExpectedPool(ndfs);
                driver.Dispose();
                Assert.That(ReadOnDiskUnreservedPages(image), Is.EqualTo(liveAfterB), "after creating B");
            }

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                INdfsFileSystem ndfs = driver.GetFileSystem()!;

                // Step 3: delete file A, leaving B in place.
                driver.DeleteFile("/CAROL/A:DAT");
                uint liveAfterDeleteA = ExpectedPool(ndfs);
                driver.Dispose();
                Assert.That(ReadOnDiskUnreservedPages(image), Is.EqualTo(liveAfterDeleteA), "after deleting A");
            }
        }

        [Test]
        public void AddUser_OnDiskUnreservedPages_TakesTheQuotaFromThePool()
        {
            // Adding a user can allocate a fresh user-file data page on demand
            // (EnsureUserFilePageAllocated) — that allocation must also be reflected here.
            string image = MakeImage("adduser.img");

            uint liveFreeAfterAdd;

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                INdfsFileSystem ndfs = driver.GetFileSystem()!;

                // Add enough users to force at least one new user-file data page
                // (32 entries per 2 KB page).
                for (int i = 0; i < 40; i++)
                {
                    ndfs.AddUser("USER" + i, reservedPages: 5);
                }

                liveFreeAfterAdd = ExpectedPool(ndfs);
                driver.Dispose();
            }

            Assert.That(ReadOnDiskUnreservedPages(image), Is.EqualTo(liveFreeAfterAdd),
                "On-disk Unreserved Pages must take the quotas of the new users from the pool.");
        }

        [Test]
        public void Mutations_ExtendedInfoChecksum_RemainsByteIdentical()
        {
            string image = MakeImage("checksum.img");

            byte[] checksumBefore = ReadExtendedInfoChecksumBytes(image);

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/DAVE");
                driver.WriteFile("/DAVE/A:DAT", MakePattern(5 * PageSize));
                driver.WriteFile("/DAVE/B:DAT", MakePattern(3 * PageSize));
                driver.DeleteFile("/DAVE/A:DAT");
                driver.Dispose();
            }

            byte[] checksumAfter = ReadExtendedInfoChecksumBytes(image);

            Assert.That(checksumAfter, Is.EqualTo(checksumBefore),
                "The Extended Info Block's checksum (offset 0x07D0) must be untouched by master-block free-count persistence.");
        }
    }
}
