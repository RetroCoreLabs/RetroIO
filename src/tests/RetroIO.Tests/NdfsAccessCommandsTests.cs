//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using NUnit.Framework;

using RetroIO.Storage.Ndfs.Creation;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.FileSystems;
using RetroIO.Storage.Ndfs.Security;

namespace RetroIO.Tests
{
    /// <summary>
    /// The SINTRAN access and friend commands on the NDFS file system:
    /// <c>@LIST-FRIENDS</c>, <c>@CREATE-FRIEND</c>, <c>@DELETE-FRIEND</c>,
    /// <c>@SET-FRIEND-ACCESS</c>, <c>@SET-FILE-ACCESS</c> and
    /// <c>@SET-DEFAULT-FILE-ACCESS</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every change is checked after the image is closed and mounted again, so a change that
    /// only lives in memory fails. The same cases are tested in the norskdata-ndfs C, Python and
    /// TypeScript libraries (test_write_comprehensive.c, test_write_persistence.py,
    /// write-persistence.test.ts).
    /// </para>
    /// <para>
    /// Access word: OWN bits 0-4, FRIEND 5-9, PUBLIC 10-14; R=1 W=2 A=4 C=8 D=16 in each tier.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class NdfsAccessCommandsTests
    {
        private const string Owner = "ALICE";
        private const string Friend = "BOB";
        private const string DataFile = "ALICE/DATA:SYMB";

        private string _testDir = string.Empty;

        /// <summary>
        /// Creates a scratch directory for the images these tests build.
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "retrofs-access-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);
        }

