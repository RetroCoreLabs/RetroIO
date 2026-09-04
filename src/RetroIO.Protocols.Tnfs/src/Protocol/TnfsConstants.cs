//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Protocols.Tnfs.Protocol;

/// <summary>
/// TNFS Protocol Constants (specification version 1.2).
/// Consolidated constants from all TNFS protocol definitions.
/// </summary>
public static class TnfsConstants
{
    // Protocol Version
    public const byte PROTOVERSION_LSB = 0x02;  // Version 1.2
    public const byte PROTOVERSION_MSB = 0x01;

    // Default Port
    public const int DEFAULT_PORT = 16384;

    // Packet Sizes
    public const int TNFS_HEADERSZ = 4;      // Session(2) + Seq(1) + Cmd(1)
    public const int MAXMSGSZ = 1024;         // Maximum message size
    public const int TNFS_MAX_PAYLOAD = MAXMSGSZ - TNFS_HEADERSZ - 1;  // Header + status
    public const int MaxMessageSize = 532;    // Alternative max message size
    public const int MaxIOSize = 512;         // Maximum I/O per operation
    public const int DefaultReadSize = 512;   // Default read buffer size

    // Command Classes
    public const byte CLASS_SESSION = 0x00;
    public const byte CLASS_DIRECTORY = 0x10;
    public const byte CLASS_FILE = 0x20;

    // Session Commands (0x00 - 0x0F)
    public const byte TNFS_MOUNT = 0x00;
    public const byte TNFS_UMOUNT = 0x01;

    // Directory Commands (0x10 - 0x1F)
    public const byte TNFS_OPENDIR = 0x10;
    public const byte TNFS_READDIR = 0x11;
    public const byte TNFS_CLOSEDIR = 0x12;
    public const byte TNFS_MKDIR = 0x13;
    public const byte TNFS_RMDIR = 0x14;
    public const byte TNFS_TELLDIR = 0x15;
    public const byte TNFS_SEEKDIR = 0x16;
    public const byte TNFS_OPENDIRX = 0x17;
    public const byte TNFS_READDIRX = 0x18;

    // File Commands (0x20 - 0x2F)
    public const byte TNFS_OPENFILE_OLD = 0x20;
    public const byte TNFS_READ = 0x21;
    public const byte TNFS_WRITE = 0x22;
    public const byte TNFS_CLOSE = 0x23;
    public const byte TNFS_STAT = 0x24;
    public const byte TNFS_LSEEK = 0x25;
    public const byte TNFS_UNLINK = 0x26;
    public const byte TNFS_CHMOD = 0x27;
    public const byte TNFS_RENAME = 0x28;
    public const byte TNFS_OPEN = 0x29;

    // Limits
    public const int MAX_SESSIONS = 256;
    public const int MAX_SESSIONS_PER_IP = 8;
    public const int MAX_TCP_CONN = 64;
    public const int MAX_FD_PER_CONN = 16;
    public const int MAX_DHND_PER_CONN = 16;
    public const int MAX_FILENAME_LEN = 256;
    public const int MAX_FILEPATH = 1024;
    public const int MAX_TNFSPATH = 1024;
    public const int MAX_IOSZ = 8192;
    public const int MaxFilenameLength = 256;
    public const int MaxPathLength = 256;

    // Timeouts (seconds)
    public const int SESSION_TIMEOUT = 600;       // 10 minutes
    public const int CONN_TIMEOUT = 300;          // 5 minutes
    public const int DIR_HANDLE_TIMEOUT = 300;    // 5 minutes
    public const int DefaultTimeout = 5000;       // Default timeout (milliseconds)
    public const int MaxRetries = 3;              // Maximum retry attempts

    // TCP Keepalive (seconds)
    public const int TCP_KA_IDLE = 60;
    public const int TCP_KA_INTVL = 10;
    public const int TCP_KA_COUNT = 6;

    // Directory Options (OPENDIRX)
    public const byte TNFS_DIROPT_NO_FOLDERSFIRST = 0x01;
    public const byte TNFS_DIROPT_DIR_PATTERN = 0x02;
    public const byte TNFS_DIROPT_NO_SKIPHIDDEN = 0x04;
    public const byte TNFS_DIROPT_NO_SKIPSPECIAL = 0x08;
    public const byte TNFS_DIROPT_NO_FOLDERS = 0x10;
    public const byte TNFS_DIROPT_TRAVERSE = 0x20;

    // Directory Sort Options (OPENDIRX)
    public const byte TNFS_DIRSORT_NONE = 0x00;
    public const byte TNFS_DIRSORT_NAME = 0x01;
    public const byte TNFS_DIRSORT_SIZE = 0x02;
    public const byte TNFS_DIRSORT_MODIFIED = 0x03;
    public const byte TNFS_DIRSORT_CASE = 0x10;
    public const byte TNFS_DIRSORT_DESCENDING = 0x20;

    // Directory Status Flags (READDIRX)
    public const byte TNFS_DIRSTATUS_EOF = 0x01;

    // Directory Entry Flags
    public const byte TNFS_DIRENTRY_DIR = 0x01;
    public const byte TNFS_DIRENTRY_HIDDEN = 0x02;
    public const byte TNFS_DIRENTRY_SPECIAL = 0x04;

    // File Open Flags (little-endian uint16)
    public const ushort TNFS_O_RDONLY = 0x0001;
    public const ushort TNFS_O_WRONLY = 0x0002;
    public const ushort TNFS_O_RDWR = 0x0003;
    public const ushort TNFS_O_APPEND = 0x0008;
    public const ushort TNFS_O_CREAT = 0x0100;
    public const ushort TNFS_O_TRUNC = 0x0200;
    public const ushort TNFS_O_EXCL = 0x0400;

    // Seek Whence
    public const byte TNFS_SEEK_SET = 0x00;
    public const byte TNFS_SEEK_CUR = 0x01;
    public const byte TNFS_SEEK_END = 0x02;

    // STAT Response Offsets
    public const int ST_MODE_OFFSET = 0;
    public const int ST_UID_OFFSET = 2;
    public const int ST_GID_OFFSET = 4;
    public const int ST_SIZE_OFFSET = 6;
    public const int ST_ATIME_OFFSET = 10;
    public const int ST_MTIME_OFFSET = 14;
    public const int ST_CTIME_OFFSET = 18;
    public const int TNFS_STAT_SIZE = 22;

    // Timeout Values (milliseconds)
    public const ushort TIMEOUT_LSB = 0x05;  // 5 seconds
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
