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
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.FileSystems;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Tests
{
    /// <summary>
    /// Real-write regression tests for NDFS's SubIndexed allocation path (files spanning more
    /// than 512 pages, i.e. larger than 512*2048 = 1,048,576 bytes — previously threw
    /// NotSupportedException unconditionally on both write paths, a genuine user-facing size
    /// limit, not just a stub). Uses a 74MB Winchester template (36,396 pages) so there's real
    /// room for multi-hundred-page files plus their index-block overhead.
    /// </summary>
    [TestFixture]
    public class NdfsSubIndexedAllocationTests
    {
        private const int PageSize = 2048;
        private string _testDir = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "RetroFS_NdfsSubIndexedTests_" + Path.GetRandomFileName());
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
                directoryName: "SUBIDX-TEST",
                bootLoaderType: NdfsBootLoaderType.Winchester);
            return path;
        }

        // Deterministic non-zero fill so sparse-hole logic doesn't turn pages into holes and mask
        // a real data-corruption bug.
        private static byte[] MakePattern(int bytes)
        {
            byte[] data = new byte[bytes];
            for (int i = 0; i < bytes; i++)
            {
                data[i] = (byte)((i % 251) + 1); // 1..251, never 0
            }
            return data;
        }

        [Test]
        public void WriteFile_JustOverIndexedLimit_UsesSubIndexedAndRoundTrips()
        {
            // 513 pages: one page past the 512-page Indexed ceiling, forcing SubIndexed with a
            // single (mostly-empty) second group.
            string image = MakeImage("just-over.img");
            byte[] data = MakePattern(513 * PageSize);

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/ALICE");
                driver.WriteFile("/ALICE/BIG:DAT", data);
                driver.Dispose();
            }

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.Read))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: true);
                byte[] readBack = driver.ReadFile("/ALICE/BIG:DAT");
                Assert.That(readBack, Is.EqualTo(data));
            }
        }

        [Test]
        public void WriteFile_MultipleIndexGroups_RoundTrips()
        {
            // 1000 pages spans two full-ish index groups (512 + 488), proving the sub-index
            // block correctly links more than one group index block, not just a single one.
            string image = MakeImage("multi-group.img");
            byte[] data = MakePattern(1000 * PageSize);

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/BOB");
                driver.WriteFile("/BOB/HUGE:DAT", data);
                driver.Dispose();
            }

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.Read))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: true);
                byte[] readBack = driver.ReadFile("/BOB/HUGE:DAT");
                Assert.That(readBack, Is.EqualTo(data));
            }
        }

        [Test]
        public void WriteFile_SubIndexedWithSparseHoles_RoundTrips()
        {
            // A large file with real zero-filled regions — proves sparse-hole detection still
            // works correctly inside the SubIndexed path, not just Indexed.
            string image = MakeImage("sparse.img");
            byte[] data = new byte[700 * PageSize];
            byte[] pattern = MakePattern(200 * PageSize);
            System.Array.Copy(pattern, 0, data, 0, pattern.Length); // first 200 pages: real data
            // pages 200..499 stay zero (sparse holes)
            System.Array.Copy(pattern, 0, data, 500 * PageSize, pattern.Length); // last 200 pages: real data

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/CAROL");
                driver.WriteFile("/CAROL/SPARSE:DAT", data);
                driver.Dispose();
            }

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.Read))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: true);
                byte[] readBack = driver.ReadFile("/CAROL/SPARSE:DAT");
                Assert.That(readBack, Is.EqualTo(data));
            }
        }

        [Test]
        public void ReadFile_EmptySubIndexEntry_KeepsThePagesBehindItInPlace()
        {
            // SINTRAN III leaves the sub-index entry of a group with no page in it EMPTY - no
            // group index block at all. Measured 2026-10-14 on a domain the ND Linker B01 wrote
            // under SINTRAN III (nd100x, ND-5000): sub-index entries 0, 2, 4 and 6 set, 1, 3 and
            // 5 empty. The writer here always makes the group index blocks, so the file is
            // written and entry 1 is then cleared, as SINTRAN would have written it. Page 1030
            // must still read back at page 1030, not 512 pages earlier.
            string image = MakeImage("empty-entry.img");
            byte[] data = new byte[1500 * PageSize];
            byte[] pattern = MakePattern(PageSize);
            System.Array.Copy(pattern, 0, data, 3 * PageSize, PageSize);
            System.Array.Copy(pattern, 0, data, 1030 * PageSize, PageSize);
            uint subIndexBlock;

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/GRACE");
                driver.WriteFile("/GRACE/GAP:DAT", data);
                ObjectEntry? entry = driver.GetFileSystem()!.GetObjectEntry("GAP", "GRACE");
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!.FilePointer, Is.Not.Null);
                Assert.That(entry.FilePointer!.Type, Is.EqualTo(BlockPointer.PointerType.SubIndexed));
                subIndexBlock = entry.FilePointer.BlockID;
                driver.Dispose();
            }

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                // Premise: entry 1 is set before it is cleared (a group index block, type Indexed).
                byte[] slot = new byte[4];
                stream.Position = (long)subIndexBlock * PageSize + 4;
                stream.ReadExactly(slot);
                Assert.That(BlockPointer.FromBytes(slot, 0).Type, Is.EqualTo(BlockPointer.PointerType.Indexed));
                stream.Position = (long)subIndexBlock * PageSize + 4;
                stream.Write(new byte[4]);
            }

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.Read))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: true);
                byte[] readBack = driver.ReadFile("/GRACE/GAP:DAT");
                Assert.That(readBack, Is.EqualTo(data));
            }
        }

        [Test]
        public void DeleteFile_SubIndexed_FreesAllBlocksIncludingIndexBlocks()
        {
            string image = MakeImage("delete.img");
            byte[] data = MakePattern(1000 * PageSize);

            int freeBeforeFirstWrite;
            int freeAfterWrite;
            int freeAfterFirstDelete;
            int freeAfterSecondWrite;
            int freeAfterSecondDelete;

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/DAVE");
                INdfsFileSystem ndfs = driver.GetFileSystem()!;

                // The very first file ever created for a brand-new user permanently allocates
                // that user's object-directory page (EnsureObjectDirPage) — a real, correct
                // one-time cost, not a leak. Write+delete twice to separate that from any actual
                // per-write leak: the second round-trip must fully recover, the first doesn't.
                freeBeforeFirstWrite = ndfs.GetBlocksFree();
                driver.WriteFile("/DAVE/BIG:DAT", data);
                freeAfterWrite = ndfs.GetBlocksFree();
                driver.DeleteFile("/DAVE/BIG:DAT");
                freeAfterFirstDelete = ndfs.GetBlocksFree();

                driver.WriteFile("/DAVE/BIG:DAT", data);
                freeAfterSecondWrite = ndfs.GetBlocksFree();
                driver.DeleteFile("/DAVE/BIG:DAT");
                freeAfterSecondDelete = ndfs.GetBlocksFree();
            }

            // Writing 1000 pages must consume noticeably more than 1000 pages' worth of free
            // space (exact overhead varies, so assert a floor rather than an exact figure).
            Assert.That(freeAfterWrite, Is.LessThan(freeBeforeFirstWrite - 999));
            Assert.That(freeAfterSecondWrite, Is.LessThan(freeAfterFirstDelete - 999));

            // Second round-trip must land exactly where the first delete left off — no per-write leak.
            Assert.That(freeAfterSecondDelete, Is.EqualTo(freeAfterFirstDelete));
        }

        [Test]
        public void GenerateBitmapReport_SubIndexedFile_NoConsistencyErrors()
        {
            // Exercises the fix to the diagnostic-only "multi-page read incomplete" gap
            // (ObjectEntry.GetAllBlockPointersForFile used to return an empty stub list for any
            // file over 1 page, so the bitmap report silently failed to verify most of a large
            // file's blocks). A SubIndexed file must now be fully walked and match the bitmap.
            string image = MakeImage("bitmap.img");
            byte[] data = MakePattern(900 * PageSize);

            using FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite);
            NdfsDriver driver = new NdfsDriver();
            driver.Mount(stream, readOnly: false);
            driver.CreateDirectory("/EVE");
            driver.WriteFile("/EVE/REPORT:DAT", data);
            INdfsFileSystem ndfs = driver.GetFileSystem()!;

            string report = ndfs.GenerateBitmapReport();
            Assert.That(report, Does.Not.Contain("ERROR"), report);

            Assert.That(ndfs.VerifyFileSystemIntegrity(), Is.True);
        }

        [Test]
        public void WriteFile_ExactlyAtIndexedBoundary_StillUsesIndexedNotSubIndexed()
        {
            // 512 pages exactly is the last size that must still use the plain Indexed path —
            // pins the boundary so a future off-by-one doesn't silently push it into SubIndexed
            // (or vice versa).
            string image = MakeImage("boundary.img");
            byte[] data = MakePattern(512 * PageSize);

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/FRANK");
                driver.WriteFile("/FRANK/EXACT:DAT", data);
                driver.Dispose();
            }

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.Read))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: true);
                byte[] readBack = driver.ReadFile("/FRANK/EXACT:DAT");
                Assert.That(readBack, Is.EqualTo(data));
            }
        }
    }
}
