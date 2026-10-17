//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading;
using System.Threading.Tasks;

namespace RetroIO.Identity
{
    /// <summary>
    /// RetroIDP - GUID-based Identity Provider
    /// Manages users, groups, and external identity mappings
    /// </summary>
    public interface IRetroIDP
    {
        // ====================================================================
        // USER MANAGEMENT
        // ====================================================================

        /// <summary>
        /// Create new user with GUID identity
        /// </summary>
        ValueTask<Guid> CreateUserAsync(RetroUser user, CancellationToken ct = default);

        /// <summary>
        /// Get user by GUID
        /// </summary>
        ValueTask<RetroUser?> GetUserAsync(Guid userId, CancellationToken ct = default);

        /// <summary>
        /// Get user by username
        /// </summary>
        ValueTask<RetroUser?> GetUserByUsernameAsync(string username, CancellationToken ct = default);

        /// <summary>
        /// Get user by external identity (OAuth, LDAP, etc.)
        /// </summary>
        ValueTask<RetroUser?> GetUserByExternalIdentityAsync(
            string provider, string externalUserId, CancellationToken ct = default);

        /// <summary>
        /// Update user
        /// </summary>
        ValueTask<bool> UpdateUserAsync(RetroUser user, CancellationToken ct = default);

        /// <summary>
        /// Delete user
        /// </summary>
        ValueTask<bool> DeleteUserAsync(Guid userId, CancellationToken ct = default);

        /// <summary>
        /// List all users
        /// </summary>
        ValueTask<RetroUser[]> ListUsersAsync(CancellationToken ct = default);

        // ====================================================================
        // PASSWORD MANAGEMENT
        // ====================================================================

        /// <summary>
        /// Set user password (Argon2id hash)
        /// </summary>
        ValueTask<bool> SetPasswordAsync(Guid userId, string password, CancellationToken ct = default);

        /// <summary>
        /// Verify password
        /// </summary>
        ValueTask<bool> VerifyPasswordAsync(Guid userId, string password, CancellationToken ct = default);

        /// <summary>
        /// Verify password by username
        /// </summary>
        ValueTask<bool> VerifyPasswordAsync(string username, string password, CancellationToken ct = default);

        // ====================================================================
        // GROUP MANAGEMENT
        // ====================================================================

        /// <summary>
        /// Create new group
        /// </summary>
        ValueTask<Guid> CreateGroupAsync(RetroGroup group, CancellationToken ct = default);

        /// <summary>
        /// Get group by GUID
        /// </summary>
        ValueTask<RetroGroup?> GetGroupAsync(Guid groupId, CancellationToken ct = default);

        /// <summary>
        /// Get group by name
        /// </summary>
        ValueTask<RetroGroup?> GetGroupByNameAsync(string groupName, CancellationToken ct = default);

        /// <summary>
        /// Update group
        /// </summary>
        ValueTask<bool> UpdateGroupAsync(RetroGroup group, CancellationToken ct = default);

        /// <summary>
        /// Delete group
        /// </summary>
        ValueTask<bool> DeleteGroupAsync(Guid groupId, CancellationToken ct = default);

        /// <summary>
        /// List all groups
        /// </summary>
        ValueTask<RetroGroup[]> ListGroupsAsync(CancellationToken ct = default);

        // ====================================================================
        // GROUP MEMBERSHIP
        // ====================================================================

        /// <summary>
        /// Add user to group
        /// </summary>
        ValueTask<bool> AddUserToGroupAsync(Guid userId, Guid groupId, CancellationToken ct = default);

        /// <summary>
        /// Remove user from group
        /// </summary>
        ValueTask<bool> RemoveUserFromGroupAsync(Guid userId, Guid groupId, CancellationToken ct = default);

        /// <summary>
        /// Get all groups for user
        /// </summary>
        ValueTask<RetroGroup[]> GetUserGroupsAsync(Guid userId, CancellationToken ct = default);

        /// <summary>
        /// Get all members of group
        /// </summary>
        ValueTask<RetroUser[]> GetGroupMembersAsync(Guid groupId, CancellationToken ct = default);

        /// <summary>
        /// Check if user is member of group
        /// </summary>
        ValueTask<bool> IsUserInGroupAsync(Guid userId, Guid groupId, CancellationToken ct = default);

        // ====================================================================
        // EXTERNAL IDENTITY MAPPING
        // ====================================================================

        /// <summary>
        /// Link external identity to internal user
        /// </summary>
        ValueTask<bool> LinkExternalIdentityAsync(
            Guid userId, ExternalIdentity externalIdentity, CancellationToken ct = default);

        /// <summary>
        /// Unlink external identity from user
        /// </summary>
        ValueTask<bool> UnlinkExternalIdentityAsync(
            Guid userId, string provider, string externalUserId, CancellationToken ct = default);

        /// <summary>
        /// Get all external identities for user
        /// </summary>
        ValueTask<ExternalIdentity[]> GetExternalIdentitiesAsync(
            Guid userId, CancellationToken ct = default);

        // ====================================================================
        // AUTHENTICATION
        // ====================================================================

        /// <summary>
        /// Authenticate user with username/password
        /// </summary>
        ValueTask<RetroUser?> AuthenticateAsync(
            string username, string password, CancellationToken ct = default);

        /// <summary>
        /// Authenticate via external identity (auto-provision if needed)
        /// </summary>
        ValueTask<RetroUser?> AuthenticateViaExternalIdentityAsync(
            string provider, string externalUserId, string externalUsername,
            bool autoProvision = true, CancellationToken ct = default);

        // ====================================================================
        // PERSISTENCE
        // ====================================================================

        /// <summary>
        /// Save all state to disk
        /// </summary>
        ValueTask SaveAsync(CancellationToken ct = default);

        /// <summary>
        /// Load all state from disk
        /// </summary>
        ValueTask LoadAsync(CancellationToken ct = default);
    }
}
