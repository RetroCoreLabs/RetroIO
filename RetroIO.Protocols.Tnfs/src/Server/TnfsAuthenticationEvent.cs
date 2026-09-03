//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;

namespace RetroIO.Protocols.Tnfs.Server;

/// <summary>
/// Authentication request from TNFS protocol layer
/// </summary>
public sealed class TnfsAuthenticationRequest
{
    /// <summary>Username provided in MOUNT command</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Password provided in MOUNT command</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Client IP address for audit logging</summary>
    public string ClientIP { get; set; } = string.Empty;

    /// <summary>Mount point requested</summary>
    public string MountPoint { get; set; } = string.Empty;
}

/// <summary>
/// Authentication response back to TNFS protocol layer
/// </summary>
public sealed class TnfsAuthenticationResponse
{
    /// <summary>Was authentication successful?</summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>Authenticated user ID (Guid.Empty if failed)</summary>
    public Guid UserId { get; set; } = Guid.Empty;

    /// <summary>Authenticated username (for audit logging)</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Is authenticated user an admin (RetroIDP)?</summary>
    public bool IsAdmin { get; set; }

    /// <summary>Error message if authentication failed</summary>
    public string? ErrorMessage { get; set; }

    public static TnfsAuthenticationResponse Success(Guid userId, string username, bool isAdmin = false)
    {
        return new TnfsAuthenticationResponse
        {
            IsAuthenticated = true,
            UserId = userId,
            Username = username,
            IsAdmin = isAdmin
        };
    }

    public static TnfsAuthenticationResponse Failed(string errorMessage)
    {
        return new TnfsAuthenticationResponse
        {
            IsAuthenticated = false,
            UserId = Guid.Empty,
            Username = string.Empty,
            ErrorMessage = errorMessage
        };
    }
}

/// <summary>
/// Event handler delegate for authentication requests
/// </summary>
public delegate TnfsAuthenticationResponse TnfsAuthenticateEventHandler(TnfsAuthenticationRequest request);
