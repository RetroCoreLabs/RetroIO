//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Tn3270
{
    /// <summary>
    /// One EBCDIC code page: what each buffer byte shows, which byte a typed character becomes, and
    /// the IDs the Character Sets query reply reports for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The buffer always keeps the byte, never the character, so a byte the page does not define is
    /// read back to the host unchanged (GA23-0059-4 p. 4-10).
    /// </para>
    /// <para>
    /// Bytes X'00' to X'3F' are 3270 controls and orders, not text. The stored controls NUL, FF, CR,
    /// NL, EM and EO (X'FF') show as a blank (GA23-0059-4 p. 4-10). DUP, FM and SUB have no single
    /// Unicode character and are drawn by the renderer, so they give no character here.
    /// </para>
    /// <para>
    /// The tables and where they come from are in <see cref="CodePageTables"/>.
    /// </para>
    /// </remarks>
    public sealed class CodePage
    {
        private static readonly List<CodePage> s_all = new List<CodePage>();

        private readonly char[] _toUnicode = new char[256];
        private readonly Dictionary<char, byte> _fromUnicode = new Dictionary<char, byte>();
        private readonly bool _graphicEscape;

        /// <summary>
        /// Code page 037, USA and Canada: the default for a new 3270 connection (Ronny's decision,
        /// 22 November 2026).
        /// </summary>
        public static readonly CodePage Cp037 = Add(37, 697, "USA, Canada", CodePageTables.Cp37);

        /// <summary>
        /// Code page 273, Germany and Austria.
        /// </summary>
        public static readonly CodePage Cp273 = Add(273, 697, "Germany, Austria", CodePageTables.Cp273);

        /// <summary>
        /// Code page 277, Denmark and Norway.
        /// </summary>
        public static readonly CodePage Cp277 = Add(277, 697, "Denmark, Norway", CodePageTables.Cp277);

        /// <summary>
        /// Code page 278, Finland and Sweden.
        /// </summary>
        public static readonly CodePage Cp278 = Add(278, 697, "Finland, Sweden", CodePageTables.Cp278);

        /// <summary>
        /// Code page 280, Italy.
        /// </summary>
        public static readonly CodePage Cp280 = Add(280, 697, "Italy", CodePageTables.Cp280);

        /// <summary>
        /// Code page 284, Spain and Latin America.
        /// </summary>
        public static readonly CodePage Cp284 = Add(284, 697, "Spain, Latin America", CodePageTables.Cp284);

        /// <summary>
        /// Code page 285, United Kingdom.
        /// </summary>
        public static readonly CodePage Cp285 = Add(285, 697, "United Kingdom", CodePageTables.Cp285);

        /// <summary>
        /// Code page 297, France.
        /// </summary>
        public static readonly CodePage Cp297 = Add(297, 697, "France", CodePageTables.Cp297);

        /// <summary>
        /// Code page 500, International.
        /// </summary>
        public static readonly CodePage Cp500 = Add(500, 697, "International", CodePageTables.Cp500);

        /// <summary>
        /// Code page 871, Iceland.
        /// </summary>
        public static readonly CodePage Cp871 = Add(871, 697, "Iceland", CodePageTables.Cp871);

        /// <summary>
        /// Code page 1047, Latin 1 for open systems (z/OS UNIX).
        /// </summary>
        public static readonly CodePage Cp1047 = Add(1047, 697, "Latin 1, Open Systems", CodePageTables.Cp1047);

        /// <summary>
        /// Code page 1140, USA and Canada with the euro sign.
        /// </summary>
        public static readonly CodePage Cp1140 = Add(1140, 695, "USA, Canada (euro)", CodePageTables.Cp1140);

        /// <summary>
        /// Code page 1141, Germany and Austria with the euro sign.
        /// </summary>
        public static readonly CodePage Cp1141 = Add(1141, 695, "Germany, Austria (euro)", CodePageTables.Cp1141);

        /// <summary>
        /// Code page 1142, Denmark and Norway with the euro sign (at X'5A', not X'9F' as on 1140).
        /// </summary>
        public static readonly CodePage Cp1142 = Add(1142, 695, "Denmark, Norway (euro)", CodePageTables.Cp1142);

        /// <summary>
        /// Code page 1143, Finland and Sweden with the euro sign.
        /// </summary>
        public static readonly CodePage Cp1143 = Add(1143, 695, "Finland, Sweden (euro)", CodePageTables.Cp1143);

        /// <summary>
        /// Code page 1144, Italy with the euro sign.
        /// </summary>
        public static readonly CodePage Cp1144 = Add(1144, 695, "Italy (euro)", CodePageTables.Cp1144);

        /// <summary>
        /// Code page 1145, Spain and Latin America with the euro sign.
        /// </summary>
        public static readonly CodePage Cp1145 = Add(1145, 695, "Spain, Latin America (euro)", CodePageTables.Cp1145);

        /// <summary>
        /// Code page 1146, United Kingdom with the euro sign.
        /// </summary>
        public static readonly CodePage Cp1146 = Add(1146, 695, "United Kingdom (euro)", CodePageTables.Cp1146);

        /// <summary>
        /// Code page 1147, France with the euro sign.
        /// </summary>
        public static readonly CodePage Cp1147 = Add(1147, 695, "France (euro)", CodePageTables.Cp1147);

        /// <summary>
        /// Code page 1148, International with the euro sign.
        /// </summary>
        public static readonly CodePage Cp1148 = Add(1148, 695, "International (euro)", CodePageTables.Cp1148);

        /// <summary>
        /// Code page 1149, Iceland with the euro sign.
        /// </summary>
        public static readonly CodePage Cp1149 = Add(1149, 695, "Iceland (euro)", CodePageTables.Cp1149);

        /// <summary>
        /// Code page 310, the Graphic Escape set: APL symbols, box pieces, underscored letters.
        /// </summary>
        /// <remarks>
        /// Characters reach it by the GE order (GA23-0059-4 p. 4-9) or by character set X'F1'. It is
        /// not a base page and is not in <see cref="All"/>.
        /// </remarks>
        public static readonly CodePage Cp310 = new CodePage(310, 963, "Graphic Escape APL/TN", CodePageTables.Cp310, graphicEscape: true);

        private CodePage(int number, int characterSet, string name, ushort[] upper, bool graphicEscape)
        {
            Number = number;
            CharacterSet = characterSet;
            Name = name;
            _graphicEscape = graphicEscape;
            for (int i = 0; i < upper.Length; i++)
            {
                _toUnicode[0x40 + i] = (char)upper[i];
            }

            if (!graphicEscape)
            {
                // Stored controls shown as a blank (GA23-0059-4 p. 4-10). In code page 310 these
                // bytes cannot occur: GE refuses X'00'-X'3F' and X'FF' (p. 4-9).
                _toUnicode[FormatControl.Null] = ' ';
                _toUnicode[FormatControl.FormFeed] = ' ';
                _toUnicode[FormatControl.CarriageReturn] = ' ';
                _toUnicode[FormatControl.NewLine] = ' ';
                _toUnicode[FormatControl.EndOfMedium] = ' ';
                _toUnicode[0xFF] = ' ';
            }

            // Typing: X'40' to X'FE' only, the first byte for a character wins. X'FF' (EO) shows as a
            // blank but typing a space must give X'40'. The underscored letters of 310 are left out:
            // a typed "A" is the base page's A.
            for (int b = 0x40; b < 0xFF; b++)
            {
                char c = _toUnicode[b];
                if (c == '\0' || (graphicEscape && IsUnderscored((byte)b)))
                {
                    continue;
                }

                _fromUnicode.TryAdd(c, (byte)b);
            }
        }

        /// <summary>
        /// Gets every base code page, in the order they are listed above.
        /// </summary>
        public static IReadOnlyList<CodePage> All => s_all;

        /// <summary>
        /// Gets the code page number (CPGID), for example 277.
        /// </summary>
        public int Number { get; }

        /// <summary>
        /// Gets the character set number (GCSGID) IBM's registry gives as the page's maximal set:
        /// 697 for the base pages, 695 for the euro pages 1140 to 1149, 963 for 310.
        /// </summary>
        /// <remarks>
        /// With <see cref="Number"/> it makes the CGCSGID of the Character Sets query reply
        /// (GA23-0059-4 p. 6-28).
        /// </remarks>
        public int CharacterSet { get; }

        /// <summary>
        /// Gets the page's name in plain words, for a settings list.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Finds a base code page by its number.
        /// </summary>
        /// <param name="number">
        /// The code page number, for example 1142.
        /// </param>
        /// <param name="codePage">
        /// The code page, when there is one.
        /// </param>
        /// <returns>
        /// False when this package has no base page with that number.
        /// </returns>
        public static bool TryGet(int number, out CodePage codePage)
        {
            for (int i = 0; i < s_all.Count; i++)
            {
                if (s_all[i].Number == number)
                {
                    codePage = s_all[i];
                    return true;
                }
            }

            codePage = Cp037;
            return false;
        }

        /// <summary>
        /// The character to show for a buffer byte.
        /// </summary>
        /// <param name="ebcdic">
        /// The buffer byte.
        /// </param>
        /// <returns>
        /// The Unicode character, or '\0' when the byte has no character of its own (DUP, FM, SUB,
        /// the order range, a byte the page leaves undefined, or one the renderer draws).
        /// </returns>
        public char ToUnicode(byte ebcdic) => _toUnicode[ebcdic];

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
        /// False when the page has no such character.
        /// </returns>
        public bool TryFromUnicode(char c, out byte ebcdic) => _fromUnicode.TryGetValue(c, out ebcdic);

        /// <summary>
        /// True for a byte the renderer must draw with a line under it: the underscored letters
        /// X'41'-X'69' of code page 310, for which Unicode has no single character.
        /// </summary>
        /// <param name="ebcdic">
        /// The buffer byte.
        /// </param>
        /// <returns>
        /// True for an underscored letter of code page 310; always false on a base page.
        /// </returns>
        public bool IsUnderscored(byte ebcdic) => _graphicEscape && ebcdic >= 0x41 && ebcdic <= 0x69 && _toUnicode[ebcdic] >= 'A' && _toUnicode[ebcdic] <= 'Z';

        /// <summary>
        /// The page's number and name, for logs.
        /// </summary>
        /// <returns>
        /// For example "277 Denmark, Norway".
        /// </returns>
        public override string ToString() => Number + " " + Name;

        private static CodePage Add(int number, int characterSet, string name, ushort[] upper)
        {
            CodePage page = new CodePage(number, characterSet, name, upper, graphicEscape: false);
            s_all.Add(page);
            return page;
        }
    }
}
