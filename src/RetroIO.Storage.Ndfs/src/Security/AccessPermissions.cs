//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Storage.Ndfs.Security
{
    /// <summary>
    /// Represents parsed access permissions from NDFS AccessBits.
    /// AccessBits structure (15 bits total):
    /// - Bits 14-9: Public access permissions (6 bits)
    /// - Bits 8-3: Friend access permissions (6 bits)
    /// - Bits 2-0: Own access permissions (3 bits) [Note: Only 3 bits in practice]
    ///
    /// Permission bits (per level):
    /// - Bit 5: Delete
    /// - Bit 4: Execute/Common
    /// - Bit 3: Append
    /// - Bit 2: Write
    /// - Bit 1: Read
    /// - Bit 0: List/Directory
    /// </summary>
    public class AccessPermissions
    {
        /// <summary>
        /// Raw 15-bit access bits value.
        /// </summary>
        public ushort AccessBits { get; set; }

        /// <summary>
        /// Owner permissions (bits 2-0, but typically 5 bits in extended format).
        /// </summary>
        public byte OwnPermissions { get; private set; }

        /// <summary>
        /// Friend permissions (bits 8-3).
        /// </summary>
        public byte FriendPermissions { get; private set; }

        /// <summary>
        /// Public permissions (bits 14-9).
        /// </summary>
        public byte PublicPermissions { get; private set; }

        /// <summary>
        /// Creates empty permissions (no access).
        /// </summary>
        public AccessPermissions()
        {
            AccessBits = 0;
            ParseBits();
        }

        /// <summary>
        /// Creates permissions from raw AccessBits value.
        /// </summary>
        /// <param name="accessBits">
        /// 15-bit access bits
        /// </param>
        public AccessPermissions(ushort accessBits)
        {
            AccessBits = accessBits;
            ParseBits();
        }

        /// <summary>
        /// Creates permissions with explicit values for each tier.
        /// </summary>
        /// <param name="ownPerms">
        /// Owner permissions (5 bits)
        /// </param>
        /// <param name="friendPerms">
        /// Friend permissions (6 bits)
        /// </param>
        /// <param name="publicPerms">
        /// Public permissions (6 bits)
        /// </param>
        public AccessPermissions(byte ownPerms, byte friendPerms, byte publicPerms)
        {
            OwnPermissions = (byte)(ownPerms & 0x1F);
            FriendPermissions = (byte)(friendPerms & 0x3F);
            PublicPermissions = (byte)(publicPerms & 0x3F);
            BuildBits();
        }

        /// <summary>
        /// Parses AccessBits into separate permission levels.
        /// Format: PPPPPPFFFFFFOOOOO (P=Public, F=Friend, O=Own)
        /// </summary>
        private void ParseBits()
        {
            // Bits 14-9: Public (6 bits)
            PublicPermissions = (byte)((AccessBits >> 9) & 0x3F);

            // Bits 8-3: Friend (6 bits)
            FriendPermissions = (byte)((AccessBits >> 3) & 0x3F);

            // Bits 2-0: Own (5 bits, but only using 3 in standard format)
            OwnPermissions = (byte)(AccessBits & 0x1F);
        }

        /// <summary>
        /// Builds AccessBits from separate permission levels.
        /// </summary>
        private void BuildBits()
        {
            AccessBits = 0;
            AccessBits |= (ushort)((PublicPermissions & 0x3F) << 9);
            AccessBits |= (ushort)((FriendPermissions & 0x3F) << 3);
            AccessBits |= (ushort)(OwnPermissions & 0x1F);
        }

        /// <summary>
        /// Checks if specified permission bit is set for given access level.
        /// </summary>
        /// <param name="accessType">
        /// Access level (Own/Friend/Public)
        /// </param>
        /// <param name="permissionBit">
        /// Permission bit to check (0-5)
        /// </param>
        /// <returns>
        /// True if permission is granted
        /// </returns>
        public bool HasPermission(FileAccessType accessType, int permissionBit)
        {
            byte perms = accessType switch
            {
                FileAccessType.Own => OwnPermissions,
                FileAccessType.Friend => FriendPermissions,
                FileAccessType.Public => PublicPermissions,
                _ => 0
            };

            return (perms & (1 << permissionBit)) != 0;
        }

        /// <summary>
        /// Checks Read permission.
        /// </summary>
        public bool CanRead(FileAccessType accessType) => HasPermission(accessType, 1);

        /// <summary>
        /// Checks Write permission.
        /// </summary>
        public bool CanWrite(FileAccessType accessType) => HasPermission(accessType, 2);

        /// <summary>
        /// Checks Append permission.
        /// </summary>
        public bool CanAppend(FileAccessType accessType) => HasPermission(accessType, 3);

        /// <summary>
        /// Checks Execute/Common permission.
        /// </summary>
        public bool CanExecute(FileAccessType accessType) => HasPermission(accessType, 4);

        /// <summary>
        /// Checks Delete permission.
        /// </summary>
        public bool CanDelete(FileAccessType accessType) => HasPermission(accessType, 5);

        /// <summary>
        /// Checks List/Directory permission.
        /// </summary>
        public bool CanList(FileAccessType accessType) => HasPermission(accessType, 0);

        /// <summary>
        /// Sets permission bit for specific access level.
        /// </summary>
        public void SetPermission(FileAccessType accessType, int permissionBit, bool value)
        {
            byte mask = (byte)(1 << permissionBit);

            switch (accessType)
            {
                case FileAccessType.Own:
                    if (value)
                        OwnPermissions |= mask;
                    else
                        OwnPermissions &= (byte)~mask;
                    break;

                case FileAccessType.Friend:
                    if (value)
                        FriendPermissions |= mask;
                    else
                        FriendPermissions &= (byte)~mask;
                    break;

                case FileAccessType.Public:
                    if (value)
                        PublicPermissions |= mask;
                    else
                        PublicPermissions &= (byte)~mask;
                    break;
            }

            BuildBits();
        }

        /// <summary>
        /// Gets permission string for specific access type.
        /// Format: "DLXAWR" (Delete, List, eXecute, Append, Write, Read)
        /// </summary>
        public string GetPermissionString(FileAccessType accessType)
        {
            return $"{(CanDelete(accessType) ? 'D' : '-')}" +
                   $"{(CanList(accessType) ? 'L' : '-')}" +
                   $"{(CanExecute(accessType) ? 'X' : '-')}" +
                   $"{(CanAppend(accessType) ? 'A' : '-')}" +
                   $"{(CanWrite(accessType) ? 'W' : '-')}" +
                   $"{(CanRead(accessType) ? 'R' : '-')}";
        }

        /// <summary>
        /// Gets full permission string showing all three tiers.
        /// Format: "Own:DLXAWR Friend:DLXAWR Public:DLXAWR"
        /// </summary>
        public override string ToString()
        {
            return $"Own:{GetPermissionString(FileAccessType.Own)} " +
                   $"Friend:{GetPermissionString(FileAccessType.Friend)} " +
                   $"Public:{GetPermissionString(FileAccessType.Public)}";
        }

        /// <summary>
        /// Creates default permissions (0x4FF - owner full, friends read/write, public read).
        /// </summary>
        public static AccessPermissions Default()
        {
            return new AccessPermissions(0x4FF);
        }
    }
}
