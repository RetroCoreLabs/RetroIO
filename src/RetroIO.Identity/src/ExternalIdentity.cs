//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;

namespace RetroIO.Identity
{
    /// <summary>
    /// External identity mapping (OAuth, LDAP, SAML)
    /// Maps external authentication providers to internal RetroFS user GUIDs
    /// </summary>
    public sealed class ExternalIdentity
    {
        /// <summary>Provider type (Google, GitHub, Azure AD, LDAP, etc.)</summary>
        public string Provider { get; set; } = string.Empty;

        /// <summary>External user ID (stable ID from provider)</summary>
        public string ExternalUserId { get; set; } = string.Empty;

        /// <summary>External username/email (for display)</summary>
        public string ExternalUsername { get; set; } = string.Empty;

        /// <summary>Maps to this internal GUID</summary>
        public Guid InternalUserId { get; set; }

        /// <summary>Last authenticated</summary>
        public DateTime? LastAuthAt { get; set; }
    }
}
