//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Protocols.Tnfs.Server;
using RetroIO.Security.Implementation.Auditing;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Server.Core.Utils;

namespace RetroIO.Protocols.Tnfs.Server.Handlers;

/// <summary>
/// Handles TNFS file commands (STAT, OPEN, READ, WRITE, etc.).
/// </summary>
public class FileCommandHandler
{
    private readonly SessionManager _sessionManager;
    private readonly ILogger<FileCommandHandler> _logger;
    private readonly IFileSystemBackend _fileSystem;
    private readonly SecurityEventEmitter? _securityEvents;

    // Unix file mode constants (POSIX stat st_mode bits)
    private const ushort S_IFMT = 0xF000;   // File type mask
    private const ushort S_IFSOCK = 0xC000; // Socket
    private const ushort S_IFLNK = 0xA000;  // Symbolic link
    private const ushort S_IFREG = 0x8000;  // Regular file
    private const ushort S_IFBLK = 0x6000;  // Block device
    private const ushort S_IFDIR = 0x4000;  // Directory
    private const ushort S_IFCHR = 0x2000;  // Character device
    private const ushort S_IFIFO = 0x1000;  // FIFO

    /// <summary>
    /// Creates the handler.
    /// </summary>
    /// <param name="sessionManager">
    /// The session manager used to find and validate sessions.
    /// </param>
    /// <param name="logger">
    /// The logger to write to.
    /// </param>
    /// <param name="fileSystem">
    /// The file system backend that file operations run against.
    /// </param>
    /// <param name="securityEvents">
    /// The emitter for security events, or null for none.
    /// </param>
    public FileCommandHandler(
        SessionManager sessionManager,
        ILogger<FileCommandHandler> logger,
        IFileSystemBackend fileSystem,
        SecurityEventEmitter? securityEvents = null)
    {
        _sessionManager = sessionManager;
        _logger = logger;
        _fileSystem = fileSystem;
        _securityEvents = securityEvents;
    }

    /// <summary>
    /// Handle STAT command (0x24) - Get file information.
    /// Request: header + filepath (null-terminated string)
    /// Response: header + returnCode + stat_data(22 bytes)
    /// </summary>
    public TnfsMessage HandleStat(TnfsMessage request)
    {
        // Validate session
        var session = _sessionManager.FindSessionAndValidateIP(
            request.Header.SessionId, request.Header.ClientIP!);

        if (session == null)
        {
            _logger.LogWarning("STAT: Invalid session 0x{SessionId:X4}",
                request.Header.SessionId);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADSESSION);
        }

        // Parse request: null-terminated file path
        string path = EndianHelper.ReadNullTerminatedString(request.Data.Span);

