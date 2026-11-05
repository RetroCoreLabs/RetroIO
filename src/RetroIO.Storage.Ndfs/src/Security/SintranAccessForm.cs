//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Storage.Ndfs.Security
{
    /// <summary>
    /// File access in SINTRAN's own written form, as the access commands take it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SINTRAN writes access as one field per tier: <c>N</c> for no access, or any combination of
    /// R read, W write, A append, C common, D directory; <c>N</c> must stand alone
    /// (<c>@SET-FILE-ACCESS</c> rule 2, SINTRAN III Reference Manual ND-60.128.5). The commands
    /// that set all three tiers take them in the order PUBLIC, FRIEND, OWN, and an empty field
    /// means "no change":
    /// </para>
    /// <code>
    /// @SET-FILE-ACCESS F-1:DATA N,N,RWACD
    /// @SET-DEFAULT-FILE-ACCESS N,RWA,RWACD
    /// @SET-FRIEND-ACCESS BUDDY,W
    /// </code>
    /// <para>
    /// The 15-bit access word holds OWN in bits 0-4, FRIEND in bits 5-9 and PUBLIC in bits 10-14;
    /// within a tier R is bit 0, W bit 1, A bit 2, C bit 3, D bit 4. The same five bits, shifted to
    /// bits 8-12, are a friend's rights in a user entry. Mirrors <c>ndfs_parse_access_rights</c> and
    /// <c>ndfs_apply_access_triple</c> in the norskdata-ndfs C library.
    /// </para>
    /// </remarks>
    public static class SintranAccessForm
    {
        /// <summary>
        /// Shift of the PUBLIC tier in the 15-bit access word.
        /// </summary>
        public const int PublicShift = 10;

        /// <summary>
        /// Shift of the FRIEND tier in the 15-bit access word.
        /// </summary>
        public const int FriendShift = 5;

        /// <summary>
        /// Shift of the OWN tier in the 15-bit access word.
        /// </summary>
        public const int OwnShift = 0;

        /// <summary>
        /// Parses one SINTRAN access field into a 5-bit tier value.
        /// </summary>
        /// <param name="field">
        /// <c>N</c>, or letters from R W A C D in any order and case.
        /// </param>
        /// <returns>
        /// The rights, R = bit 0 ... D = bit 4; 0 for <c>N</c>.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown for an empty field, an unknown letter, or <c>N</c> together with other letters.
        /// </exception>
        public static byte ParseAccessRights(string field)
        {
            if (string.IsNullOrEmpty(field))
                throw new ArgumentException("Empty access field", nameof(field));

            int rights = 0;
            bool none = false;
            bool letters = false;
            for (int i = 0; i < field.Length; i++)
            {
                switch (char.ToUpperInvariant(field[i]))
                {
                    case 'R': rights |= 0x01; letters = true; break;
                    case 'W': rights |= 0x02; letters = true; break;
                    case 'A': rights |= 0x04; letters = true; break;
                    case 'C': rights |= 0x08; letters = true; break;
                    case 'D': rights |= 0x10; letters = true; break;
                    case 'N': none = true; break;
                    default:
                        throw new ArgumentException($"Bad access letter '{field[i]}' in '{field}'", nameof(field));
                }
            }

            // "N must stand alone" (@SET-FILE-ACCESS rule 2).
            if (none && letters)
                throw new ArgumentException($"N must stand alone: '{field}'", nameof(field));

            return (byte)rights;
        }

        /// <summary>
        /// Applies <c>PUBLIC,FRIEND,OWN</c> to a 15-bit access word.
        /// </summary>
        /// <param name="oldBits">
        /// The access word before the change.
        /// </param>
        /// <param name="triple">
        /// Up to three comma-separated fields in SINTRAN's order PUBLIC, FRIEND, OWN. A missing or
        /// empty field keeps that tier (<c>N,,RWACD</c> changes PUBLIC and OWN only). Spaces are
        /// ignored.
        /// </param>
        /// <returns>
        /// The new access word.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown for a bad field or more than three fields.
        /// </exception>
        public static ushort ApplyAccessTriple(ushort oldBits, string triple)
        {
            ArgumentNullException.ThrowIfNull(triple);

            int bits = oldBits & 0x7FFF;
            Span<char> field = stackalloc char[16];
            int length = 0;
            int fieldIndex = 0;

            for (int i = 0; i <= triple.Length; i++)
            {
                char c = i < triple.Length ? triple[i] : ',';
                if (c == ' ')
                    continue;

                if (c != ',')
                {
                    if (length == field.Length)
                        throw new ArgumentException($"Access field too long in '{triple}'", nameof(triple));
                    field[length++] = c;
                    continue;
                }

                if (fieldIndex >= 3)
                    throw new ArgumentException($"More than three access fields: '{triple}'", nameof(triple));

                if (length > 0)
                {
                    int shift = fieldIndex == 0 ? PublicShift : fieldIndex == 1 ? FriendShift : OwnShift;
                    int rights = ParseAccessRights(new string(field.Slice(0, length)));
                    bits = (bits & ~(0x1F << shift)) | (rights << shift);
                }

                fieldIndex++;
                length = 0;
            }

            return (ushort)bits;
        }

        /// <summary>
        /// Writes a 5-bit tier value as one SINTRAN access field, the form
        /// <see cref="ParseAccessRights"/> reads back.
        /// </summary>
        /// <param name="rights">
        /// The rights, R = bit 0 ... D = bit 4; higher bits are ignored.
        /// </param>
        /// <returns>
        /// <c>N</c> for none, otherwise the letters in the order R W A C D, e.g. <c>RWA</c>.
        /// </returns>
        public static string FormatAccessRights(int rights)
        {
            Span<char> letters = stackalloc char[5];
            int length = 0;
            if ((rights & 0x01) != 0) letters[length++] = 'R';
            if ((rights & 0x02) != 0) letters[length++] = 'W';
            if ((rights & 0x04) != 0) letters[length++] = 'A';
            if ((rights & 0x08) != 0) letters[length++] = 'C';
            if ((rights & 0x10) != 0) letters[length++] = 'D';
            return length == 0 ? "N" : new string(letters.Slice(0, length));
        }

        /// <summary>
        /// Writes a 15-bit access word as <c>PUBLIC,FRIEND,OWN</c>, the form
        /// <see cref="ApplyAccessTriple"/> reads back; SINTRAN's default 0x04FF is
        /// <c>R,RWA,RWACD</c>.
        /// </summary>
        /// <param name="bits">
        /// The access word.
        /// </param>
        /// <returns>
        /// The three fields, comma-separated.
        /// </returns>
        public static string FormatAccessTriple(ushort bits)
        {
            return FormatAccessRights(bits >> PublicShift) + "," +
                   FormatAccessRights(bits >> FriendShift) + "," +
                   FormatAccessRights(bits >> OwnShift);
        }
    }
}
