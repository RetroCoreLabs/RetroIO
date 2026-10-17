//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Tnfs.Server;

/// <summary>
/// Authentication request from TNFS protocol layer
/// </summary>
public sealed class TnfsAuthenticationRequest
{
    /// <summary>
    /// Username provided in MOUNT command
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Password provided in MOUNT command
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Client IP address for audit logging
    /// </summary>
    public string ClientIP { get; set; } = string.Empty;

    /// <summary>
    /// Mount point requested
    /// </summary>
    public string MountPoint { get; set; } = string.Empty;
}

/// <summary>
/// Authentication response back to TNFS protocol layer
/// </summary>
public sealed class TnfsAuthenticationResponse
{
    /// <summary>
    /// Was authentication successful?
    /// </summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>
    /// Authenticated user ID (Guid.Empty if failed)
    /// </summary>
    public Guid UserId { get; set; } = Guid.Empty;

    /// <summary>
    /// Authenticated username (for audit logging)
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Is authenticated user an admin (RetroIDP)?
    /// </summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// Error message if authentication failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Creates a response that reports a successful authentication.
    /// </summary>
    /// <param name="userId">
    /// The authenticated user ID.
    /// </param>
    /// <param name="username">
    /// The authenticated user name.
    /// </param>
    /// <param name="isAdmin">
    /// True when the user is an administrator.
    /// </param>
    /// <returns>
    /// A response with IsAuthenticated set to true.
    /// </returns>
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

    /// <summary>
    /// Creates a response that reports a failed authentication.
    /// </summary>
    /// <param name="errorMessage">
    /// The reason for the failure.
    /// </param>
    /// <returns>
    /// A response with IsAuthenticated set to false and an empty user ID.
    /// </returns>
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
