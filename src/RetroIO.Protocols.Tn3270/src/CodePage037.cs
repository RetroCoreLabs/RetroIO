//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Tn3270
{
    /// <summary>
    /// EBCDIC code page 037 (USA and Canada), kept under its first name for callers written before
    /// <see cref="CodePage"/> existed.
    /// </summary>
    /// <remarks>
    /// Every call goes to <see cref="CodePage.Cp037"/>. A display that shows another page should be
    /// read through <see cref="Display3270.CodePage"/> instead.
    /// </remarks>
    public static class CodePage037
    {
        /// <summary>
        /// The character to show for a buffer byte.
        /// </summary>
        /// <param name="ebcdic">
        /// The buffer byte.
        /// </param>
        /// <returns>
        /// The Unicode character, or '\0' when the byte has no character of its own.
        /// </returns>
        public static char ToUnicode(byte ebcdic) => CodePage.Cp037.ToUnicode(ebcdic);

        /// <summary>
        /// The EBCDIC byte for a typed character.
        /// </summary>
        /// <param name="c">
        /// The character.
        /// </param>
        /// <param name="ebcdic">
        /// The byte, when there is one.
        /// </param>
        /// <returns>
        /// False when code page 037 has no such character.
        /// </returns>
        public static bool TryFromUnicode(char c, out byte ebcdic) => CodePage.Cp037.TryFromUnicode(c, out ebcdic);
    }
}
