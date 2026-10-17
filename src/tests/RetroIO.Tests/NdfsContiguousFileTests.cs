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
using RetroIO.Storage.Ndfs.Creation;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tests
{
    /// <summary>
    /// Covers contiguous file creation, added 2026-07-30 to support the COSMOS file-access
    /// server's <c>Create-file</c> operation when it carries a page count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this, every create path in the library produced an indexed file - the page count had
    /// nowhere to go. SINTRAN's <c>@CREATE-FILE name pages</c> produces a file it reports as
    /// <c>CONTINUOUS FILE</c>: all pages allocated up front, consecutive on the pack, and the file
    /// itself empty.
    /// </para>
    /// <para>
    /// The chosen behaviour on overflow is to fail, matching SINTRAN, which cannot extend a
    /// contiguous run once the pages after it belong to other files.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class NdfsContiguousFileTests
    {
        private string _testDir = string.Empty;

        /// <summary>
        /// Creates the scratch folder the test images live in.
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "RetroFS_NdfsContiguousFileTests");
            Directory.CreateDirectory(_testDir);
        }

        /// <summary>
        /// Removes the scratch folder and every image in it.
        /// </summary>
        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }

        /// <summary>
        /// Creates a blank floppy image with one user directory on it.
        /// </summary>
        /// <param name="name">
        /// The image file name.
        /// </param>
        /// <returns>
        /// The full path of the created image.
        /// </returns>
        private string MakeImage(string name)
        {
            string path = Path.Combine(_testDir, name);
            NdfsImageCreator.CreateImageFromTemplate(
                path,
                NdfsImageTemplate.Floppy12MB,
                directoryName: "CONT-TEST",
                bootLoaderType: NdfsBootLoaderType.Floppy);
            return path;
        }

        /// <summary>
        /// Finds a file's object entry by name, without LINQ.
        /// </summary>
        /// <param name="ndfs">
        /// The mounted filesystem to search.
        /// </param>
        /// <param name="objectName">
        /// The bare object name, without a type suffix.
        /// </param>
        /// <returns>
        /// The matching entry, or <c>null</c> when the pack holds no such file.
        /// </returns>
        private static ObjectEntry? FindEntry(INdfsFileSystem ndfs, string objectName)
        {
            System.Collections.Generic.IReadOnlyList<ObjectEntry> entries = ndfs.GetObjectEntries();
            for (int i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].ObjectName, objectName, StringComparison.OrdinalIgnoreCase))
                {
                    return entries[i];
                }
            }
            return null;
        }

        /// <summary>
        /// A created contiguous file is flagged contiguous, sized to its reservation, and empty.
        /// </summary>
        /// <remarks>
        /// These are the three things a SINTRAN client reads back from the object entry, and the
        /// combination is what makes the machine print <c>CONTINUOUS FILE</c> with zero bytes.
        /// </remarks>
        [Test]
        public void CreateContiguousFile_MarksTheEntryContiguousAndReservesThePages()
        {
            string image = MakeImage("create.img");

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/ALICE");

                INdfsFileSystem ndfs = driver.GetFileSystem()!;
                ndfs.CreateContiguousFile("/ALICE/RESERVED:DATA", 8);

                ObjectEntry? entry = FindEntry(ndfs, "RESERVED");

                Assert.That(entry, Is.Not.Null, "the file was not created");
                Assert.That(entry!.FileTypeFlags.IsContiguous(), Is.True, "must carry the contiguous flag");
                Assert.That(entry.FileTypeFlags.IsIndexed(), Is.False, "must not also claim to be indexed");
                Assert.That(entry.PagesInFile, Is.EqualTo(8u), "the whole reservation is allocated");
                Assert.That(entry.BytesInFile, Is.EqualTo(0uL), "a fresh contiguous file is empty");

                driver.Dispose();
            }
        }

        /// <summary>
        /// The reserved pages are consecutive on the pack and all marked used.
        /// </summary>
        /// <remarks>
        /// This is the property the whole file kind exists for. It is checked against the bitmap
        /// rather than the pointer, because a run that is not actually allocated would be handed
        /// out again to the next file.
        /// </remarks>
        [Test]
        public void CreateContiguousFile_AllocatesOneUnbrokenRunInTheBitmap()
        {
            string image = MakeImage("run.img");

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/ALICE");

                INdfsFileSystem ndfs = driver.GetFileSystem()!;
                ndfs.CreateContiguousFile("/ALICE/RUN:DATA", 6);

                ObjectEntry entry = FindEntry(ndfs, "RUN")!;
                uint start = entry.FilePointer!.BlockID;

                Assert.That(start, Is.GreaterThan(0u), "the file must point at a real block");

                // Page N of the file is block start+N - there is no index block in between, which
                // is exactly why every one of those blocks has to be marked used.
                for (uint i = 0; i < 6; i++)
                {
                    Assert.That(ndfs.IsBlockUsed(start + i), Is.True, "block " + (start + i) + " must be allocated");
                }

                driver.Dispose();
            }
        }

        /// <summary>
        /// Data written into a contiguous file survives a remount and reads back byte for byte.
        /// </summary>
        [Test]
        public void ContiguousFile_WrittenDataSurvivesARemount()
        {
            string image = MakeImage("roundtrip.img");
            byte[] payload = Encoding.ASCII.GetBytes("contiguous payload written in place");

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/ALICE");

                INdfsFileSystem ndfs = driver.GetFileSystem()!;
                ndfs.CreateContiguousFile("/ALICE/PAYLOAD:DATA", 4);
                ndfs.WriteFile("/ALICE/PAYLOAD:DATA", payload);

                driver.Dispose();
            }

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);

                INdfsFileSystem ndfs = driver.GetFileSystem()!;
                byte[] read = ndfs.ReadFile("/ALICE/PAYLOAD:DATA");

                Assert.That(read.Length, Is.EqualTo(payload.Length));
                for (int i = 0; i < payload.Length; i++)
                {
                    Assert.That(read[i], Is.EqualTo(payload[i]), "byte " + i + " differs");
                }

                // Writing into the file must not have converted it or moved its run.
                ObjectEntry entry = FindEntry(ndfs, "PAYLOAD")!;
                Assert.That(entry.FileTypeFlags.IsContiguous(), Is.True, "must still be contiguous after a write");
                Assert.That(entry.PagesInFile, Is.EqualTo(4u), "the reservation must not shrink to fit the data");

                driver.Dispose();
            }
        }

        /// <summary>
        /// A write larger than the reservation fails instead of growing or converting the file.
        /// </summary>
        /// <remarks>
        /// This is the deliberate design choice. SINTRAN cannot grow a continuous file, so silently
        /// converting it to an indexed one would produce a pack that disagrees with what the client
        /// asked for.
        /// </remarks>
        [Test]
        public void ContiguousFile_WriteBeyondTheReservation_Fails()
        {
            string image = MakeImage("overflow.img");

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/ALICE");

                INdfsFileSystem ndfs = driver.GetFileSystem()!;
                ndfs.CreateContiguousFile("/ALICE/SMALL:DATA", 2);

                // 2 pages is 4096 bytes; one byte more must be refused.
                byte[] tooBig = new byte[4097];

                Assert.Throws<InvalidOperationException>(
                    () => ndfs.WriteFile("/ALICE/SMALL:DATA", tooBig));

                // The failed write must leave the file exactly as it was.
                ObjectEntry entry = FindEntry(ndfs, "SMALL")!;
                Assert.That(entry.BytesInFile, Is.EqualTo(0uL), "a refused write must not update the length");
                Assert.That(entry.PagesInFile, Is.EqualTo(2u), "a refused write must not change the reservation");

                driver.Dispose();
            }
        }

        /// <summary>
        /// A write that exactly fills the reservation is accepted.
        /// </summary>
        /// <remarks>
        /// Guards the boundary from the other side: an off-by-one in the page arithmetic would
        /// reject the one size that must always fit.
        /// </remarks>
        [Test]
        public void ContiguousFile_WriteThatExactlyFillsTheReservation_Succeeds()
        {
            string image = MakeImage("exact.img");

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/ALICE");

                INdfsFileSystem ndfs = driver.GetFileSystem()!;
                ndfs.CreateContiguousFile("/ALICE/EXACT:DATA", 3);

                byte[] exact = new byte[3 * 2048];
                for (int i = 0; i < exact.Length; i++)
                {
                    exact[i] = (byte)(i & 0xFF);
                }

                Assert.DoesNotThrow(() => ndfs.WriteFile("/ALICE/EXACT:DATA", exact));

                byte[] read = ndfs.ReadFile("/ALICE/EXACT:DATA");
                Assert.That(read.Length, Is.EqualTo(exact.Length));
                for (int i = 0; i < exact.Length; i++)
                {
                    Assert.That(read[i], Is.EqualTo(exact[i]), "byte " + i + " differs");
                }

                driver.Dispose();
            }
        }

        /// <summary>
        /// A shorter second write does not leave the tail of the previous one readable.
        /// </summary>
        /// <remarks>
        /// The run is not reallocated between writes, so the old bytes are still physically there
        /// unless the write path clears the rest of the reservation.
        /// </remarks>
        [Test]
        public void ContiguousFile_ShorterRewrite_DoesNotLeaveTheOldTailBehind()
        {
            string image = MakeImage("shrink.img");

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/ALICE");

                INdfsFileSystem ndfs = driver.GetFileSystem()!;
                ndfs.CreateContiguousFile("/ALICE/SHRINK:DATA", 3);

                byte[] longData = new byte[3 * 2048];
                for (int i = 0; i < longData.Length; i++)
                {
                    longData[i] = 0xAB;
                }
                ndfs.WriteFile("/ALICE/SHRINK:DATA", longData);

                byte[] shortData = Encoding.ASCII.GetBytes("short");
                ndfs.WriteFile("/ALICE/SHRINK:DATA", shortData);

                byte[] read = ndfs.ReadFile("/ALICE/SHRINK:DATA");
                Assert.That(read.Length, Is.EqualTo(shortData.Length), "the file must report the new length");

                for (int i = 0; i < read.Length; i++)
                {
                    Assert.That(read[i], Is.EqualTo(shortData[i]), "byte " + i + " differs");
                }

                driver.Dispose();
            }
        }

        /// <summary>
        /// Creating a contiguous file over an existing name fails.
        /// </summary>
        [Test]
        public void CreateContiguousFile_OnAnExistingName_Fails()
        {
            string image = MakeImage("dup.img");

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/ALICE");

                INdfsFileSystem ndfs = driver.GetFileSystem()!;
                ndfs.CreateContiguousFile("/ALICE/ONCE:DATA", 2);

                Assert.Throws<InvalidOperationException>(
                    () => ndfs.CreateContiguousFile("/ALICE/ONCE:DATA", 2));

                driver.Dispose();
            }
        }

        /// <summary>
        /// A zero-page reservation is rejected.
        /// </summary>
        /// <remarks>
        /// A file with no pages has no run to point at, so the pointer would be block zero - which
        /// the read path treats as "no data allocated".
        /// </remarks>
        [Test]
        public void CreateContiguousFile_WithZeroPages_Fails()
        {
            string image = MakeImage("zero.img");

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/ALICE");

                INdfsFileSystem ndfs = driver.GetFileSystem()!;

                Assert.Throws<ArgumentOutOfRangeException>(
                    () => ndfs.CreateContiguousFile("/ALICE/EMPTY:DATA", 0));

                driver.Dispose();
            }
        }

        /// <summary>
        /// The user's page count is charged the whole reservation at creation, not at write time.
        /// </summary>
        /// <remarks>
        /// A contiguous file holds its pages whether or not any data reaches them, so the quota has
        /// to move when the run is allocated. Writing into the file afterwards must not charge the
        /// same pages a second time.
        /// </remarks>
        [Test]
        public void CreateContiguousFile_ChargesTheReservationOnceToTheUser()
        {
            string image = MakeImage("quota.img");

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                driver.CreateDirectory("/ALICE");

                INdfsFileSystem ndfs = driver.GetFileSystem()!;

                UserEntry? before = null;
                System.Collections.Generic.IReadOnlyList<UserEntry> users = ndfs.GetUsers();
                for (int i = 0; i < users.Count; i++)
                {
                    if (string.Equals(users[i].UserName, "ALICE", StringComparison.OrdinalIgnoreCase))
                    {
                        before = users[i];
                        break;
                    }
                }
                Assert.That(before, Is.Not.Null, "the test user was not created");
                uint pagesUsedBefore = before!.PagesUsed;

                ndfs.CreateContiguousFile("/ALICE/CHARGED:DATA", 5);
                Assert.That(before.PagesUsed, Is.EqualTo(pagesUsedBefore + 5u),
                    "creation must charge the whole run");

                ndfs.WriteFile("/ALICE/CHARGED:DATA", new byte[100]);
                Assert.That(before.PagesUsed, Is.EqualTo(pagesUsedBefore + 5u),
                    "writing into an already-allocated run must not charge again");

                driver.Dispose();
            }
        }
    }
}
