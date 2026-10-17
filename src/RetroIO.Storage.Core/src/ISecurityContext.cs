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
    ///  - Unix: Maps to UID/GID/groups.
    ///  - Windows: Maps to SID/token.
    ///  - NDFS: Maps to owner/friends/protection.
    ///  - Commodore: May ignore (no native security).
    ///  - TNFS: Forwarded to remote server.
    /// <para>
    /// Design principles:
    ///  - Generic enough for all filesystems.
    ///  - Extensible via property bag.
    ///  - Immutable once created (thread-safe).
    ///  - Can represent anonymous/guest access.
    /// </para>
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
        /// </summary>
        /// <remarks>
        /// Examples:
        ///  - "unix.uid" = 1000
        ///  - "unix.gid" = 1000
        ///  - "unix.groups" = ["users", "sudo"]
        ///  - "windows.sid" = "S-1-5-21-..."
        ///  - "ndfs.owner" = "SYSTEM"
        ///  - "ndfs.friends" = ["USER1", "USER2"]
        /// </remarks>
        IReadOnlyDictionary<string, object> Properties { get; }

        /// <summary>
        /// Gets a property value with type safety.
        /// </summary>
        /// <typeparam name="T">
        /// The type the stored value is expected to have.
        /// </typeparam>
        /// <param name="key">
        /// The property key, for example <c>unix.uid</c>.
        /// </param>
        /// <returns>
        /// The value stored under <paramref name="key"/> when it is a <typeparamref name="T"/>; otherwise the default of <typeparamref name="T"/>.
        /// </returns>
        T? GetProperty<T>(string key);

        /// <summary>
        /// Checks if the user has a specific permission.
        /// Generic permission check - filesystems interpret as needed.
        /// </summary>
        /// <param name="permission">
        /// Permission name (e.g., "read", "write", "execute", "delete").
        /// </param>
        /// <returns>
        /// True if permitted.
        /// </returns>
        bool HasPermission(string permission);
    }

    /// <summary>
    /// Anonymous/guest security context.
    /// </summary>
    public sealed class AnonymousSecurityContext : ISecurityContext
    {
        /// <summary>
        /// The single shared guest context.
        /// </summary>
        public static readonly AnonymousSecurityContext Instance = new AnonymousSecurityContext();

        private AnonymousSecurityContext() { }

        /// <summary>
        /// Always null: a guest has no identity.
        /// </summary>
        public string? UserId => null;

        /// <summary>
        /// Always <c>Guest</c>.
        /// </summary>
        public string? UserDisplayName => "Guest";

        /// <summary>
        /// Always empty: a guest belongs to no group.
        /// </summary>
        public string[] Groups => Array.Empty<string>();

        /// <summary>
        /// Always true.
        /// </summary>
        public bool IsAnonymous => true;

        /// <summary>
        /// Always false.
        /// </summary>
        public bool IsAdministrator => false;

        /// <summary>
        /// Always empty. Each read returns a new empty dictionary.
        /// </summary>
        public IReadOnlyDictionary<string, object> Properties => new Dictionary<string, object>();

        /// <summary>
        /// Always returns the default of <typeparamref name="T"/>: a guest carries no properties.
        /// </summary>
        /// <typeparam name="T">
        /// The type the caller expects.
        /// </typeparam>
        /// <param name="key">
        /// The property key. Ignored.
        /// </param>
        /// <returns>
        /// The default of <typeparamref name="T"/>.
        /// </returns>
        public T? GetProperty<T>(string key) => default;

        /// <summary>
        /// Always denies: a guest has no permissions.
        /// </summary>
        /// <param name="permission">
        /// The permission asked for. Ignored.
        /// </param>
        /// <returns>
        /// Always false.
        /// </returns>
        public bool HasPermission(string permission) => false;
    }

    /// <summary>
    /// System/administrator security context with full access.
    /// </summary>
    public sealed class SystemSecurityContext : ISecurityContext
    {
        /// <summary>
        /// The single shared system context.
        /// </summary>
        public static readonly SystemSecurityContext Instance = new SystemSecurityContext();

        private SystemSecurityContext() { }

        /// <summary>
        /// Always <c>SYSTEM</c>.
        /// </summary>
        public string? UserId => "SYSTEM";

        /// <summary>
        /// Always <c>System</c>.
        /// </summary>
        public string? UserDisplayName => "System";

        /// <summary>
        /// Always <c>SYSTEM</c> and <c>Administrators</c>. Each read returns a new array.
        /// </summary>
        public string[] Groups => new[] { "SYSTEM", "Administrators" };

        /// <summary>
        /// Always false.
        /// </summary>
        public bool IsAnonymous => false;

        /// <summary>
        /// Always true.
        /// </summary>
        public bool IsAdministrator => true;

        /// <summary>
        /// Always empty. Each read returns a new empty dictionary.
        /// </summary>
        public IReadOnlyDictionary<string, object> Properties => new Dictionary<string, object>();

        /// <summary>
        /// Always returns the default of <typeparamref name="T"/>: the system context carries no properties.
        /// </summary>
        /// <typeparam name="T">
        /// The type the caller expects.
        /// </typeparam>
        /// <param name="key">
        /// The property key. Ignored.
        /// </param>
        /// <returns>
        /// The default of <typeparamref name="T"/>.
        /// </returns>
        public T? GetProperty<T>(string key) => default;

        /// <summary>
        /// Always grants: the system context has every permission.
        /// </summary>
        /// <param name="permission">
        /// The permission asked for. Ignored.
        /// </param>
        /// <returns>
        /// Always true.
        /// </returns>
        public bool HasPermission(string permission) => true;  // System has all permissions
    }

    /// <summary>
    /// Standard user security context.
    /// </summary>
    public sealed class UserSecurityContext : ISecurityContext
    {
        private readonly Dictionary<string, object> _properties;

        /// <summary>
        /// Creates a context for one signed-in user.
        /// </summary>
        /// <param name="userId">
        /// The user identity. Required.
        /// </param>
        /// <param name="displayName">
        /// The name shown in UI and logs. When null, <paramref name="userId"/> is used.
        /// </param>
        /// <param name="groups">
        /// The groups the user belongs to. When null, the user belongs to none.
        /// </param>
        /// <param name="isAdmin">
        /// True to make this an administrator context, which <see cref="HasPermission"/> always grants.
        /// </param>
        /// <param name="properties">
        /// Filesystem-specific properties, including <c>permission.name</c> entries read by <see cref="HasPermission"/>.
        /// The dictionary is kept, not copied. When null, the context has no properties.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="userId"/> is null.
        /// </exception>
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

        /// <summary>
        /// The user identity given to the constructor. Never null.
        /// </summary>
        public string? UserId { get; }

        /// <summary>
        /// The display name given to the constructor, or the user identity when none was given.
        /// </summary>
        public string? UserDisplayName { get; }

        /// <summary>
        /// The groups given to the constructor, or an empty array.
        /// </summary>
        public string[] Groups { get; }

        /// <summary>
        /// Always false.
        /// </summary>
        public bool IsAnonymous => false;

        /// <summary>
        /// True when the context was created with <c>isAdmin</c> set.
        /// </summary>
        public bool IsAdministrator { get; }

        /// <summary>
        /// The properties given to the constructor, or an empty dictionary.
        /// </summary>
        public IReadOnlyDictionary<string, object> Properties => _properties;

        /// <summary>
        /// Gets a property value with type safety.
        /// </summary>
        /// <typeparam name="T">
        /// The type the stored value is expected to have.
        /// </typeparam>
        /// <param name="key">
        /// The property key.
        /// </param>
        /// <returns>
        /// The value stored under <paramref name="key"/> when it is a <typeparamref name="T"/>; otherwise the default of <typeparamref name="T"/>.
        /// </returns>
        public T? GetProperty<T>(string key)
        {
            if (_properties.TryGetValue(key, out object? value) && value is T typed)
            {
                return typed;
            }
            return default;
        }

        /// <summary>
        /// Checks if the user has a specific permission.
        /// </summary>
        /// <remarks>
        /// Decided in this order:
        ///  - An administrator is always granted.
        ///  - A bool property named <c>permission.</c> followed by <paramref name="permission"/> decides it.
        ///  - Anything else is denied.
        /// </remarks>
        /// <param name="permission">
        /// Permission name (e.g., "read", "write", "execute", "delete").
        /// </param>
        /// <returns>
        /// True if permitted.
        /// </returns>
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
