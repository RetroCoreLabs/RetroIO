//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Tnfs.Protocol;

/// <summary>
/// TNFS protocol command codes (specification version 1.2).
/// </summary>
public enum TnfsCommand : byte
{
    // Session commands (0x00-0x0F)
    /// <summary>
    /// Mount filesystem
    /// </summary>
    Mount = 0x00,

    /// <summary>
    /// Unmount filesystem
    /// </summary>
    Unmount = 0x01,

    // Directory commands (0x10-0x1F)
    /// <summary>
    /// Open directory
    /// </summary>
    OpenDir = 0x10,

    /// <summary>
    /// Read directory entry
    /// </summary>
    ReadDir = 0x11,

    /// <summary>
    /// Close directory
    /// </summary>
    CloseDir = 0x12,

    /// <summary>
    /// Make directory
    /// </summary>
    MkDir = 0x13,

    /// <summary>
    /// Remove directory
    /// </summary>
    RmDir = 0x14,

    /// <summary>
    /// Tell directory position
    /// </summary>
    TellDir = 0x15,

    /// <summary>
    /// Seek directory position
    /// </summary>
    SeekDir = 0x16,

    /// <summary>
    /// Open directory extended
    /// </summary>
    OpenDirX = 0x17,

    /// <summary>
    /// Read directory extended
    /// </summary>
    ReadDirX = 0x18,

    // File commands (0x20-0x2F)
    /// <summary>
    /// Read from file
    /// </summary>
    Read = 0x21,

    /// <summary>
    /// Write to file
    /// </summary>
    Write = 0x22,

    /// <summary>
    /// Close file
    /// </summary>
    Close = 0x23,

    /// <summary>
    /// Get file status
    /// </summary>
    Stat = 0x24,

    /// <summary>
    /// Seek in file
    /// </summary>
    Seek = 0x25,

    /// <summary>
    /// Unlink (delete) file
    /// </summary>
    Unlink = 0x26,

    /// <summary>
    /// Change file permissions (Unix-style)
    /// </summary>
    Chmod = 0x27,

    /// <summary>
    /// Rename file
    /// </summary>
    Rename = 0x28,

    /// <summary>
    /// Open file (CORRECT CODE per spec)
    /// </summary>
    Open = 0x29
}

/// <summary>
/// TNFS protocol return/error codes.
/// Exact mapping to C server error codes in tnfsd/src/errortable.h
/// </summary>
public enum TnfsReturnCode : byte
{
    // Success
    /// <summary>
    /// Success (0x00)
    /// </summary>
    SUCCESS = 0x00,

    // Standard Unix errors - EXACT mapping to C server
    /// <summary>
    /// Operation not permitted (0x01)
    /// </summary>
    EPERM = 0x01,

    /// <summary>
    /// No such file or directory (0x02)
    /// </summary>
    ENOENT = 0x02,

    /// <summary>
    /// I/O error (0x03)
    /// </summary>
    EIO = 0x03,

    /// <summary>
    /// No such device or address (0x04)
    /// </summary>
    ENXIO = 0x04,

    /// <summary>
    /// Argument list too long (0x05)
    /// </summary>
    E2BIG = 0x05,

    /// <summary>
    /// Bad file descriptor (0x06)
    /// </summary>
    EBADF = 0x06,

    /// <summary>
    /// Try again (0x07)
    /// </summary>
    EAGAIN = 0x07,

    /// <summary>
    /// Out of memory (0x08)
    /// </summary>
    ENOMEM = 0x08,

    /// <summary>
    /// Permission denied (0x09)
    /// </summary>
    EACCES = 0x09,

    /// <summary>
    /// Device or resource busy (0x0A)
    /// </summary>
    EBUSY = 0x0A,

    /// <summary>
    /// File exists (0x0B)
    /// </summary>
    EEXIST = 0x0B,

    /// <summary>
    /// Not a directory (0x0C)
    /// </summary>
    ENOTDIR = 0x0C,

    /// <summary>
    /// Is a directory (0x0D)
    /// </summary>
    EISDIR = 0x0D,

    /// <summary>
    /// Invalid argument (0x0E)
    /// </summary>
    EINVAL = 0x0E,

    /// <summary>
    /// File table overflow (0x0F)
    /// </summary>
    ENFILE = 0x0F,

    /// <summary>
    /// Too many open files (0x10)
    /// </summary>
    EMFILE = 0x10,

    /// <summary>
    /// File too large (0x11)
    /// </summary>
    EFBIG = 0x11,

    /// <summary>
    /// No space left on device (0x12)
    /// </summary>
    ENOSPC = 0x12,

    /// <summary>
    /// Illegal seek (0x13)
    /// </summary>
    ESPIPE = 0x13,

    /// <summary>
    /// Read-only file system (0x14)
    /// </summary>
    EROFS = 0x14,

    /// <summary>
    /// File name too long (0x15)
    /// </summary>
    ENAMETOOLONG = 0x15,

