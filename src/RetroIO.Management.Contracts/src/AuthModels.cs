//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Management.Contracts;

/// <summary>Credentials posted to the management API's login endpoint.</summary>
public class LoginRequest
{
    /// <summary>RetroIDP user name.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Password, verified against the Argon2id hash held by RetroIDP.</summary>
    public string Password { get; set; } = string.Empty;
}

/// <summary>What a successful login returns.</summary>
/// <remarks>
/// The token goes in an <c>Authorization: Bearer</c> header on every subsequent REST call, and in
/// the <c>access_token</c> query string when opening the SignalR connection (the WebSocket
/// handshake cannot carry an Authorization header, so that is how ASP.NET Core does it).
/// </remarks>
public class LoginResponse
{
    /// <summary>Signed bearer token.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>When the token stops being accepted, in UTC.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Name of the user the token was issued to.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Display name of that user.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// True when the user may use the management API. Management requires an administrator; a
    /// non-admin can log in and be told so, rather than having every call fail with a bare 403.
    /// </summary>
    public bool IsAdmin { get; set; }
}

/// <summary>Names shared by the server's authorization policy and any client that inspects a token.</summary>
public static class ManagementAuth
{
    /// <summary>Authorization policy every management endpoint requires.</summary>
    public const string PolicyName = "RetroManagement";

    /// <summary>Claim carrying the RetroIDP user id.</summary>
    public const string UserIdClaim = "retrofs:uid";

    /// <summary>Claim set to "true" when the user is an administrator.</summary>
    public const string AdminClaim = "retrofs:admin";

    /// <summary>Query-string parameter the SignalR handshake carries the token in.</summary>
    public const string AccessTokenQueryParameter = "access_token";

    /// <summary>Route of the login endpoint, relative to the API root.</summary>
    public const string LoginPath = "/api/v1/auth/login";
}
