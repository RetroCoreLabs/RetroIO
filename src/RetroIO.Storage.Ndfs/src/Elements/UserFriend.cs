//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Storage.Ndfs.Elements
{
    /// <summary>
    /// Represents a friend entry in NDFS user's friend list.
    /// Each user can have up to 8 friends with specific access permissions.
    /// </summary>
    public class UserFriend
    {
        /// <summary>
        /// Packed permission bits.
        /// Bit format:
        /// 15    - Entry used (if 1, this friend slot is active)
        /// 14-13 - Reserved
        /// 12    - Directory Access
        /// 11    - Common Access
        /// 10    - Append Access
        /// 9     - Write access
        /// 8     - Read access
        /// 7-0   - Friend Index (user index of the friend)
        /// </summary>
        public ushort Bits { get; set; }

        /// <summary>
        /// Gets whether this friend slot is in use.
        /// </summary>
        public bool EntryUsed
        {
            get { return (Bits & (1 << 15)) != 0; }
        }

        /// <summary>
        /// Gets whether friend has directory access permission.
        /// </summary>
        public bool DirectoryAccess
        {
            get { return (Bits & (1 << 12)) != 0; }
        }

        /// <summary>
        /// Gets whether friend has common access permission.
        /// </summary>
        public bool CommonAccess
        {
            get { return (Bits & (1 << 11)) != 0; }
        }

        /// <summary>
        /// Gets whether friend has append access permission.
        /// </summary>
        public bool AppendAccess
        {
            get { return (Bits & (1 << 10)) != 0; }
        }

        /// <summary>
        /// Gets whether friend has write access permission.
        /// </summary>
        public bool WriteAccess
        {
            get { return (Bits & (1 << 9)) != 0; }
        }

        /// <summary>
        /// Gets whether friend has read access permission.
        /// </summary>
        public bool ReadAccess
        {
            get { return (Bits & (1 << 8)) != 0; }
        }

        /// <summary>
        /// Gets the user index of the friend.
        /// </summary>
        public ushort FriendUserIndex
        {
            get { return (ushort)(Bits & 0xFF); }
        }

        /// <summary>
        /// Creates an empty (unused) friend entry.
        /// </summary>
        public UserFriend()
        {
            Bits = 0;
        }

        /// <summary>
        /// Creates a friend entry from packed bits.
        /// </summary>
        /// <param name="bits">Packed permission bits</param>
        public UserFriend(ushort bits)
        {
            Bits = bits;
        }

        /// <summary>
        /// Creates a friend entry with specified permissions.
        /// </summary>
        /// <param name="friendUserId">Friend's user index (0-255)</param>
        /// <param name="read">Read permission</param>
        /// <param name="write">Write permission</param>
        /// <param name="append">Append permission</param>
        /// <param name="common">Common access permission</param>
        /// <param name="directory">Directory access permission</param>
        public UserFriend(byte friendUserId, bool read = false, bool write = false,
            bool append = false, bool common = false, bool directory = false)
        {
            Bits = (ushort)friendUserId;
            Bits |= (ushort)(1 << 15); // Mark as used

            if (read) Bits |= (ushort)(1 << 8);
            if (write) Bits |= (ushort)(1 << 9);
            if (append) Bits |= (ushort)(1 << 10);
            if (common) Bits |= (ushort)(1 << 11);
            if (directory) Bits |= (ushort)(1 << 12);
        }

        /// <summary>
        /// Initializes from packed bits.
        /// </summary>
        /// <param name="bits">Packed permission bits</param>
        public void Initialize(ushort bits)
        {
            Bits = bits;
        }

        /// <summary>
        /// Sets friend with specified permissions.
        /// </summary>
        /// <param name="friendUserId">Friend's user index</param>
        /// <param name="permissions">Permission bits (bits 8-12)</param>
        public void SetFriend(byte friendUserId, byte permissions)
        {
            Bits = (ushort)friendUserId;
            Bits |= (ushort)(1 << 15); // Mark as used
            Bits |= (ushort)((permissions & 0x1F) << 8); // Apply permission bits
        }

        /// <summary>
        /// Clears this friend slot (marks as unused).
        /// </summary>
        public void Clear()
        {
            Bits = 0;
        }

        /// <summary>
        /// Checks if friend has specific permission.
        /// </summary>
        /// <param name="operation">Operation to check</param>
        /// <returns>True if friend has permission</returns>
        public bool HasPermission(FileOperation operation)
        {
            if (!EntryUsed) return false;

            switch (operation)
            {
                case FileOperation.Read:
                    return ReadAccess;
                case FileOperation.Write:
                    return WriteAccess;
                case FileOperation.Append:
                    return AppendAccess;
                case FileOperation.Delete:
                    return WriteAccess; // Delete requires write
                case FileOperation.List:
                    return DirectoryAccess;
                case FileOperation.Execute:
                    return CommonAccess;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Converts to byte array (2 bytes, big-endian).
        /// </summary>
        public byte[] ToBytes()
        {
            return new byte[]
            {
                (byte)(Bits >> 8),
                (byte)(Bits & 0xFF)
            };
        }

        /// <summary>
        /// Parses from byte array (2 bytes, big-endian).
        /// </summary>
        public static UserFriend FromBytes(byte[] data, int offset)
        {
            if (data == null || data.Length < offset + 2)
                throw new System.ArgumentException("Insufficient data for UserFriend");

            ushort bits = (ushort)((data[offset] << 8) | data[offset + 1]);
            return new UserFriend(bits);
        }

        /// <summary>
        /// Gets a string representation of permissions.
        /// </summary>
        public string GetPermissionString()
        {
            if (!EntryUsed) return "-----";

            return $"{(ReadAccess ? 'R' : '-')}" +
                   $"{(WriteAccess ? 'W' : '-')}" +
                   $"{(AppendAccess ? 'A' : '-')}" +
                   $"{(CommonAccess ? 'C' : '-')}" +
                   $"{(DirectoryAccess ? 'D' : '-')}";
        }

        public override string ToString()
        {
            if (!EntryUsed)
                return "[Empty]";

            return $"Friend[{FriendUserIndex}] {GetPermissionString()}";
        }
    }

    /// <summary>
    /// File operations for permission checking.
    /// </summary>
    public enum FileOperation
    {
        Read,
        Write,
        Append,
        Delete,
        List,
        Execute
    }
}
