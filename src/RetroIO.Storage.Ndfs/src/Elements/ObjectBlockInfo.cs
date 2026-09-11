//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Storage.Ndfs.Elements
{
    /// <summary>
    /// A user's object-block position: the files they hold, and the files they may hold.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a user interface needs this</b></para>
    /// A user sitting at <see cref="AllocatedObjectBlocks"/> equal to
    /// <see cref="MaxObjectBlocks"/> with that last block full cannot take another file, and
    /// that state is otherwise indistinguishable from a user with plenty of room — which is
    /// precisely how "creating file 257 failed" becomes a mystery. Showing
    /// <see cref="FilesInUse"/> against <see cref="MaxFiles"/> makes it obvious, and
    /// <see cref="CanGrant"/> says whether the operator can do anything about it.
    /// <para><b>Allocation is on demand</b></para>
    /// <see cref="AllocatedObjectBlocks"/> climbs by itself as files are created. Only
    /// <see cref="MaxObjectBlocks"/> needs an operator, via
    /// <c>NdfsFileSystem.GiveObjectBlocks</c> or <c>@GIVE-OBJECT-BLOCKS</c> on the machine.
    /// </remarks>
    public sealed class ObjectBlockInfo
    {
        /// <summary>
        /// The user area this describes.
        /// </summary>
        public string UserName { get; }

        /// <summary>
        /// The user's index in the user file, 0..255.
        /// </summary>
        public int UserIndex { get; }

        /// <summary>
        /// ACOBL — object blocks in use, 1..<see cref="MaxObjectBlocks"/>.
        /// </summary>
        public int AllocatedObjectBlocks { get; }

        /// <summary>
        /// MXOBL — object blocks permitted, 1..16.
        /// </summary>
        public int MaxObjectBlocks { get; }

        /// <summary>
        /// Entries actually present for this user. Counted live, because SINTRAN never compacts
        /// and deletions leave holes, so any stored total drifts.
        /// </summary>
        public int FilesInUse { get; }

        /// <summary>
        /// The user's file ceiling — what <c>@USER-STATISTICS</c> prints as MAXIMUM NUMBER OF
        /// FILES.
        /// </summary>
        public int MaxFiles
        {
            get { return MaxObjectBlocks * NdfsObjectBlocks.FilesPerObjectBlock; }
        }

        /// <summary>
        /// Files the user can take without allocating another object block.
        /// </summary>
        public int AllocatedFiles
        {
            get { return AllocatedObjectBlocks * NdfsObjectBlocks.FilesPerObjectBlock; }
        }

        /// <summary>
        /// True while more object blocks can still be granted, i.e. MXOBL is below 16.
        /// </summary>
        public bool CanGrant
        {
            get { return MaxObjectBlocks < NdfsObjectBlocks.MaxObjectBlocks; }
        }

        /// <summary>
        /// How many object blocks <c>GiveObjectBlocks</c> may still add.
        /// </summary>
        public int GrantableBlocks
        {
            get { return NdfsObjectBlocks.MaxObjectBlocks - MaxObjectBlocks; }
        }

        /// <summary>
        /// True when the user cannot take another file without a grant — every permitted block
        /// is allocated and full.
        /// </summary>
        public bool IsFull
        {
            get { return FilesInUse >= MaxFiles; }
        }

        /// <summary>
        /// Creates the report.
        /// </summary>
        /// <param name="userName">
        /// The user area described.
        /// </param>
        /// <param name="userIndex">
        /// The user's index in the user file.
        /// </param>
        /// <param name="allocatedObjectBlocks">
        /// ACOBL — blocks in use.
        /// </param>
        /// <param name="maxObjectBlocks">
        /// MXOBL — blocks permitted.
        /// </param>
        /// <param name="filesInUse">
        /// Entries actually present for this user.
        /// </param>
        public ObjectBlockInfo(
            string userName,
            int userIndex,
            int allocatedObjectBlocks,
            int maxObjectBlocks,
            int filesInUse)
        {
            UserName = userName;
            UserIndex = userIndex;
            AllocatedObjectBlocks = allocatedObjectBlocks;
            MaxObjectBlocks = maxObjectBlocks;
            FilesInUse = filesInUse;
        }
    }
}