        if (string.IsNullOrEmpty(path))
        {
            _logger.LogWarning("STAT: Empty path");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EINVAL);
        }

        _logger.LogInformation("STAT: Session=0x{SessionId:X4}, Path='{Path}'",
            session.SessionId, path);

        // Resolve to absolute path using session's root directory
        string fullPath = _fileSystem.CombinePath(session.RootPath, path.TrimStart('/'));

        // Validate path security
        if (!PathValidator.IsPathSafe(path, session.RootPath, _fileSystem))
        {
            _logger.LogWarning("STAT: Path '{Path}' rejected (security)", path);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EACCES);
        }

        // Check if path exists
        bool isDirectory = _fileSystem.DirectoryExists(fullPath);
        bool isFile = !isDirectory && _fileSystem.FileExists(fullPath);

        if (!isDirectory && !isFile)
        {
            _logger.LogDebug("STAT: Path not found: {Path}", fullPath);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.ENOENT);
        }

        try
        {
            // Build STAT response per TNFS 1.2 spec:
            // mode(2) + uid(2) + gid(2) + size(4) + atime(4) + mtime(4) + ctime(4)
            Span<byte> statData = stackalloc byte[TnfsConstants.TNFS_STAT_SIZE];
            int writeOffset = 0;

            // mode (2 bytes) - Unix file mode bits
            ushort mode = BuildFileMode(fullPath, isDirectory);
            EndianHelper.WriteUInt16(statData.Slice(writeOffset), mode);
            writeOffset += 2;

            // uid (2 bytes) - not used, set to 0
            EndianHelper.WriteUInt16(statData.Slice(writeOffset), 0);
            writeOffset += 2;

            // gid (2 bytes) - not used, set to 0
            EndianHelper.WriteUInt16(statData.Slice(writeOffset), 0);
            writeOffset += 2;

            // Get file info for size and timestamps
            BackendFileInfo? fileInfo = isDirectory ? null : _fileSystem.GetFileInfo(fullPath);
            DateTime creationTime = DateTime.UtcNow;
            DateTime lastWriteTime = DateTime.UtcNow;
            DateTime lastAccessTime = DateTime.UtcNow;
            long fileSize = 0;

            if (isDirectory)
            {
                // For directories, try to get DirectoryInfo
                if (Directory.Exists(fullPath))
                {
                    DirectoryInfo dirInfo = new DirectoryInfo(fullPath);
                    creationTime = dirInfo.CreationTimeUtc;
                    lastWriteTime = dirInfo.LastWriteTimeUtc;
                    lastAccessTime = dirInfo.LastAccessTimeUtc;
                    // Directory size is typically reported as block size (e.g., 4096 or 8192)
                    fileSize = 4096;
                }
            }
            else if (fileInfo != null)
            {
                creationTime = fileInfo.CreationTimeUtc ?? creationTime;
                lastWriteTime = fileInfo.LastWriteTimeUtc ?? lastWriteTime;
                lastAccessTime = fileInfo.LastAccessTimeUtc ?? lastAccessTime;
                fileSize = fileInfo.Length;
            }

            // size (4 bytes) - file size
            EndianHelper.WriteUInt32(statData.Slice(writeOffset), (uint)fileSize);
            writeOffset += 4;

            // atime (4 bytes) - access time
            uint atime = ToUnixTimestamp(lastAccessTime);
            EndianHelper.WriteUInt32(statData.Slice(writeOffset), atime);
            writeOffset += 4;

            // mtime (4 bytes) - modification time
            uint mtime = ToUnixTimestamp(lastWriteTime);
            EndianHelper.WriteUInt32(statData.Slice(writeOffset), mtime);
            writeOffset += 4;

            // ctime (4 bytes) - creation time
            uint ctime = ToUnixTimestamp(creationTime);
            EndianHelper.WriteUInt32(statData.Slice(writeOffset), ctime);
            writeOffset += 4;

            _logger.LogDebug("STAT: Success - {Path} size={Size} mode=0x{Mode:X4}", path, fileSize, mode);

            // Create response with stat data
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.SUCCESS, statData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "STAT: Error accessing {Path}", fullPath);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EIO);
        }
    }

    private ushort BuildFileMode(string fullPath, bool isDirectory)
    {
        ushort mode = 0;

        // Set file type bits
        if (isDirectory)
            mode |= S_IFDIR; // 0x4000
        else
            mode |= S_IFREG; // 0x8000

        // Set permission bits (0777 = rwxrwxrwx)
        // For simplicity, use 0755 for directories, 0644 for files
        if (isDirectory)
            mode |= 0x01ED; // 0755 octal = 0x01ED
        else
            mode |= 0x01A4; // 0644 octal = 0x01A4

        return mode;
    }

    private uint ToUnixTimestamp(DateTime dateTime)
    {
        DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return (uint)(dateTime.ToUniversalTime() - epoch).TotalSeconds;
    }

    /// <summary>
    /// Handle OPEN command (0x29) - Open file for reading/writing.
    /// Request: header + flags(2) + mode(2) + filepath (null-terminated)
    /// Response: header + returnCode + fd(1 byte)
    /// </summary>
    public TnfsMessage HandleOpen(TnfsMessage request)
    {
        // Validate session
        var session = _sessionManager.FindSessionAndValidateIP(
            request.Header.SessionId, request.Header.ClientIP!);

        if (session == null)
        {
            _logger.LogWarning("OPEN: Invalid session 0x{SessionId:X4}",
                request.Header.SessionId);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADSESSION);
        }

        // Parse request: flags(2) + mode(2) + path\0
        if (request.Data.Length < 5) // Minimum: 2+2+1 (flags+mode+path)
        {
            _logger.LogWarning("OPEN: Invalid request size {Size}", request.Data.Length);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EINVAL);
        }

        ReadOnlySpan<byte> data = request.Data.Span;
        ushort flags = EndianHelper.ReadUInt16(data.Slice(0));
        ushort mode = EndianHelper.ReadUInt16(data.Slice(2));
        string path = EndianHelper.ReadNullTerminatedString(data.Slice(4));

        if (string.IsNullOrEmpty(path))
        {
            _logger.LogWarning("OPEN: Empty path");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EINVAL);
        }

        _logger.LogInformation("OPEN: Session=0x{SessionId:X4}, Path='{Path}', Flags=0x{Flags:X4}, Mode=0x{Mode:X4}",
            session.SessionId, path, flags, mode);

        // Resolve to absolute path
        string fullPath = _fileSystem.CombinePath(session.RootPath, path.TrimStart('/'));

        _logger.LogDebug("OPEN: Resolved path - RootPath='{RootPath}', RequestPath='{RequestPath}', FullPath='{FullPath}'",
            session.RootPath, path, fullPath);

        // Validate path security
        if (!PathValidator.IsPathSafe(path, session.RootPath, _fileSystem))
        {
            _logger.LogWarning("OPEN: Path '{Path}' rejected (security)", path);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EACCES);
        }

        // Find free file descriptor
        int fd = session.FindFreeFileDescriptor();
        if (fd < 0)
        {
            _logger.LogWarning("OPEN: No free file descriptors");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EMFILE);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Convert TNFS flags to FileAccess and FileMode
            FileAccess access = ConvertToFileAccess(flags);
            FileMode fileMode = ConvertToFileMode(flags);

            // Open the file
            FileStream fs = _fileSystem.OpenFile(fullPath, fileMode, access);
            session.FileDescriptors[fd] = fs;

            stopwatch.Stop();
            _logger.LogDebug("OPEN: Success - fd={Fd}, path={Path}, CanRead={CanRead}, Length={Length}",
                fd, fullPath, fs.CanRead, fs.Length);

            // Emit security event
            _ = EmitFileEventAsync(
                session,
                SecurityEventType.FileOpen,
                path,
                success: true,
                durationMs: stopwatch.ElapsedMilliseconds,
                metadata: new Dictionary<string, object>
                {
                    { "FileDescriptor", fd },
                    { "Flags", $"0x{flags:X4}" },
                    { "Access", access.ToString() }
                });

            // Return file descriptor (1 byte)
            Span<byte> response = stackalloc byte[1];
            response[0] = (byte)fd;
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.SUCCESS, response);
        }
        catch (FileNotFoundException)
        {
            _logger.LogDebug("OPEN: File not found: {Path}", fullPath);
            _ = EmitFileEventAsync(session, SecurityEventType.FileOpen, path, false, "File not found");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.ENOENT);
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogDebug("OPEN: Access denied: {Path}", fullPath);
            _ = EmitFileEventAsync(session, SecurityEventType.FileOpen, path, false, "Access denied");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EACCES);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OPEN: Error opening {Path}", fullPath);
            _ = EmitFileEventAsync(session, SecurityEventType.FileOpen, path, false, $"Error: {ex.Message}");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EIO);
        }
    }

    /// <summary>
    /// Handle READ command (0x21) - Read from open file.
    /// Request: header + fd(1) + length(2)
    /// Response: header + returnCode + bytesRead(2) + data[bytesRead]
    /// </summary>
    public TnfsMessage HandleRead(TnfsMessage request)
    {
        // Validate session
        var session = _sessionManager.FindSessionAndValidateIP(
            request.Header.SessionId, request.Header.ClientIP!);

        if (session == null)
        {
            _logger.LogWarning("READ: Invalid session 0x{SessionId:X4}",
                request.Header.SessionId);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADSESSION);
        }

        // Parse request: fd(1) + length(2)
        if (request.Data.Length < 3)
        {
            _logger.LogWarning("READ: Invalid request size {Size}", request.Data.Length);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EINVAL);
        }

        ReadOnlySpan<byte> data = request.Data.Span;
        byte fd = data[0];
        ushort length = EndianHelper.ReadUInt16(data.Slice(1));

        _logger.LogDebug("READ: Session=0x{SessionId:X4}, fd={Fd}, length={Length}",
            session.SessionId, fd, length);

        // Validate file descriptor
        if (fd >= session.FileDescriptors.Length || session.FileDescriptors[fd] == null)
        {
            _logger.LogWarning("READ: Invalid file descriptor {Fd}", fd);
            _ = EmitFileEventAsync(session, SecurityEventType.FileRead, $"fd:{fd}", false, "Invalid file descriptor");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADF);
        }

        FileStream? fs = session.FileDescriptors[fd];
        if (fs == null)
        {
            _logger.LogWarning("READ: Null FileStream for fd {Fd}", fd);
            _ = EmitFileEventAsync(session, SecurityEventType.FileRead, $"fd:{fd}", false, "Null file descriptor");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADF);
        }

        // Additional validation
        try
        {
            _logger.LogDebug("READ: FileStream state - fd={Fd}, CanRead={CanRead}, Position={Position}, Length={Length}, Name={Name}",
                fd, fs.CanRead, fs.Position, fs.Length, fs.Name);
        }
        catch (Exception validationEx)
        {
            _logger.LogError(validationEx, "READ: FileStream validation failed for fd {Fd} - stream may be disposed", fd);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADF);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Read from file
            byte[] buffer = new byte[length];
            int bytesRead = fs.Read(buffer, 0, length);

            stopwatch.Stop();

            // Response: bytesRead(2) + data
            byte[] response = new byte[2 + bytesRead];
            EndianHelper.WriteUInt16(response.AsSpan().Slice(0), (ushort)bytesRead);
            Array.Copy(buffer, 0, response, 2, bytesRead);

            _logger.LogDebug("READ: Success - fd={Fd}, bytesRead={BytesRead}", fd, bytesRead);

            // Emit security event
            _ = EmitFileEventAsync(
                session,
                SecurityEventType.FileRead,
                fs.Name,
                success: true,
                durationMs: stopwatch.ElapsedMilliseconds,
                metadata: new Dictionary<string, object>
                {
                    { "FileDescriptor", fd },
                    { "BytesRead", bytesRead },
                    { "RequestedLength", length }
                });

            return TnfsMessage.CreateResponse(request, TnfsReturnCode.SUCCESS, response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "READ: Error reading from fd {Fd}, FileStream.Name={Name}, Exception={ExceptionType}",
                fd, fs?.Name ?? "null", ex.GetType().Name);
            _ = EmitFileEventAsync(session, SecurityEventType.FileRead, fs?.Name ?? $"fd:{fd}", false, $"Error: {ex.Message}");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EIO);
        }
    }

    /// <summary>
    /// Handle WRITE command (0x22) - Write to open file.
    /// Request: header + fd(1) + size(2) + data[size]
    /// Response: header + returnCode + bytesWritten(2)
    /// </summary>
    public TnfsMessage HandleWrite(TnfsMessage request)
    {
        // Validate session
        var session = _sessionManager.FindSessionAndValidateIP(
            request.Header.SessionId, request.Header.ClientIP!);

        if (session == null)
        {
            _logger.LogWarning("WRITE: Invalid session 0x{SessionId:X4}",
                request.Header.SessionId);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADSESSION);
        }

        // Parse request: fd(1) + size(2) + data
        if (request.Data.Length < 3)
        {
            _logger.LogWarning("WRITE: Invalid request size {Size}", request.Data.Length);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EINVAL);
        }

        ReadOnlySpan<byte> data = request.Data.Span;
        byte fd = data[0];
        ushort size = EndianHelper.ReadUInt16(data.Slice(1));

        if (request.Data.Length < 3 + size)
        {
            _logger.LogWarning("WRITE: Data size mismatch - expected {Expected}, got {Actual}",
                3 + size, request.Data.Length);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EINVAL);
        }

        _logger.LogDebug("WRITE: Session=0x{SessionId:X4}, fd={Fd}, size={Size}",
            session.SessionId, fd, size);

        // Validate file descriptor
        if (fd >= session.FileDescriptors.Length || session.FileDescriptors[fd] == null)
        {
            _logger.LogWarning("WRITE: Invalid file descriptor {Fd}", fd);
            _ = EmitFileEventAsync(session, SecurityEventType.FileWrite, $"fd:{fd}", false, "Invalid file descriptor");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADF);
        }

        FileStream? fs = session.FileDescriptors[fd];
        if (fs == null)
        {
            _ = EmitFileEventAsync(session, SecurityEventType.FileWrite, $"fd:{fd}", false, "Null file descriptor");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADF);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Write to file
            fs.Write(data.Slice(3, size));
            int bytesWritten = size;

            stopwatch.Stop();

            // Response: bytesWritten(2)
            Span<byte> response = stackalloc byte[2];
            EndianHelper.WriteUInt16(response, (ushort)bytesWritten);

            _logger.LogDebug("WRITE: Success - fd={Fd}, bytesWritten={BytesWritten}", fd, bytesWritten);

            // Emit security event
            _ = EmitFileEventAsync(
                session,
                SecurityEventType.FileWrite,
                fs.Name,
                success: true,
                durationMs: stopwatch.ElapsedMilliseconds,
                metadata: new Dictionary<string, object>
                {
                    { "FileDescriptor", fd },
                    { "BytesWritten", bytesWritten }
                });

            return TnfsMessage.CreateResponse(request, TnfsReturnCode.SUCCESS, response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WRITE: Error writing to fd {Fd}", fd);
            _ = EmitFileEventAsync(session, SecurityEventType.FileWrite, fs.Name, false, $"Error: {ex.Message}");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EIO);
        }
    }

    /// <summary>
    /// Handle CLOSE command (0x23) - Close open file.
    /// Request: header + fd(1)
    /// Response: header + returnCode
    /// </summary>
    public TnfsMessage HandleClose(TnfsMessage request)
    {
        // Validate session
        var session = _sessionManager.FindSessionAndValidateIP(
            request.Header.SessionId, request.Header.ClientIP!);

        if (session == null)
        {
            _logger.LogWarning("CLOSE: Invalid session 0x{SessionId:X4}",
                request.Header.SessionId);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADSESSION);
        }

        // Parse request: fd(1)
        if (request.Data.Length < 1)
        {
            _logger.LogWarning("CLOSE: Invalid request size {Size}", request.Data.Length);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EINVAL);
        }

        byte fd = request.Data.Span[0];

        _logger.LogDebug("CLOSE: Session=0x{SessionId:X4}, fd={Fd}",
            session.SessionId, fd);

        // Validate file descriptor
        if (fd >= session.FileDescriptors.Length || session.FileDescriptors[fd] == null)
        {
            _logger.LogWarning("CLOSE: Invalid file descriptor {Fd}", fd);
            _ = EmitFileEventAsync(session, SecurityEventType.FileClose, $"fd:{fd}", false, "Invalid file descriptor");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADF);
        }

        FileStream? fs = session.FileDescriptors[fd];
        string fileName = fs?.Name ?? $"fd:{fd}";

        var stopwatch = Stopwatch.StartNew();
        try
        {
            session.FileDescriptors[fd]?.Dispose();
            session.FileDescriptors[fd] = null;

            stopwatch.Stop();
            _logger.LogDebug("CLOSE: Success - fd={Fd}", fd);

            // Emit security event
            _ = EmitFileEventAsync(
                session,
                SecurityEventType.FileClose,
                fileName,
                success: true,
                durationMs: stopwatch.ElapsedMilliseconds,
                metadata: new Dictionary<string, object>
                {
                    { "FileDescriptor", fd }
                });

            return TnfsMessage.CreateResponse(request, TnfsReturnCode.SUCCESS);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CLOSE: Error closing fd {Fd}", fd);
            _ = EmitFileEventAsync(session, SecurityEventType.FileClose, fileName, false, $"Error: {ex.Message}");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EIO);
        }
    }

    /// <summary>
    /// Handle LSEEK command (0x25) - Seek in open file.
    /// Request: header + fd(1) + whence(1) + offset(4 signed)
    /// Response: header + returnCode + newPosition(4)
    /// </summary>
    public TnfsMessage HandleLseek(TnfsMessage request)
    {
        // Validate session
        var session = _sessionManager.FindSessionAndValidateIP(
            request.Header.SessionId, request.Header.ClientIP!);

        if (session == null)
        {
            _logger.LogWarning("LSEEK: Invalid session 0x{SessionId:X4}",
                request.Header.SessionId);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADSESSION);
        }

        // Parse request: fd(1) + whence(1) + offset(4)
        if (request.Data.Length < 6)
        {
            _logger.LogWarning("LSEEK: Invalid request size {Size}", request.Data.Length);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EINVAL);
        }

        ReadOnlySpan<byte> data = request.Data.Span;
        byte fd = data[0];
        byte whence = data[1];
        int offset = EndianHelper.ReadInt32(data.Slice(2));

        _logger.LogDebug("LSEEK: Session=0x{SessionId:X4}, fd={Fd}, whence={Whence}, offset={Offset}",
            session.SessionId, fd, whence, offset);

        // Validate file descriptor
        if (fd >= session.FileDescriptors.Length || session.FileDescriptors[fd] == null)
        {
            _logger.LogWarning("LSEEK: Invalid file descriptor {Fd}", fd);
            _ = EmitFileEventAsync(session, SecurityEventType.FileSeek, $"fd:{fd}", false, "Invalid file descriptor");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADF);
        }

        FileStream? fs = session.FileDescriptors[fd];
        if (fs == null)
        {
            _ = EmitFileEventAsync(session, SecurityEventType.FileSeek, $"fd:{fd}", false, "Null file descriptor");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADF);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Convert TNFS whence to SeekOrigin
            SeekOrigin origin = whence switch
            {
                0 => SeekOrigin.Begin,     // SEEK_SET
                1 => SeekOrigin.Current,   // SEEK_CUR
                2 => SeekOrigin.End,       // SEEK_END
                _ => throw new ArgumentException("Invalid whence value")
            };

            long newPosition = fs.Seek(offset, origin);

            stopwatch.Stop();

            // Response: newPosition(4)
            Span<byte> response = stackalloc byte[4];
            EndianHelper.WriteUInt32(response, (uint)newPosition);

            _logger.LogDebug("LSEEK: Success - fd={Fd}, newPosition={NewPosition}", fd, newPosition);

            // Emit security event
            _ = EmitFileEventAsync(
                session,
                SecurityEventType.FileSeek,
                fs.Name,
                success: true,
                durationMs: stopwatch.ElapsedMilliseconds,
                metadata: new Dictionary<string, object>
                {
                    { "FileDescriptor", fd },
                    { "Offset", offset },
                    { "Whence", whence },
                    { "NewPosition", newPosition }
                });

            return TnfsMessage.CreateResponse(request, TnfsReturnCode.SUCCESS, response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LSEEK: Error seeking fd {Fd}", fd);
            _ = EmitFileEventAsync(session, SecurityEventType.FileSeek, fs.Name, false, $"Error: {ex.Message}");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EIO);
        }
    }

    /// <summary>
    /// Handle UNLINK command (0x30) - Delete file.
    /// Request: header + filepath (null-terminated)
    /// Response: header + returnCode
    /// </summary>
    public TnfsMessage HandleUnlink(TnfsMessage request)
    {
        // Validate session
        var session = _sessionManager.FindSessionAndValidateIP(
            request.Header.SessionId, request.Header.ClientIP!);

        if (session == null)
        {
            _logger.LogWarning("UNLINK: Invalid session 0x{SessionId:X4}",
                request.Header.SessionId);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADSESSION);
        }

        // Parse request: path\0
        string path = EndianHelper.ReadNullTerminatedString(request.Data.Span);

        if (string.IsNullOrEmpty(path))
        {
            _logger.LogWarning("UNLINK: Empty path");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EINVAL);
        }

        _logger.LogInformation("UNLINK: Session=0x{SessionId:X4}, Path='{Path}'",
            session.SessionId, path);

        // Resolve to absolute path
        string fullPath = _fileSystem.CombinePath(session.RootPath, path.TrimStart('/'));

        // Validate path security
        if (!PathValidator.IsPathSafe(path, session.RootPath, _fileSystem))
        {
            _logger.LogWarning("UNLINK: Path '{Path}' rejected (security)", path);
            _ = EmitFileEventAsync(session, SecurityEventType.FileDelete, path, false, "Path security violation");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EACCES);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            File.Delete(fullPath);
            stopwatch.Stop();

            _logger.LogDebug("UNLINK: Success - {Path}", fullPath);

            // Emit security event
            _ = EmitFileEventAsync(
                session,
                SecurityEventType.FileDelete,
                path,
                success: true,
                durationMs: stopwatch.ElapsedMilliseconds);

            return TnfsMessage.CreateResponse(request, TnfsReturnCode.SUCCESS);
        }
        catch (FileNotFoundException)
        {
            _logger.LogDebug("UNLINK: File not found: {Path}", fullPath);
            _ = EmitFileEventAsync(session, SecurityEventType.FileDelete, path, false, "File not found");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.ENOENT);
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogDebug("UNLINK: Access denied: {Path}", fullPath);
            _ = EmitFileEventAsync(session, SecurityEventType.FileDelete, path, false, "Access denied");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EACCES);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UNLINK: Error deleting {Path}", fullPath);
            _ = EmitFileEventAsync(session, SecurityEventType.FileDelete, path, false, $"Error: {ex.Message}");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EIO);
        }
    }

    /// <summary>
    /// Handle RENAME command (0x31) - Rename file or directory.
    /// Request: header + source\0 + dest\0
    /// Response: header + returnCode
    /// </summary>
    public TnfsMessage HandleRename(TnfsMessage request)
    {
        // Validate session
        var session = _sessionManager.FindSessionAndValidateIP(
            request.Header.SessionId, request.Header.ClientIP!);

        if (session == null)
        {
            _logger.LogWarning("RENAME: Invalid session 0x{SessionId:X4}",
                request.Header.SessionId);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADSESSION);
        }

        // Parse request: source\0dest\0
        ReadOnlySpan<byte> data = request.Data.Span;
        string sourcePath = EndianHelper.ReadNullTerminatedString(data);

        // Find the second null-terminated string
        int sourceLength = sourcePath.Length + 1; // +1 for null terminator
        if (data.Length <= sourceLength)
        {
            _logger.LogWarning("RENAME: Missing destination path");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EINVAL);
        }

        string destPath = EndianHelper.ReadNullTerminatedString(data.Slice(sourceLength));

        if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(destPath))
        {
            _logger.LogWarning("RENAME: Empty source or dest path");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EINVAL);
        }

        _logger.LogInformation("RENAME: Session=0x{SessionId:X4}, Source='{Source}', Dest='{Dest}'",
            session.SessionId, sourcePath, destPath);

        // Resolve to absolute paths
        string fullSourcePath = _fileSystem.CombinePath(session.RootPath, sourcePath.TrimStart('/'));
        string fullDestPath = _fileSystem.CombinePath(session.RootPath, destPath.TrimStart('/'));

        // Validate path security
        if (!PathValidator.IsPathSafe(sourcePath, session.RootPath, _fileSystem) ||
            !PathValidator.IsPathSafe(destPath, session.RootPath, _fileSystem))
        {
            _logger.LogWarning("RENAME: Path rejected (security)");
            _ = EmitFileEventAsync(session, SecurityEventType.FileRename, $"{sourcePath} -> {destPath}", false, "Path security violation");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EACCES);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Check if source exists
            bool isDirectory = _fileSystem.DirectoryExists(fullSourcePath);
            bool isFile = !isDirectory && _fileSystem.FileExists(fullSourcePath);

            if (!isDirectory && !isFile)
            {
                _logger.LogDebug("RENAME: Source not found: {Path}", fullSourcePath);
                _ = EmitFileEventAsync(session, SecurityEventType.FileRename, $"{sourcePath} -> {destPath}", false, "Source not found");
                return TnfsMessage.CreateResponse(request, TnfsReturnCode.ENOENT);
            }

            // Perform rename
            if (isDirectory)
                Directory.Move(fullSourcePath, fullDestPath);
            else
                File.Move(fullSourcePath, fullDestPath);

            stopwatch.Stop();

            _logger.LogDebug("RENAME: Success - {Source} -> {Dest}", fullSourcePath, fullDestPath);

            // Emit security event
            _ = EmitFileEventAsync(
                session,
                SecurityEventType.FileRename,
                $"{sourcePath} -> {destPath}",
                success: true,
                durationMs: stopwatch.ElapsedMilliseconds,
                metadata: new Dictionary<string, object>
                {
                    { "SourcePath", sourcePath },
                    { "DestPath", destPath },
                    { "IsDirectory", isDirectory }
                });

            return TnfsMessage.CreateResponse(request, TnfsReturnCode.SUCCESS);
        }
        catch (FileNotFoundException)
        {
            _logger.LogDebug("RENAME: Source not found: {Path}", fullSourcePath);
            _ = EmitFileEventAsync(session, SecurityEventType.FileRename, $"{sourcePath} -> {destPath}", false, "Source not found");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.ENOENT);
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogDebug("RENAME: Access denied");
            _ = EmitFileEventAsync(session, SecurityEventType.FileRename, $"{sourcePath} -> {destPath}", false, "Access denied");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EACCES);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RENAME: Error renaming");
            _ = EmitFileEventAsync(session, SecurityEventType.FileRename, $"{sourcePath} -> {destPath}", false, $"Error: {ex.Message}");
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EIO);
        }
    }

    // Helper methods for OPEN command
    private FileAccess ConvertToFileAccess(ushort flags)
    {
        // TNFS flags: O_RDONLY=0, O_WRONLY=1, O_RDWR=2
        int accessMode = flags & 0x03;
        return accessMode switch
        {
            0 => FileAccess.Read,
            1 => FileAccess.Write,
            2 => FileAccess.ReadWrite,
            _ => FileAccess.Read
        };
    }

    private FileMode ConvertToFileMode(ushort flags)
    {
        // TNFS flags: O_CREAT=0x0100, O_EXCL=0x0200, O_APPEND=0x0400, O_TRUNC=0x0800
        bool create = (flags & 0x0100) != 0;
        bool excl = (flags & 0x0200) != 0;
        bool trunc = (flags & 0x0800) != 0;

        if (create && excl)
            return FileMode.CreateNew;
        else if (create && trunc)
            return FileMode.Create;
        else if (create)
            return FileMode.OpenOrCreate;
        else if (trunc)
            return FileMode.Truncate;
        else
            return FileMode.Open;
    }

    /// <summary>
    /// Emit file operation security event
    /// </summary>
    private async Task EmitFileEventAsync(
        TnfsSession session,
        SecurityEventType eventType,
        string path,
        bool success,
        string? denialReason = null,
        long? durationMs = null,
        Dictionary<string, object>? metadata = null)
    {
        if (_securityEvents == null)
            return;

        var entry = new SecurityAuditEntry
        {
            EventType = eventType,
            Severity = success ? AuditSeverity.Info : AuditSeverity.Warning,
            PrincipalId = session.UserId,
            PrincipalName = session.Username,
            IpAddress = session.ClientIP.ToString(),
            SessionId = session.SessionId.ToString("X4"),
            Operation = eventType.ToString(),
            Path = path,
            AccessGranted = success,
            DenialReason = denialReason,
            Message = success
                ? $"{eventType}: {path} by {session.Username}"
                : $"{eventType} denied: {path} - {denialReason}",
            DurationMs = durationMs,
            Metadata = metadata
        };

        await _securityEvents.EmitAsync(entry);
    }
}
