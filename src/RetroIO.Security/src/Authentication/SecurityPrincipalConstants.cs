//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;

namespace RetroIO.Security.Authentication;

/// <summary>
/// Security principal constants and well-known identities
/// </summary>
public static class SecurityPrincipalConstants
{
    /// <summary>
    /// UNKNOWN principal GUID - used when no identity is known
    /// Value: 00000000-0000-0000-0000-000000000000
    /// </summary>
    public static readonly Guid UnknownPrincipalId = Guid.Empty;

    /// <summary>
    /// UNKNOWN principal name
    /// </summary>
    public const string UnknownPrincipalName = "UNKNOWN";

    /// <summary>
    /// Create UNKNOWN security principal
    /// Used when no identity is known (e.g., unauthenticated mount operations)
    /// </summary>
    /// <returns>SecurityPrincipal with UNKNOWN identity</returns>
    public static SecurityPrincipal CreateUnknownPrincipal()
    {
        return new SecurityPrincipal
        {
            PrincipalId = UnknownPrincipalId,
            Name = UnknownPrincipalName,
            AuthMethod = AuthenticationMethod.Anonymous,
            AuthenticatedAt = DateTime.UtcNow,
            ExpiresAt = null, // UNKNOWN principal never expires
            Roles = new System.Collections.Generic.HashSet<string> { "anonymous" },
            Claims = new System.Collections.Generic.Dictionary<string, string>()
        };
    }

    /// <summary>
    /// Check if principal is UNKNOWN
    /// </summary>
    /// <param name="principal">Principal to check</param>
    /// <returns>True if principal is UNKNOWN identity</returns>
    public static bool IsUnknownPrincipal(SecurityPrincipal? principal)
    {
        return principal == null || principal.PrincipalId == UnknownPrincipalId;
    }

    /// <summary>
    /// Check if principal GUID is UNKNOWN
    /// </summary>
    /// <param name="principalId">Principal ID to check</param>
    /// <returns>True if GUID is UNKNOWN (all zeros)</returns>
    public static bool IsUnknownPrincipalId(Guid principalId)
    {
        return principalId == UnknownPrincipalId;
    }
}
