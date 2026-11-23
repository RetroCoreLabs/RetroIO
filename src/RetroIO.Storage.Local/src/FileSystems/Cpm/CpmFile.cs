//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;

namespace RetroIO.Storage.Local.FileSystems.Cpm
{
    /// <summary>
    /// One file on a CP/M disk.
    /// </summary>
    public sealed class CpmFile
    {
        /// <summary>
        /// Initializes a file whose archive flag is clear.
        /// </summary>
        /// <param name="user">
        /// User number, 0 to 15.
        /// </param>
        /// <param name="name">
        /// NAME.TYP.
        /// </param>
        /// <param name="size">
        /// Length in bytes, a whole number of 128-byte records.
        /// </param>
        /// <param name="readOnly">
        /// Whether the R/O attribute is set.
        /// </param>
        /// <param name="system">
        /// Whether the SYS attribute is set.
        /// </param>
        /// <param name="blocks">
        /// The allocation blocks in order.
        /// </param>
        public CpmFile(int user, string name, int size, bool readOnly, bool system, IReadOnlyList<int> blocks)
            : this(user, name, size, readOnly, system, false, blocks)
        {
        }

        /// <summary>
        /// Creates a file description.
        /// </summary>
        /// <param name="user">
        /// User number, 0 to 15.
        /// </param>
        /// <param name="name">
        /// Name as NAME.TYP.
        /// </param>
        /// <param name="size">
        /// Length in bytes, a whole number of 128-byte records.
        /// </param>
        /// <param name="readOnly">
        /// The read-only attribute (bit 7 of the first type byte).
        /// </param>
        /// <param name="system">
        /// The system attribute (bit 7 of the second type byte), which hides the file from DIR.
        /// </param>
        /// <param name="blocks">
        /// The file's allocation blocks in order.
        /// </param>
        /// <param name="archive">
        /// Whether the archive attribute is set (bit 7 of the third type byte, the CP/M 3 archive flag).
        /// </param>
        public CpmFile(int user, string name, int size, bool readOnly, bool system, bool archive, IReadOnlyList<int> blocks)
        {
            Archive = archive;
            User = user;
            Name = name;
            Size = size;
            ReadOnly = readOnly;
            System = system;
            Blocks = blocks;
        }

        /// <summary>
        /// Gets the user number, 0 to 15.
        /// </summary>
        public int User { get; }

        /// <summary>
        /// Gets the name as NAME.TYP.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the length in bytes, a whole number of 128-byte records.
        /// </summary>
        public int Size { get; }

        /// <summary>
        /// Gets a value indicating whether the read-only attribute is set.
        /// </summary>
        public bool ReadOnly { get; }

        /// <summary>
        /// Gets a value indicating whether the system attribute is set.
        /// </summary>
        public bool System { get; }

        /// <summary>
        /// Gets a value indicating whether the archive attribute is set.
        /// </summary>
        public bool Archive { get; }

        /// <summary>
        /// Gets the allocation blocks of the file in order.
        /// </summary>
        public IReadOnlyList<int> Blocks { get; }
    }
}
