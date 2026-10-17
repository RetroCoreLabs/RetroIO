//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Storage.Local.V7.Structures
{
    /// <summary>
    /// UNIX V7 on-disk inode structure (64 bytes).
    /// Represents file metadata and block addresses.
    /// 16 inodes per 1024-byte block.
    /// </summary>
    public class V7Inode
    {
        // =================================================================
        // COMMON FIELDS (14 bytes)
        // =================================================================

        /// <summary>
        /// File type and permissions (16 bits).
        /// Upper 4 bits = file type, lower 12 bits = permissions.
        /// </summary>
        public ushort Mode { get; set; }

        /// <summary>
        /// Number of hard links to this inode.
        /// </summary>
        public ushort NLink { get; set; }

        /// <summary>
        /// Owner user ID.
        /// </summary>
        public ushort Uid { get; set; }

        /// <summary>
        /// Owner group ID.
        /// </summary>
        public ushort Gid { get; set; }

        /// <summary>
        /// File size in bytes (32-bit, up to 2GB).
        /// </summary>
        public uint Size { get; set; }

        // =================================================================
        // BLOCK ADDRESSES (28 bytes = 7 × 4)
        // =================================================================

        /// <summary>
        /// Block addresses (7 entries × 4 bytes = 28 bytes).
        /// [0-3]: Direct blocks (4 KB direct access)
        /// [4]: Single indirect block (256 KB)
        /// [5]: Double indirect block (64 MB)
        /// [6]: Triple indirect block (16 GB)
        /// </summary>
        public uint[] Addr { get; set; } = new uint[V7Constants.TOTAL_ADDR];

        // =================================================================
        // RESERVED AND FLAGS (12 bytes)
        // =================================================================

        /// <summary>
        /// Reserved for future use (10 bytes = 5 × 2).
        /// </summary>
        public ushort[] Reserved { get; set; } = new ushort[5];

        /// <summary>
        /// User-changeable flags.
        /// </summary>
        public ushort Flags { get; set; }

        // =================================================================
        // TIMESTAMPS (12 bytes)
        // =================================================================

        /// <summary>
        /// Last access time (Unix timestamp).
        /// </summary>
        public uint ATime { get; set; }

        /// <summary>
        /// Last modification time (Unix timestamp).
        /// </summary>
        public uint MTime { get; set; }

        /// <summary>
        /// Creation/inode change time (Unix timestamp).
        /// </summary>
        public uint CTime { get; set; }

        // =================================================================
        // HELPER PROPERTIES - FILE TYPE
        // =================================================================

        /// <summary>
        /// File type extracted from Mode field.
        /// </summary>
        public ushort FileType => (ushort)(Mode & V7Constants.IFMT);

        /// <summary>
        /// Permission bits extracted from Mode field.
        /// </summary>
        public ushort Permissions => (ushort)(Mode & V7Constants.IPERM);

        /// <summary>
        /// True if this is a regular file.
        /// </summary>
        public bool IsRegularFile => FileType == V7Constants.IFREG;

        /// <summary>
        /// True if this is a directory.
        /// </summary>
        public bool IsDirectory => FileType == V7Constants.IFDIR;

        /// <summary>
        /// True if this is a character device.
        /// </summary>
        public bool IsCharDevice => FileType == V7Constants.IFCHR;

        /// <summary>
        /// True if this is a block device.
        /// </summary>
        public bool IsBlockDevice => FileType == V7Constants.IFBLK;

        /// <summary>
        /// True if this is a symbolic link.
        /// </summary>
        public bool IsSymLink => FileType == V7Constants.IFLNK;

        /// <summary>
        /// True if this is a socket.
        /// </summary>
        public bool IsSocket => FileType == V7Constants.IFSOCK;

        /// <summary>
        /// True if inode is allocated (mode != 0).
        /// </summary>
        public bool IsAllocated => Mode != 0;

        // =================================================================
        // HELPER PROPERTIES - TIMESTAMPS
        // =================================================================

        /// <summary>
        /// Last access time as DateTime.
        /// </summary>
        public DateTime AccessTime => DateTimeOffset.FromUnixTimeSeconds(ATime).DateTime;

        /// <summary>
        /// Last modification time as DateTime.
        /// </summary>
        public DateTime ModificationTime => DateTimeOffset.FromUnixTimeSeconds(MTime).DateTime;

        /// <summary>
        /// Creation/change time as DateTime.
        /// </summary>
        public DateTime ChangeTime => DateTimeOffset.FromUnixTimeSeconds(CTime).DateTime;

        // =================================================================
        // HELPER PROPERTIES - PERMISSIONS
        // =================================================================

        /// <summary>
        /// True if Set UID bit is set.
        /// </summary>
        public bool HasSetUid => (Mode & V7Constants.ISUID) != 0;

        /// <summary>
        /// True if Set GID bit is set.
        /// </summary>
        public bool HasSetGid => (Mode & V7Constants.ISGID) != 0;

        /// <summary>
        /// True if Sticky bit is set.
        /// </summary>
        public bool HasStickyBit => (Mode & V7Constants.ISVTX) != 0;

        /// <summary>
        /// Gets permissions as octal string (e.g., "0755").
        /// </summary>
        public string PermissionsOctal => Convert.ToString(Permissions, 8).PadLeft(4, '0');

        /// <summary>
        /// Gets permission string (e.g., "rwxr-xr-x").
        /// </summary>
        public string PermissionsString
        {
            get
            {
                char[] perms = new char[9];
                perms[0] = (Permissions & 0x100) != 0 ? 'r' : '-';  // Owner read
                perms[1] = (Permissions & 0x080) != 0 ? 'w' : '-';  // Owner write
                perms[2] = (Permissions & 0x040) != 0 ? 'x' : '-';  // Owner execute
                perms[3] = (Permissions & 0x020) != 0 ? 'r' : '-';  // Group read
                perms[4] = (Permissions & 0x010) != 0 ? 'w' : '-';  // Group write
                perms[5] = (Permissions & 0x008) != 0 ? 'x' : '-';  // Group execute
                perms[6] = (Permissions & 0x004) != 0 ? 'r' : '-';  // Other read
                perms[7] = (Permissions & 0x002) != 0 ? 'w' : '-';  // Other write
                perms[8] = (Permissions & 0x001) != 0 ? 'x' : '-';  // Other execute

                // Handle SetUID, SetGID, Sticky
                if (HasSetUid) perms[2] = (Permissions & 0x040) != 0 ? 's' : 'S';
                if (HasSetGid) perms[5] = (Permissions & 0x008) != 0 ? 's' : 'S';
                if (HasStickyBit) perms[8] = (Permissions & 0x001) != 0 ? 't' : 'T';

                return new string(perms);
            }
        }

        // =================================================================
        // INODE LOCATION CALCULATIONS
        // =================================================================

        /// <summary>
        /// Calculates which filesystem block contains the given inode number.
        /// Formula: itod(ino) = ((ino) + 2*INOPB - 1) / INOPB
        /// </summary>
        public static uint InodeToBlock(ushort inodeNumber)
        {
            return (uint)((inodeNumber + 2 * V7Constants.INODES_PER_BLOCK - 1) / V7Constants.INODES_PER_BLOCK);
        }

        /// <summary>
        /// Calculates offset within block for the given inode number.
        /// Formula: itoo(ino) = ((ino) + 2*INOPB - 1) % INOPB
        /// </summary>
        public static int InodeToOffset(ushort inodeNumber)
        {
            return ((inodeNumber + 2 * V7Constants.INODES_PER_BLOCK - 1) % V7Constants.INODES_PER_BLOCK) * V7Constants.INODE_SIZE;
        }

        // =================================================================
        // PARSING AND SERIALIZATION
        // =================================================================

        /// <summary>
        /// Parses inode from byte array.
        /// </summary>
        public static V7Inode Parse(byte[] buffer, int offset)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (offset < 0 || offset + V7Constants.INODE_SIZE > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            var inode = new V7Inode();

            // Common fields (0-13)
            inode.Mode = BitConverter.ToUInt16(buffer, offset);
            inode.NLink = BitConverter.ToUInt16(buffer, offset + 2);
            inode.Uid = BitConverter.ToUInt16(buffer, offset + 4);
            inode.Gid = BitConverter.ToUInt16(buffer, offset + 6);
            inode.Size = V7EndianHelper.ReadUInt32PDP(buffer, offset + 8);

            // Block addresses (12-39) - PDP-endian 32-bit values
            for (int i = 0; i < V7Constants.TOTAL_ADDR; i++)
            {
                inode.Addr[i] = V7EndianHelper.ReadUInt32PDP(buffer, offset + 12 + (i * 4));
            }

            // Reserved (40-49)
            for (int i = 0; i < 5; i++)
            {
                inode.Reserved[i] = BitConverter.ToUInt16(buffer, offset + 40 + (i * 2));
            }

            // Flags (50-51)
            inode.Flags = BitConverter.ToUInt16(buffer, offset + 50);

            // Timestamps (52-63) - PDP-endian 32-bit values
            inode.ATime = V7EndianHelper.ReadUInt32PDP(buffer, offset + 52);
            inode.MTime = V7EndianHelper.ReadUInt32PDP(buffer, offset + 56);
            inode.CTime = V7EndianHelper.ReadUInt32PDP(buffer, offset + 60);

            return inode;
        }

        /// <summary>
        /// Writes inode to byte array.
        /// </summary>
        public void WriteTo(byte[] buffer, int offset)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (offset < 0 || offset + V7Constants.INODE_SIZE > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            // Common fields (0-13)
            BitConverter.TryWriteBytes(buffer.AsSpan(offset), Mode);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 2), NLink);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 4), Uid);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 6), Gid);
            V7EndianHelper.WriteUInt32PDP(buffer, offset + 8, Size);

            // Block addresses (12-39) - PDP-endian 32-bit values
            for (int i = 0; i < V7Constants.TOTAL_ADDR; i++)
            {
                V7EndianHelper.WriteUInt32PDP(buffer, offset + 12 + (i * 4), Addr[i]);
            }

            // Reserved (40-49)
            for (int i = 0; i < 5; i++)
            {
                BitConverter.TryWriteBytes(buffer.AsSpan(offset + 40 + (i * 2)), Reserved[i]);
            }

            // Flags (50-51)
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 50), Flags);

            // Timestamps (52-63) - PDP-endian 32-bit values
            V7EndianHelper.WriteUInt32PDP(buffer, offset + 52, ATime);
            V7EndianHelper.WriteUInt32PDP(buffer, offset + 56, MTime);
            V7EndianHelper.WriteUInt32PDP(buffer, offset + 60, CTime);
        }

        // =================================================================
        // FILE TYPE NAME
        // =================================================================

        /// <summary>
        /// Gets file type as string.
        /// </summary>
        public string FileTypeName
        {
            get
            {
                return FileType switch
                {
                    V7Constants.IFREG => "regular file",
                    V7Constants.IFDIR => "directory",
                    V7Constants.IFCHR => "character device",
                    V7Constants.IFBLK => "block device",
                    V7Constants.IFLNK => "symbolic link",
                    V7Constants.IFSOCK => "socket",
                    _ => $"unknown(0x{FileType:X4})"
                };
            }
        }

        /// <summary>
        /// Returns a text with the file type, size, link count, permissions, owner and group, or a free marker when the inode is not allocated.
        /// </summary>
        /// <returns>
        /// The text.
        /// </returns>
        public override string ToString()
        {
            if (!IsAllocated)
                return "Inode (free)";

            return $"Inode: {FileTypeName}, size={Size}, links={NLink}, " +
                   $"perm={PermissionsString}, uid={Uid}, gid={Gid}";
        }
    }
}
