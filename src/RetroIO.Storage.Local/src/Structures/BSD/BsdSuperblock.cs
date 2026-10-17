//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;
using RetroIO.Storage.Local.V7.Structures;

namespace RetroIO.Storage.Local.BSD.Structures
{
    /// <summary>
    /// UNIX V7 Filesystem Superblock (1024 bytes, at filesystem block 1).
    ///
    /// IMPORTANT NAMING NOTE:
    /// This is the V7 FILESYSTEM FORMAT (from UNIX Version 7, 1979), NOT FFS/UFS!
    /// V7 is NOT part of the BSD Fast File System (FFS) / Unix File System (UFS) family.
    ///
    /// V7 Filesystem Characteristics:
    /// - 512-byte blocks
    /// - 14-byte filenames (maximum)
    /// - Simple inode structure (no cylinder groups, no fragments)
    /// - Superblock at offset 1024 bytes (block 1, after 512-byte boot block)
    /// - Used by: UNIX Version 7 (1979), BSD 2.11, early UNIX systems
    ///
    /// For the MODERN BSD filesystem, see FfsSuperblock (Fast File System / UFS).
    ///
    /// Structure contains:
    /// - Filesystem size and inode count
    /// - Free block and free inode lists
    /// - Filesystem state and modification times
    /// </summary>
    public class BsdSuperblock
    {
        // =================================================================
        // BASIC FILESYSTEM PARAMETERS
        // =================================================================

        /// <summary>
        /// First block after inode list (size of inode table in blocks).
        /// Example: if ISize = 802, then blocks 2-801 are inodes, data starts at 802.
        /// </summary>
        public ushort ISize { get; set; }

        /// <summary>
        /// Size in blocks of entire volume (daddr_t = int32, 4 bytes).
        /// </summary>
        public int FSize { get; set; }

        // =================================================================
        // FREE BLOCK LIST (CACHE)
        // =================================================================

        /// <summary>
        /// Number of addresses in Free array (0-50).
        /// </summary>
        public short NFree { get; set; }

        /// <summary>
        /// Free block list (50 entries, cached from disk).
        /// Entry [NFree-1] points to next fblk structure on disk (if NFree == 50).
        /// </summary>
        public uint[] Free { get; set; } = new uint[V7Constants.NICFREE];

        // =================================================================
        // FREE INODE LIST (CACHE)
        // =================================================================

        /// <summary>
        /// Number of addresses in Inode array (0-100).
        /// </summary>
        public short NInode { get; set; }

        /// <summary>
        /// Free inode list (100 entries, cached).
        /// </summary>
        public ushort[] Inode { get; set; } = new ushort[V7Constants.NICINOD];

        // =================================================================
        // LOCK FLAGS
        // =================================================================

        /// <summary>
        /// Lock during free list manipulation.
        /// </summary>
        public byte FLock { get; set; }

        /// <summary>
        /// Lock during inode list manipulation.
        /// </summary>
        public byte ILock { get; set; }

        /// <summary>
        /// Super block modified flag.
        /// </summary>
        public byte FMod { get; set; }

        /// <summary>
        /// Mounted read-only flag.
        /// </summary>
        public byte ROnly { get; set; }

        // =================================================================
        // TIMESTAMPS AND STATISTICS
        // =================================================================

        /// <summary>
        /// Last superblock update (Unix timestamp, 32-bit).
        /// </summary>
        public uint Time { get; set; }

        /// <summary>
        /// Total free blocks in entire filesystem.
        /// </summary>
        public uint TFree { get; set; }

        /// <summary>
        /// Total free inodes in entire filesystem.
        /// </summary>
        public ushort TInode { get; set; }

        // =================================================================
        // OPTIMIZATION PARAMETERS
        // =================================================================

        /// <summary>
        /// Optimal step in free list pattern.
        /// </summary>
        public short Step { get; set; }

        /// <summary>
        /// Number of blocks per pattern.
        /// </summary>
        public short Cyl { get; set; }

