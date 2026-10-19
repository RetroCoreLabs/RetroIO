//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Tnfs.Protocol;

/// <summary>
/// TNFS Protocol Constants (specification version 1.2).
/// Consolidated constants from all TNFS protocol definitions.
/// </summary>
public static class TnfsConstants
{
    // Protocol Version
    /// <summary>
    /// Low byte of the protocol version, 0x02 (version 1.2).
    /// </summary>
    public const byte PROTOVERSION_LSB = 0x02;  // Version 1.2
    /// <summary>
    /// High byte of the protocol version, 0x01 (version 1.2).
    /// </summary>
    public const byte PROTOVERSION_MSB = 0x01;

    // Default Port
    /// <summary>
    /// Default TNFS port number, 16384.
    /// </summary>
    public const int DEFAULT_PORT = 16384;

    // Packet Sizes
    /// <summary>
    /// Size of the message header in bytes: session ID (2), sequence number (1) and command (1).
    /// </summary>
    public const int TNFS_HEADERSZ = 4;      // Session(2) + Seq(1) + Cmd(1)
    /// <summary>
    /// Maximum message size in bytes, 1024.
    /// </summary>
    public const int MAXMSGSZ = 1024;         // Maximum message size
    /// <summary>
    /// Maximum payload size in bytes: the maximum message size minus the header and one status byte.
    /// </summary>
    public const int TNFS_MAX_PAYLOAD = MAXMSGSZ - TNFS_HEADERSZ - 1;  // Header + status
    /// <summary>
    /// Maximum number of bytes for one I/O operation, 512.
    /// </summary>
    public const int MaxIOSize = 512;         // Maximum I/O per operation (the only I/O size limit; tnfsd config.h MAX_IOSZ is also 512)
    /// <summary>
    /// Default read buffer size in bytes, 512.
    /// </summary>
    public const int DefaultReadSize = 512;   // Default read buffer size

    // Command Classes
    /// <summary>
    /// Command class value for session commands, 0x00.
    /// </summary>
    public const byte CLASS_SESSION = 0x00;
    /// <summary>
    /// Command class value for directory commands, 0x10.
    /// </summary>
    public const byte CLASS_DIRECTORY = 0x10;
    /// <summary>
    /// Command class value for file commands, 0x20.
    /// </summary>
    public const byte CLASS_FILE = 0x20;

    // Session Commands (0x00 - 0x0F)
    /// <summary>
    /// Command code for MOUNT, 0x00.
    /// </summary>
    public const byte TNFS_MOUNT = 0x00;
    /// <summary>
    /// Command code for UMOUNT, 0x01.
    /// </summary>
    public const byte TNFS_UMOUNT = 0x01;

    // Directory Commands (0x10 - 0x1F)
    /// <summary>
    /// Command code for OPENDIR, 0x10.
    /// </summary>
    public const byte TNFS_OPENDIR = 0x10;
    /// <summary>
    /// Command code for READDIR, 0x11.
    /// </summary>
    public const byte TNFS_READDIR = 0x11;
    /// <summary>
    /// Command code for CLOSEDIR, 0x12.
    /// </summary>
    public const byte TNFS_CLOSEDIR = 0x12;
    /// <summary>
    /// Command code for MKDIR, 0x13.
    /// </summary>
    public const byte TNFS_MKDIR = 0x13;
    /// <summary>
    /// Command code for RMDIR, 0x14.
    /// </summary>
    public const byte TNFS_RMDIR = 0x14;
    /// <summary>
    /// Command code for TELLDIR, 0x15.
    /// </summary>
    public const byte TNFS_TELLDIR = 0x15;
    /// <summary>
    /// Command code for SEEKDIR, 0x16.
    /// </summary>
    public const byte TNFS_SEEKDIR = 0x16;
    /// <summary>
    /// Command code for OPENDIRX, 0x17.
    /// </summary>
    public const byte TNFS_OPENDIRX = 0x17;
    /// <summary>
    /// Command code for READDIRX, 0x18.
    /// </summary>
    public const byte TNFS_READDIRX = 0x18;

