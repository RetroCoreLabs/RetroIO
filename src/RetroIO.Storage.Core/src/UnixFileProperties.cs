//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// The property keys of a Unix inode, shared by the Unix file systems (V7, BSD FFS).
    /// </summary>
    public static class UnixFileProperties
    {
        /// <summary>
        /// The permission bits of the mode, with set-user-id, set-group-id and sticky (0 to 7777 octal).
        /// </summary>
        public const string ModeKey = "unix.mode";

        /// <summary>
        /// The owner's user id.
        /// </summary>
        public const string UidKey = "unix.uid";

        /// <summary>
        /// The group id.
        /// </summary>
        public const string GidKey = "unix.gid";

        /// <summary>
        /// The three keys.
        /// </summary>
        public static readonly string[] Keys = { ModeKey, UidKey, GidKey };
    }
}
