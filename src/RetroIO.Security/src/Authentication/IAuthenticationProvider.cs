//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RetroIO.Security.Authentication;

/// <summary>
/// Authentication method enumeration
/// </summary>
public enum AuthenticationMethod : byte
{
    /// <summary>
    /// No authentication (for public access)
    /// </summary>
    Anonymous = 0,

    /// <summary>
    /// Username/password authentication
    /// </summary>
    Password = 1,

    /// <summary>
    /// API key authentication
    /// </summary>
    ApiKey = 2,

    /// <summary>
    /// X.509 certificate authentication
    /// </summary>
    Certificate = 3,

    /// <summary>
    /// OAuth 2.0 token authentication
    /// </summary>
    OAuth2 = 4,

    /// <summary>
    /// Session token authentication
    /// </summary>
    SessionToken = 5
}

/// <summary>
/// Authentication failure reason enumeration
/// </summary>
public enum AuthenticationFailureReason : byte
{
    /// <summary>
    /// No failure - successful authentication
    /// </summary>
    None = 0,

    /// <summary>
    /// Invalid credentials provided
    /// </summary>
    InvalidCredentials = 1,

    /// <summary>
    /// Account has been locked
    /// </summary>
    AccountLocked = 2,

    /// <summary>
    /// Account has been disabled
    /// </summary>
    AccountDisabled = 3,

    /// <summary>
    /// Token has expired
    /// </summary>
    TokenExpired = 4,

    /// <summary>
    /// Token is invalid or corrupted
    /// </summary>
    InvalidToken = 5,

    /// <summary>
    /// Certificate validation failed
    /// </summary>
    InvalidCertificate = 6,

    /// <summary>
    /// Insufficient permissions
    /// </summary>
    InsufficientPermissions = 7,

    /// <summary>
    /// Internal authentication system error
    /// </summary>
    InternalError = 8
}

/// <summary>
/// Authentication request containing credentials and context
/// </summary>
public sealed class AuthenticationRequest
{
    /// <summary>
    /// Authentication method being used
    /// </summary>
    public AuthenticationMethod Method { get; set; }

    /// <summary>
    /// Username (for password auth)
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Password (for password auth) - use SecureString in production
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// API key (for API key auth)
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Session token (for session token auth)
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// Certificate thumbprint (for certificate auth)
    /// </summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>
    /// Client IP address
    /// </summary>
    public string? ClientIpAddress { get; set; }

    /// <summary>
    /// Additional authentication properties
    /// </summary>
    public Dictionary<string, string> Properties { get; set; } = new();
}

/// <summary>
/// Security principal representing an authenticated user or service
/// </summary>
public sealed class SecurityPrincipal
{
    /// <summary>
    /// Unique principal identifier
    /// </summary>
    public Guid PrincipalId { get; set; }

    /// <summary>
    /// Principal name (username, service name, etc.)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Authentication method used
    /// </summary>
    public AuthenticationMethod AuthMethod { get; set; }

    /// <summary>
    /// Authentication timestamp
    /// </summary>
    public DateTime AuthenticatedAt { get; set; }

    /// <summary>
    /// Token expiration timestamp (if applicable)
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Principal roles
    /// </summary>
    public HashSet<string> Roles { get; set; } = new();

    /// <summary>
    /// Principal claims (key-value pairs)
    /// </summary>
    public Dictionary<string, string> Claims { get; set; } = new();

    /// <summary>
    /// Check if principal has a specific role
    /// </summary>
    public bool IsInRole(string role) => Roles.Contains(role);

    /// <summary>
    /// Check if principal has a specific claim with value
    /// </summary>
    public bool HasClaim(string claimType, string claimValue)
    {
        return Claims.TryGetValue(claimType, out string? value) && value == claimValue;
    }

    /// <summary>
    /// Check if token has expired
    /// </summary>
    public bool IsExpired()
    {
        return ExpiresAt.HasValue && DateTime.UtcNow >= ExpiresAt.Value;
    }
}

/// <summary>
/// Authentication result containing principal or failure information
/// </summary>
public sealed class AuthenticationResult
{
    /// <summary>
    /// Whether authentication was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Authenticated principal (null if authentication failed)
    /// </summary>
    public SecurityPrincipal? Principal { get; set; }

    /// <summary>
    /// Failure reason (None if successful)
    /// </summary>
    public AuthenticationFailureReason FailureReason { get; set; }

    /// <summary>
    /// Human-readable error message
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Session token (for stateful authentication)
    /// </summary>
    public string? SessionToken { get; set; }

    /// <summary>
    /// Token expiration timestamp
    /// </summary>
    public DateTime? TokenExpiresAt { get; set; }

    /// <summary>
    /// Create successful authentication result
    /// </summary>
    public static AuthenticationResult Successful(SecurityPrincipal principal,
        string? sessionToken = null, DateTime? tokenExpiresAt = null)
    {
        return new AuthenticationResult
        {
            Success = true,
            Principal = principal,
            FailureReason = AuthenticationFailureReason.None,
            SessionToken = sessionToken,
            TokenExpiresAt = tokenExpiresAt
        };
    }

    /// <summary>
    /// Create failed authentication result
    /// </summary>
    public static AuthenticationResult Failed(AuthenticationFailureReason reason,
        string errorMessage)
    {
        return new AuthenticationResult
        {
            Success = false,
            Principal = null,
            FailureReason = reason,
            ErrorMessage = errorMessage
        };
    }
}

/// <summary>
/// Authentication provider interface - implement to provide custom authentication
/// </summary>
public interface IAuthenticationProvider
{
    /// <summary>
    /// Authenticate a request and return principal
    /// </summary>
    /// <param name="request">
    /// Authentication request with credentials
    /// </param>
    /// <param name="ct">
    /// Cancellation token
    /// </param>
    /// <returns>
    /// Authentication result with principal or failure information
    /// </returns>
    ValueTask<AuthenticationResult> AuthenticateAsync(
        AuthenticationRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Validate an existing session token
    /// </summary>
    /// <param name="token">
    /// Session token to validate
    /// </param>
    /// <param name="ct">
    /// Cancellation token
    /// </param>
    /// <returns>
    /// Principal if token is valid, null otherwise
    /// </returns>
    ValueTask<SecurityPrincipal?> ValidateTokenAsync(
        string token,
        CancellationToken ct = default);

    /// <summary>
    /// Revoke a session token (logout)
    /// </summary>
    /// <param name="token">
    /// Session token to revoke
    /// </param>
    /// <param name="ct">
    /// Cancellation token
    /// </param>
    /// <returns>
    /// True if token was revoked, false if not found
    /// </returns>
    ValueTask<bool> RevokeTokenAsync(
        string token,
        CancellationToken ct = default);

    /// <summary>
    /// Refresh a session token (extend expiration)
    /// </summary>
    /// <param name="token">
    /// Session token to refresh
    /// </param>
    /// <param name="ct">
    /// Cancellation token
    /// </param>
    /// <returns>
    /// New authentication result with refreshed token
    /// </returns>
    ValueTask<AuthenticationResult> RefreshTokenAsync(
        string token,
        CancellationToken ct = default);
}