    // File Commands (0x20 - 0x2F)
    /// <summary>
    /// Command code for the old OPEN command, 0x20.
    /// </summary>
    public const byte TNFS_OPENFILE_OLD = 0x20;
    /// <summary>
    /// Command code for READ, 0x21.
    /// </summary>
    public const byte TNFS_READ = 0x21;
    /// <summary>
    /// Command code for WRITE, 0x22.
    /// </summary>
    public const byte TNFS_WRITE = 0x22;
    /// <summary>
    /// Command code for CLOSE, 0x23.
    /// </summary>
    public const byte TNFS_CLOSE = 0x23;
    /// <summary>
    /// Command code for STAT, 0x24.
    /// </summary>
    public const byte TNFS_STAT = 0x24;
    /// <summary>
    /// Command code for LSEEK, 0x25.
    /// </summary>
    public const byte TNFS_LSEEK = 0x25;
    /// <summary>
    /// Command code for UNLINK, 0x26.
    /// </summary>
    public const byte TNFS_UNLINK = 0x26;
    /// <summary>
    /// Command code for CHMOD, 0x27.
    /// </summary>
    public const byte TNFS_CHMOD = 0x27;
    /// <summary>
    /// Command code for RENAME, 0x28.
    /// </summary>
    public const byte TNFS_RENAME = 0x28;
    /// <summary>
    /// Command code for OPEN, 0x29.
    /// </summary>
    public const byte TNFS_OPEN = 0x29;

    // Limits
    /// <summary>
    /// Limit on the number of sessions, 256.
    /// </summary>
    public const int MAX_SESSIONS = 256;
    /// <summary>
    /// Limit on the number of sessions from one IP address, 8.
    /// </summary>
    public const int MAX_SESSIONS_PER_IP = 8;
    /// <summary>
    /// Limit on the number of TCP connections, 64.
    /// </summary>
    public const int MAX_TCP_CONN = 64;
    /// <summary>
    /// Limit on the number of open file descriptors per connection, 16.
    /// </summary>
    public const int MAX_FD_PER_CONN = 16;
    /// <summary>
    /// Limit on the number of open directory handles per connection, 16.
    /// </summary>
    public const int MAX_DHND_PER_CONN = 16;
    /// <summary>
    /// Limit on the length of a file name, 256.
    /// </summary>
    public const int MAX_FILENAME_LEN = 256;
    /// <summary>
    /// Limit on the length of a file path, 1024.
    /// </summary>
    public const int MAX_FILEPATH = 1024;
    /// <summary>
    /// Limit on the length of a TNFS path, 1024.
    /// </summary>
    public const int MAX_TNFSPATH = 1024;

    // Timeouts (seconds)
    /// <summary>
    /// Session timeout in seconds, 600 (10 minutes).
    /// </summary>
    public const int SESSION_TIMEOUT = 600;       // 10 minutes
    /// <summary>
    /// Connection timeout in seconds, 300 (5 minutes).
    /// </summary>
    public const int CONN_TIMEOUT = 300;          // 5 minutes
    /// <summary>
    /// Directory handle timeout in seconds, 300 (5 minutes).
    /// </summary>
    public const int DIR_HANDLE_TIMEOUT = 300;    // 5 minutes
    /// <summary>
    /// Default socket timeout in milliseconds, 5000.
    /// </summary>
    public const int DefaultTimeout = 5000;       // Default timeout (milliseconds)
    /// <summary>
    /// Maximum number of retry attempts, 3.
    /// </summary>
    public const int MaxRetries = 3;              // Maximum retry attempts

    // TCP Keepalive (seconds)
    /// <summary>
    /// TCP keepalive idle time in seconds, 60.
    /// </summary>
    public const int TCP_KA_IDLE = 60;
    /// <summary>
    /// TCP keepalive probe interval in seconds, 10.
    /// </summary>
    public const int TCP_KA_INTVL = 10;
    /// <summary>
    /// TCP keepalive probe count, 6.
    /// </summary>
    public const int TCP_KA_COUNT = 6;