    /// <summary>
    /// Function not implemented (0x16)
    /// </summary>
    ENOSYS = 0x16,

    /// <summary>
    /// Directory not empty (0x17)
    /// </summary>
    ENOTEMPTY = 0x17,

    /// <summary>
    /// Too many symbolic links (0x18)
    /// </summary>
    ELOOP = 0x18,

    /// <summary>
    /// No data available (0x19)
    /// </summary>
    ENODATA = 0x19,

    /// <summary>
    /// Not a stream (0x1A)
    /// </summary>
    ENOSTR = 0x1A,

    /// <summary>
    /// Protocol error (0x1B)
    /// </summary>
    EPROTO = 0x1B,

    /// <summary>
    /// File descriptor in bad state (0x1C)
    /// </summary>
    EBADFD = 0x1C,

    /// <summary>
    /// Too many users (0x1D)
    /// </summary>
    EUSERS = 0x1D,

    /// <summary>
    /// No buffer space available (0x1E)
    /// </summary>
    ENOBUFS = 0x1E,

    /// <summary>
    /// Operation already in progress (0x1F)
    /// </summary>
    EALREADY = 0x1F,

    /// <summary>
    /// Stale file handle (0x20)
    /// </summary>
    ESTALE = 0x20,

    /// <summary>
    /// End of file (0x21)
    /// </summary>
    EOF = 0x21,

    // TNFS specific errors
    /// <summary>
    /// Invalid session ID (0xFF)
    /// </summary>
    EBADSESSION = 0xFF
}

/// <summary>
/// TNFS file open flags (specification version 1.2).
/// These are ushort flags that can be combined.
/// </summary>
[System.Flags]
public enum TnfsOpenFlags : ushort
{
    /// <summary>
    /// Open for reading only (0x0000)
    /// </summary>
    O_RDONLY = 0x0000,

    /// <summary>
    /// Open for writing only (0x0001)
    /// </summary>
    O_WRONLY = 0x0001,

    /// <summary>
    /// Open for reading and writing (0x0002)
    /// </summary>
    O_RDWR = 0x0002,

    /// <summary>
    /// Append mode (0x0008)
    /// </summary>
    O_APPEND = 0x0008,

    /// <summary>
    /// Create file if it doesn't exist (0x0100)
    /// </summary>
    O_CREAT = 0x0100,

    /// <summary>
    /// Truncate file if it doesn't exist (0x0800)
    /// </summary>
    O_TRUNC = 0x0800,

    /// <summary>
    /// Exclusive create - fail if file exists (0x0200)
    /// </summary>
    O_EXCL = 0x0200
}

/// <summary>
/// TNFS seek origin (specification version 1.2).
/// </summary>
public enum TnfsSeekOrigin : byte
{
    /// <summary>
    /// Seek from beginning of file (0x00)
    /// </summary>
    SeekSet = 0x00,

    /// <summary>
    /// Seek from current position (0x01)
    /// </summary>
    SeekCur = 0x01,

    /// <summary>
    /// Seek from end of file (0x02)
    /// </summary>
    SeekEnd = 0x02
}

/// <summary>
/// Extended directory entry flags (READDIRX).
/// </summary>
[System.Flags]
public enum TnfsDirEntryFlags : byte
{
    /// <summary>
    /// No special flags
    /// </summary>
    None = 0x00,

    /// <summary>
    /// Entry is a directory (0x01)
    /// </summary>
    Directory = 0x01,

    /// <summary>
    /// Entry is hidden (0x02)
    /// </summary>
    Hidden = 0x02,

    /// <summary>
    /// Entry is a special file (0x04)
    /// </summary>
    Special = 0x04
}

/// <summary>
/// Extended directory status flags (READDIRX).
/// </summary>
[System.Flags]
public enum TnfsDirStatusFlags : byte
{
    /// <summary>
    /// No status flags
    /// </summary>
    None = 0x00,

    /// <summary>
    /// End of directory reached (0x01)
    /// </summary>
    EOF = 0x01
}

/// <summary>
/// TNFS file attributes (bitmask).
/// </summary>
[System.Flags]
public enum TnfsFileAttributes : byte
{
    /// <summary>
    /// No attributes
    /// </summary>
    None = 0x00,

    /// <summary>
    /// Is a directory
    /// </summary>
    Directory = 0x01,

    /// <summary>
    /// Is a symbolic link
    /// </summary>
    SymbolicLink = 0x02,

    /// <summary>
    /// Is a block device
    /// </summary>
    BlockDevice = 0x04,

    /// <summary>
    /// Is a character device
    /// </summary>
    CharacterDevice = 0x08,

    /// <summary>
    /// Is a named pipe (FIFO)
    /// </summary>
    NamedPipe = 0x10,

    /// <summary>
    /// Is a socket
    /// </summary>
    Socket = 0x20,

    /// <summary>
    /// Is hidden
    /// </summary>
    Hidden = 0x40,

    /// <summary>
    /// Is system file
    /// </summary>
    System = 0x80
}
