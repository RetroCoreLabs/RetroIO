//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.ListRouting
{
    /// <summary>
    /// An in-memory <see cref="IRoutingTable"/> seeded from a fixed set of entries,
    /// kept sorted by system number so the "first system greater than or equal to
    /// the requested one" lookup is a simple ascending scan.
    /// </summary>
    /// <remarks>
    /// VERIFIED lookup semantics (COSMOS Programmer Guide ND-60.164, <c>XSGSY</c>;
    /// XMSG-PROTOCOL.md section 9.1). Sorting on construction is an implementation
    /// choice, not a wire requirement.
    /// </remarks>
    public sealed class InMemoryRoutingTable : IRoutingTable
    {
        // Entries sorted ascending by System so TryLookup returns the smallest
        // system that is >= the query (the "first >= requested" XSGSY rule).
        private readonly RoutingTableEntry[] _entries;

        /// <summary>
        /// Initialises a routing table from a collection of entries.
        /// </summary>
        /// <param name="entries">
        /// The routing-table entries to seed; copied and sorted by system number.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="entries"/> is null.
        /// </exception>
        public InMemoryRoutingTable(IReadOnlyList<RoutingTableEntry> entries)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            _entries = new RoutingTableEntry[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                _entries[i] = entries[i];
            }

            SortBySystemAscending(_entries);
        }

        /// <summary>
        /// Finds the first entry whose system number is greater than or equal to
        /// <paramref name="querySystem"/>.
        /// </summary>
        /// <param name="querySystem">
        /// The system number being queried.
        /// </param>
        /// <param name="entry">
        /// When this method returns <c>true</c>, the matching entry; otherwise a
        /// default entry (system <c>0</c>).
        /// </param>
        /// <returns>
        /// <c>true</c> if a matching entry exists; otherwise <c>false</c>.
        /// </returns>
        public bool TryLookup(ushort querySystem, out RoutingTableEntry entry)
        {
            // Ascending scan: the first entry with System >= querySystem is the
            // smallest qualifying system, exactly the XSGSY "first >= requested" rule.
            for (int i = 0; i < _entries.Length; i++)
            {
                if (_entries[i].System >= querySystem)
                {
                    entry = _entries[i];
                    return true;
                }
            }

            entry = default;
            return false;
        }

        /// <summary>
        /// Sorts entries ascending by system number using an in-place insertion sort.
        /// </summary>
        /// <param name="entries">
        /// The entries to sort in place.
        /// </param>
        /// <remarks>
        /// Insertion sort avoids the delegate/closure allocation of
        /// <see cref="Array.Sort{T}(T[], System.Comparison{T})"/> and is more than
        /// adequate for the small routing tables this type holds.
        /// </remarks>
        private static void SortBySystemAscending(RoutingTableEntry[] entries)
        {
            for (int i = 1; i < entries.Length; i++)
            {
                RoutingTableEntry key = entries[i];
                int j = i - 1;
                while (j >= 0 && entries[j].System > key.System)
                {
                    entries[j + 1] = entries[j];
                    j--;
                }

                entries[j + 1] = key;
            }
        }
    }
}