    // Directory Options (OPENDIRX)
    /// <summary>
    /// OPENDIRX directory option flag, 0x01: no folders first.
    /// </summary>
    public const byte TNFS_DIROPT_NO_FOLDERSFIRST = 0x01;
    /// <summary>
    /// OPENDIRX directory option flag, 0x02: directory pattern.
    /// </summary>
    public const byte TNFS_DIROPT_DIR_PATTERN = 0x02;
    /// <summary>
    /// OPENDIRX directory option flag, 0x04: do not skip hidden entries.
    /// </summary>
    public const byte TNFS_DIROPT_NO_SKIPHIDDEN = 0x04;
    /// <summary>
    /// OPENDIRX directory option flag, 0x08: do not skip special entries.
    /// </summary>
    public const byte TNFS_DIROPT_NO_SKIPSPECIAL = 0x08;
    /// <summary>
    /// OPENDIRX directory option flag, 0x10: no folders.
    /// </summary>
    public const byte TNFS_DIROPT_NO_FOLDERS = 0x10;
    /// <summary>
    /// OPENDIRX directory option flag, 0x20: traverse.
    /// </summary>
    public const byte TNFS_DIROPT_TRAVERSE = 0x20;

    // Directory Sort Options (OPENDIRX)
    /// <summary>
    /// OPENDIRX sort option, 0x00: no sorting.
    /// </summary>
    public const byte TNFS_DIRSORT_NONE = 0x00;
    /// <summary>
    /// OPENDIRX sort option, 0x01: sort by name.
    /// </summary>
    public const byte TNFS_DIRSORT_NAME = 0x01;
    /// <summary>
    /// OPENDIRX sort option, 0x02: sort by size.
    /// </summary>
    public const byte TNFS_DIRSORT_SIZE = 0x02;
    /// <summary>
    /// OPENDIRX sort option, 0x03: sort by modified time.
    /// </summary>
    public const byte TNFS_DIRSORT_MODIFIED = 0x03;
    /// <summary>
    /// OPENDIRX sort option flag, 0x10: case.
    /// </summary>
    public const byte TNFS_DIRSORT_CASE = 0x10;
    /// <summary>
    /// OPENDIRX sort option flag, 0x20: descending order.
    /// </summary>
    public const byte TNFS_DIRSORT_DESCENDING = 0x20;

    // Directory Status Flags (READDIRX)
    /// <summary>
    /// READDIRX status flag, 0x01: end of the directory.
    /// </summary>
    public const byte TNFS_DIRSTATUS_EOF = 0x01;

    // Directory Entry Flags
    /// <summary>
    /// Directory entry flag, 0x01: the entry is a directory.
    /// </summary>
    public const byte TNFS_DIRENTRY_DIR = 0x01;
    /// <summary>
    /// Directory entry flag, 0x02: the entry is hidden.
    /// </summary>
    public const byte TNFS_DIRENTRY_HIDDEN = 0x02;
    /// <summary>
    /// Directory entry flag, 0x04: the entry is special.
    /// </summary>
    public const byte TNFS_DIRENTRY_SPECIAL = 0x04;

    // File Open Flags (little-endian uint16)
    /// <summary>
    /// File open flag, 0x0001: read only.
    /// </summary>
    public const ushort TNFS_O_RDONLY = 0x0001;
    /// <summary>
    /// File open flag, 0x0002: write only.
    /// </summary>
    public const ushort TNFS_O_WRONLY = 0x0002;
    /// <summary>
    /// File open flag, 0x0003: read and write.
    /// </summary>
    public const ushort TNFS_O_RDWR = 0x0003;
    /// <summary>
    /// File open flag, 0x0008: append.
    /// </summary>
    public const ushort TNFS_O_APPEND = 0x0008;
    /// <summary>
    /// File open flag, 0x0100: create the file if it does not exist.
    /// </summary>
    public const ushort TNFS_O_CREAT = 0x0100;
    /// <summary>
    /// File open flag, 0x0200: truncate the file.
    /// </summary>
    public const ushort TNFS_O_TRUNC = 0x0200;
    /// <summary>
    /// File open flag, 0x0400: exclusive create.
    /// </summary>
    public const ushort TNFS_O_EXCL = 0x0400;

