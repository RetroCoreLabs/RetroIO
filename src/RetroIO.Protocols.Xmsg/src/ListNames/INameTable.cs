//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.ListNames
{
    /// <summary>
    /// A name table that can answer an XROUT <c>XSGNI</c> "get next name" request.
    /// </summary>
    /// <remarks>
    /// The lookup rule is "the first name whose magic number is greater than or equal to the one
    /// asked for". VERIFIED 2026-11-10 from a complete LIST-NAMES walk against D100: the asker sends
    /// the previous reply's magic number plus one, and D100 answered with its rows in ascending magic
    /// order, from <c>0x00640001</c> (D100) to <c>0x4E1F0001</c> (D19999), then the end-of-table form.
    /// </remarks>
    public interface INameTable
    {
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
        bool TryLookupFrom(uint fromMagic, out NameTableEntry entry);
    }
}
