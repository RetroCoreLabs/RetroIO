//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Storage.Local.BSD.Structures;

namespace RetroIO.Storage.Local.FFS.Structures
{
    /// <summary>
    /// FFS directory entry structure (variable length).
    /// Minimum size is 8 bytes (header), total size determined by d_reclen.
    /// Directory entries are 4-byte aligned.
    ///
    /// REFERENCE: SunOS 3.4 source code
    /// File: D:\data\Torrent2\sunos_3.4_src\squashfs-root\sun\sys\ufs\fsdir.h
    ///
    /// Original structure (OLD SunOS format):
    /// struct direct {
    ///     u_long  d_ino;      // inode number of entry
    ///     u_short d_reclen;   // length of this record
    ///     u_short d_namlen;   // length of string in d_name
    ///     char    d_name[MAXNAMLEN + 1];
    /// };
    ///
    /// NOTE: The d_type field was added in later BSD versions and does NOT exist in old SunOS 3.4!
    /// </summary>
    public class FfsDirectoryEntry
    {
        // =================================================================
        // DIRECTORY ENTRY FIELDS (header = 8 bytes minimum)
        // =================================================================

        /// <summary>
        /// Inode number (0 = unused entry).
        /// </summary>
        public uint d_ino { get; set; }

        /// <summary>
        /// Length of this directory entry in bytes (including name and padding).
        /// Must be a multiple of 4 (DIRENT_ALIGNMENT).
        /// </summary>
        public ushort d_reclen { get; set; }

        /// <summary>
        /// File type (quick lookup without reading inode).
        /// Values: DT_REG, DT_DIR, DT_LNK, DT_CHR, DT_BLK, DT_FIFO, DT_SOCK, DT_UNKNOWN.
        /// </summary>
        public byte d_type { get; set; }

        /// <summary>
        /// Length of file name in bytes (not including null terminator).
        /// Old SunOS format: 2 bytes (ushort).
        /// New BSD format: 1 byte (lower byte only).
        /// </summary>
        public ushort d_namlen { get; set; }

        /// <summary>
        /// File name (null-terminated, variable length, up to 255 characters).
        /// </summary>
        public string d_name { get; set; } = string.Empty;

        // =================================================================
        // FILE TYPE CONSTANTS
        // =================================================================

        public const byte DT_UNKNOWN = 0;   // Unknown type
        public const byte DT_FIFO = 1;      // Named pipe (FIFO)
        public const byte DT_CHR = 2;       // Character device
        public const byte DT_DIR = 4;       // Directory
        public const byte DT_BLK = 6;       // Block device
        public const byte DT_REG = 8;       // Regular file
        public const byte DT_LNK = 10;      // Symbolic link
        public const byte DT_SOCK = 12;     // Socket

        // =================================================================
        // HELPER PROPERTIES
        // =================================================================

        /// <summary>
        /// True if this is an unused (deleted) entry.
        /// </summary>
        public bool IsUnused => d_ino == 0;

        /// <summary>
        /// True if this is the current directory entry (".").
        /// </summary>
        public bool IsDot => d_name == ".";

        /// <summary>
        /// True if this is the parent directory entry ("..").
        /// </summary>
        public bool IsDotDot => d_name == "..";

        /// <summary>
        /// True if this entry represents a directory.
        /// </summary>
        public bool IsDirectory => d_type == DT_DIR;

        /// <summary>
        /// True if this entry represents a regular file.
        /// </summary>
        public bool IsRegularFile => d_type == DT_REG;

        /// <summary>
        /// True if this entry represents a symbolic link.
        /// </summary>
        public bool IsSymbolicLink => d_type == DT_LNK;

        /// <summary>
        /// Gets file type as string.
        /// </summary>
        public string FileTypeName
        {
            get
            {
                return d_type switch
                {
                    DT_REG => "regular file",
                    DT_DIR => "directory",
                    DT_LNK => "symbolic link",
                    DT_CHR => "character device",
                    DT_BLK => "block device",
                    DT_FIFO => "FIFO",
                    DT_SOCK => "socket",
                    _ => "unknown"
                };
            }
        }

        // =================================================================
        // PARSING
        // =================================================================

