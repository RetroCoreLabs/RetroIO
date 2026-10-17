//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The SINTRAN III password fold: turns a typed password into the single 16-bit word that
    /// SINTRAN stores and sends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is NOT a cryptographic hash. SINTRAN folds the typed characters into one word:
    /// <c>acc = ROL16(acc, 3) + toupper(c)</c>, repeated until CR. Consequences that matter when
    /// using it: it is <b>case-insensitive</b> and <b>not salted</b>, so two users with the same
    /// password store the same word, and a word can be attacked by folding a dictionary. Treat a
    /// captured word as equivalent to the password itself.
    /// </para>
    /// <para>
    /// Carved from the L-VSX-500 disassembly (segment S3CP, routine LOGIN at octal 060616) and
    /// verified against real stored account values. On 2026-07-29 it was confirmed a second way,
    /// on the wire: a remote file-access request carries this word and never the plaintext, and
    /// varying only the password moved exactly the two bytes this method predicts. See
    /// <c>DOC/XMSG-FA-ACCESS-PASSWORD-ON-THE-WIRE-2026-07-29.md</c> and
    /// <c>tools/sintran-segment-carver/versions/L-VSX-500/re/PASSWORD-ALGORITHM.md</c>.
    /// </para>
    /// <para>
    /// Only letters are folded case-insensitively; digits and punctuation go in by their raw ASCII
    /// value, so the usable alphabet is far wider than A-Z and 0-9. The fold loop in the
    /// disassembly adds the character UNMASKED - it does not clamp to 7 bits - so this
    /// implementation does not either. That only differs for bytes >= 0x80, which cannot be typed
    /// on an ND terminal anyway.
    /// </para>
    /// </remarks>
    public static class SintranPassword
    {
        /// <summary>
        /// Encodes a typed password into the 16-bit word SINTRAN stores and puts on the wire.
        /// </summary>
        /// <param name="password">
        /// The password as typed. <see langword="null"/> or empty encodes to 0, which is how SINTRAN
        /// represents "no password".
        /// </param>
        /// <returns>
        /// The encoded word.
        /// </returns>
        public static ushort Encode(string? password)
        {
            if (string.IsNullOrEmpty(password))
            {
                // A user with no password stores 0 - the fold of the empty string.
                return 0;
            }

            int accumulator = 0;
            for (int i = 0; i < password!.Length; i++)
            {
                int character = password[i];

                // Letters only are uppercased. Clearing bit 5 blindly would corrupt digits, which
                // have that bit set - a mistake worth naming, because it silently yields a
                // plausible-looking wrong word.
                if (character >= 'a' && character <= 'z')
                {
                    character -= 32;
                }

                // ROL16 by 3, then add. The rotate is 16-bit, so the three bits shifted off the top
                // re-enter at the bottom.
                accumulator = ((accumulator << 3) | (accumulator >> 13)) & 0xFFFF;
                accumulator = (accumulator + character) & 0xFFFF;
            }

            return (ushort)accumulator;
        }

        /// <summary>
        /// Encodes a typed password into <paramref name="destination"/> in the byte order it takes on
        /// the wire (high byte first).
        /// </summary>
        /// <param name="password">
        /// The password as typed.
        /// </param>
        /// <param name="destination">
        /// A span of at least two bytes.
        /// </param>
        /// <exception cref="ArgumentException">
        /// <paramref name="destination"/> is shorter than two bytes.
        /// </exception>
        public static void EncodeTo(string? password, Span<byte> destination)
        {
            if (destination.Length < 2)
            {
                throw new ArgumentException(
                    "The destination needs at least two bytes for a folded password word.",
                    nameof(destination));
            }

            ushort word = Encode(password);
            destination[0] = (byte)(word >> 8);
            destination[1] = (byte)(word & 0xFF);
        }

        /// <summary>
        /// Tests whether a typed password encodes to an expected stored word.
        /// </summary>
        /// <remarks>
        /// Because the fold is case-insensitive, this returns <see langword="true"/> for any casing
        /// of the same string - which is SINTRAN's real behaviour, not a bug in the comparison.
        /// </remarks>
        /// <param name="password">
        /// The password as typed.
        /// </param>
        /// <param name="storedWord">
        /// The stored or captured 16-bit word.
        /// </param>
        /// <returns>
        /// <see langword="true"/> if the password encodes to that word.
        /// </returns>
        public static bool Matches(string? password, ushort storedWord)
        {
            return Encode(password) == storedWord;
        }
    }
}
