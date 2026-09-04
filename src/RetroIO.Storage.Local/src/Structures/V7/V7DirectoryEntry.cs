//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Text;

namespace RetroIO.Storage.Local.V7.Structures
{
    /// <summary>
    /// UNIX V7 directory entry (variable length, 4-byte aligned).
    /// Minimum 12 bytes, maximum 72 bytes.
    /// </summary>
    public class V7DirectoryEntry
    {
        // =================================================================
        // FIELDS
        // =================================================================

        /// <summary>
        /// Inode number (0 = deleted entry).
        /// </summary>
        public ushort Inode { get; set; }

        /// <summary>
        /// Total record length (includes this entry + padding).
        /// Must be multiple of 4 bytes.
        /// </summary>
        public ushort RecLen { get; set; }

        /// <summary>
        /// Actual name length (without null terminator).
        /// </summary>
        public ushort NameLen { get; set; }

        /// <summary>
        /// Filename (up to 63 characters + null terminator).
        /// </summary>
        public string Name { get; set; } = string.Empty;

        // =================================================================
        // HELPER PROPERTIES
        // =================================================================

        /// <summary>
        /// True if this is a deleted entry (inode == 0).
        /// </summary>
        public bool IsDeleted => Inode == 0;

        /// <summary>
        /// True if this is the "." entry.
        /// </summary>
        public bool IsDot => Name == ".";

        /// <summary>
        /// True if this is the ".." entry.
        /// </summary>
        public bool IsDotDot => Name == "..";

        /// <summary>
        /// Actual size needed for this entry (header + name + null + padding).
        /// </summary>
        public int ActualSize => CalculateEntrySize(Name);

        /// <summary>
        /// Slack space in this entry (RecLen - ActualSize).
        /// Can be used to insert new entries.
        /// </summary>
        public int SlackSpace => RecLen - ActualSize;

        // =================================================================
        // ENTRY SIZE CALCULATION
        // =================================================================

        /// <summary>
        /// Calculates the size needed for a directory entry with given name.
        /// Formula: ((header_size + name_length + null + alignment - 1) &amp; ~(alignment - 1))
        /// Result is always a multiple of 4 bytes.
        /// </summary>
        public static int CalculateEntrySize(string name)
        {
            if (string.IsNullOrEmpty(name))
                return V7Constants.MIN_DIRENT_SIZE;

            // Header (6 bytes) + name length + null terminator (1 byte)
            int size = V7Constants.DIRENT_HEADER_SIZE + name.Length + 1;

            // Round up to 4-byte boundary
            return (size + V7Constants.DIRENT_ALIGNMENT - 1) & ~(V7Constants.DIRENT_ALIGNMENT - 1);
        }

        // =================================================================
        // PARSING
        // =================================================================

        /// <summary>
        /// Parses directory entry from byte array.
        /// </summary>
        /// <param name="buffer">Buffer containing directory data.</param>
        /// <param name="offset">Offset where entry starts.</param>
        /// <returns>Parsed directory entry, or null if invalid.</returns>
        public static V7DirectoryEntry? Parse(byte[] buffer, int offset)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (offset < 0 || offset + V7Constants.MIN_DIRENT_SIZE > buffer.Length)
                return null;

            var entry = new V7DirectoryEntry();

            // Read header (6 bytes)
            entry.Inode = BitConverter.ToUInt16(buffer, offset);
            entry.RecLen = BitConverter.ToUInt16(buffer, offset + 2);
            entry.NameLen = BitConverter.ToUInt16(buffer, offset + 4);

            // Validate
            if (entry.RecLen < V7Constants.MIN_DIRENT_SIZE)
                return null;

            if (entry.NameLen > V7Constants.MAX_NAME_LEN)
                return null;

            if (offset + entry.RecLen > buffer.Length)
                return null;

            // Read name
            if (entry.NameLen > 0)
            {
                entry.Name = ReadString(buffer, offset + 6, entry.NameLen);
            }

            return entry;
        }

        /// <summary>
        /// Parses all directory entries from a directory block.
        /// </summary>
        /// <param name="buffer">Buffer containing directory block (1024 bytes).</param>
        /// <param name="directorySize">Total size of directory in bytes.</param>
        /// <returns>List of directory entries.</returns>
        public static V7DirectoryEntry[] ParseAll(byte[] buffer, uint directorySize)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            var entries = new System.Collections.Generic.List<V7DirectoryEntry>();
            int offset = 0;

            while (offset < buffer.Length && offset < directorySize)
            {
                var entry = Parse(buffer, offset);

                if (entry == null || entry.RecLen == 0)
                    break;

                // Include even deleted entries (for potential recovery or analysis)
                entries.Add(entry);

                offset += entry.RecLen;
            }