        // =================================================================
        // FILESYSTEM IDENTIFICATION
        // =================================================================

        /// <summary>
        /// Filesystem name (6 characters).
        /// </summary>
        public string FsName { get; set; } = string.Empty;

        /// <summary>
        /// Filesystem pack name (6 characters).
        /// </summary>
        public string FsPack { get; set; } = string.Empty;

        // =================================================================
        // INODE ALLOCATION OPTIMIZATION
        // =================================================================

        /// <summary>
        /// Last allocated inode (for circular search).
        /// </summary>
        public ushort LastI { get; set; }

        /// <summary>
        /// Estimated number of free inodes before LastI.
        /// </summary>
        public ushort NBehind { get; set; }

        /// <summary>
        /// Mount flags.
        /// </summary>
        public ushort Flags { get; set; }

        // =================================================================
        // HELPER PROPERTIES
        // =================================================================

        /// <summary>
        /// Number of inode blocks (ISize - 2, since block 0 is boot, block 1 is superblock).
        /// </summary>
        public int InodeBlockCount => ISize - 2;

        /// <summary>
        /// Total number of inodes (16 inodes per block).
        /// </summary>
        public int TotalInodes => InodeBlockCount * V7Constants.INODES_PER_BLOCK;

        /// <summary>
        /// Number of data blocks.
        /// </summary>
        public int DataBlockCount => FSize - ISize;

        /// <summary>
        /// True if filesystem is mounted read-only.
        /// </summary>
        public bool IsReadOnly => ROnly != 0;

        /// <summary>
        /// True if superblock has been modified.
        /// </summary>
        public bool IsModified => FMod != 0;

        /// <summary>
        /// Last update time as DateTime.
        /// </summary>
        public DateTime LastUpdateTime => DateTimeOffset.FromUnixTimeSeconds(Time).DateTime;

        // =================================================================
        // VALIDATION
        // =================================================================

        /// <summary>
        /// Validates superblock structure.
        /// </summary>
        public bool Validate()
        {
            // Check basic sanity
            if (ISize == 0 || FSize == 0)
                return false;

            if (ISize >= FSize)
                return false;

            // Check free list bounds
            if (NFree < 0 || NFree > V7Constants.NICFREE)
                return false;

            // Check inode list bounds
            if (NInode < 0 || NInode > V7Constants.NICINOD)
                return false;

            // Check total free counts
            if (TFree > DataBlockCount)
                return false;

            if (TInode > TotalInodes)
                return false;

            return true;
        }

        // =================================================================
        // PARSING AND SERIALIZATION
        // =================================================================

        /// <summary>
        /// Parses superblock from byte array, auto-detecting endianness.
        /// Tries PDP-endian first (PDP-11 BSD 2.11), then big-endian (SunOS UFS).
        /// </summary>
        public static BsdSuperblock Parse(byte[] buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (buffer.Length < V7Constants.SUPERBLOCK_SIZE)
                throw new ArgumentException($"Buffer too small. Expected {V7Constants.SUPERBLOCK_SIZE} bytes.");

            // Try PDP-endian first (PDP-11 BSD 2.11)
            var sbPdp = ParsePDP(buffer);
            if (sbPdp.Validate())
            {
                return sbPdp;
            }

            // Try big-endian (SunOS UFS)
            var sbBe = ParseBigEndian(buffer);
            if (sbBe.Validate())
            {
                return sbBe;
            }

            // If neither validates, return PDP version (original behavior)
            return sbPdp;
        }

