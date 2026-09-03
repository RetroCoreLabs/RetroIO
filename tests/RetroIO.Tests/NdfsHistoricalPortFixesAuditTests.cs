//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Ndfs.Creation;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tests
{
    [TestFixture]
    public class NdfsHistoricalPortFixesAuditTests
    {
        private string _testDir = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "RetroFS_NdfsHistoricalAudit_" + Path.GetRandomFileName());
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
                directoryName: "AUDIT-TEST",
                bootLoaderType: NdfsBootLoaderType.Winchester);
            return path;
        }

        [Test]
        public void PerUserPartition_FilesForDifferentUsers_LandInEachOwnersRegion()
        {
            // "Per-user object-file partitioning" — user U owns object slots U*256..U*256+255. A
            // flat global slot allocator would place ALICE's file inside SYSTEM's region.
            string image = MakeImage("partition.img");

            using FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite);
            NdfsDriver driver = new NdfsDriver();
            driver.Mount(stream, readOnly: false);
            driver.CreateDirectory("/ALICE");
            driver.CreateDirectory("/BOB");

            driver.WriteFile("/ALICE/FILE:DAT", new byte[] { 1 });
            driver.WriteFile("/BOB/FILE:DAT", new byte[] { 2 });

            INdfsFileSystem ndfs = driver.GetFileSystem()!;
            var aliceUser = ndfs.GetUser(FindUserIndex(ndfs, "ALICE"));
            var bobUser = ndfs.GetUser(FindUserIndex(ndfs, "BOB"));
            Assert.That(aliceUser, Is.Not.Null);
            Assert.That(bobUser, Is.Not.Null);

            var objects = ndfs.GetObjectFile().GetObjects();
            ObjectEntryOwnerCheck(objects, "ALICE", aliceUser!.UserIndex);
            ObjectEntryOwnerCheck(objects, "BOB", bobUser!.UserIndex);
        }

        private static int FindUserIndex(INdfsFileSystem ndfs, string userName)
        {
            var users = ndfs.GetUsers();
            for (int i = 0; i < users.Count; i++)
            {
                if (string.Equals(users[i].UserName, userName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return users[i].UserIndex;
                }
            }
            Assert.Fail($"User '{userName}' not found");
            return -1;
        }

        private static void ObjectEntryOwnerCheck(System.Collections.Generic.IReadOnlyList<RetroIO.Storage.Ndfs.Elements.ObjectEntry> objects, string userName, int expectedUserIndex)
        {
            for (int i = 0; i < objects.Count; i++)
            {
                if (string.Equals(objects[i].UserName, userName, System.StringComparison.OrdinalIgnoreCase))
                {
                    // The object index's high byte must be the owning user's index — that's what
                    // "lands in the owner's region" actually means on disk.
                    int ownerFromObjectIndex = objects[i].ObjectIndex >> 8;
                    Assert.That(ownerFromObjectIndex, Is.EqualTo(expectedUserIndex),
                        $"{userName}'s file's ObjectIndex high byte must equal their UserIndex, not land in another user's region");
                    return;
                }
            }
            Assert.Fail($"No object found for user '{userName}'");
        }

        [Test]
        public void VersionChain_NewFile_IsSelfReferential()
        {
            // A single-version file must point its Next/Prev version at its OWN object index —
            // zeroed version pointers make SINTRAN report a broken chain (";2" instead of no version suffix).
            string image = MakeImage("version.img");

            using FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite);
            NdfsDriver driver = new NdfsDriver();
            driver.Mount(stream, readOnly: false);
            driver.CreateDirectory("/CAROL");
            driver.WriteFile("/CAROL/FILE:DAT", new byte[] { 1, 2, 3 });

            INdfsFileSystem ndfs = driver.GetFileSystem()!;
            var objects = ndfs.GetObjectFile().GetObjects();

            bool found = false;
            for (int i = 0; i < objects.Count; i++)
            {
                if (string.Equals(objects[i].UserName, "CAROL", System.StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    Assert.That(objects[i].ObjectEntryNextVersion, Is.EqualTo((uint)objects[i].ObjectIndex),
                        "single-version file must be self-referential (Next), not zero");
                    Assert.That(objects[i].ObjectEntryPrevVersion, Is.EqualTo((uint)objects[i].ObjectIndex),
                        "single-version file must be self-referential (Prev), not zero");
                }
            }
            Assert.That(found, Is.True);
        }

        [Test]
        public void NameTerminator_LongAndShortNames_SurviveRoundTrip()
        {
            // "Name field encoding" — the field must be terminated by ONE 0x27 byte followed by
            // NULs, not padded entirely with 0x27 (that broke SINTRAN visibility for real). Test
            // both a name shorter than the 16-byte field (needs the terminator) and one that fills
            // it exactly (no room for a terminator, per the on-disk format).
            string image = MakeImage("names.img");

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/DAVE");
                driver.WriteFile("/DAVE/AB:DAT", new byte[] { 1 });               // short name
                driver.WriteFile("/DAVE/SIXTEENCHARNAME:DAT", new byte[] { 2 });  // exactly 16 chars
                driver.Dispose();
            }

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.Read))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: true);
                var entries = driver.ListDirectory("/DAVE");

                bool foundShort = false, foundLong = false;
                for (int i = 0; i < entries.Length; i++)
                {
                    if (string.Equals(entries[i].Name, "AB:DAT", System.StringComparison.OrdinalIgnoreCase)) foundShort = true;
                    if (string.Equals(entries[i].Name, "SIXTEENCHARNAME:DAT", System.StringComparison.OrdinalIgnoreCase)) foundLong = true;
                }

                Assert.That(foundShort, Is.True, "short name must round-trip without extra 0x27 padding leaking into the name");
                Assert.That(foundLong, Is.True, "a name filling the whole 16-byte field (no terminator room) must still round-trip correctly");
            }
        }

        [Test]
        public void DeleteClear_DeletedFile_DoesNotReappearAfterRemount()
        {
            // "Whole-emptied page zeroing" — deleting a file must not leave stale bytes that make
            // it reappear on the next mount.
            string image = MakeImage("deleteclear.img");

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/EVE");
                driver.WriteFile("/EVE/GONE:DAT", new byte[] { 1, 2, 3 });
                driver.DeleteFile("/EVE/GONE:DAT");
                driver.Dispose();
            }

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.Read))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: true);
                var entries = driver.ListDirectory("/EVE");

                for (int i = 0; i < entries.Length; i++)
                {
                    Assert.That(entries[i].Name, Is.Not.EqualTo("GONE:DAT"), "deleted file must not reappear after remount");
                }
            }
        }

        [Test]
        public void Over512PageGuard_LargeFile_UsesSubIndexedNotRejected()
        {
            // The ">512 guard" — files over 512 pages must use SubIndexed (this session's own fix),
            // not the old unconditional rejection this item's wording refers to.
            string image = MakeImage("large.img");
            byte[] data = new byte[600 * 2048];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251 + 1);

            using FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite);
            NdfsDriver driver = new NdfsDriver();
            driver.Mount(stream, readOnly: false);
            driver.CreateDirectory("/FRANK");

            Assert.DoesNotThrow(() => driver.WriteFile("/FRANK/BIG:DAT", data));
            Assert.That(driver.ReadFile("/FRANK/BIG:DAT"), Is.EqualTo(data));
        }
    }
}