        /// <summary>
        /// Removes the scratch directory.
        /// </summary>
        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }

        /// <summary>
        /// Builds a floppy image with users ALICE and BOB and one file ALICE/DATA:SYMB, and
        /// closes it again.
        /// </summary>
        /// <param name="name">
        /// Distinguishes the image file so tests do not collide.
        /// </param>
        /// <returns>
        /// The image path.
        /// </returns>
        private string MakeImage(string name)
        {
            string imagePath = Path.Combine(_testDir, name + ".img");
            NdfsImageCreator.CreateImageFromTemplate(
                imagePath,
                NdfsImageTemplate.Floppy360KB,
                directoryName: "ACCESS",
                bootLoaderType: NdfsBootLoaderType.Floppy);

            using (NdfsDriver driver = Open(imagePath, readOnly: false, out INdfsFileSystem fs))
            {
                driver.CreateDirectory("/" + Owner);
                driver.CreateDirectory("/" + Friend);
                fs.WriteFile(DataFile, Encoding.ASCII.GetBytes("HELLO\r\n"));
            }
            return imagePath;
        }

        /// <summary>
        /// Mounts an image.
        /// </summary>
        /// <param name="imagePath">
        /// The image file.
        /// </param>
        /// <param name="readOnly">
        /// Mount read-only.
        /// </param>
        /// <param name="fs">
        /// Receives the mounted file system.
        /// </param>
        /// <returns>
        /// The driver, which the caller must dispose.
        /// </returns>
        private static NdfsDriver Open(string imagePath, bool readOnly, out INdfsFileSystem fs)
        {
            FileStream stream = new FileStream(imagePath, FileMode.Open, readOnly ? FileAccess.Read : FileAccess.ReadWrite);
            NdfsDriver driver = new NdfsDriver();
            driver.Mount(stream, readOnly: readOnly);
            INdfsFileSystem? mounted = driver.GetFileSystem();
            Assert.That(mounted, Is.Not.Null, "driver must expose the NDFS filesystem");
            fs = mounted!;
            return driver;
        }

        // ---------------------------------------------------------------
        // An empty file
        // ---------------------------------------------------------------

        /// <summary>
        /// Finds a file's object entry by object name.
        /// </summary>
        /// <param name="fs">
        /// The mounted file system.
        /// </param>
        /// <param name="objectName">
        /// The object name, without the type.
        /// </param>
        /// <returns>
        /// The entry.
        /// </returns>
        private static ObjectEntry EntryOf(INdfsFileSystem fs, string objectName)
        {
            IReadOnlyList<ObjectEntry> entries = fs.GetObjectEntries();
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].ObjectName == objectName)
                    return entries[i];
            }
            Assert.Fail("no entry " + objectName);
            return null!;
        }

        /// <summary>
        /// Pages the owner has used.
        /// </summary>
        /// <param name="fs">
        /// The mounted file system.
        /// </param>
        /// <returns>
        /// The owner's pages-used count.
        /// </returns>
        private static uint OwnerPagesUsed(INdfsFileSystem fs)
        {
            IReadOnlyList<UserEntry> users = fs.GetUsers();
            for (int i = 0; i < users.Count; i++)
            {
                if (users[i].UserName == Owner)
                    return users[i].PagesUsed;
            }
            Assert.Fail("no user " + Owner);
            return 0;
        }

        /// <summary>
        /// An empty file owns no page: 0 pages, file pointer 0, no index block, the indexed flag,
        /// and no change to the free page count or the owner's quota. Overwriting a file with
        /// nothing gives its pages back, and the empty file can be filled again.
        /// </summary>
        /// <remarks>
        /// Measured 2026-11-07 on nd100x (a copy of SMD0.IMG): <c>@CREATE-FILE E0:SYMB,0</c>,
        /// <c>@COPY-FILE "C0:SYMB",E0:SYMB</c> and a copy of an empty file that held one page all
        /// gave pages 0, pointer 0, bytes 0xFFFFFFFF, flags 0x0008, and the free page map did not
        /// move. Checked again after the image is mounted a second time.
        /// </remarks>
        [Test]
        public void EmptyFileOwnsNoPage()
        {
            string imagePath = MakeImage("empty");
            uint usedBefore;
            uint freeBefore;

            using (NdfsDriver driver = Open(imagePath, readOnly: false, out INdfsFileSystem fs))
            {
                usedBefore = OwnerPagesUsed(fs);
                freeBefore = fs.GetBitFile().GetFreePages();
                fs.WriteFile("ALICE/E0:SYMB", Array.Empty<byte>());
                fs.WriteFile("ALICE/F3:DATA", new byte[] { 0x33, 0x33, 0x33 });
                Assert.That(OwnerPagesUsed(fs), Is.EqualTo(usedBefore + 2), "1 data page + 1 index block");
                fs.WriteFile("ALICE/F3:DATA", Array.Empty<byte>());
            }

            using (NdfsDriver driver = Open(imagePath, readOnly: false, out INdfsFileSystem fs))
            {
                ObjectEntry e0 = EntryOf(fs, "E0");
                Assert.That(e0.PagesInFile, Is.EqualTo(0u));
                Assert.That(e0.BytesInFile, Is.EqualTo(0UL));
                Assert.That(e0.FilePointer!.BlockID, Is.EqualTo(0u));
                Assert.That(e0.FileTypeFlags, Is.EqualTo(FileTypes.IndexedFile));
                ObjectEntry f3 = EntryOf(fs, "F3");
                Assert.That(f3.PagesInFile, Is.EqualTo(0u));
                Assert.That(f3.FilePointer!.BlockID, Is.EqualTo(0u));
                Assert.That(OwnerPagesUsed(fs), Is.EqualTo(usedBefore));
                Assert.That(fs.GetBitFile().GetFreePages(), Is.EqualTo(freeBefore));
                Assert.That(fs.ReadFile("ALICE/E0:SYMB").Length, Is.EqualTo(0));

                fs.WriteFile("ALICE/E0:SYMB", Encoding.ASCII.GetBytes("FILLED\r\n"));
                Assert.That(Encoding.ASCII.GetString(fs.ReadFile("ALICE/E0:SYMB")), Is.EqualTo("FILLED\r\n"));
                Assert.That(fs.VerifyFileSystemIntegrity(), Is.True);
            }
        }

        // ---------------------------------------------------------------
        // SINTRAN's written form
        // ---------------------------------------------------------------

        /// <summary>
        /// One access field: letters in any order and case, N alone for none.
        /// </summary>
        [Test]
        public void ParseAccessRightsReadsSintranLetters()
        {
            Assert.That(SintranAccessForm.ParseAccessRights("R"), Is.EqualTo(0x01));
            Assert.That(SintranAccessForm.ParseAccessRights("rwa"), Is.EqualTo(0x07));
            Assert.That(SintranAccessForm.ParseAccessRights("DCAWR"), Is.EqualTo(0x1F));
            Assert.That(SintranAccessForm.ParseAccessRights("N"), Is.EqualTo(0x00));
        }

        /// <summary>
        /// N must stand alone (@SET-FILE-ACCESS rule 2); unknown letters and empty fields are refused.
        /// </summary>
        [Test]
        public void ParseAccessRightsRefusesBadFields()
        {
            Assert.Throws<ArgumentException>(() => SintranAccessForm.ParseAccessRights("NR"));
            Assert.Throws<ArgumentException>(() => SintranAccessForm.ParseAccessRights("RX"));
            Assert.Throws<ArgumentException>(() => SintranAccessForm.ParseAccessRights(""));
        }

        /// <summary>
        /// PUBLIC,FRIEND,OWN in SINTRAN's order; R,RWA,RWACD is SINTRAN's own default 0x04FF.
        /// </summary>
        [Test]
        public void ApplyAccessTripleSetsAllThreeTiers()
        {
            Assert.That(SintranAccessForm.ApplyAccessTriple(0, "R,RWA,RWACD"), Is.EqualTo(0x04FF));
            Assert.That(SintranAccessForm.ApplyAccessTriple(0x7FFF, "N,N,N"), Is.EqualTo(0x0000));
            Assert.That(SintranAccessForm.ApplyAccessTriple(0, " R , RWA , RWACD "), Is.EqualTo(0x04FF), "spaces are ignored");
        }

        /// <summary>
        /// An empty or missing field keeps that tier.
        /// </summary>
        [Test]
        public void ApplyAccessTripleKeepsEmptyFields()
        {
            Assert.That(SintranAccessForm.ApplyAccessTriple(0x04FF, ",,N"), Is.EqualTo(0x04E0), "only OWN changes");
            Assert.That(SintranAccessForm.ApplyAccessTriple(0x04FF, ",RW,"), Is.EqualTo(0x047F), "only FRIEND changes");
            Assert.That(SintranAccessForm.ApplyAccessTriple(0x04FF, "N"), Is.EqualTo(0x00FF), "only PUBLIC given");
            Assert.That(SintranAccessForm.ApplyAccessTriple(0x04FF, ""), Is.EqualTo(0x04FF), "nothing given");
        }

        /// <summary>
        /// A fourth field or a bad letter is refused.
        /// </summary>
        [Test]
        public void ApplyAccessTripleRefusesBadInput()
        {
            Assert.Throws<ArgumentException>(() => SintranAccessForm.ApplyAccessTriple(0, "R,R,R,R"));
            Assert.Throws<ArgumentException>(() => SintranAccessForm.ApplyAccessTriple(0, "R,NW,R"));
        }

        /// <summary>
        /// The written form reads back to the same word, for every 15-bit access word.
        /// </summary>
        [Test]
        public void FormatAccessTripleRoundTrips()
        {
            Assert.That(SintranAccessForm.FormatAccessTriple(0x04FF), Is.EqualTo("R,RWA,RWACD"));
            Assert.That(SintranAccessForm.FormatAccessTriple(0x0000), Is.EqualTo("N,N,N"));
            Assert.That(SintranAccessForm.FormatAccessRights(0x15), Is.EqualTo("RAD"));

            for (int bits = 0; bits <= 0x7FFF; bits++)
            {
                string text = SintranAccessForm.FormatAccessTriple((ushort)bits);
                ushort back = SintranAccessForm.ApplyAccessTriple(0, text);
                if (back != bits)
                    Assert.Fail($"0x{bits:X4} -> '{text}' -> 0x{back:X4}");
            }
        }

        // ---------------------------------------------------------------
        // Friends
        // ---------------------------------------------------------------

        /// <summary>
        /// @CREATE-FRIEND gives RWA; @SET-FRIEND-ACCESS changes the rights in place
        /// (the manual's example gives W); @DELETE-FRIEND removes it. Each step survives a remount.
        /// </summary>
        [Test]
        public void FriendCommandsPersist()
        {
            string image = MakeImage("friends");

            using (Open(image, false, out INdfsFileSystem fs))
            {
                Assert.That(fs.ListFriends(Owner).Count, Is.EqualTo(0), "a new user has no friends");
                fs.AddFriend(Owner, Friend);
            }

            using (Open(image, false, out INdfsFileSystem fs))
            {
                IReadOnlyList<NdfsFriendInfo> friends = fs.ListFriends(Owner);
                Assert.That(friends.Count, Is.EqualTo(1));
                Assert.That(friends[0].Name, Is.EqualTo(Friend));
                Assert.That(friends[0].Rights, Is.EqualTo("RWA--"), "SINTRAN gives a new friend RWA");
                fs.SetFriendAccess(Owner, Friend, "W");
            }

            using (Open(image, false, out INdfsFileSystem fs))
            {
                IReadOnlyList<NdfsFriendInfo> friends = fs.ListFriends(Owner);
                Assert.That(friends.Count, Is.EqualTo(1));
                Assert.That(friends[0].Rights, Is.EqualTo("-W---"));
                fs.RemoveFriend(Owner, Friend);
            }

            using (Open(image, false, out INdfsFileSystem fs))
            {
                Assert.That(fs.ListFriends(Owner).Count, Is.EqualTo(0));
            }
        }

        /// <summary>
        /// A friend set to N stays on the list with no rights; empty rights change nothing.
        /// </summary>
        [Test]
        public void SetFriendAccessNoneKeepsFriendAndEmptyChangesNothing()
        {
            string image = MakeImage("friendnone");

            using (Open(image, false, out INdfsFileSystem fs))
            {
                fs.AddFriend(Owner, Friend);
                fs.SetFriendAccess(Owner, Friend, "");
                Assert.That(fs.ListFriends(Owner)[0].Rights, Is.EqualTo("RWA--"), "empty = no change");
                fs.SetFriendAccess(Owner, Friend, "N");
            }

            using (Open(image, false, out INdfsFileSystem fs))
            {
                IReadOnlyList<NdfsFriendInfo> friends = fs.ListFriends(Owner);
                Assert.That(friends.Count, Is.EqualTo(1), "N keeps the friend on the list");
                Assert.That(friends[0].Bits & 0x1F00, Is.EqualTo(0), "no rights bits left");
            }
        }

        /// <summary>
        /// Changing a user who is not a friend is refused, as is adding one twice.
        /// </summary>
        [Test]
        public void FriendCommandsRefuseWrongFriend()
        {
            string image = MakeImage("friendbad");

            using (Open(image, false, out INdfsFileSystem fs))
            {
                Assert.Throws<ArgumentException>(() => fs.SetFriendAccess(Owner, Friend, "R"), "not a friend yet");
                Assert.Throws<ArgumentException>(() => fs.RemoveFriend(Owner, Friend), "not a friend yet");
                fs.AddFriend(Owner, Friend);
                Assert.Throws<ArgumentException>(() => fs.AddFriend(Owner, Friend), "already a friend");
                Assert.Throws<ArgumentException>(() => fs.AddFriend(Owner, "NOSUCHUSER"));
            }
        }

        // ---------------------------------------------------------------
        // File access and default file access
        // ---------------------------------------------------------------

        /// <summary>
        /// @SET-FILE-ACCESS: the new word survives a remount.
        /// </summary>
        [Test]
        public void SetFileAccessPersists()
        {
            string image = MakeImage("fileaccess");

            using (Open(image, false, out INdfsFileSystem fs))
            {
                ushort before = fs.GetFileAccess(DataFile);
                ushort after = SintranAccessForm.ApplyAccessTriple(before, "N,R,RWACD");
                fs.SetFileAccess(DataFile, after);
            }

            using (Open(image, false, out INdfsFileSystem fs))
            {
                Assert.That(fs.GetFileAccess(DataFile), Is.EqualTo(0x003F), "PUBLIC none, FRIEND R, OWN RWACD");
            }
        }

        /// <summary>
        /// A missing file is reported, not ignored.
        /// </summary>
        [Test]
        public void SetFileAccessRefusesMissingFile()
        {
            string image = MakeImage("filemissing");

            using (Open(image, false, out INdfsFileSystem fs))
            {
                Assert.Throws<FileNotFoundException>(() => fs.SetFileAccess("ALICE/NOFILE:SYMB", 0x04FF));
                Assert.Throws<FileNotFoundException>(() => fs.GetFileAccess("ALICE/NOFILE:SYMB"));
            }
        }

        /// <summary>
        /// @SET-DEFAULT-FILE-ACCESS: a new user starts at SINTRAN's 0x04FF and the change survives
        /// a remount.
        /// </summary>
        [Test]
        public void SetDefaultFileAccessPersists()
        {
            string image = MakeImage("default");

            using (Open(image, false, out INdfsFileSystem fs))
            {
                Assert.That(fs.GetDefaultFileAccess(Owner), Is.EqualTo(0x04FF), "R,RWA,RWACD");
                fs.SetDefaultFileAccess(Owner, SintranAccessForm.ApplyAccessTriple(fs.GetDefaultFileAccess(Owner), "N,,"));
            }

            using (Open(image, false, out INdfsFileSystem fs))
            {
                Assert.That(fs.GetDefaultFileAccess(Owner), Is.EqualTo(0x00FF));
            }
        }

        /// <summary>
        /// A new file gets its owner's default access, indexed and contiguous alike, as SINTRAN
        /// gives it: measured 2026-11-04 on nd100x, with SYSTEM's default set to N,RW,RWACD,
        /// <c>@CREATE-FILE ACCTEST:SYMB,0</c> and <c>@CREATE-FILE ACCCON:DATA,4</c> both stored 0x007F.
        /// </summary>
        [Test]
        public void NewFilesGetTheOwnersDefaultAccess()
        {
            string image = MakeImage("newfiles");

            using (Open(image, false, out INdfsFileSystem fs))
            {
                fs.SetDefaultFileAccess(Owner, SintranAccessForm.ApplyAccessTriple(0, "N,RW,RWACD"));
                fs.WriteFile("ALICE/NEWIDX:SYMB", Encoding.ASCII.GetBytes("X\r\n"));
                fs.CreateContiguousFile("ALICE/NEWCON:DATA", 2);
            }

            using (Open(image, true, out INdfsFileSystem fs))
            {
                Assert.That(fs.GetFileAccess("ALICE/NEWIDX:SYMB"), Is.EqualTo((ushort)0x007F), "indexed");
                Assert.That(fs.GetFileAccess("ALICE/NEWCON:DATA"), Is.EqualTo((ushort)0x007F), "contiguous");
                Assert.That(fs.GetFileAccess(DataFile), Is.EqualTo((ushort)0x04FF), "a file made before the change keeps its own");
            }
        }

        /// <summary>
        /// On a read-only mount every change is refused and nothing in memory moves.
        /// </summary>
        [Test]
        public void ReadOnlyMountRefusesChanges()
        {
            string image = MakeImage("readonly");

            using (Open(image, true, out INdfsFileSystem fs))
            {
                Assert.Throws<InvalidOperationException>(() => fs.AddFriend(Owner, Friend));
                Assert.Throws<InvalidOperationException>(() => fs.SetFileAccess(DataFile, 0));
                Assert.Throws<InvalidOperationException>(() => fs.SetDefaultFileAccess(Owner, 0));
                Assert.That(fs.ListFriends(Owner).Count, Is.EqualTo(0));
                Assert.That(fs.GetDefaultFileAccess(Owner), Is.EqualTo(0x04FF));
            }
        }
    }
}