        /// <summary>
        /// Parses superblock from byte array using PDP-endian (PDP-11 BSD 2.11).
        /// </summary>
        public static BsdSuperblock ParsePDP(byte[] buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (buffer.Length < V7Constants.SUPERBLOCK_SIZE)
                throw new ArgumentException($"Buffer too small. Expected {V7Constants.SUPERBLOCK_SIZE} bytes.");

            var sb = new BsdSuperblock();

            // Basic parameters (0-5)
            sb.ISize = BitConverter.ToUInt16(buffer, 0x000);
            sb.FSize = V7EndianHelper.ReadInt32PDP(buffer, 0x002);

            // Free block list (6-207)
            sb.NFree = BitConverter.ToInt16(buffer, 0x006);
            for (int i = 0; i < V7Constants.NICFREE; i++)
            {
                sb.Free[i] = V7EndianHelper.ReadUInt32PDP(buffer, 0x008 + (i * 4));
            }

            // Free inode list (208-409)
            sb.NInode = BitConverter.ToInt16(buffer, 0x0D0);
            for (int i = 0; i < V7Constants.NICINOD; i++)
            {
                sb.Inode[i] = BitConverter.ToUInt16(buffer, 0x0D2 + (i * 2));
            }

            // Locks and flags (410-413)
            sb.FLock = buffer[0x19A];
            sb.ILock = buffer[0x19C];
            sb.FMod = buffer[0x19B];
            sb.ROnly = buffer[0x19D];

            // Timestamps and statistics (414-423)
            sb.Time = V7EndianHelper.ReadUInt32PDP(buffer, 0x19E);
            sb.TFree = V7EndianHelper.ReadUInt32PDP(buffer, 0x1A2);
            sb.TInode = BitConverter.ToUInt16(buffer, 0x1A6);

            // Optimization parameters (424-427)
            sb.Step = BitConverter.ToInt16(buffer, 0x1A8);
            sb.Cyl = BitConverter.ToInt16(buffer, 0x1AA);

            // Filesystem names (428-439) - Note: documentation shows fs_fsmnt at 0x1AC
            sb.FsName = ReadString(buffer, 0x1AC, 12);  // Mount point, use first 6 chars as name
            sb.FsPack = ReadString(buffer, 0x1AC + 6, 6);

            // Inode allocation optimization (440-445)
            sb.LastI = BitConverter.ToUInt16(buffer, 0x1B8);
            sb.NBehind = BitConverter.ToUInt16(buffer, 0x1BA);
            sb.Flags = BitConverter.ToUInt16(buffer, 0x1BC);

            return sb;
        }

        /// <summary>
        /// Parses superblock from byte array using big-endian (SunOS UFS).
        /// </summary>
        public static BsdSuperblock ParseBigEndian(byte[] buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (buffer.Length < V7Constants.SUPERBLOCK_SIZE)
                throw new ArgumentException($"Buffer too small. Expected {V7Constants.SUPERBLOCK_SIZE} bytes.");

            var sb = new BsdSuperblock();

            // Basic parameters (0-5) - 16-bit values are also big-endian
            sb.ISize = ReadUInt16BE(buffer, 0x000);
            sb.FSize = V7EndianHelper.ReadInt32BE(buffer, 0x002);

            // Free block list (6-207)
            sb.NFree = ReadInt16BE(buffer, 0x006);
            for (int i = 0; i < V7Constants.NICFREE; i++)
            {
                sb.Free[i] = V7EndianHelper.ReadUInt32BE(buffer, 0x008 + (i * 4));
            }

            // Free inode list (208-409)
            sb.NInode = ReadInt16BE(buffer, 0x0D0);
            for (int i = 0; i < V7Constants.NICINOD; i++)
            {
                sb.Inode[i] = ReadUInt16BE(buffer, 0x0D2 + (i * 2));
            }

            // Locks and flags (410-413) - single bytes, no endianness
            sb.FLock = buffer[0x19A];
            sb.ILock = buffer[0x19C];
            sb.FMod = buffer[0x19B];
            sb.ROnly = buffer[0x19D];

            // Timestamps and statistics (414-423)
            sb.Time = V7EndianHelper.ReadUInt32BE(buffer, 0x19E);
            sb.TFree = V7EndianHelper.ReadUInt32BE(buffer, 0x1A2);
            sb.TInode = ReadUInt16BE(buffer, 0x1A6);

            // Optimization parameters (424-427)
            sb.Step = ReadInt16BE(buffer, 0x1A8);
            sb.Cyl = ReadInt16BE(buffer, 0x1AA);

            // Filesystem names (428-439)
            sb.FsName = ReadString(buffer, 0x1AC, 12);
            sb.FsPack = ReadString(buffer, 0x1AC + 6, 6);

            // Inode allocation optimization (440-445)
            sb.LastI = ReadUInt16BE(buffer, 0x1B8);
            sb.NBehind = ReadUInt16BE(buffer, 0x1BA);
            sb.Flags = ReadUInt16BE(buffer, 0x1BC);

            return sb;
        }

