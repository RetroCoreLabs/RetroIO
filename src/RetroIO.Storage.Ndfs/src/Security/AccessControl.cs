//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Storage.Ndfs.Elements;

namespace RetroIO.Storage.Ndfs.Security
{
    /// <summary>
    /// Provides access control enforcement for NDFS files.
    /// Implements 3-tier permission system (OWN/FRIEND/PUBLIC) with friend resolution.
    /// </summary>
    public class AccessControl
    {
        /// <summary>
        /// Determines the access level a user has for a file.
        /// </summary>
        /// <param name="file">Object entry (file) to check</param>
        /// <param name="user">User requesting access</param>
        /// <param name="owner">Owner of the file</param>
        /// <returns>Access level (Own, Friend, or Public)</returns>
        public static FileAccessType GetAccessLevel(ObjectEntry file, UserEntry user, UserEntry owner)
        {
            if (file == null || user == null || owner == null)
                return FileAccessType.Public;

            // If user owns the file
            if (user.UserIndex == owner.UserIndex)
                return FileAccessType.Own;

            // Check if user is in owner's friend list
            if (owner.IsFriend(user.UserIndex))
                return FileAccessType.Friend;

            // Otherwise, public access
            return FileAccessType.Public;
        }

        /// <summary>
        /// Checks if user can perform specific operation on file.
        /// </summary>
        /// <param name="file">Object entry to check</param>
        /// <param name="user">User requesting access</param>
        /// <param name="owner">Owner of the file</param>
        /// <param name="operation">Operation to perform</param>
        /// <returns>True if user has permission</returns>
        public static bool CanPerformOperation(ObjectEntry file, UserEntry user, UserEntry owner, FileOperationType operation)
        {
            if (file == null || user == null || owner == null)
                return false;

            var accessLevel = GetAccessLevel(file, user, owner);
            var permissions = new AccessPermissions((ushort)file.AccessBits);

            return operation switch
            {
                FileOperationType.Read => permissions.CanRead(accessLevel),
                FileOperationType.Write => permissions.CanWrite(accessLevel),
                FileOperationType.Append => permissions.CanAppend(accessLevel),
                FileOperationType.Delete => permissions.CanDelete(accessLevel),
                FileOperationType.Execute => permissions.CanExecute(accessLevel),
                FileOperationType.List => permissions.CanList(accessLevel),
                _ => false
            };
        }

        /// <summary>
        /// Checks if user is in owner's friend list and gets their permissions.
        /// </summary>
        /// <param name="user">User to check</param>
        /// <param name="owner">Owner whose friend list to check</param>
        /// <returns>UserFriend entry if found, null otherwise</returns>
        public static UserFriend? GetFriendPermissions(UserEntry user, UserEntry owner)
        {
            if (user == null || owner == null)
                return null;

            return owner.GetFriend(user.UserIndex);
        }

        /// <summary>
        /// Validates friend-specific permissions for an operation.
        /// Used when friend has custom permissions that differ from file's Friend tier.
        /// </summary>
        /// <param name="friend">Friend entry with custom permissions</param>
        /// <param name="operation">Operation to check</param>
        /// <returns>True if friend has specific permission</returns>
        public static bool HasFriendPermission(UserFriend friend, FileOperationType operation)
        {
            if (friend == null || !friend.EntryUsed)
                return false;

            return operation switch
            {
                FileOperationType.Read => friend.ReadAccess,
                FileOperationType.Write => friend.WriteAccess,
                FileOperationType.Append => friend.AppendAccess,
                FileOperationType.Delete => friend.WriteAccess, // Delete requires write
                FileOperationType.Execute => friend.CommonAccess,
                FileOperationType.List => friend.DirectoryAccess,
                _ => false
            };
        }

        /// <summary>
        /// Comprehensive access check combining file permissions and friend-specific permissions.
        /// </summary>
        /// <param name="file">File to access</param>
        /// <param name="user">User requesting access</param>
        /// <param name="owner">File owner</param>
        /// <param name="operation">Operation to perform</param>
        /// <returns>True if access is granted</returns>
        public static bool CheckAccess(ObjectEntry file, UserEntry user, UserEntry owner, FileOperationType operation)
        {
            if (file == null || user == null || owner == null)
                return false;

            // Owner always has full access
            if (user.UserIndex == owner.UserIndex)
                return true;

            // Check if user is a friend with custom permissions
            var friendEntry = GetFriendPermissions(user, owner);
            if (friendEntry != null && friendEntry.EntryUsed)
            {
                // Friend-specific permissions override file's Friend tier
                return HasFriendPermission(friendEntry, operation);
            }

            // Fall back to file's standard permission tiers
            return CanPerformOperation(file, user, owner, operation);
        }

        /// <summary>
        /// Creates default file permissions.
        /// Default: 0x4FF (owner full access, friends read/write, public read)
        /// </summary>
        public static AccessPermissions CreateDefaultPermissions()
        {
            return AccessPermissions.Default();
        }

        /// <summary>
        /// Creates permissions allowing full access to owner only.
        /// </summary>
        public static AccessPermissions CreateOwnerOnlyPermissions()
        {
            // Owner: Full (0x3F), Friend: None (0x00), Public: None (0x00)
            return new AccessPermissions(0x1F, 0x00, 0x00);
        }

        /// <summary>
        /// Creates permissions allowing read-only access to everyone.
        /// </summary>
        public static AccessPermissions CreateReadOnlyPermissions()
        {
            // Owner: Full (0x3F), Friend: Read (0x02), Public: Read (0x02)
            return new AccessPermissions(0x1F, 0x02, 0x02);
        }

        /// <summary>
        /// Gets human-readable access summary for a user on a file.
        /// </summary>
        public static string GetAccessSummary(ObjectEntry file, UserEntry user, UserEntry owner)
        {
            if (file == null || user == null || owner == null)
                return "No access";

            var accessLevel = GetAccessLevel(file, user, owner);
            var permissions = new AccessPermissions((ushort)file.AccessBits);

            var levelName = accessLevel switch
            {
                FileAccessType.Own => "Owner",
                FileAccessType.Friend => "Friend",
                FileAccessType.Public => "Public",
                _ => "Unknown"
            };

            return $"{levelName}: {permissions.GetPermissionString(accessLevel)}";
        }
    }
}
