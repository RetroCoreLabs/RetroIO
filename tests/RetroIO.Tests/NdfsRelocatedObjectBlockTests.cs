//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

using NUnit.Framework;

using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tests
{
    /// <summary>
    /// File numbering when SINTRAN has RELOCATED a user's overflow object block.
    /// </summary>
    /// <remarks>
    /// <para><b>What this pack contains, and why it matters</b></para>
    /// <c>BIGDISK0-K-201users.img.gz</c> is a real SINTRAN III K pack captured on 2026-08-02
    /// from a live machine, carrying <b>201 users</b> — the first pack anyone here has had with
    /// more than 64 users AND a multi-block user at the same time.
    /// <para>
    /// On it, user 8 holds 300 files. Its block 0 is in index-block group 0; its overflow block
    /// is in group <b>4</b>, not group 1. That is because SINTRAN <b>skips</b> a group whose
    /// slot belongs to someone else, and <b>relocates</b> an overflow block when the rightful
    /// home owner later needs it. The block was observed moving twice:
    /// </para>
    ///  - initially at group 2
    ///  - user 136 (home = group 2, slot 8) created files → moved to group 3
    ///  - user 200 (home = group 3, slot 8) created files → moved to group 4
    /// <para>
    /// All 300 files survived every move, and SINTRAN reported them throughout as FILE 0..299.
    /// </para>
    /// <para><b>The bug this pins down</b></para>
    /// The old formula derived the logical block from the PHYSICAL group
    /// (<c>block = page / 512</c>), which gave user 8 numbers 0..255 and then
    /// <b>1024..1067</b> where SINTRAN says <b>256..299</b>. The logical block is the ORDINAL
    /// RANK of the group among those the user occupies, ascending. No synthetic image can
    /// exercise this, because our own writer never relocates — only a pack SINTRAN itself
    /// rearranged can.
    /// </remarks>
    [TestFixture]
    public class NdfsRelocatedObjectBlockTests
    {
        private const string FixtureName = "BIGDISK0-K-201users.img.gz";

        private string _imagePath = string.Empty;
        private FileStream? _stream;
        private NdfsDriver? _driver;
        private INdfsFileSystem? _fs;

        /// <summary>
        /// Decompresses the captured pack to a scratch file and mounts it read-only.
        /// </summary>
        [OneTimeSetUp]
        public void Mount()
        {
            string gz = Path.Combine(
                TestContext.CurrentContext.TestDirectory, "TestData", FixtureName);
            Assert.That(File.Exists(gz), Is.True, $"fixture missing: {gz}");

            _imagePath = Path.Combine(Path.GetTempPath(), "retrofs-201users-" + Guid.NewGuid().ToString("N") + ".img");
            using (FileStream src = File.OpenRead(gz))
            using (GZipStream unzip = new GZipStream(src, CompressionMode.Decompress))
            using (FileStream dst = File.Create(_imagePath))
            {
                unzip.CopyTo(dst);
            }

            _stream = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            _driver = new NdfsDriver();
            _driver.Mount(_stream, readOnly: true);
            _fs = _driver.GetFileSystem();
            Assert.That(_fs, Is.Not.Null, "the captured pack must mount");
        }

        /// <summary>
        /// Releases the mount and removes the scratch image.
        /// </summary>
        [OneTimeTearDown]
        public void Unmount()
        {
            _driver?.Dispose();
            _stream?.Dispose();
            try
            {
                if (File.Exists(_imagePath)) File.Delete(_imagePath);
            }
            catch
            {
                // A leftover scratch file in TEMP is harmless.
            }
        }

        /// <summary>
        /// Returns the file numbers a user's entries carry, ascending.
        /// </summary>
        /// <param name="userIndex">
        /// The user to collect.
        /// </param>
        /// <returns>
        /// The sorted file numbers.
        /// </returns>
        private List<int> FileNumbersOf(int userIndex)
        {
            List<int> numbers = new List<int>();
            IReadOnlyList<ObjectEntry> all = _fs!.GetObjectEntries();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].UserIndex == userIndex) numbers.Add(all[i].FileNumber);
            }
            numbers.Sort();
            return numbers;
        }

        /// <summary>
        /// The pack really does carry more than 64 users.
        /// </summary>
        /// <remarks>
        /// If this ever fails the fixture was replaced, and every other test here is
        /// exercising nothing.
        /// </remarks>
        [Test]
        public void FixtureHasMoreThanSixtyFourUsers()
        {
            Assert.That(_fs!.GetUsers().Count, Is.EqualTo(201));
        }

        /// <summary>
        /// User 8's overflow block really is relocated, not at the naive position.
        /// </summary>
        /// <remarks>
        /// The naive model puts block 1 at group 1 (page 576). It is at group 4 (page 2112),
        /// because groups 1, 2 and 3 are the homes of users 72, 136 and 200.
        /// </remarks>
        [Test]
        public void UserEightsOverflowBlockIsRelocated()
        {
            HashSet<int> groups = new HashSet<int>();
            IReadOnlyList<ObjectEntry> all = _fs!.GetObjectEntries();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].UserIndex != 8) continue;
                groups.Add((all[i].ObjectIndex / 32) / 512);
            }

            List<int> sorted = new List<int>(groups);
            sorted.Sort();

            Assert.That(sorted, Is.EqualTo(new List<int> { 0, 4 }),
                "block 0 in the home group 0, overflow relocated to group 4");
            Assert.That(sorted, Does.Not.Contain(1),
                "group 1 is user 72's home - SINTRAN skipped it");
        }

        /// <summary>
        /// User 8's 300 files are numbered 0..299, exactly as SINTRAN reports them.
        /// </summary>
        /// <remarks>
        /// This is the assertion the old physical-group formula failed: it produced
        /// 0..255 then 1024..1067.
        /// </remarks>
        [Test]
        public void UserEightIsNumberedZeroToTwoHundredNinetyNine()
        {
            List<int> numbers = FileNumbersOf(8);

            Assert.That(numbers.Count, Is.EqualTo(300));
            for (int i = 0; i < numbers.Count; i++)
            {
                Assert.That(numbers[i], Is.EqualTo(i),
                    $"file {i} must be numbered {i}; the physical-group formula gave {numbers[i]}");
            }
        }

        /// <summary>
        /// The relocation did not disturb the users that displaced it.
        /// </summary>
        /// <param name="userIndex">
        /// A user whose home group user 8's overflow block passed through.
        /// </param>
        /// <param name="expected">
        /// How many files that user holds.
        /// </param>
        [TestCase(72, 6)]
        [TestCase(136, 3)]
        [TestCase(200, 2)]
        public void DisplacingUsersAreIntactAndNumberedFromZero(int userIndex, int expected)
        {
            List<int> numbers = FileNumbersOf(userIndex);

            Assert.That(numbers.Count, Is.EqualTo(expected));
            for (int i = 0; i < numbers.Count; i++)
            {
                Assert.That(numbers[i], Is.EqualTo(i));
            }
        }

        /// <summary>
        /// High users are readable at all, and own their files.
        /// </summary>
        /// <remarks>
        /// Byte 34 carries the true owner, so a user past 63 must still attribute correctly —
        /// the old model derived the owner from the physical position's high byte, which breaks
        /// down completely here.
        /// </remarks>
        [Test]
        public void HighUsersOwnTheirFiles()
        {
            UserEntry? u200 = null;
            IReadOnlyList<UserEntry> users = _fs!.GetUsers();
            for (int i = 0; i < users.Count; i++)
            {
                if (users[i].UserIndex == 200) u200 = users[i];
            }

            Assert.That(u200, Is.Not.Null, "user 200 must exist");
            Assert.That(u200!.UserName, Is.EqualTo("BIGU200"));
            Assert.That(FileNumbersOf(200).Count, Is.EqualTo(2));
        }
    }
}
