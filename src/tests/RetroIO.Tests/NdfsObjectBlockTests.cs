//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using NUnit.Framework;

using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Tests
{
    /// <summary>
    /// Object blocks: more than 256 files in one SINTRAN user area.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A user area holds 256 files per <i>object block</i>, one block by default and up to 16
    /// (4096 files) on version K. RetroFS assumed 256 was a hard limit until 2026-08-01; these
    /// tests cover the structure that assumption hid.
    /// </para>
    /// <para>
    /// The numbers asserted here were measured on a live SINTRAN III K pack - see
    /// <c>docs/NDFS-Object-Blocks.md</c>.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class NdfsObjectBlockTests
    {
        private const int FilesPerObjectBlock = 256;

        // ---------------------------------------------------------------
        // Byte 47: MXOBL / ACOBL
        // ---------------------------------------------------------------

        /// <summary>
        /// A fresh user gets one object block, which is what SINTRAN does.
        /// </summary>
        [Test]
        public void DefaultUserEntryHasOneObjectBlock()
        {
            UserEntry ue = new UserEntry();
            Assert.That(ue.MaxObjectBlocks, Is.EqualTo(1));
            Assert.That(ue.AllocatedObjectBlocks, Is.EqualTo(1));
        }

        /// <summary>
        /// Byte 47 parses and re-serialises without loss. <c>0x31</c> is the real value read off
        /// user BIGMAN, whose reported MAXIMUM NUMBER OF FILES was 1024 while holding 482 files.
        /// </summary>
        [TestCase(0x00, 1, 1)]    // default user: 256 files
        [TestCase(0x31, 4, 2)]    // BIGMAN on the verified pack
        [TestCase(0xFF, 16, 16)]  // the SINTRAN K ceiling: 4096 files
        [TestCase(0x10, 2, 1)]    // granted a second block, not yet used
        public void Byte47RoundTrips(int raw, int expectedMax, int expectedAllocated)
        {
            byte[] data = new byte[64];
            data[0] = 0x81;               // entry-used | user-entry flag, else FromBytes rejects it
            data[47] = (byte)raw;

            UserEntry? ue = UserEntry.FromBytes(data, 0);
            Assert.That(ue, Is.Not.Null);
            Assert.That(ue.MaxObjectBlocks, Is.EqualTo(expectedMax));
            Assert.That(ue.AllocatedObjectBlocks, Is.EqualTo(expectedAllocated));
            Assert.That(ue.ToBytes()[47], Is.EqualTo((byte)raw));
        }

        /// <summary>
        /// A caller cannot store a count SINTRAN could not express.
        /// </summary>
        [Test]
        public void Byte47WriteIsClamped()
        {
            UserEntry ue = new UserEntry();

            ue.MaxObjectBlocks = 99;
            ue.AllocatedObjectBlocks = 99;
            Assert.That(ue.ToBytes()[47], Is.EqualTo((byte)0xFF), "16 max, 16 allocated");

            ue.MaxObjectBlocks = 0;
            ue.AllocatedObjectBlocks = 0;
            Assert.That(ue.ToBytes()[47], Is.EqualTo((byte)0x00), "1 max, 1 allocated");

            ue.MaxObjectBlocks = 2;
            ue.AllocatedObjectBlocks = 9;
            Assert.That(ue.ToBytes()[47], Is.EqualTo((byte)0x11), "allocated clamped to max");
        }

        // ---------------------------------------------------------------
        // The file number is not the physical position
        // ---------------------------------------------------------------

        /// <summary>
        /// The vector that proves the model, measured on the live machine with LIST-FILES.
        /// </summary>
        /// <remarks>
        /// User 8 (BIGMAN): F0010 = FILE 8 at physical 2056, F0250 = FILE 190 at 2238, and
        /// F0500 = FILE 307 at 18483. The last is the important one - it sits in the user's SECOND
        /// object block, and the old model reads 18483 as file 51 owned by user 72.
        /// </remarks>
        [TestCase(2056, 8, 8)]
        [TestCase(2238, 8, 190)]
        [TestCase(18483, 8, 307)]
        public void FileNumberMatchesTheLiveMachine(int physical, int user, int expected)
        {
            Assert.That(ObjectEntry.ComputeFileNumber(physical, user), Is.EqualTo(expected));
        }

        /// <summary>
        /// A position outside the user's blocks returns -1, so a mismatch is visible rather than
        /// producing a plausible wrong number.
        /// </summary>
        [Test]
        public void FileNumberRejectsAPositionOutsideTheUser()
        {
            // 18483 belongs to user 8's second block, not to user 0.
            Assert.That(ObjectEntry.ComputeFileNumber(18483, 0), Is.EqualTo(-1));
        }

        /// <summary>
        /// Inside block 0 the number equals the slot - which is why this went unnoticed for years.
        /// </summary>
        [Test]
        public void FirstBlockFileNumberEqualsSlot()
        {
            int[] slots = { 0, 1, 31, 32, 255 };
            for (int i = 0; i < slots.Length; i++)
            {
                int physical = ObjectFile.PhysicalSlot(3, 0, slots[i]);
                Assert.That(ObjectEntry.ComputeFileNumber(physical, 3), Is.EqualTo(slots[i]));
            }
        }

        /// <summary>
        /// Numbering must not jump or repeat at 255 to 256 - the boundary the old model broke on.
        /// </summary>
        [Test]
        public void FileNumberCrossesTheBlockBoundaryContiguously()
        {
            const int user = 8;
            List<int> numbers = new List<int>();

            for (int block = 0; block < 3; block++)
            {
                for (int slot = 0; slot < FilesPerObjectBlock; slot++)
                {
                    int physical = ObjectFile.PhysicalSlot(user, block, slot);
                    numbers.Add(ObjectEntry.ComputeFileNumber(physical, user));
                }
            }

            for (int i = 0; i < numbers.Count; i++)
            {
                Assert.That(numbers[i], Is.EqualTo(i));
            }
        }

        /// <summary>
        /// Block n sits at page n*512 + user*8 - the placement measured on the pack, where BIGMAN
        /// (user 8) had entries on pages 64-71 and 576-583.
        /// </summary>
        [Test]
        public void BlockPlacementStrideIsOneIndexBlock()
        {
            const int entriesPerPage = 32;

            int firstPageBlock0 = ObjectFile.PhysicalSlot(8, 0, 0) / entriesPerPage;
            int lastPageBlock0 = ObjectFile.PhysicalSlot(8, 0, FilesPerObjectBlock - 1) / entriesPerPage;
            int firstPageBlock1 = ObjectFile.PhysicalSlot(8, 1, 0) / entriesPerPage;
            int lastPageBlock1 = ObjectFile.PhysicalSlot(8, 1, FilesPerObjectBlock - 1) / entriesPerPage;

            Assert.That(firstPageBlock0, Is.EqualTo(64));
            Assert.That(lastPageBlock0, Is.EqualTo(71));
            Assert.That(firstPageBlock1, Is.EqualTo(576));
            Assert.That(lastPageBlock1, Is.EqualTo(583));
            Assert.That(firstPageBlock1 - firstPageBlock0, Is.EqualTo(512));
        }

        // ---------------------------------------------------------------
        // Free-slot search across blocks
        // ---------------------------------------------------------------

        /// <summary>
        /// Occupies the first <paramref name="count"/> slots of one of a user's object blocks.
        /// </summary>
        /// <param name="objectFile">
        /// The object file to populate.
        /// </param>
        /// <param name="user">
        /// The owning user.
        /// </param>
        /// <param name="block">
        /// The object block to fill.
        /// </param>
        /// <param name="count">
        /// How many slots to occupy.
        /// </param>
        private static void Fill(ObjectFile objectFile, int user, int block, int count)
        {
            for (int slot = 0; slot < count; slot++)
            {
                int physical = ObjectFile.PhysicalSlot(user, block, slot);
                ObjectEntry entry = new ObjectEntry
                {
                    UserIndex = user,
                    ObjectIndex = physical,
                    ObjectName = "X"
                };
                objectFile.AddObject(entry);
            }
        }

        /// <summary>
        /// While the first block has room, allocation stays in it.
        /// </summary>
        [Test]
        public void FreeSlotStaysInTheFirstBlockWhileItHasRoom()
        {
            ObjectFile of = new ObjectFile();
            Fill(of, 5, 0, 10);

            int slot = of.FindFreeUserSlot(5, 4, 1);
            Assert.That(ObjectEntry.ComputeFileNumber(slot, 5), Is.EqualTo(10));
        }

        /// <summary>
        /// Deleting a file leaves a hole that SINTRAN never compacts, so it must be reused.
        /// </summary>
        [Test]
        public void FreeSlotReusesAHole()
        {
            ObjectFile of = new ObjectFile();
            Fill(of, 5, 0, 10);

            int hole = ObjectFile.PhysicalSlot(5, 0, 4);
            of.RemoveObject(hole);

            int slot = of.FindFreeUserSlot(5, 4, 1);
            Assert.That(slot, Is.EqualTo(hole));
            Assert.That(ObjectEntry.ComputeFileNumber(slot, 5), Is.EqualTo(4));
        }

        /// <summary>
        /// File 257 - the case that used to fail outright.
        /// </summary>
        [Test]
        public void FullFirstBlockGrowsIntoTheSecond()
        {
            ObjectFile of = new ObjectFile();
            Fill(of, 5, 0, FilesPerObjectBlock);

            int slot = of.FindFreeUserSlot(5, 4, 1);
            Assert.That(slot, Is.GreaterThanOrEqualTo(0));
            Assert.That(ObjectEntry.ComputeFileNumber(slot, 5), Is.EqualTo(256),
                "the 257th file is the first file of block 1");
        }

        /// <summary>
        /// Only at the user's maximum is "object table is full" the truth.
        /// </summary>
        [Test]
        public void FullAtTheUsersMaximum()
        {
            ObjectFile of = new ObjectFile();
            Fill(of, 5, 0, FilesPerObjectBlock);

            Assert.That(of.FindFreeUserSlot(5, 1, 1), Is.EqualTo(-1));
        }

        /// <summary>
        /// A hole in block 0 is still found when block 1 is already allocated.
        /// </summary>
        [Test]
        public void SearchSpansEveryAllocatedBlock()
        {
            ObjectFile of = new ObjectFile();
            Fill(of, 5, 0, FilesPerObjectBlock);
            Fill(of, 5, 1, 5);

            int hole = ObjectFile.PhysicalSlot(5, 0, 100);
            of.RemoveObject(hole);

            Assert.That(of.FindFreeUserSlot(5, 4, 2), Is.EqualTo(hole));
        }

        /// <summary>
        /// Users past 64 keep working - they live in later index-block groups.
        /// </summary>
        /// <remarks>
        /// RetroFS reaches 256 users by giving index-block group <c>g</c> to users
        /// <c>g*64..g*64+63</c>, so placement for a high user's FIRST block must stay unchanged.
        /// </remarks>
        [Test]
        public void HighUserFirstBlockIsPlacedNormally()
        {
            ObjectFile of = new ObjectFile();
            Assert.That(of.FindFreeUserSlot(64, 4, 1), Is.GreaterThanOrEqualTo(0));
        }

        /// <summary>
        /// A user's SECOND object block occupies the same pages as user 64+U's FIRST block.
        /// </summary>
        /// <remarks>
        /// This is the collision that makes the multi-block and multi-user layouts incompatible.
        /// Both are real - the multi-user layout is how RetroFS reaches 256 users, the multi-block
        /// one was measured on a live SINTRAN K pack - so the filesystem layer refuses to grow into
        /// a block whose pages another user already owns.
        /// </remarks>
        [Test]
        public void SecondBlockOfAUserOverlapsTheHighUserRegion()
        {
            const int usersPerIndexBlock = 64;

            int secondBlockOfUser8 = ObjectFile.PhysicalSlot(8, 1, 0);
            int firstBlockOfUser72 = ObjectFile.PhysicalSlot(usersPerIndexBlock + 8, 0, 0);

            Assert.That(secondBlockOfUser8, Is.EqualTo(firstBlockOfUser72));
        }

        /// <summary>
        /// The single-argument overload keeps the historical single-block behaviour, so callers
        /// that know nothing about object blocks are unaffected.
        /// </summary>
        [Test]
        public void DefaultOverloadPreservesSingleBlockBehaviour()
        {
            ObjectFile of = new ObjectFile();
            Fill(of, 5, 0, FilesPerObjectBlock);

            Assert.That(of.FindFreeUserSlot(5), Is.EqualTo(-1));
        }
    }
}
