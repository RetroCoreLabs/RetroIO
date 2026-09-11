//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;

namespace RetroIO.Security.Implementation
{
    /// <summary>
    /// Security principal representing an authenticated (or anonymous) user
    /// Used throughout the security pipeline
    /// </summary>
    public sealed class SecurityPrincipal
    {
        /// <summary>User GUID (Guid.Empty = UNKNOWN/anonymous)</summary>
        public Guid PrincipalId { get; set; }

        /// <summary>Username (for display/logging)</summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>Display name</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Group GUIDs user is member of</summary>
        public List<Guid> Groups { get; set; } = new();

        /// <summary>Is this UNKNOWN (unauthenticated)?</summary>
        public bool IsAnonymous => PrincipalId == Guid.Empty;

        /// <summary>Is this admin? (bypass all checks)</summary>
        public bool IsAdministrator { get; set; }

        /// <summary>Session token (for multi-request sessions)</summary>
        public string? SessionToken { get; set; }

        /// <summary>Token expiration</summary>
        public DateTime? ExpiresAt { get; set; }

        /// <summary>Authentication method used</summary>
        public string AuthMethod { get; set; } = "None";

        /// <summary>Client IP address</summary>
        public string? IpAddress { get; set; }

        /// <summary>Additional claims/metadata</summary>
        public Dictionary<string, string> Claims { get; set; } = new();

        /// <summary>Create UNKNOWN principal</summary>
        public static SecurityPrincipal CreateUnknown(string? ipAddress = null)
        {
            return new SecurityPrincipal
            {
                PrincipalId = Guid.Empty,
                Username = "UNKNOWN",
                DisplayName = "Anonymous User",
                IsAdministrator = false,
                AuthMethod = "Anonymous",
                IpAddress = ipAddress
            };
        }

        /// <summary>Create authenticated principal from RetroUser</summary>
        public static SecurityPrincipal FromRetroUser(
            RetroIO.Identity.RetroUser user,
            string authMethod = "Password",
            string? sessionToken = null,
            string? ipAddress = null)
        {
            return new SecurityPrincipal
            {
                PrincipalId = user.UserId,
                Username = user.Username,
                DisplayName = user.DisplayName,
                Groups = new List<Guid>(user.Groups),
                IsAdministrator = user.IsAdmin,
                SessionToken = sessionToken,
                AuthMethod = authMethod,
                IpAddress = ipAddress
            };
        }
    }
}