    // Seek Whence
    /// <summary>
    /// Seek origin, 0x00: from the start of the file.
    /// </summary>
    public const byte TNFS_SEEK_SET = 0x00;
    /// <summary>
    /// Seek origin, 0x01: from the current position.
    /// </summary>
    public const byte TNFS_SEEK_CUR = 0x01;
    /// <summary>
    /// Seek origin, 0x02: from the end of the file.
    /// </summary>
    public const byte TNFS_SEEK_END = 0x02;

    // STAT Response Offsets
    /// <summary>
    /// Byte offset of the mode field in a STAT response, 0.
    /// </summary>
    public const int ST_MODE_OFFSET = 0;
    /// <summary>
    /// Byte offset of the user ID field in a STAT response, 2.
    /// </summary>
    public const int ST_UID_OFFSET = 2;
    /// <summary>
    /// Byte offset of the group ID field in a STAT response, 4.
    /// </summary>
    public const int ST_GID_OFFSET = 4;
    /// <summary>
    /// Byte offset of the size field in a STAT response, 6.
    /// </summary>
    public const int ST_SIZE_OFFSET = 6;
    /// <summary>
    /// Byte offset of the access time field in a STAT response, 10.
    /// </summary>
    public const int ST_ATIME_OFFSET = 10;
    /// <summary>
    /// Byte offset of the modification time field in a STAT response, 14.
    /// </summary>
    public const int ST_MTIME_OFFSET = 14;
    /// <summary>
    /// Byte offset of the change time field in a STAT response, 18.
    /// </summary>
    public const int ST_CTIME_OFFSET = 18;
    /// <summary>
    /// Size of a STAT response body in bytes, 22.
    /// </summary>
    public const int TNFS_STAT_SIZE = 22;

    // Timeout Values (milliseconds)
    /// <summary>
    /// Low byte of the timeout value, 0x05 (5 seconds).
    /// </summary>
    public const ushort TIMEOUT_LSB = 0x05;  // 5 seconds
    /// <summary>
    /// High byte of the timeout value, 0x00.
    /// </summary>
    public const ushort TIMEOUT_MSB = 0x00;

    /// <summary>
    /// Get human-readable command name from command code.
    /// </summary>
    public static string GetCommandName(byte cmd)
    {
        return cmd switch
        {
            TNFS_MOUNT => "MOUNT",
            TNFS_UMOUNT => "UNMOUNT",
            TNFS_OPENDIR => "OPENDIR",
            TNFS_READDIR => "READDIR",
            TNFS_CLOSEDIR => "CLOSEDIR",
            TNFS_MKDIR => "MKDIR",
            TNFS_RMDIR => "RMDIR",
            TNFS_TELLDIR => "TELLDIR",
            TNFS_SEEKDIR => "SEEKDIR",
            TNFS_OPENDIRX => "OPENDIRX",
            TNFS_READDIRX => "READDIRX",
            TNFS_OPENFILE_OLD => "OPEN_OLD",
            TNFS_READ => "READ",
            TNFS_WRITE => "WRITE",
            TNFS_CLOSE => "CLOSE",
            TNFS_STAT => "STAT",
            TNFS_LSEEK => "LSEEK",
            TNFS_UNLINK => "UNLINK",
            TNFS_CHMOD => "CHMOD",
            TNFS_RENAME => "RENAME",
            TNFS_OPEN => "OPEN",
            _ => $"UNKNOWN(0x{cmd:X2})"
        };
    }
}
