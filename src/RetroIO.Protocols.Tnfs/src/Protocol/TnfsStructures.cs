//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Tnfs.Protocol;

/// <summary>
/// TNFS directory entry information.
/// </summary>
public sealed class TnfsDirectoryEntry
{
    private long _size;
    private uint _modifiedTime;
    private bool _metadataLoaded;
    private Func<string, TnfsFileStat?>? _statProvider;
    private string? _fullPath;

    /// <summary>Entry name (filename or directory name)</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>File attributes</summary>
    public TnfsFileAttributes Attributes { get; set; }

    /// <summary>File size in bytes (lazily loaded via STAT if needed)</summary>
    public long Size
    {
        get
        {
            EnsureMetadataLoaded();
            return _size;
        }
        set
        {
            _size = value;
            _metadataLoaded = true; // Mark as loaded if explicitly set
        }
    }

    /// <summary>Last modification time (Unix timestamp, lazily loaded via STAT if needed)</summary>
    public uint ModifiedTime
    {
        get
        {
            EnsureMetadataLoaded();
            return _modifiedTime;
        }
        set
        {
            _modifiedTime = value;
            _metadataLoaded = true; // Mark as loaded if explicitly set
        }
    }

    /// <summary>Is this entry a directory? (triggers lazy STAT if metadata not loaded)</summary>
    public bool IsDirectory
    {
        get
        {
            EnsureMetadataLoaded();
            return (Attributes & TnfsFileAttributes.Directory) != 0;
        }
    }

    /// <summary>Is this entry a symbolic link? (triggers lazy STAT if metadata not loaded)</summary>
    public bool IsSymbolicLink
    {
        get
        {
            EnsureMetadataLoaded();
            return (Attributes & TnfsFileAttributes.SymbolicLink) != 0;
        }
    }

    /// <summary>Is this entry hidden? (triggers lazy STAT if metadata not loaded)</summary>
    public bool IsHidden
    {
        get
        {
            EnsureMetadataLoaded();
            return (Attributes & TnfsFileAttributes.Hidden) != 0;
        }
    }

    /// <summary>Convert Unix timestamp to DateTime</summary>
    public DateTime GetModifiedDateTime()
    {
        return DateTimeOffset.FromUnixTimeSeconds(ModifiedTime).DateTime;
    }

    /// <summary>Enable lazy loading of metadata via STAT command</summary>
    /// <param name="fullPath">Full path to the file</param>
    /// <param name="statProvider">Function to call STAT command</param>
    /// <param name="metadataAlreadyLoaded">True if metadata was already loaded from READDIRX</param>
    internal void EnableLazyLoad(string fullPath, Func<string, TnfsFileStat?> statProvider, bool metadataAlreadyLoaded = false)
    {
        _fullPath = fullPath;
        _statProvider = statProvider;
        _metadataLoaded = metadataAlreadyLoaded;
    }

    private void EnsureMetadataLoaded()
    {
        if (_metadataLoaded || _statProvider == null || _fullPath == null)
            return;

        // Lazy load metadata using STAT command
        var stat = _statProvider(_fullPath);
        if (stat != null)
        {
            _size = stat.Size;
            _modifiedTime = stat.ModifiedTime;
            Attributes = stat.Attributes; // Update attributes from STAT (includes directory flag)
        }

        _metadataLoaded = true; // Don't try again even if STAT failed
    }
}

/// <summary>
/// TNFS file status information (from STAT command).
/// </summary>
public sealed class TnfsFileStat
{
    /// <summary>File size in bytes</summary>
    public long Size { get; set; }

    /// <summary>File attributes</summary>
    public TnfsFileAttributes Attributes { get; set; }

    /// <summary>Last modification time (Unix timestamp)</summary>
    public uint ModifiedTime { get; set; }

    /// <summary>Creation time (Unix timestamp)</summary>
    public uint CreationTime { get; set; }

