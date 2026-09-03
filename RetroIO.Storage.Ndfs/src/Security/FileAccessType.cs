//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Storage.Ndfs.Security
{
    /// <summary>
    /// Represents the three-tier access level in NDFS.
    /// </summary>
    public enum FileAccessType
    {
        /// <summary>
        /// Owner access - user who owns the file.
        /// </summary>
        Own = 0,

        /// <summary>
        /// Friend access - users in owner's friend list.
        /// </summary>
        Friend = 1,

        /// <summary>
        /// Public access - all other users.
        /// </summary>
        Public = 2
    }

    /// <summary>
    /// File operation types for permission checking.
    /// </summary>
    public enum FileOperationType
    {
        /// <summary>
        /// Read file contents.
        /// </summary>
        Read,

        /// <summary>
        /// Write/modify file contents.
        /// </summary>
        Write,

        /// <summary>
        /// Append to file.
        /// </summary>
        Append,

        /// <summary>
        /// Delete file.
        /// </summary>
        Delete,

        /// <summary>
        /// Execute/run file.
        /// </summary>
        Execute,

        /// <summary>
        /// List directory contents.
        /// </summary>
        List
    }
}
