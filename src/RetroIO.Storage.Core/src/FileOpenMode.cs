//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// File open modes (POSIX-aligned).
    /// Maps to POSIX open() flags and .NET FileMode.
    /// </summary>
    public enum FileOpenMode
    {
        /// <summary>
        /// Open existing file.
        /// POSIX: O_RDONLY/O_WRONLY/O_RDWR (no create flags)
        /// .NET: FileMode.Open
        /// Fails if file does not exist (ENOENT).
        /// </summary>
        Open = 0,

        /// <summary>
        /// Create new file or truncate existing file.
        /// POSIX: O_CREAT | O_TRUNC
        /// .NET: FileMode.Create
        /// If file exists, truncates to zero length.
        /// </summary>
        Create = 1,

        /// <summary>
        /// Create new file, fail if it already exists.
        /// POSIX: O_CREAT | O_EXCL
        /// .NET: FileMode.CreateNew
        /// Fails with EEXIST if file already exists.
        /// </summary>
        CreateNew = 2,

        /// <summary>
        /// Open existing file or create if it doesn't exist.
        /// POSIX: O_CREAT (without O_EXCL)
        /// .NET: FileMode.OpenOrCreate
        /// Does not truncate if file exists.
        /// </summary>
        OpenOrCreate = 3,

        /// <summary>
        /// Open existing file and truncate to zero length.
        /// POSIX: O_TRUNC (without O_CREAT)
        /// .NET: FileMode.Truncate
        /// Fails if file does not exist (ENOENT).
        /// </summary>
        Truncate = 4,

        /// <summary>
        /// Open existing file for appending.
        /// POSIX: O_APPEND
        /// .NET: FileMode.Append
        /// All writes are appended to end of file.
        /// </summary>
        Append = 5
    }

    /// <summary>
    /// File access modes (POSIX-aligned).
    /// Maps to POSIX access flags and .NET FileAccess.
    /// </summary>
    public enum FileAccessMode
    {
        /// <summary>
        /// Read-only access.
        /// POSIX: O_RDONLY (0x0000)
        /// .NET: FileAccess.Read
        /// TNFS: TNFS_O_RDONLY (0x0000)
        /// </summary>
        Read = 0,

        /// <summary>
        /// Write-only access.
        /// POSIX: O_WRONLY (0x0001)
        /// .NET: FileAccess.Write
        /// TNFS: TNFS_O_WRONLY (0x0001)
        /// </summary>
        Write = 1,

        /// <summary>
        /// Read and write access.
        /// POSIX: O_RDWR (0x0002)
        /// .NET: FileAccess.ReadWrite
        /// TNFS: TNFS_O_RDWR (0x0002)
        /// </summary>
        ReadWrite = 2
    }
}