    /// <summary>Last access time (Unix timestamp)</summary>
    public uint AccessTime { get; set; }

    /// <summary>File permissions (Unix-style)</summary>
    public ushort Permissions { get; set; }

    /// <summary>Is this a directory?</summary>
    public bool IsDirectory => (Attributes & TnfsFileAttributes.Directory) != 0;
}

/// <summary>
/// TNFS server version information.
/// </summary>
public sealed class TnfsVersionInfo
{
    /// <summary>Server protocol version</summary>
    public ushort ProtocolVersion { get; set; }

    /// <summary>Server software version string</summary>
    public string ServerVersion { get; set; } = string.Empty;

    /// <summary>Server platform/OS string</summary>
    public string Platform { get; set; } = string.Empty;
}

/// <summary>
/// TNFS filesystem information (free space, etc).
/// </summary>
public sealed class TnfsFreeSpace
{
    /// <summary>Total bytes available</summary>
    public ulong TotalBytes { get; set; }

    /// <summary>Free bytes available</summary>
    public ulong FreeBytes { get; set; }

    /// <summary>Used bytes</summary>
    public ulong UsedBytes => TotalBytes - FreeBytes;
}

/// <summary>
/// Extended directory entry (from READDIRX).
/// </summary>
public sealed class TnfsDirEntryExtended
{
    /// <summary>Entry flags</summary>
    public TnfsDirEntryFlags Flags { get; set; }

    /// <summary>File size in bytes</summary>
    public uint Size { get; set; }

    /// <summary>Last modification time (Unix timestamp)</summary>
    public uint ModifiedTime { get; set; }

    /// <summary>Creation time (Unix timestamp)</summary>
    public uint CreatedTime { get; set; }

    /// <summary>Entry name (filename or directory name)</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Is this entry a directory?</summary>
    public bool IsDirectory => (Flags & TnfsDirEntryFlags.Directory) != 0;

    /// <summary>Is this entry hidden?</summary>
    public bool IsHidden => (Flags & TnfsDirEntryFlags.Hidden) != 0;

    /// <summary>Is this entry special?</summary>
    public bool IsSpecial => (Flags & TnfsDirEntryFlags.Special) != 0;
}

/// <summary>
/// Result from OPENDIRX command.
/// </summary>
public sealed class TnfsOpenDirXResult
{
    /// <summary>Directory handle</summary>
    public byte DirHandle { get; set; }

    /// <summary>Total entry count in directory</summary>
    public ushort EntryCount { get; set; }
}

/// <summary>
/// Result from READDIRX command.
/// </summary>
public sealed class TnfsReadDirXResult
{
    /// <summary>Directory entries</summary>
    public TnfsDirEntryExtended[] Entries { get; set; } = System.Array.Empty<TnfsDirEntryExtended>();

    /// <summary>Is end of directory?</summary>
    public bool IsEndOfDirectory { get; set; }
}

/// <summary>
/// TNFS operation result wrapper.
/// </summary>
/// <typeparam name="T">Result data type</typeparam>
public sealed class TnfsResult<T>
{
    /// <summary>Return code from server</summary>
    public TnfsReturnCode ReturnCode { get; set; }

    /// <summary>Operation succeeded?</summary>
    public bool Success => ReturnCode == TnfsReturnCode.SUCCESS;

    /// <summary>Result data (if successful)</summary>
    public T? Data { get; set; }

    /// <summary>Error message (if failed)</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>Create successful result</summary>
    public static TnfsResult<T> CreateSuccess(T data)
    {
        return new TnfsResult<T>
        {
            ReturnCode = TnfsReturnCode.SUCCESS,
            Data = data
        };
    }

    /// <summary>Create error result</summary>
    public static TnfsResult<T> CreateError(TnfsReturnCode code, string message)
    {
        return new TnfsResult<T>
        {
            ReturnCode = code,
            ErrorMessage = message
        };
    }
}
