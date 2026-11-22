//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Tn3270
{
    /// <summary>
    /// EBCDIC code page 037 (US/Canada), the default for a new 3270 connection (Ronny's decision,
    /// 22 November 2026).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bytes X'40' to X'FE' come from RetroTerm docs/spec/IBM3270/EBCDIC.md, "Code page 037", whose
    /// source is IBM's ICU table ibm-37_P100-1999.ucm and which was checked there against
    /// unicode.org CP037.TXT, Python cp037, x3270 and GA27-2837-9 fig. 10-12. The unit tests check
    /// it once more against .NET's own IBM037 encoding.
    /// </para>
    /// <para>
    /// Bytes X'00' to X'3F' are 3270 controls and orders, not text. ICU maps them to C0/C1 control
    /// characters, which is right for text files and wrong for a 3270 buffer (EBCDIC.md). The
    /// buffer controls that are stored (NUL, FF, CR, NL, EM, EO) are shown as a blank
    /// (GA23-0059-4 p. 4-10); DUP and FM are shown by the renderer as overscored asterisk and
    /// semicolon, which have no single Unicode character, so this table gives them no character.
    /// </para>
    /// </remarks>
    public static class CodePage037
    {
        // X'40'..X'FF', row by row from EBCDIC.md. X'FF' is EO, shown as a blank.
        private static readonly ushort[] UpperCodes =
        {
            0x0020, 0x00A0, 0x00E2, 0x00E4, 0x00E0, 0x00E1, 0x00E3, 0x00E5, 0x00E7, 0x00F1, 0x00A2, 0x002E, 0x003C, 0x0028, 0x002B, 0x007C,
            0x0026, 0x00E9, 0x00EA, 0x00EB, 0x00E8, 0x00ED, 0x00EE, 0x00EF, 0x00EC, 0x00DF, 0x0021, 0x0024, 0x002A, 0x0029, 0x003B, 0x00AC,
            0x002D, 0x002F, 0x00C2, 0x00C4, 0x00C0, 0x00C1, 0x00C3, 0x00C5, 0x00C7, 0x00D1, 0x00A6, 0x002C, 0x0025, 0x005F, 0x003E, 0x003F,
            0x00F8, 0x00C9, 0x00CA, 0x00CB, 0x00C8, 0x00CD, 0x00CE, 0x00CF, 0x00CC, 0x0060, 0x003A, 0x0023, 0x0040, 0x0027, 0x003D, 0x0022,
            0x00D8, 0x0061, 0x0062, 0x0063, 0x0064, 0x0065, 0x0066, 0x0067, 0x0068, 0x0069, 0x00AB, 0x00BB, 0x00F0, 0x00FD, 0x00FE, 0x00B1,
            0x00B0, 0x006A, 0x006B, 0x006C, 0x006D, 0x006E, 0x006F, 0x0070, 0x0071, 0x0072, 0x00AA, 0x00BA, 0x00E6, 0x00B8, 0x00C6, 0x00A4,
            0x00B5, 0x007E, 0x0073, 0x0074, 0x0075, 0x0076, 0x0077, 0x0078, 0x0079, 0x007A, 0x00A1, 0x00BF, 0x00D0, 0x00DD, 0x00DE, 0x00AE,
            0x005E, 0x00A3, 0x00A5, 0x00B7, 0x00A9, 0x00A7, 0x00B6, 0x00BC, 0x00BD, 0x00BE, 0x005B, 0x005D, 0x00AF, 0x00A8, 0x00B4, 0x00D7,
            0x007B, 0x0041, 0x0042, 0x0043, 0x0044, 0x0045, 0x0046, 0x0047, 0x0048, 0x0049, 0x00AD, 0x00F4, 0x00F6, 0x00F2, 0x00F3, 0x00F5,
            0x007D, 0x004A, 0x004B, 0x004C, 0x004D, 0x004E, 0x004F, 0x0050, 0x0051, 0x0052, 0x00B9, 0x00FB, 0x00FC, 0x00F9, 0x00FA, 0x00FF,
            0x005C, 0x00F7, 0x0053, 0x0054, 0x0055, 0x0056, 0x0057, 0x0058, 0x0059, 0x005A, 0x00B2, 0x00D4, 0x00D6, 0x00D2, 0x00D3, 0x00D5,
            0x0030, 0x0031, 0x0032, 0x0033, 0x0034, 0x0035, 0x0036, 0x0037, 0x0038, 0x0039, 0x00B3, 0x00DB, 0x00DC, 0x00D9, 0x00DA, 0x0020,
        };

        private static readonly char[] ToUnicodeTable = BuildToUnicode();
        private static readonly byte[] FromUnicodeTable = BuildFromUnicode();

        /// <summary>
        /// The character to show for a buffer byte. '\0' when the byte has no character of its own
        /// (DUP, FM, SUB and the order range) - the renderer draws those.
        /// </summary>
        /// <param name="ebcdic">
        /// The buffer byte.
        /// </param>
        /// <returns>
        /// The Unicode character.
        /// </returns>
        public static char ToUnicode(byte ebcdic) => ToUnicodeTable[ebcdic];

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
        public static bool TryFromUnicode(char c, out byte ebcdic)
        {
            if (c < FromUnicodeTable.Length)
            {
                ebcdic = FromUnicodeTable[c];
                return ebcdic != 0;
            }

            ebcdic = 0;
            return false;
        }

        private static char[] BuildToUnicode()
        {
            var table = new char[256];
            for (int i = 0; i < UpperCodes.Length; i++)
            {
                table[0x40 + i] = (char)UpperCodes[i];
            }

            // Stored controls shown as a blank (GA23-0059-4 p. 4-10).
            table[FormatControl.Null] = ' ';
            table[FormatControl.FormFeed] = ' ';
            table[FormatControl.CarriageReturn] = ' ';
            table[FormatControl.NewLine] = ' ';
            table[FormatControl.EndOfMedium] = ' ';
            return table;
        }

        private static byte[] BuildFromUnicode()
        {
            // Every character in the table is below U+0100.
            var table = new byte[256];

            // X'40' to X'FE' only: X'FF' (EO) is shown as a blank but typing a space must give X'40'.
            for (int i = 0; i < UpperCodes.Length - 1; i++)
            {
                char c = (char)UpperCodes[i];
                if (table[c] == 0)
                {
                    table[c] = (byte)(0x40 + i);
                }
            }

            return table;
        }
    }
}