        private static ushort ReadUInt16BE(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        private static short ReadInt16BE(byte[] buffer, int offset)
        {
            return (short)ReadUInt16BE(buffer, offset);
        }

        /// <summary>
        /// Writes superblock to byte array.
        /// </summary>
        public void WriteTo(byte[] buffer, int offset)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (offset < 0 || offset + V7Constants.SUPERBLOCK_SIZE > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            // Clear buffer first
            Array.Fill(buffer, (byte)0, offset, V7Constants.SUPERBLOCK_SIZE);

            // Basic parameters (0-5)
            BitConverter.TryWriteBytes(buffer.AsSpan(offset), ISize);
            V7EndianHelper.WriteInt32PDP(buffer, offset + 2, FSize);

            // Free block list (6-207)
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 6), NFree);
            for (int i = 0; i < V7Constants.NICFREE; i++)
            {
                V7EndianHelper.WriteUInt32PDP(buffer, offset + 8 + (i * 4), Free[i]);
            }

            // Free inode list (208-409)
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 0x0D0), NInode);
            for (int i = 0; i < V7Constants.NICINOD; i++)
            {
                BitConverter.TryWriteBytes(buffer.AsSpan(offset + 0x0D2 + (i * 2)), Inode[i]);
            }

            // Locks and flags (410-413)
            buffer[offset + 0x19A] = FLock;
            buffer[offset + 0x19C] = ILock;
            buffer[offset + 0x19B] = FMod;
            buffer[offset + 0x19D] = ROnly;

            // Timestamps and statistics (414-423)
            V7EndianHelper.WriteUInt32PDP(buffer, offset + 0x19E, Time);
            V7EndianHelper.WriteUInt32PDP(buffer, offset + 0x1A2, TFree);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 0x1A6), TInode);

            // Optimization parameters (424-427)
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 0x1A8), Step);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 0x1AA), Cyl);

            // Filesystem names (428-439)
            WriteString(buffer, offset + 0x1AC, FsName, 12);

            // Inode allocation optimization (440-445)
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 0x1B8), LastI);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 0x1BA), NBehind);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 0x1BC), Flags);
        }

        // =================================================================
        // HELPER METHODS
        // =================================================================

        private static string ReadString(byte[] buffer, int offset, int maxLength)
        {
            int length = 0;
            while (length < maxLength && buffer[offset + length] != 0)
            {
                length++;
            }

            if (length == 0)
                return string.Empty;

            return Encoding.ASCII.GetString(buffer, offset, length);
        }

        private static void WriteString(byte[] buffer, int offset, string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
                return;

            int length = Math.Min(value.Length, maxLength - 1);
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            Array.Copy(bytes, 0, buffer, offset, Math.Min(length, bytes.Length));
            buffer[offset + length] = 0;
        }

        /// <summary>
        /// Returns a text with the file system name, pack name, size, inode count, free counts and the result of the validity check.
        /// </summary>
        /// <returns>
        /// The text.
        /// </returns>
        public override string ToString()
        {
            return $"BSD Superblock: {FsName} ({FsPack}), " +
                   $"Size={FSize} blocks, Inodes={TotalInodes}, " +
                   $"Free={TFree} blocks/{TInode} inodes, " +
                   $"Valid={Validate()}";
        }
    }
}

