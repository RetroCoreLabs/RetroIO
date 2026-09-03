//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Text;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Protocols.Tnfs.Server;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Server.Core.Utils;

namespace RetroIO.Protocols.Tnfs.Server.Handlers;

/// <summary>
/// Handles TNFS directory commands (OPENDIR, READDIR, CLOSEDIR).
/// </summary>
public sealed class DirectoryCommandHandler
{
    private readonly SessionManager _sessionManager;
    private readonly ILogger<DirectoryCommandHandler> _logger;
    private readonly IFileSystemBackend _fileSystem;

    public DirectoryCommandHandler(
        SessionManager sessionManager,
        ILogger<DirectoryCommandHandler> logger,
        IFileSystemBackend fileSystem)
    {
        _sessionManager = sessionManager;
        _logger = logger;
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Handle OPENDIR command (0x10).
    /// Opens a directory for reading and returns directory handle.
    /// </summary>
    public TnfsMessage HandleOpenDir(TnfsMessage request)
    {
        // Validate session
        var session = _sessionManager.FindSessionAndValidateIP(
            request.Header.SessionId, request.Header.ClientIP!);

        if (session == null)
        {
            _logger.LogWarning("OPENDIR: Invalid session 0x{SessionId:X4}",
                request.Header.SessionId);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADSESSION);
        }

        // Parse directory path (null-terminated string)
        string dirPath = EndianHelper.ReadNullTerminatedString(request.Data.Span);

        if (string.IsNullOrEmpty(dirPath))
        {
            dirPath = "/";  // Default to root
        }

        _logger.LogInformation("OPENDIR: Session=0x{SessionId:X4}, Path='{Path}'",
            session.SessionId, dirPath);

        // Validate and resolve path
        string fullPath = _fileSystem.CombinePath(session.RootPath, dirPath.TrimStart('/'));

        if (!PathValidator.IsPathSafe(dirPath, session.RootPath, _fileSystem))
        {
            _logger.LogWarning("OPENDIR: Path '{Path}' rejected (security)", dirPath);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EACCES);
        }

