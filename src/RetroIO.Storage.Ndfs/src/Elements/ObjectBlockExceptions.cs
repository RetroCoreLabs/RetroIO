//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;

namespace RetroIO.Storage.Ndfs.Elements
{
    /// <summary>
    /// Thrown when a user cannot take another file because their object blocks are full.
    /// </summary>
    /// <remarks>
    /// <para><b>Two limits, only one of which an operator can lift</b></para>
    /// A copy or import loop needs to tell these apart, and matching on message text is
    /// fragile, so <see cref="IsHardLimit"/> carries the distinction:
    ///  - <c>false</c> — every object block the user is ALLOWED is full, but that maximum is
    ///    still below the SINTRAN ceiling of 16. Granting more blocks fixes it, via
    ///    <c>NdfsFileSystem.GiveObjectBlocks</c> here or <c>@GIVE-OBJECT-BLOCKS</c> on a
    ///    real machine.
    ///  - <c>true</c> — the user already holds all 16 object blocks, i.e. 4096 files. That is
    ///    the SINTRAN maximum; there is nothing left to grant and the only remedy is deleting
    ///    files or using another user area.
    /// <para>
    /// Derives from <see cref="InvalidOperationException"/> because that is what the file
    /// system threw for a full object table before object blocks were understood, so existing
    /// callers keep working.
    /// </para>
    /// </remarks>
    public class ObjectBlockLimitException : InvalidOperationException
    {
        /// <summary>
        /// The user area that filled up.
        /// </summary>
        public string UserName { get; }

        /// <summary>
        /// The user's MXOBL — object blocks permitted, 1..16.
        /// </summary>
        public int MaxObjectBlocks { get; }

        /// <summary>
        /// The user's ACOBL — object blocks actually allocated, 1..<see cref="MaxObjectBlocks"/>.
        /// </summary>
        public int AllocatedObjectBlocks { get; }

        /// <summary>
        /// True when <see cref="MaxObjectBlocks"/> is already at the SINTRAN maximum of 16, so
        /// no grant can help.
        /// </summary>
        public bool IsHardLimit { get; }

        /// <summary>
        /// Creates the exception, composing the operator-facing message from the limit reached.
        /// </summary>
        /// <param name="userName">
        /// The user area that filled up.
        /// </param>
        /// <param name="maxObjectBlocks">
        /// The user's MXOBL, 1..16.
        /// </param>
        /// <param name="allocatedObjectBlocks">
        /// The user's ACOBL, 1..<paramref name="maxObjectBlocks"/>.
        /// </param>
        /// <param name="isHardLimit">
        /// True when <paramref name="maxObjectBlocks"/> is already the SINTRAN maximum of 16.
        /// </param>
        public ObjectBlockLimitException(
            string userName,
            int maxObjectBlocks,
            int allocatedObjectBlocks,
            bool isHardLimit)
            : base(BuildMessage(userName, maxObjectBlocks, allocatedObjectBlocks, isHardLimit))
        {
            UserName = userName;
            MaxObjectBlocks = maxObjectBlocks;
            AllocatedObjectBlocks = allocatedObjectBlocks;
            IsHardLimit = isHardLimit;
        }

