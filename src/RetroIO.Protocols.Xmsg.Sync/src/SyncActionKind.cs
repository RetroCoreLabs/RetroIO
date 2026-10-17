//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Sync
{
    /// <summary>
    /// What the daemon should do about one file.
    /// </summary>
    /// <remarks>
    /// <para><b>Create and Overwrite are separate because the WIRE separates them</b></para>
    /// <para>
    /// It would be tidier to have one "push" and let something lower down work out whether the
    /// file is there. It would also be wrong: a SINTRAN filespec is QUOTED when the file is being
    /// created and bare when it already exists, so the decision has to be made before the request
    /// is built. Keeping the two apart here means the difference is visible in a plan a person can
    /// read, instead of hidden in a boolean.
    /// </para>
    /// </remarks>
    public enum SyncActionKind
    {
        /// <summary>
        /// Nothing to do.
        /// </summary>
        None = 0,

        /// <summary>
        /// The remote file does not exist and must be made, then filled.
        /// </summary>
        Create = 1,

        /// <summary>
        /// The remote file exists and its contents are to be replaced.
        /// </summary>
        Overwrite = 2,

        /// <summary>
        /// The local file is gone and the remote one is to be removed.
        /// </summary>
        /// <remarks>
        /// Only ever produced when the mapping's
        /// <see cref="SyncFolderMapping.DeleteRemoteWhenLocalDeleted"/> is on. Whether the FA
        /// <c>DeleteFile</c> operation works at all is UNVERIFIED - see task #23 - so this says
        /// what is intended, not what has been proven to work.
        /// </remarks>
        DeleteRemote = 3,

        /// <summary>
        /// A remote file is to be fetched to the local folder.
        /// </summary>
        Pull = 4,

        /// <summary>
        /// The file was considered and deliberately left alone, with a reason.
        /// </summary>
        /// <remarks>
        /// Kept in the plan rather than dropped, because the interesting skips are the ones a
        /// person needs to see: a name too long to carry, or a delete that a setting refused. A
        /// file that simply has not changed produces NO action at all - listing every unchanged
        /// file on every pass would bury the two lines that matter.
        /// </remarks>
        Skip = 5,
    }
}