            return entries.ToArray();
        }

        // =================================================================
        // SERIALIZATION
        // =================================================================

        /// <summary>
        /// Writes directory entry to byte array.
        /// </summary>
        /// <param name="buffer">Buffer to write to.</param>
        /// <param name="offset">Offset where entry should be written.</param>
        public void WriteTo(byte[] buffer, int offset)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (offset < 0 || offset + RecLen > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            // Clear the entry space
            Array.Fill(buffer, (byte)0, offset, RecLen);

            // Write header
            BitConverter.TryWriteBytes(buffer.AsSpan(offset), Inode);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 2), RecLen);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 4), NameLen);

            // Write name
            if (!string.IsNullOrEmpty(Name) && NameLen > 0)
            {
                WriteString(buffer, offset + 6, Name, NameLen);
            }
        }

        // =================================================================
        // DIRECTORY TEMPLATE
        // =================================================================

        /// <summary>
        /// Creates "." entry for a new directory.
        /// </summary>
        /// <param name="inodeNumber">Inode number of the directory itself.</param>
        /// <returns>Directory entry for "."</returns>
        public static V7DirectoryEntry CreateDotEntry(ushort inodeNumber)
        {
            return new V7DirectoryEntry
            {
                Inode = inodeNumber,
                RecLen = 12,  // Minimum size for "."
                NameLen = 1,
                Name = "."
            };
        }

        /// <summary>
        /// Creates ".." entry for a new directory.
        /// </summary>
        /// <param name="parentInodeNumber">Inode number of parent directory.</param>
        /// <param name="remainingSpace">Remaining space in block (defaults to 500 bytes).</param>
        /// <returns>Directory entry for ".."</returns>
        public static V7DirectoryEntry CreateDotDotEntry(ushort parentInodeNumber, ushort remainingSpace = 500)
        {
            return new V7DirectoryEntry
            {
                Inode = parentInodeNumber,
                RecLen = remainingSpace,  // Claims rest of block
                NameLen = 2,
                Name = ".."
            };
        }

        /// <summary>
        /// Creates a new directory block with "." and ".." entries.
        /// </summary>
        /// <param name="inodeNumber">Inode number of the directory.</param>
        /// <param name="parentInodeNumber">Inode number of parent directory.</param>
        /// <returns>512-byte directory template.</returns>
        public static byte[] CreateDirectoryTemplate(ushort inodeNumber, ushort parentInodeNumber)
        {
            byte[] template = new byte[V7Constants.DIRBLKSIZ];

            // "." entry
            var dot = CreateDotEntry(inodeNumber);
            dot.WriteTo(template, 0);

            // ".." entry (claims rest of 512-byte block)
            var dotdot = CreateDotDotEntry(parentInodeNumber, (ushort)(V7Constants.DIRBLKSIZ - 12));
            dotdot.WriteTo(template, 12);

            return template;
        }

        // =================================================================
        // HELPER METHODS
        // =================================================================

        private static string ReadString(byte[] buffer, int offset, int length)
        {
            if (length == 0)
                return string.Empty;

            // Find null terminator or use full length
            int actualLength = length;
            for (int i = 0; i < length && offset + i < buffer.Length; i++)
            {
                if (buffer[offset + i] == 0)
                {
                    actualLength = i;
                    break;
                }
            }

            if (actualLength == 0)
                return string.Empty;

            return Encoding.ASCII.GetString(buffer, offset, actualLength);
        }

        private static void WriteString(byte[] buffer, int offset, string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
                return;

            int length = Math.Min(value.Length, maxLength);
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            Array.Copy(bytes, 0, buffer, offset, Math.Min(length, bytes.Length));

            // Null terminator
            if (offset + length < buffer.Length)
            {
                buffer[offset + length] = 0;
            }
        }

        // =================================================================
        // VALIDATION
        // =================================================================

        /// <summary>
        /// Validates directory entry structure.
        /// </summary>
        public bool Validate()
        {
            // Check RecLen is valid
            if (RecLen < V7Constants.MIN_DIRENT_SIZE || RecLen % V7Constants.DIRENT_ALIGNMENT != 0)
                return false;

            // Check NameLen
            if (NameLen > V7Constants.MAX_NAME_LEN)
                return false;

            // Check name matches NameLen
            if (Name.Length != NameLen)
                return false;

            // Check RecLen is large enough for name
            if (RecLen < ActualSize)
                return false;

            return true;
        }

        public override string ToString()
        {
            if (IsDeleted)
                return $"DirEntry (deleted, reclen={RecLen})";

            return $"DirEntry: inode={Inode}, name=\"{Name}\", reclen={RecLen}";
        }
    }
}
