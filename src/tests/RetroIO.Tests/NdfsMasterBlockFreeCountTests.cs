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
    /// Real SINTRAN reads this cached field to report free disk space without rescanning the
    /// bitmap. Before this fix it was only ever written once, at image-creation time
    /// (<see cref="NdfsImageCreator"/>) — every subsequent create/delete/user-add through
    /// <see cref="NdfsFileSystem"/> left it stale relative to the real bit-file state.
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
        public void CreateFile_OnDiskUnreservedPages_MatchesRealFreePageCount()
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
                liveFreePagesAfterCreate = ndfs.GetPagesAvailable();

                driver.Dispose(); // flush buffered writes to the underlying stream
            }

            onDiskAfterCreate = ReadOnDiskUnreservedPages(image);

            Assert.That(onDiskAfterCreate, Is.EqualTo(liveFreePagesAfterCreate),
                "On-disk Unreserved Pages must be rewritten to match the true free-page count after a file create.");
        }

        [Test]
        public void DeleteFile_OnDiskUnreservedPages_IncreasesBackAfterDelete()
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
                uint liveFreePagesAfterDelete = ndfs.GetPagesAvailable();

                driver.Dispose();

                onDiskAfterDelete = ReadOnDiskUnreservedPages(image);

                Assert.That(onDiskAfterDelete, Is.EqualTo(liveFreePagesAfterDelete),
                    "On-disk Unreserved Pages must be rewritten to match the true free-page count after a file delete.");
            }

            Assert.That(onDiskAfterDelete, Is.GreaterThan(onDiskAfterCreate),
                "Deleting the file must free pages back, so the on-disk count must go back up.");
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
                uint liveAfterA = ndfs.GetPagesAvailable();
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
                uint liveAfterB = ndfs.GetPagesAvailable();
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
                uint liveAfterDeleteA = ndfs.GetPagesAvailable();
                driver.Dispose();
                Assert.That(ReadOnDiskUnreservedPages(image), Is.EqualTo(liveAfterDeleteA), "after deleting A");
            }
        }

        [Test]
        public void AddUser_OnDiskUnreservedPages_ReflectsPageAllocatedForNewUserEntry()
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

                liveFreeAfterAdd = ndfs.GetPagesAvailable();
                driver.Dispose();
            }

            Assert.That(ReadOnDiskUnreservedPages(image), Is.EqualTo(liveFreeAfterAdd),
                "On-disk Unreserved Pages must reflect pages allocated for new user-file entries.");
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