        /// <summary>
        /// Parses directory entry from byte array (big-endian format for SunOS).
        /// Uses old SunOS 3.4 format: d_ino(4) + d_reclen(2) + d_namlen(2) + d_name[]
        /// There is NO d_type field in old SunOS - that was added in later BSD versions.
        /// </summary>
        /// <param name="buffer">
        /// Buffer containing directory entry.
        /// </param>
        /// <param name="offset">
        /// Offset where entry starts.
        /// </param>
        /// <returns>
        /// Parsed directory entry.
        /// </returns>
        public static FfsDirectoryEntry Parse(byte[] buffer, int offset)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (offset < 0 || offset + FfsConstants.DIRENT_HEADER_SIZE > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            var entry = new FfsDirectoryEntry();

            // Read inode (4 bytes, offset 0-3) - big-endian
            entry.d_ino = BsdEndianHelper.ReadUInt32BE(buffer, offset + 0);

            // Read reclen (2 bytes, offset 4-5) - big-endian
            entry.d_reclen = ReadUInt16BE(buffer, offset + 4);

            // Read namlen (2 bytes, offset 6-7) - big-endian (OLD SUNOS FORMAT)
            // Per SunOS 3.4 fsdir.h: "u_short d_namlen"
            entry.d_namlen = ReadUInt16BE(buffer, offset + 6);

            // Old SunOS format doesn't have d_type field - set to unknown
            entry.d_type = DT_UNKNOWN;

            // Read name (variable length, starts at offset 8)
            if (entry.d_namlen > 0)
            {
                int nameLength = Math.Min((int)entry.d_namlen, entry.d_reclen - FfsConstants.DIRENT_HEADER_SIZE);
                nameLength = Math.Min(nameLength, buffer.Length - offset - FfsConstants.DIRENT_HEADER_SIZE);

                if (nameLength > 0)
                {
                    entry.d_name = System.Text.Encoding.ASCII.GetString(buffer, offset + FfsConstants.DIRENT_HEADER_SIZE, nameLength);
                }
            }

            return entry;
        }

        private static ushort ReadUInt16BE(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        /// <summary>
        /// Calculates the actual size needed for this entry (including alignment).
        /// </summary>
        /// <param name="nameLength">
        /// Length of file name.
        /// </param>
        /// <returns>
        /// Size in bytes (aligned to 4-byte boundary).
        /// </returns>
        public static ushort CalculateRecordLength(int nameLength)
        {
            // Header (8 bytes) + name + null terminator + padding to 4-byte alignment
            int size = FfsConstants.DIRENT_HEADER_SIZE + nameLength + 1;

            // Round up to next multiple of 4
            if ((size % FfsConstants.DIRENT_ALIGNMENT) != 0)
            {
                size += FfsConstants.DIRENT_ALIGNMENT - (size % FfsConstants.DIRENT_ALIGNMENT);
            }

            return (ushort)size;
        }

        /// <summary>
        /// Parses all directory entries from a directory data buffer.
        /// </summary>
        /// <param name="buffer">
        /// Buffer containing directory entries.
        /// </param>
        /// <param name="includeDots">
        /// If false, exclude "." and ".." entries.
        /// </param>
        /// <returns>
        /// Array of parsed directory entries.
        /// </returns>
        public static FfsDirectoryEntry[] ParseAll(byte[] buffer, bool includeDots = true)
        {
            if (buffer == null || buffer.Length == 0)
            {
                Console.WriteLine("[DEBUG FfsDirectoryEntry.ParseAll] Buffer is null or empty");
                return Array.Empty<FfsDirectoryEntry>();
            }

            Console.WriteLine($"[DEBUG FfsDirectoryEntry.ParseAll] Parsing {buffer.Length} bytes");
            var entries = new System.Collections.Generic.List<FfsDirectoryEntry>();
            int offset = 0;
            int entryCount = 0;

            while (offset < buffer.Length)
            {
                // Ensure we have enough space for header
                if (offset + FfsConstants.DIRENT_HEADER_SIZE > buffer.Length)
                {
                    Console.WriteLine($"  Entry {entryCount}: Not enough space for header at offset {offset}");
                    break;
                }

                // Parse entry
                var entry = Parse(buffer, offset);
                Console.WriteLine($"  Entry {entryCount} at offset {offset}: ino={entry.d_ino}, reclen={entry.d_reclen}, type={entry.d_type}, namlen={entry.d_namlen}, name=\"{entry.d_name}\"");

                // Stop on invalid record length
                // Note: d_reclen can be very large (e.g., DIRBLKSIZ) for the last entry in a block
                // This is normal in FFS - the last entry's reclen extends to the block boundary
                if (entry.d_reclen == 0)
                {
                    Console.WriteLine($"    Zero d_reclen, stopping");
                    break;
                }

                // Sanity check: reclen should not exceed reasonable directory block size (typically 8192)
                // But allow up to 64KB for very large directories
                if (entry.d_reclen > 8192)
                {
                    Console.WriteLine($"    Unreasonably large d_reclen={entry.d_reclen}, stopping");
                    break;
                }

                // Add entry if valid
                if (!entry.IsUnused)
                {
                    if (includeDots || (!entry.IsDot && !entry.IsDotDot))
                    {
                        Console.WriteLine($"    Adding entry: \"{entry.d_name}\"");
                        entries.Add(entry);
                    }
                    else
                    {
                        Console.WriteLine($"    Skipping dot/dotdot entry");
                    }
                }
                else
                {
                    Console.WriteLine($"    Unused entry (ino=0)");
                }

                // Move to next entry
                offset += entry.d_reclen;
                entryCount++;
            }

            Console.WriteLine($"  Total entries parsed: {entryCount}, valid entries: {entries.Count}");
            return entries.ToArray();
        }

        public override string ToString()
        {
            if (IsUnused)
                return "DirEntry (unused)";

            return $"DirEntry: \"{d_name}\" (inode={d_ino}, type={FileTypeName}, len={d_reclen})";
        }
    }
}
