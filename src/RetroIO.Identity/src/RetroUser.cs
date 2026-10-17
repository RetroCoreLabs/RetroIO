//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Identity
{
    /// <summary>
    /// RetroIDP User - GUID-based internal identity
    /// Primary identity that never changes, globally unique
    /// </summary>
    public sealed class RetroUser
    {
        /// <summary>Primary identity (never changes, globally unique)</summary>
        public Guid UserId { get; set; }

        /// <summary>Username (unique, can be changed)</summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>Display name (for UI)</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Email (optional, for notifications)</summary>
        public string? Email { get; set; }

        /// <summary>Password hash (Argon2id) - only for password auth</summary>
        public string? PasswordHash { get; set; }

        /// <summary>Member of these groups (list of group GUIDs)</summary>
        public List<Guid> Groups { get; set; } = new();

        /// <summary>External identity mappings (OAuth, LDAP, etc.)</summary>
        public List<ExternalIdentity> ExternalIdentities { get; set; } = new();

        /// <summary>Is account active?</summary>
        public bool IsActive { get; set; } = true;

        /// <summary>Is admin? (bypass all permission checks)</summary>
        public bool IsAdmin { get; set; } = false;

        /// <summary>When null, TNFS access is allowed (legacy default).</summary>
        public bool? AllowTnfs { get; set; }

        /// <summary>When null, Econet access is denied until explicitly enabled.</summary>
        public bool? AllowEconet { get; set; }

        /// <summary>Created timestamp</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Last successful login on any protocol</summary>
        public DateTime? LastLoginAt { get; set; }

        /// <summary>Last successful TNFS login</summary>
        public DateTime? LastLoginTnfsAt { get; set; }

        /// <summary>Last successful Econet login</summary>
        public DateTime? LastLoginEconetAt { get; set; }

        /// <summary>Last failed login attempt (wrong password or protocol denied)</summary>
        public DateTime? LastFailedLoginAt { get; set; }

        /// <summary>Additional metadata</summary>
        public Dictionary<string, string> Metadata { get; set; } = new();

        public bool EffectiveAllowTnfs => AllowTnfs ?? true;

        public bool EffectiveAllowEconet => AllowEconet ?? false;
    }
}
