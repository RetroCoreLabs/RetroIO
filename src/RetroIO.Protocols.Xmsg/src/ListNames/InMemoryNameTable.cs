//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.ListNames
{
    /// <summary>
    /// An in-memory <see cref="INameTable"/> seeded from a fixed set of rows, kept sorted by magic
    /// number so the "first magic greater than or equal to the one asked for" lookup is a simple
    /// ascending scan.
    /// </summary>
    /// <remarks>
    /// Sorting on construction is an implementation choice, not a wire requirement.
    /// </remarks>
    public sealed class InMemoryNameTable : INameTable
    {
        // Rows sorted ascending by Magic so TryLookupFrom returns the smallest magic that is
        // >= the one asked for.
        private readonly NameTableEntry[] _entries;

        /// <summary>
        /// Initialises a name table from a collection of rows.
        /// </summary>
        /// <param name="entries">
        /// The rows to seed; copied and sorted by magic number.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="entries"/> is null.
        /// </exception>
        public InMemoryNameTable(IReadOnlyList<NameTableEntry> entries)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            _entries = new NameTableEntry[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                _entries[i] = entries[i];
            }

            SortByMagicAscending(_entries);
        }

        /// <summary>
        /// Finds the first row whose magic number is greater than or equal to
        /// <paramref name="fromMagic"/>.
        /// </summary>
        /// <param name="fromMagic">
        /// The lower bound asked for.
        /// </param>
        /// <param name="entry">
        /// When this method returns <c>true</c>, the matching row; otherwise a default row.
        /// </param>
        /// <returns>
        /// <c>true</c> if a matching row exists; otherwise <c>false</c>.
        /// </returns>
        public bool TryLookupFrom(uint fromMagic, out NameTableEntry entry)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                if (_entries[i].Magic >= fromMagic)
                {
                    entry = _entries[i];
                    return true;
                }
            }

            entry = default;
            return false;
        }

        /// <summary>
        /// Sorts rows ascending by magic number using an in-place insertion sort.
        /// </summary>
        /// <param name="entries">
        /// The rows to sort in place.
        /// </param>
        /// <remarks>
        /// Insertion sort avoids the delegate allocation of a comparison-based
        /// <see cref="Array.Sort{T}(T[], Comparison{T})"/> and is enough for a name table.
        /// </remarks>
        private static void SortByMagicAscending(NameTableEntry[] entries)
        {
            for (int i = 1; i < entries.Length; i++)
            {
                NameTableEntry key = entries[i];
                int j = i - 1;
                while (j >= 0 && entries[j].Magic > key.Magic)
                {
                    entries[j + 1] = entries[j];
                    j--;
                }

                entries[j + 1] = key;
            }
        }
    }
}
