//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Net;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Protocols.Tnfs.Server;

namespace RetroIO.Protocols.Tnfs.Server;

/// <summary>
/// TNFS session state.
/// Tracks active session, open files, and directory handles.
/// </summary>
public sealed class TnfsSession
{
    /// <summary>
    /// Session ID (unique identifier).
    /// </summary>
    public ushort SessionId { get; init; }

    /// <summary>
    /// Client IP address.
    /// </summary>
    public IPAddress ClientIP { get; init; } = IPAddress.None;

    /// <summary>
    /// Authenticated user ID (Guid.Empty = UNKNOWN/anonymous)
    /// </summary>
    public Guid UserId { get; set; } = Guid.Empty;

    /// <summary>
    /// Username (for audit logging)
    /// </summary>
    public string Username { get; set; } = "UNKNOWN";

    /// <summary>
    /// Is authenticated user an admin (RetroIDP)?
    /// </summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// Is user authenticated?
    /// </summary>
    public bool IsAuthenticated => UserId != Guid.Empty;

    /// <summary>
    /// Root directory path for this session.
    /// </summary>
    public string RootPath { get; init; } = "/";

    /// <summary>
    /// Session creation timestamp (UTC) — set once when the session is created, unlike
    /// LastContact which updates on every request.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Last contact timestamp (UTC).
    /// </summary>
    public DateTime LastContact { get; set; }

    /// <summary>
    /// Last sequence number received.
    /// </summary>
    public byte LastSequenceNumber { get; set; }

    /// <summary>
    /// Last message sent (for retry support).
    /// Cached to handle duplicate sequence numbers.
    /// </summary>
    public byte[] LastMessage { get; init; } = new byte[TnfsConstants.MAXMSGSZ];

    /// <summary>
    /// Length of last message sent.
    /// </summary>
    public int LastMessageLength { get; set; }

    /// <summary>
    /// TCP connection ID (0 if session uses UDP).
    /// </summary>
    public int TcpConnectionId { get; set; }

    /// <summary>
    /// Open file descriptors (FileStream objects).
    /// Index is the TNFS file descriptor number.
    /// Null = slot available.
    /// </summary>
    public FileStream?[] FileDescriptors { get; init; } = new FileStream?[TnfsConstants.MAX_FD_PER_CONN];

    /// <summary>
    /// Open directory handles.
    /// Index is the TNFS directory handle number.
    /// </summary>
    public DirectoryHandle[] DirectoryHandles { get; init; } = new DirectoryHandle[TnfsConstants.MAX_DHND_PER_CONN];

    /// <summary>
    /// Find first available file descriptor slot.
    /// Returns -1 if no slots available.
    /// </summary>
    public int FindFreeFileDescriptor()
    {
        for (int i = 0; i < FileDescriptors.Length; i++)
        {
            if (FileDescriptors[i] == null)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Find first available directory handle slot.
    /// Returns -1 if no slots available.
    /// </summary>
    public int FindFreeDirectoryHandle()
    {
        for (int i = 0; i < DirectoryHandles.Length; i++)
        {
            if (!DirectoryHandles[i].IsOpen)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Close all open file descriptors.
    /// Called when session is terminated.
    /// </summary>
    public void CloseAllFiles()
    {
        for (int i = 0; i < FileDescriptors.Length; i++)
        {
            if (FileDescriptors[i] != null)
            {
                try
                {
                    FileDescriptors[i]?.Dispose();
                }
                catch
                {
                    // Ignore errors during cleanup
                }
                FileDescriptors[i] = null;
            }
        }
    }

    /// <summary>
    /// Close all directory handles.
    /// Called when session is terminated.
    /// </summary>
    public void CloseAllDirectories()
    {
        for (int i = 0; i < DirectoryHandles.Length; i++)
        {
            DirectoryHandles[i].Close();
        }
    }

    /// <summary>
    /// Check if session has timed out.
    /// </summary>
    public bool IsExpired(TimeSpan timeout)
    {
        return DateTime.UtcNow - LastContact > timeout;
    }

    public override string ToString()
    {
        return $"Session 0x{SessionId:X4}: IP={ClientIP}, Root={RootPath}, LastContact={LastContact:HH:mm:ss}";
    }
}

/// <summary>
/// Directory handle state.
/// Tracks open directory, current position, and cached entries.
/// </summary>
public struct DirectoryHandle
{
    /// <summary>
    /// Is this handle currently open?
    /// </summary>
    public bool IsOpen { get; set; }

    /// <summary>
    /// Is directory data loaded/cached?
    /// </summary>
    public bool IsLoaded { get; set; }

    /// <summary>
    /// Directory path.
    /// </summary>
    public string Path { get; set; }

    /// <summary>
    /// Directory options (for OPENDIRX).
    /// </summary>
    public byte DirectoryOptions { get; set; }

    /// <summary>
    /// Sort options (for OPENDIRX).
    /// </summary>
    public byte SortOptions { get; set; }

    /// <summary>
    /// Pattern filter (for OPENDIRX).
    /// </summary>
    public string? Pattern { get; set; }

    /// <summary>
    /// Timestamp when handle was opened.
    /// </summary>
    public DateTime OpenedAt { get; set; }

    /// <summary>
    /// Cached directory entries (for READDIRX).
    /// </summary>
    public DirectoryEntry[]? CachedEntries { get; set; }

    /// <summary>
    /// Current read position in cached entries.
    /// </summary>
    public int CurrentPosition { get; set; }

    /// <summary>
    /// Directory enumerator (for standard READDIR).
    /// </summary>
    public IEnumerator<string>? Enumerator { get; set; }

    /// <summary>
    /// Close directory handle and free resources.
    /// </summary>
    public void Close()
    {
        IsOpen = false;
        Enumerator?.Dispose();
        Enumerator = null;
        CachedEntries = null;
        CurrentPosition = 0;
    }
}

/// <summary>
/// Directory entry with metadata (for READDIRX).
/// </summary>
public struct DirectoryEntry
{
    public string Name { get; set; }
    public uint Size { get; set; }
    public uint ModifiedTime { get; set; }
    public uint CreatedTime { get; set; }
    public byte Flags { get; set; }

    public bool IsDirectory => (Flags & TnfsConstants.TNFS_DIRENTRY_DIR) != 0;
    public bool IsHidden => (Flags & TnfsConstants.TNFS_DIRENTRY_HIDDEN) != 0;
    public bool IsSpecial => (Flags & TnfsConstants.TNFS_DIRENTRY_SPECIAL) != 0;
}
