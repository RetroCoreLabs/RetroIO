//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Ndfs
{
    /// <summary>
    /// The access rights one tier of the SINTRAN file access word can grant.
    /// </summary>
    /// <remarks>
    /// <para><b>Where this comes from</b></para>
    /// The access word is record bytes 26-27 of the 64-byte listing entry. It holds three of these
    /// five-bit tiers - own, friend and public - packed by
    /// <see cref="FaObjectEntryHeader.PackAccess"/>.
    /// <para><b>Evidence</b></para>
    /// The tier split and order are stated in the SINTRAN III Monitor Calls manual appendix C and in
    /// the annotated <c>@DUMP-OBJECT-ENTRY</c> listing in the System Supervisor manual, which prints
    /// the field as "ACCESS WORD".
    /// <para><b>Four of the five letters are confirmed, one is not</b></para>
    /// <see cref="Read"/>, <see cref="Write"/>, <see cref="Append"/> and <see cref="Directory"/> are
    /// all names D100 printed back for bits we set, via <c>FILE-STATISTICS</c> on 2026-08-05.
    /// <see cref="C"/> alone is still unnamed: no file we hold sets it without the others, so
    /// nothing has ever made SINTRAN print a word for it.
    /// </remarks>
    [Flags]
    public enum FaAccessRights : byte
    {
        /// <summary>
        /// No access at all: the tier may not touch the file.
        /// </summary>
        /// <remarks>
        /// A whole access word of zero denies every tier including the owner. That is what we sent on
        /// every record until 2026-08-05, and it appears on no real record.
        /// </remarks>
        None = 0,

        /// <summary>
        /// Read access, wire bit <c>0x01</c>.
        /// </summary>
        Read = 1 << 0,

        /// <summary>
        /// Write access, wire bit <c>0x02</c>.
        /// </summary>
        Write = 1 << 1,

        /// <summary>
        /// Append access, wire bit <c>0x04</c>.
        /// </summary>
        Append = 1 << 2,

        /// <summary>
        /// The "C" right, wire bit <c>0x08</c>. Its meaning is UNKNOWN.
        /// </summary>
        /// <remarks>
        /// Present in the manual's tier table as the letter C. No captured record we hold sets it on
        /// its own, so nothing distinguishes what it grants.
        /// </remarks>
        C = 1 << 3,

        /// <summary>
        /// Directory access, wire bit <c>0x10</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Named by the machine itself</b></para>
        /// Confirmed 2026-08-05 by running <c>FILE-STATISTICS</c> on D100 against a file we served
        /// with this bit set in the OWN tier. D100's own decoder printed
        /// <c>OWN ACCESS : READ, WRITE, APPEND, DIRECTORY</c>. That is ND naming our bit back to us,
        /// which is as direct as evidence gets - and it is falsifiable: clear the bit and the word
        /// must disappear from that line.
        /// <para><b>Where it appears</b></para>
        /// Set in the OWN tier of the 40 ordinary user files in the capture, and in every tier of the
        /// one file that grants everything.
        /// </remarks>
        Directory = 1 << 4,

        /// <summary>
        /// Every right in the tier, wire bits <c>0x1F</c>.
        /// </summary>
        All = Read | Write | Append | C | Directory,
    }
}