        /// <summary>
        /// Composes the operator-facing text for a full object table.
        /// </summary>
        /// <param name="userName">
        /// The user area that filled up.
        /// </param>
        /// <param name="maxObjectBlocks">
        /// The user's MXOBL, 1..16.
        /// </param>
        /// <param name="allocatedObjectBlocks">
        /// The user's ACOBL.
        /// </param>
        /// <param name="isHardLimit">
        /// True when no further blocks can be granted.
        /// </param>
        /// <returns>
        /// A message naming the remedy, or stating plainly that there is none.
        /// </returns>
        private static string BuildMessage(
            string userName,
            int maxObjectBlocks,
            int allocatedObjectBlocks,
            bool isHardLimit)
        {
            int maxFiles = maxObjectBlocks * NdfsObjectBlocks.FilesPerObjectBlock;

            if (isHardLimit)
            {
                // MXOBL is 16. Say so plainly rather than sending the operator off to a
                // command that will only refuse.
                return $"User {userName} has reached the SINTRAN maximum of {maxFiles} files "
                    + $"({maxObjectBlocks} object blocks). This is a hard limit: no further object "
                    + "blocks can be granted. Delete files or use another user area.";
            }

            return $"User {userName} is full at {maxFiles} files ({allocatedObjectBlocks} of "
                + $"{maxObjectBlocks} object blocks allocated, {maxObjectBlocks} allowed). Grant "
                + $"more with GiveObjectBlocks(\"{userName}\", n) here, or @GIVE-OBJECT-BLOCKS "
                + $"{userName},n on the machine. The ceiling is "
                + $"{NdfsObjectBlocks.MaxObjectBlocks} blocks = "
                + $"{NdfsObjectBlocks.MaxObjectBlocks * NdfsObjectBlocks.FilesPerObjectBlock} files.";
        }
    }

    /// <summary>
    /// Thrown when growing a user into another object block would land on another user's pages.
    /// </summary>
    /// <remarks>
    /// A user's object block <c>n</c> occupies exactly the pages this library uses for user
    /// <c>n*64 + U</c>'s FIRST block, so the two placements cannot coexist. See
    /// <c>docs/NDFS-Object-Blocks.md</c> section 7.
    /// </remarks>
    public class ObjectBlockCollisionException : InvalidOperationException
    {
        /// <summary>
        /// The user that could not be grown.
        /// </summary>
        public string UserName { get; }

        /// <summary>
        /// The zero-based object block the user was growing into.
        /// </summary>
        public int Block { get; }

        /// <summary>
        /// The index of the user whose first object block already owns those pages.
        /// </summary>
        public int CollidingUserIndex { get; }

        /// <summary>
        /// Creates the exception for a refused growth into an occupied block.
        /// </summary>
        /// <param name="userName">
        /// The user that could not be grown.
        /// </param>
        /// <param name="block">
        /// The zero-based object block being grown into.
        /// </param>
        /// <param name="collidingUserIndex">
        /// The user index already occupying those pages.
        /// </param>
        public ObjectBlockCollisionException(string userName, int block, int collidingUserIndex)
            : base($"Cannot give user {userName} object block {block}: those pages already belong "
                + $"to user index {collidingUserIndex}. A user's second object block and a high "
                + "user's first block occupy the same place. See docs/NDFS-Object-Blocks.md.")
        {
            UserName = userName;
            Block = block;
            CollidingUserIndex = collidingUserIndex;
        }
    }

    /// <summary>
    /// The SINTRAN object-block constants, in one place so no call site re-declares them.
    /// </summary>
    /// <remarks>
    /// Verified 2026-08-01 against a live SINTRAN III K pack. See
    /// <c>docs/NDFS-Object-Blocks.md</c>.
    /// </remarks>
    public static class NdfsObjectBlocks
    {
        /// <summary>
        /// Object-file pages in one object block. Eight pages of 32 entries each.
        /// </summary>
        public const int PagesPerObjectBlock = 8;

        /// <summary>
        /// Object entries in one object-file page.
        /// </summary>
        public const int EntriesPerPage = 32;

        /// <summary>
        /// Files held by one object block — 256.
        /// </summary>
        public const int FilesPerObjectBlock = PagesPerObjectBlock * EntriesPerPage;

        /// <summary>
        /// Page pointers in one index block — 512, which is also the stride between
        /// successive object blocks of the same user.
        /// </summary>
        public const int PagesPerIndexBlock = 512;

        /// <summary>
        /// Users whose first object block fits in a single index block — 64.
        /// </summary>
        public const int UsersPerIndexBlock = PagesPerIndexBlock / PagesPerObjectBlock;

        /// <summary>
        /// The SINTRAN version-K ceiling — 16 object blocks, i.e. 4096 files per user.
        /// </summary>
        public const int MaxObjectBlocks = 16;
    }
}