        if (!_fileSystem.DirectoryExists(fullPath))
        {
            _logger.LogWarning("OPENDIR: Directory '{Path}' not found", fullPath);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.ENOENT);
        }

        // Find free directory handle
        int handle = session.FindFreeDirectoryHandle();
        if (handle < 0)
        {
            _logger.LogWarning("OPENDIR: No free directory handles for session 0x{SessionId:X4}",
                session.SessionId);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EMFILE);
        }

        try
        {
            // Enumerate directory entries
            var entries = _fileSystem.EnumerateFileSystemEntries(fullPath);
            session.DirectoryHandles[handle] = new DirectoryHandle
            {
                IsOpen = true,
                Path = fullPath,
                Enumerator = entries.GetEnumerator(),
                OpenedAt = DateTime.UtcNow
            };

            _logger.LogInformation("OPENDIR: Session=0x{SessionId:X4}, Handle={Handle}, Path='{Path}'",
                session.SessionId, handle, fullPath);

            // Response: handle (1 byte)
            Span<byte> responseData = stackalloc byte[1];
            responseData[0] = (byte)handle;

            return TnfsMessage.CreateResponse(request, TnfsReturnCode.SUCCESS, responseData);
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogWarning("OPENDIR: Access denied to '{Path}'", fullPath);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EACCES);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OPENDIR: Error opening directory '{Path}'", fullPath);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EIO);
        }
    }

    /// <summary>
    /// Handle READDIR command (0x11).
    /// Reads next directory entry and returns name + attributes.
    /// </summary>
    public TnfsMessage HandleReadDir(TnfsMessage request)
    {
        // Validate session
        var session = _sessionManager.FindSessionAndValidateIP(
            request.Header.SessionId, request.Header.ClientIP!);

        if (session == null)
        {
            _logger.LogWarning("READDIR: Invalid session 0x{SessionId:X4}",
                request.Header.SessionId);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADSESSION);
        }

        // Parse directory handle (1 byte)
        if (request.Data.Length < 1)
        {
            _logger.LogWarning("READDIR: Invalid packet size");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EINVAL);
        }

        byte handle = request.Data.Span[0];

        // Validate handle
        if (handle >= TnfsConstants.MAX_DHND_PER_CONN ||
            !session.DirectoryHandles[handle].IsOpen)
        {
            _logger.LogWarning("READDIR: Invalid directory handle {Handle}", handle);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADF);
        }

        var dirHandle = session.DirectoryHandles[handle];

        // Try to read next entry
        if (dirHandle.Enumerator == null || !dirHandle.Enumerator.MoveNext())
        {
            _logger.LogDebug("READDIR: EOF for handle {Handle}", handle);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EOF);
        }

        string entryPath = dirHandle.Enumerator.Current;
        string entryName = _fileSystem.GetFileName(entryPath);

        try
        {
            // Build response: name + 0x00 (standard READDIR has NO flags byte per TNFS 1.2 spec)
            using var buffer = TnfsBuffer.Rent(256);
            var span = buffer.Span;

            // Entry name (null-terminated) - no flags byte in standard READDIR!
            int nameBytes = Encoding.ASCII.GetBytes(entryName, span);
            span[nameBytes] = 0x00;  // Null terminator

            int totalLength = nameBytes + 1;

            _logger.LogDebug("READDIR: Session=0x{SessionId:X4}, Handle={Handle}, Entry='{Entry}'",
                session.SessionId, handle, entryName);

            return TnfsMessage.CreateResponse(request, TnfsReturnCode.SUCCESS,
                span.Slice(0, totalLength));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "READDIR: Error reading entry '{Path}'", entryPath);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EIO);
        }
    }

    /// <summary>
    /// Handle READDIRX command (0x18).
    /// Reads next directory entry with extended metadata (size, timestamps).
    /// </summary>
    public TnfsMessage HandleReadDirX(TnfsMessage request)
    {
        // Validate session
        var session = _sessionManager.FindSessionAndValidateIP(
            request.Header.SessionId, request.Header.ClientIP!);

        if (session == null)
        {
            _logger.LogWarning("READDIRX: Invalid session 0x{SessionId:X4}",
                request.Header.SessionId);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADSESSION);
        }

        // Parse directory handle (1 byte)
        if (request.Data.Length < 1)
        {
            _logger.LogWarning("READDIRX: Invalid packet size");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EINVAL);
        }

        byte handle = request.Data.Span[0];

        // Validate handle
        if (handle >= TnfsConstants.MAX_DHND_PER_CONN ||
            !session.DirectoryHandles[handle].IsOpen)
        {
            _logger.LogWarning("READDIRX: Invalid directory handle {Handle}", handle);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADF);
        }

        var dirHandle = session.DirectoryHandles[handle];

        // Try to read next entry
        if (dirHandle.Enumerator == null || !dirHandle.Enumerator.MoveNext())
        {
            _logger.LogDebug("READDIRX: EOF for handle {Handle}", handle);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EOF);
        }

        string entryPath = dirHandle.Enumerator.Current;
        string entryName = _fileSystem.GetFileName(entryPath);

        try
        {
            // Get file/directory attributes
            bool isDirectory = _fileSystem.IsDirectory(entryPath);
            FileInfo? fileInfo = isDirectory ? null : _fileSystem.GetFileInfo(entryPath);

            // Build response: flags(1) + size(4) + mtime(4) + ctime(4) + name + 0x00
            using var buffer = TnfsBuffer.Rent(256);
            var span = buffer.Span;

            int offset = 0;

            // Flags byte: bit 0 = is directory
            span[offset++] = (byte)(isDirectory ? 0x01 : 0x00);

            // Size (4 bytes, little-endian uint32)
            uint size = isDirectory ? 0u : (uint)(fileInfo?.Length ?? 0);
            span[offset++] = (byte)(size & 0xFF);
            span[offset++] = (byte)((size >> 8) & 0xFF);
            span[offset++] = (byte)((size >> 16) & 0xFF);
            span[offset++] = (byte)((size >> 24) & 0xFF);

            // Modified time (4 bytes, little-endian uint32 Unix timestamp)
            DateTime modTime = fileInfo?.LastWriteTimeUtc ?? DateTime.UtcNow;
            uint mtime = (uint)((DateTimeOffset)modTime).ToUnixTimeSeconds();
            span[offset++] = (byte)(mtime & 0xFF);
            span[offset++] = (byte)((mtime >> 8) & 0xFF);
            span[offset++] = (byte)((mtime >> 16) & 0xFF);
            span[offset++] = (byte)((mtime >> 24) & 0xFF);

            // Creation/change time (4 bytes, little-endian uint32 Unix timestamp)
            DateTime ctime = fileInfo?.CreationTimeUtc ?? DateTime.UtcNow;
            uint ctimeVal = (uint)((DateTimeOffset)ctime).ToUnixTimeSeconds();
            span[offset++] = (byte)(ctimeVal & 0xFF);
            span[offset++] = (byte)((ctimeVal >> 8) & 0xFF);
            span[offset++] = (byte)((ctimeVal >> 16) & 0xFF);
            span[offset++] = (byte)((ctimeVal >> 24) & 0xFF);

            // Entry name (null-terminated)
            int nameBytes = Encoding.ASCII.GetBytes(entryName, span.Slice(offset));
            offset += nameBytes;
            span[offset++] = 0x00;  // Null terminator

            _logger.LogDebug("READDIRX: Session=0x{SessionId:X4}, Handle={Handle}, Entry='{Entry}', IsDir={IsDir}, Size={Size}",
                session.SessionId, handle, entryName, isDirectory, size);

            return TnfsMessage.CreateResponse(request, TnfsReturnCode.SUCCESS,
                span.Slice(0, offset));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "READDIRX: Error reading entry '{Path}'", entryPath);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EIO);
        }
    }

    /// <summary>
    /// Handle CLOSEDIR command (0x12).
    /// Closes directory handle and releases resources.
    /// </summary>
    public TnfsMessage HandleCloseDir(TnfsMessage request)
    {
        // Validate session
        var session = _sessionManager.FindSessionAndValidateIP(
            request.Header.SessionId, request.Header.ClientIP!);

        if (session == null)
        {
            _logger.LogWarning("CLOSEDIR: Invalid session 0x{SessionId:X4}",
                request.Header.SessionId);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADSESSION);
        }

        // Parse directory handle (1 byte)
        if (request.Data.Length < 1)
        {
            _logger.LogWarning("CLOSEDIR: Invalid packet size");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EINVAL);
        }

        byte handle = request.Data.Span[0];

        // Validate handle
        if (handle >= TnfsConstants.MAX_DHND_PER_CONN ||
            !session.DirectoryHandles[handle].IsOpen)
        {
            _logger.LogWarning("CLOSEDIR: Invalid directory handle {Handle}", handle);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADF);
        }

        // Close directory handle
        session.DirectoryHandles[handle].Close();

        _logger.LogInformation("CLOSEDIR: Session=0x{SessionId:X4}, Handle={Handle}",
            session.SessionId, handle);

        return TnfsMessage.CreateResponse(request, TnfsReturnCode.SUCCESS);
    }

    /// <summary>
    /// Handle MKDIR command (0x14) - Create directory.
    /// Request: header + path (null-terminated)
    /// Response: header + returnCode
    /// </summary>
    public TnfsMessage HandleMkDir(TnfsMessage request)
    {
        // Validate session
        var session = _sessionManager.FindSessionAndValidateIP(
            request.Header.SessionId, request.Header.ClientIP!);

        if (session == null)
        {
            _logger.LogWarning("MKDIR: Invalid session 0x{SessionId:X4}",
                request.Header.SessionId);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADSESSION);
        }

        // Parse request: path\0
        string path = EndianHelper.ReadNullTerminatedString(request.Data.Span);

        if (string.IsNullOrEmpty(path))
        {
            _logger.LogWarning("MKDIR: Empty path");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EINVAL);
        }

        _logger.LogInformation("MKDIR: Session=0x{SessionId:X4}, Path='{Path}'",
            session.SessionId, path);

        // Resolve to absolute path
        string fullPath = _fileSystem.CombinePath(session.RootPath, path.TrimStart('/'));

        // Validate path security
        if (!PathValidator.IsPathSafe(path, session.RootPath, _fileSystem))
        {
            _logger.LogWarning("MKDIR: Path '{Path}' rejected (security)", path);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EACCES);
        }

        try
        {
            // Create directory with default permissions (0755)
            Directory.CreateDirectory(fullPath);
            _logger.LogDebug("MKDIR: Success - {Path}", fullPath);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.SUCCESS);
        }
        catch (IOException ex) when (ex.Message.Contains("already exists"))
        {
            _logger.LogDebug("MKDIR: Directory already exists: {Path}", fullPath);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EEXIST);
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogDebug("MKDIR: Access denied: {Path}", fullPath);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EACCES);
        }
        catch (DirectoryNotFoundException)
        {
            _logger.LogDebug("MKDIR: Parent directory not found: {Path}", fullPath);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.ENOENT);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MKDIR: Error creating directory {Path}", fullPath);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EIO);
        }
    }

    /// <summary>
    /// Handle RMDIR command (0x15) - Remove directory.
    /// Request: header + path (null-terminated)
    /// Response: header + returnCode
    /// </summary>
    public TnfsMessage HandleRmDir(TnfsMessage request)
    {
        // Validate session
        var session = _sessionManager.FindSessionAndValidateIP(
            request.Header.SessionId, request.Header.ClientIP!);

        if (session == null)
        {
            _logger.LogWarning("RMDIR: Invalid session 0x{SessionId:X4}",
                request.Header.SessionId);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADSESSION);
        }

        // Parse request: path\0
        string path = EndianHelper.ReadNullTerminatedString(request.Data.Span);

        if (string.IsNullOrEmpty(path))
        {
            _logger.LogWarning("RMDIR: Empty path");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EINVAL);
        }

        _logger.LogInformation("RMDIR: Session=0x{SessionId:X4}, Path='{Path}'",
            session.SessionId, path);

        // Resolve to absolute path
        string fullPath = _fileSystem.CombinePath(session.RootPath, path.TrimStart('/'));

        // Validate path security
        if (!PathValidator.IsPathSafe(path, session.RootPath, _fileSystem))
        {
            _logger.LogWarning("RMDIR: Path '{Path}' rejected (security)", path);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EACCES);
        }

        try
        {
            // Remove directory (must be empty)
            Directory.Delete(fullPath, false); // recursive = false
            _logger.LogDebug("RMDIR: Success - {Path}", fullPath);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.SUCCESS);
        }
        catch (DirectoryNotFoundException)
        {
            _logger.LogDebug("RMDIR: Directory not found: {Path}", fullPath);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.ENOENT);
        }
        catch (IOException ex) when (ex.Message.Contains("not empty"))
        {
            _logger.LogDebug("RMDIR: Directory not empty: {Path}", fullPath);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.ENOTEMPTY);
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogDebug("RMDIR: Access denied: {Path}", fullPath);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EACCES);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RMDIR: Error removing directory {Path}", fullPath);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EIO);
        }
    }
}
