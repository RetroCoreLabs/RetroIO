//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Security context for file system operations.
    /// Carries authentication and authorization information through the VFS layer.
    /// </summary>
    /// <remarks>
    /// Security contexts are attached to file handles, operations, and sessions.
    /// Each filesystem interprets the context according to its own security model:
    /// - Unix: Maps to UID/GID/groups
    /// - Windows: Maps to SID/token
    /// - NDFS: Maps to owner/friends/protection
    /// - Commodore: May ignore (no native security)
    /// - TNFS: Forwarded to remote server
    ///
    /// Design principles:
    /// 1. Generic enough for all filesystems
    /// 2. Extensible via property bag
    /// 3. Immutable once created (thread-safe)
    /// 4. Can represent anonymous/guest access
    /// </remarks>
    public interface ISecurityContext
    {
        /// <summary>
        /// Gets the authenticated user identity (username, SID, UID, etc.).
        /// Null represents anonymous/guest access.
        /// </summary>
        string? UserId { get; }

        /// <summary>
        /// Gets the user's display name (optional, for UI/logging).
        /// </summary>
        string? UserDisplayName { get; }

        /// <summary>
        /// Gets the groups/roles this user belongs to.
        /// </summary>
        string[] Groups { get; }

        /// <summary>
        /// True if this represents an anonymous/guest user.
        /// </summary>
        bool IsAnonymous { get; }

        /// <summary>
        /// True if this represents an administrative/superuser context.
        /// </summary>
        bool IsAdministrator { get; }

        /// <summary>
        /// Filesystem-specific security properties.
        /// Examples:
        /// - "unix.uid" = 1000
        /// - "unix.gid" = 1000
        /// - "unix.groups" = ["users", "sudo"]
        /// - "windows.sid" = "S-1-5-21-..."
        /// - "ndfs.owner" = "SYSTEM"
        /// - "ndfs.friends" = ["USER1", "USER2"]
        /// </summary>
        IReadOnlyDictionary<string, object> Properties { get; }

        /// <summary>
        /// Gets a property value with type safety.
        /// </summary>
        T? GetProperty<T>(string key);

        /// <summary>
        /// Checks if the user has a specific permission.
        /// Generic permission check - filesystems interpret as needed.
        /// </summary>
        /// <param name="permission">Permission name (e.g., "read", "write", "execute", "delete")</param>
        /// <returns>True if permitted</returns>
        bool HasPermission(string permission);
    }

    /// <summary>
    /// Anonymous/guest security context.
    /// </summary>
    public sealed class AnonymousSecurityContext : ISecurityContext
    {
        public static readonly AnonymousSecurityContext Instance = new AnonymousSecurityContext();

        private AnonymousSecurityContext() { }

        public string? UserId => null;
        public string? UserDisplayName => "Guest";
        public string[] Groups => Array.Empty<string>();
        public bool IsAnonymous => true;
        public bool IsAdministrator => false;
        public IReadOnlyDictionary<string, object> Properties => new Dictionary<string, object>();

        public T? GetProperty<T>(string key) => default;
        public bool HasPermission(string permission) => false;
    }

    /// <summary>
    /// System/administrator security context with full access.
    /// </summary>
    public sealed class SystemSecurityContext : ISecurityContext
    {
        public static readonly SystemSecurityContext Instance = new SystemSecurityContext();

        private SystemSecurityContext() { }

        public string? UserId => "SYSTEM";
        public string? UserDisplayName => "System";
        public string[] Groups => new[] { "SYSTEM", "Administrators" };
        public bool IsAnonymous => false;
        public bool IsAdministrator => true;
        public IReadOnlyDictionary<string, object> Properties => new Dictionary<string, object>();

        public T? GetProperty<T>(string key) => default;
        public bool HasPermission(string permission) => true;  // System has all permissions
    }

    /// <summary>
    /// Standard user security context.
    /// </summary>
    public sealed class UserSecurityContext : ISecurityContext
    {
        private readonly Dictionary<string, object> _properties;

        public UserSecurityContext(
            string userId,
            string? displayName = null,
            string[]? groups = null,
            bool isAdmin = false,
            Dictionary<string, object>? properties = null)
        {
            UserId = userId ?? throw new ArgumentNullException(nameof(userId));
            UserDisplayName = displayName ?? userId;
            Groups = groups ?? Array.Empty<string>();
            IsAdministrator = isAdmin;
            _properties = properties ?? new Dictionary<string, object>();
        }

        public string? UserId { get; }
        public string? UserDisplayName { get; }
        public string[] Groups { get; }
        public bool IsAnonymous => false;
        public bool IsAdministrator { get; }
        public IReadOnlyDictionary<string, object> Properties => _properties;

        public T? GetProperty<T>(string key)
        {
            if (_properties.TryGetValue(key, out object? value) && value is T typed)
            {
                return typed;
            }
            return default;
        }

        public bool HasPermission(string permission)
        {
            if (IsAdministrator)
                return true;

            // Check explicit permissions in properties
            if (_properties.TryGetValue($"permission.{permission}", out object? permValue) && permValue is bool allowed)
            {
                return allowed;
            }

            // Default: deny
            return false;
        }
    }
}
