//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Sintran
{
    /// <summary>
    /// The SINTRAN convention for a name held in a fixed-width field.
    /// </summary>
    /// <remarks>
    /// <para><b>The rule</b></para>
    /// A name shorter than its field is followed by an apostrophe, <c>0x27</c>, and then zero fill.
    /// A name that fills the field exactly carries no terminator at all - so the terminator is a
    /// pad marker, not a delimiter, and a reader must stop at the field width as well as at the
    /// apostrophe.
    /// <para><b>Where it turns up</b></para>
    /// Three places handled this separately before: the 16-byte directory name in a directory
    /// entry, the 16-byte user name in a user reply, and the file specification in an open request.
    /// The 64-byte object entry follows it too, though that one is written by RetroFS.
    /// <para><b>Evidence</b></para>
    /// Verified across the captures on <c>PACK-ONE</c>, <c>SYSTEM</c>, <c>TXT1</c>,
    /// <c>PATCH-FILE:OUT</c> and the 16-character names in the directory walk, which run to the end
    /// of their field with no apostrophe.
    /// </remarks>
    public static class SintranName
    {
        /// <summary>
        /// The byte that ends a name shorter than its field.
        /// </summary>
        public const byte Terminator = (byte)'\'';

        /// <summary>
        /// Writes a name into a fixed-width field, upper-cased, terminated and zero filled.
        /// </summary>
        /// <param name="destination">
        /// The field to fill. Its whole length is written.
        /// </param>
        /// <param name="name">
        /// The name. Longer than the field is truncated; characters outside ASCII become
        /// <c>?</c>.
        /// </param>
        /// <remarks>
        /// The field is not cleared first - it is written end to end, so a reused buffer cannot
        /// leave residue behind the terminator. That matters because the real server DOES leave
        /// residue there, and reproducing another machine's uninitialised memory would be
        /// fabrication.
        /// </remarks>
        public static void Write(Span<byte> destination, string name)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            string upper = name.ToUpperInvariant();

            int count = upper.Length;
            if (count > destination.Length)
            {
                count = destination.Length;
            }

            for (int i = 0; i < count; i++)
            {
                char c = upper[i];
                destination[i] = c < 128 ? (byte)c : (byte)'?';
            }

            int at = count;
            if (at < destination.Length)
            {
                destination[at++] = Terminator;
            }

            while (at < destination.Length)
            {
                destination[at++] = 0;
            }
        }

        /// <summary>
        /// Reads a name out of a fixed-width field.
        /// </summary>
        /// <param name="source">
        /// The field.
        /// </param>
        /// <returns>
        /// The name, without the terminator or anything after it.
        /// </returns>
        public static string Read(ReadOnlySpan<byte> source)
        {
            int length = 0;
            while (length < source.Length
                   && source[length] != Terminator
                   && source[length] != 0)
            {
                length++;
            }

            char[] chars = new char[length];
            for (int i = 0; i < length; i++)
            {
                chars[i] = (char)source[i];
            }

            return new string(chars);
        }
    }
}
