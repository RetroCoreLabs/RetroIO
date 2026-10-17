//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Sync
{
    /// <summary>
    /// What to do with a local file that sits in a sub-folder of a mapped folder.
    /// </summary>
    /// <remarks>
    /// <para><b>SINTRAN has no folders</b></para>
    /// <para>
    /// A user's directory is FLAT. So a local tree has nowhere to go, and the choice cannot be
    /// made silently: <c>src\parser\LEX.SYMB</c> and <c>src\codegen\LEX.SYMB</c> both become
    /// <c>LEX:SYMB</c> and the second quietly destroys the first.
    /// </para>
    /// </remarks>
    public enum SyncSubfolderPolicy
    {
        /// <summary>
        /// Only files sitting directly in the mapped folder are carried; anything deeper is
        /// skipped and reported.
        /// </summary>
        /// <remarks>
        /// The default, because it cannot lose a file. A skipped file is visible and fixable; a
        /// flattened collision is neither.
        /// </remarks>
        TopLevelOnly = 0,

        /// <summary>
        /// Files at any depth are carried, using the file name alone.
        /// </summary>
        /// <remarks>
        /// Only safe when the caller knows the tree holds no repeated file names. Two files with
        /// the same name in different sub-folders map to ONE SINTRAN file and the later transfer
        /// wins.
        /// </remarks>
        FlattenAll = 1,
    }
}
