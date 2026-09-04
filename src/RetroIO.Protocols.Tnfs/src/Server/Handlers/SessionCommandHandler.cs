//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using Microsoft.Extensions.Logging;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Protocols.Tnfs.Server;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Server.Core.Utils;

namespace RetroIO.Protocols.Tnfs.Server.Handlers;

/// <summary>
/// Handles TNFS session commands (MOUNT, UNMOUNT).
/// </summary>
public sealed class SessionCommandHandler
{
    private readonly SessionManager _sessionManager;
    private readonly ILogger<SessionCommandHandler> _logger;
    private readonly string _rootDirectory;
    private readonly IFileSystemBackend _fileSystem;

    /// <summary>
    /// Authentication event - raised when MOUNT includes credentials
    /// If no handler is registered, anonymous access is allowed
    /// </summary>
    public event TnfsAuthenticateEventHandler? OnAuthenticationRequest;

    public SessionCommandHandler(
        SessionManager sessionManager,
        ILogger<SessionCommandHandler> logger,
        string rootDirectory,
        IFileSystemBackend fileSystem)
    {
        _sessionManager = sessionManager;
        _logger = logger;
        _rootDirectory = rootDirectory;
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Handle MOUNT command (0x00).
    /// Creates new session and returns session ID + protocol version.
    /// </summary>
    public TnfsMessage HandleMount(TnfsMessage request)
    {
        // MOUNT packet format:
        // Header(4) + Protocol Version(2) + MountPoint + 0x00 + User + 0x00 + Pass + 0x00

        if (request.Data.Length < 3)  // Minimum: version(2) + null(1)
        {
            _logger.LogWarning("MOUNT: Invalid packet size ({Size} bytes)", request.Data.Length);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EINVAL);
        }

        var data = request.Data.Span;

        // Skip protocol version (2 bytes)
        int offset = 2;

        // Read mount point (null-terminated string)
        string mountPoint = EndianHelper.ReadNullTerminatedString(data.Slice(offset));
        offset += mountPoint.Length + 1; // +1 for null terminator

        // Read optional username (null-terminated string)
        string username = string.Empty;
        if (offset < data.Length)
        {
            username = EndianHelper.ReadNullTerminatedString(data.Slice(offset));
            offset += username.Length + 1;
        }

        // Read optional password (null-terminated string)
        string password = string.Empty;
        if (offset < data.Length)
        {
            password = EndianHelper.ReadNullTerminatedString(data.Slice(offset));
        }

        if (string.IsNullOrEmpty(mountPoint))
        {
            mountPoint = "/";  // Default to root
        }

        // Determine user identity via authentication event
        Guid userId = Guid.Empty;  // UNKNOWN by default
        string authenticatedUsername = "UNKNOWN";
        bool isAdmin = false;

        // If credentials provided, attempt authentication via event
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            // Raise authentication event if handler is registered
            if (OnAuthenticationRequest != null)
            {
                var authRequest = new TnfsAuthenticationRequest
                {
                    Username = username,
                    Password = password,
                    ClientIP = request.ClientIP.ToString(),
                    MountPoint = mountPoint
                };

                var authResponse = OnAuthenticationRequest(authRequest);

                if (authResponse.IsAuthenticated)
                {
                    userId = authResponse.UserId;
                    authenticatedUsername = authResponse.Username;
                    isAdmin = authResponse.IsAdmin;
                    _logger.LogInformation("MOUNT request from {IP}: mountPoint='{MountPoint}', user='{User}' authenticated as {UserId}",
                        request.ClientIP, mountPoint, username, userId);
                }
                else
                {
                    _logger.LogWarning("MOUNT request from {IP}: Authentication failed for user '{User}': {Error}",
                        request.ClientIP, username, authResponse.ErrorMessage ?? "Unknown error");
                    return TnfsMessage.CreateResponse(request, TnfsReturnCode.EACCES);
                }
            }
            else
            {
                // No authentication handler registered - allow anonymous
                _logger.LogInformation("MOUNT request from {IP}: mountPoint='{MountPoint}', user='{User}' (no auth handler - allowing anonymous)",
                    request.ClientIP, mountPoint, username);
            }
        }
        else if (!string.IsNullOrEmpty(username))
        {
            // Username without password - log and allow anonymous
            _logger.LogInformation("MOUNT request from {IP}: mountPoint='{MountPoint}', user='{User}' (no password - allowing anonymous)",
                request.ClientIP, mountPoint, username);
        }
        else
        {
            // No credentials - anonymous access
            _logger.LogInformation("MOUNT request from {IP}: mountPoint='{MountPoint}' (anonymous)",
                request.ClientIP, mountPoint);
        }

        // Construct full root path for session
        string sessionRoot = Path.Combine(_rootDirectory, mountPoint.TrimStart('/'));

        // Validate directory exists using filesystem backend
        if (!_fileSystem.DirectoryExists(sessionRoot))
        {
            _logger.LogWarning("MOUNT: Invalid directory '{Path}'", sessionRoot);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.ENOENT);
        }

        // Create session with user binding
        var session = _sessionManager.CreateSession(request.ClientIP, sessionRoot, userId, authenticatedUsername, isAdmin);

        // Build response: Protocol Version(2) + Timeout(2)
        Span<byte> responseData = stackalloc byte[4];
        responseData[0] = (byte)TnfsConstants.PROTOVERSION_LSB;
        responseData[1] = (byte)TnfsConstants.PROTOVERSION_MSB;
        responseData[2] = (byte)TnfsConstants.TIMEOUT_LSB;
        responseData[3] = (byte)TnfsConstants.TIMEOUT_MSB;

        var response = TnfsMessage.CreateResponse(request, TnfsReturnCode.SUCCESS, responseData);

        // Update header with new session ID
        var header = response.Header;
        header.SessionId = session.SessionId;
        response.Header = header;

        _logger.LogInformation("MOUNT SUCCESS: SessionId=0x{SessionId:X4}, Root='{Root}'",
            session.SessionId, sessionRoot);

        return response;
    }

    /// <summary>
    /// Handle UNMOUNT command (0x01).
    /// Removes session and cleans up resources.
    /// </summary>
    public TnfsMessage HandleUnmount(TnfsMessage request)
    {
        // Validate session exists
        var session = _sessionManager.FindSessionAndValidateIP(request.Header.SessionId, request.ClientIP);

        if (session == null)
        {
            _logger.LogWarning("UNMOUNT: Invalid session 0x{SessionId:X4}", request.Header.SessionId);
            return TnfsMessage.CreateResponse(request, TnfsReturnCode.EBADSESSION);
        }

        _logger.LogInformation("UNMOUNT: SessionId=0x{SessionId:X4}", request.Header.SessionId);

        // Remove session (this closes all files and directories)
        _sessionManager.RemoveSession(session.SessionId);

        return TnfsMessage.CreateResponse(request, TnfsReturnCode.SUCCESS);
    }
}
