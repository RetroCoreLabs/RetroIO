//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RetroIO.Identity
{
    /// <summary>
    /// JSON-based RetroIDP implementation
    /// Stores users and groups in JSON files on disk
    /// </summary>
    public sealed class JsonRetroIDP : IRetroIDP, IDisposable
    {
        private readonly string _usersFilePath;
        private readonly string _groupsFilePath;
        private readonly SemaphoreSlim _lock = new(1, 1);

        private Dictionary<Guid, RetroUser> _users = new();
        private Dictionary<Guid, RetroGroup> _groups = new();
        private Dictionary<string, Guid> _usernameIndex = new();
        private Dictionary<string, Guid> _externalIdentityIndex = new(); // "provider:externalUserId" -> userId

        public JsonRetroIDP(string usersFilePath, string groupsFilePath)
        {
            _usersFilePath = usersFilePath ?? throw new ArgumentNullException(nameof(usersFilePath));
            _groupsFilePath = groupsFilePath ?? throw new ArgumentNullException(nameof(groupsFilePath));
        }

        // ====================================================================
        // USER MANAGEMENT
        // ====================================================================

        public async ValueTask<Guid> CreateUserAsync(RetroUser user, CancellationToken ct = default)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));

            await _lock.WaitAsync(ct);
            try
            {
                // Generate GUID if not provided
                if (user.UserId == Guid.Empty)
                {
                    user.UserId = Guid.NewGuid();
                }

                // Check for duplicate username
                if (_usernameIndex.ContainsKey(user.Username.ToLowerInvariant()))
                {
                    throw new InvalidOperationException($"Username '{user.Username}' already exists");
                }

                // Add user
                _users[user.UserId] = user;
                _usernameIndex[user.Username.ToLowerInvariant()] = user.UserId;

                // Index external identities
                IndexExternalIdentities(user);

                return user.UserId;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask<RetroUser?> GetUserAsync(Guid userId, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                return _users.TryGetValue(userId, out RetroUser? user) ? user : null;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask<RetroUser?> GetUserByUsernameAsync(string username, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;

            await _lock.WaitAsync(ct);
            try
            {
                if (_usernameIndex.TryGetValue(username.ToLowerInvariant(), out Guid userId))
                {
                    return _users.TryGetValue(userId, out RetroUser? user) ? user : null;
                }
                return null;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask<RetroUser?> GetUserByExternalIdentityAsync(
            string provider, string externalUserId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(externalUserId))
                return null;

            await _lock.WaitAsync(ct);
            try
            {
                string key = $"{provider}:{externalUserId}";
                if (_externalIdentityIndex.TryGetValue(key, out Guid userId))
                {
                    return _users.TryGetValue(userId, out RetroUser? user) ? user : null;
                }
                return null;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask<bool> UpdateUserAsync(RetroUser user, CancellationToken ct = default)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));

            await _lock.WaitAsync(ct);
            try
            {
                if (!_users.ContainsKey(user.UserId))
                    return false;

                // Update username index if username changed
                RetroUser existingUser = _users[user.UserId];
                if (existingUser.Username != user.Username)
                {
                    _usernameIndex.Remove(existingUser.Username.ToLowerInvariant());
                    _usernameIndex[user.Username.ToLowerInvariant()] = user.UserId;
                }

                // Re-index external identities
                RemoveExternalIdentityIndexes(existingUser);
                IndexExternalIdentities(user);

                _users[user.UserId] = user;
                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask<bool> DeleteUserAsync(Guid userId, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (!_users.TryGetValue(userId, out RetroUser? user))
                    return false;

                _users.Remove(userId);
                _usernameIndex.Remove(user.Username.ToLowerInvariant());
                RemoveExternalIdentityIndexes(user);

                // Remove from all groups
                foreach (RetroGroup group in _groups.Values)
                {
                    group.Members.Remove(userId);
                }

                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask<RetroUser[]> ListUsersAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                RetroUser[] users = new RetroUser[_users.Count];
                int index = 0;
                foreach (RetroUser user in _users.Values)
                {
                    users[index++] = user;
                }
                return users;
            }
            finally
            {
                _lock.Release();
            }
        }

        // ====================================================================
        // PASSWORD MANAGEMENT
        // ====================================================================

        public async ValueTask<bool> SetPasswordAsync(Guid userId, string password, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentNullException(nameof(password));

            await _lock.WaitAsync(ct);
            try
            {
                if (!_users.TryGetValue(userId, out RetroUser? user))
                    return false;

                user.PasswordHash = Argon2idPasswordHasher.Hash(password);
                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask<bool> VerifyPasswordAsync(Guid userId, string password, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(password)) return false;

            await _lock.WaitAsync(ct);
            try
            {
                if (!_users.TryGetValue(userId, out RetroUser? user))
                    return false;

                if (string.IsNullOrWhiteSpace(user.PasswordHash))
                    return false;

                bool valid = Argon2idPasswordHasher.Verify(password, user.PasswordHash, out bool upgradedFromLegacy);
                if (valid && upgradedFromLegacy)
                    user.PasswordHash = Argon2idPasswordHasher.Hash(password);

                return valid;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask<bool> VerifyPasswordAsync(string username, string password, CancellationToken ct = default)
        {
            RetroUser? user = await GetUserByUsernameAsync(username, ct);
            if (user == null) return false;
            return await VerifyPasswordAsync(user.UserId, password, ct);
        }

        // ====================================================================
        // GROUP MANAGEMENT
        // ====================================================================

        public async ValueTask<Guid> CreateGroupAsync(RetroGroup group, CancellationToken ct = default)
        {
            if (group == null) throw new ArgumentNullException(nameof(group));

            await _lock.WaitAsync(ct);
            try
            {
                // Generate GUID if not provided
                if (group.GroupId == Guid.Empty)
                {
                    group.GroupId = Guid.NewGuid();
                }

                _groups[group.GroupId] = group;
                return group.GroupId;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask<RetroGroup?> GetGroupAsync(Guid groupId, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                return _groups.TryGetValue(groupId, out RetroGroup? group) ? group : null;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask<RetroGroup?> GetGroupByNameAsync(string groupName, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(groupName)) return null;

            await _lock.WaitAsync(ct);
            try
            {
                foreach (RetroGroup group in _groups.Values)
                {
                    if (string.Equals(group.GroupName, groupName, StringComparison.OrdinalIgnoreCase))
                    {
                        return group;
                    }
                }
                return null;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask<bool> UpdateGroupAsync(RetroGroup group, CancellationToken ct = default)
        {
            if (group == null) throw new ArgumentNullException(nameof(group));

            await _lock.WaitAsync(ct);
            try
            {
                if (!_groups.ContainsKey(group.GroupId))
                    return false;

                _groups[group.GroupId] = group;
                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask<bool> DeleteGroupAsync(Guid groupId, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (!_groups.Remove(groupId))
                    return false;

                // Remove group from all users
                foreach (RetroUser user in _users.Values)
                {
                    user.Groups.Remove(groupId);
                }

                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask<RetroGroup[]> ListGroupsAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                RetroGroup[] groups = new RetroGroup[_groups.Count];
                int index = 0;
                foreach (RetroGroup group in _groups.Values)
                {
                    groups[index++] = group;
                }
                return groups;
            }
            finally
            {
                _lock.Release();
            }
        }

        // ====================================================================
        // GROUP MEMBERSHIP
        // ====================================================================

        public async ValueTask<bool> AddUserToGroupAsync(Guid userId, Guid groupId, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (!_users.TryGetValue(userId, out RetroUser? user))
                    return false;

                if (!_groups.TryGetValue(groupId, out RetroGroup? group))
                    return false;

                if (!user.Groups.Contains(groupId))
                {
                    user.Groups.Add(groupId);
                }

                if (!group.Members.Contains(userId))
                {
                    group.Members.Add(userId);
                }

                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask<bool> RemoveUserFromGroupAsync(Guid userId, Guid groupId, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (_users.TryGetValue(userId, out RetroUser? user))
                {
                    user.Groups.Remove(groupId);
                }

                if (_groups.TryGetValue(groupId, out RetroGroup? group))
                {
                    group.Members.Remove(userId);
                }

                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask<RetroGroup[]> GetUserGroupsAsync(Guid userId, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (!_users.TryGetValue(userId, out RetroUser? user))
                    return Array.Empty<RetroGroup>();

                List<RetroGroup> userGroups = new List<RetroGroup>();
                for (int i = 0; i < user.Groups.Count; i++)
                {
                    if (_groups.TryGetValue(user.Groups[i], out RetroGroup? group))
                    {
                        userGroups.Add(group);
                    }
                }

                return userGroups.ToArray();
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask<RetroUser[]> GetGroupMembersAsync(Guid groupId, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (!_groups.TryGetValue(groupId, out RetroGroup? group))
                    return Array.Empty<RetroUser>();

                List<RetroUser> members = new List<RetroUser>();
                for (int i = 0; i < group.Members.Count; i++)
                {
                    if (_users.TryGetValue(group.Members[i], out RetroUser? user))
                    {
                        members.Add(user);
                    }
                }

                return members.ToArray();
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask<bool> IsUserInGroupAsync(Guid userId, Guid groupId, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (!_users.TryGetValue(userId, out RetroUser? user))
                    return false;

                return user.Groups.Contains(groupId);
            }
            finally
            {
                _lock.Release();
            }
        }

        // ====================================================================
        // EXTERNAL IDENTITY MAPPING
        // ====================================================================

        public async ValueTask<bool> LinkExternalIdentityAsync(
            Guid userId, ExternalIdentity externalIdentity, CancellationToken ct = default)
        {
            if (externalIdentity == null) throw new ArgumentNullException(nameof(externalIdentity));

            await _lock.WaitAsync(ct);
            try
            {
                if (!_users.TryGetValue(userId, out RetroUser? user))
                    return false;

                externalIdentity.InternalUserId = userId;

                // Check if already linked
                bool alreadyLinked = false;
                for (int i = 0; i < user.ExternalIdentities.Count; i++)
                {
                    ExternalIdentity existing = user.ExternalIdentities[i];
                    if (existing.Provider == externalIdentity.Provider &&
                        existing.ExternalUserId == externalIdentity.ExternalUserId)
                    {
                        user.ExternalIdentities[i] = externalIdentity;
                        alreadyLinked = true;
                        break;
                    }
                }

                if (!alreadyLinked)
                {
                    user.ExternalIdentities.Add(externalIdentity);
                }

                // Update index
                string key = $"{externalIdentity.Provider}:{externalIdentity.ExternalUserId}";
                _externalIdentityIndex[key] = userId;

                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask<bool> UnlinkExternalIdentityAsync(
            Guid userId, string provider, string externalUserId, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (!_users.TryGetValue(userId, out RetroUser? user))
                    return false;

                for (int i = user.ExternalIdentities.Count - 1; i >= 0; i--)
                {
                    ExternalIdentity identity = user.ExternalIdentities[i];
                    if (identity.Provider == provider && identity.ExternalUserId == externalUserId)
                    {
                        user.ExternalIdentities.RemoveAt(i);

                        string key = $"{provider}:{externalUserId}";
                        _externalIdentityIndex.Remove(key);

                        return true;
                    }
                }

                return false;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask<ExternalIdentity[]> GetExternalIdentitiesAsync(
            Guid userId, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (!_users.TryGetValue(userId, out RetroUser? user))
                    return Array.Empty<ExternalIdentity>();

                return user.ExternalIdentities.ToArray();
            }
            finally
            {
                _lock.Release();
            }
        }

        // ====================================================================
        // AUTHENTICATION
        // ====================================================================

        public async ValueTask<RetroUser?> AuthenticateAsync(
            string username, string password, CancellationToken ct = default)
        {
            RetroUser? user = await GetUserByUsernameAsync(username, ct);
            if (user == null || !user.IsActive)
                return null;

            bool passwordValid = await VerifyPasswordAsync(user.UserId, password, ct);
            if (!passwordValid)
                return null;

            // Update last login
            user.LastLoginAt = DateTime.UtcNow;
            return user;
        }

        public async ValueTask<RetroUser?> AuthenticateViaExternalIdentityAsync(
            string provider, string externalUserId, string externalUsername,
            bool autoProvision = true, CancellationToken ct = default)
        {
            // Try to find existing user
            RetroUser? user = await GetUserByExternalIdentityAsync(provider, externalUserId, ct);

            if (user == null && autoProvision)
            {
                // Auto-provision new user
                user = new RetroUser
                {
                    UserId = Guid.NewGuid(),
                    Username = $"{provider.ToLower()}_{externalUsername}",
                    DisplayName = externalUsername,
                    IsActive = true,
                    IsAdmin = false,
                    CreatedAt = DateTime.UtcNow
                };

                await CreateUserAsync(user, ct);

                // Link external identity
                ExternalIdentity externalIdentity = new ExternalIdentity
                {
                    Provider = provider,
                    ExternalUserId = externalUserId,
                    ExternalUsername = externalUsername,
                    InternalUserId = user.UserId,
                    LastAuthAt = DateTime.UtcNow
                };

                await LinkExternalIdentityAsync(user.UserId, externalIdentity, ct);
            }

            if (user != null)
            {
                user.LastLoginAt = DateTime.UtcNow;

                // Update external identity last auth
                for (int i = 0; i < user.ExternalIdentities.Count; i++)
                {
                    ExternalIdentity identity = user.ExternalIdentities[i];
                    if (identity.Provider == provider && identity.ExternalUserId == externalUserId)
                    {
                        identity.LastAuthAt = DateTime.UtcNow;
                        break;
                    }
                }
            }

            return user;
        }

        // ====================================================================
        // PERSISTENCE
        // ====================================================================

        public async ValueTask SaveAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                JsonSerializerOptions options = new JsonSerializerOptions { WriteIndented = true };

                // Save users
                string usersJson = JsonSerializer.Serialize(_users.Values.ToArray(), options);
                await File.WriteAllTextAsync(_usersFilePath, usersJson, ct);

                // Save groups
                string groupsJson = JsonSerializer.Serialize(_groups.Values.ToArray(), options);
                await File.WriteAllTextAsync(_groupsFilePath, groupsJson, ct);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask LoadAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                _users.Clear();
                _groups.Clear();
                _usernameIndex.Clear();
                _externalIdentityIndex.Clear();

                // Load users
                if (File.Exists(_usersFilePath))
                {
                    string usersJson = await File.ReadAllTextAsync(_usersFilePath, ct);
                    RetroUser[]? users = JsonSerializer.Deserialize<RetroUser[]>(usersJson);

                    if (users != null)
                    {
                        for (int i = 0; i < users.Length; i++)
                        {
                            RetroUser user = users[i];
                            _users[user.UserId] = user;
                            _usernameIndex[user.Username.ToLowerInvariant()] = user.UserId;
                            IndexExternalIdentities(user);
                        }
                    }
                }

                // Load groups
                if (File.Exists(_groupsFilePath))
                {
                    string groupsJson = await File.ReadAllTextAsync(_groupsFilePath, ct);
                    RetroGroup[]? groups = JsonSerializer.Deserialize<RetroGroup[]>(groupsJson);

                    if (groups != null)
                    {
                        for (int i = 0; i < groups.Length; i++)
                        {
                            RetroGroup group = groups[i];
                            _groups[group.GroupId] = group;
                        }
                    }
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        // ====================================================================
        // HELPERS
        // ====================================================================

        private void IndexExternalIdentities(RetroUser user)
        {
            for (int i = 0; i < user.ExternalIdentities.Count; i++)
            {
                ExternalIdentity identity = user.ExternalIdentities[i];
                string key = $"{identity.Provider}:{identity.ExternalUserId}";
                _externalIdentityIndex[key] = user.UserId;
            }
        }

        private void RemoveExternalIdentityIndexes(RetroUser user)
        {
            for (int i = 0; i < user.ExternalIdentities.Count; i++)
            {
                ExternalIdentity identity = user.ExternalIdentities[i];
                string key = $"{identity.Provider}:{identity.ExternalUserId}";
                _externalIdentityIndex.Remove(key);
            }
        }

        public void Dispose()
        {
            _lock?.Dispose();
        }
    }
}
