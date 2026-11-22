//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Globalization;
using System.Text;

using Xunit;

namespace RetroIO.Protocols.Tn3270.Tests
{
    public class CodePageTests
    {
        [Theory]
        [InlineData(37, 37)]
        [InlineData(273, 20273)]
        [InlineData(277, 20277)]
        [InlineData(278, 20278)]
        [InlineData(280, 20280)]
        [InlineData(284, 20284)]
        [InlineData(285, 20285)]
        [InlineData(297, 20297)]
        [InlineData(500, 500)]
        [InlineData(871, 20871)]
        [InlineData(1047, 1047)]
        [InlineData(1140, 1140)]
        [InlineData(1141, 1141)]
        [InlineData(1142, 1142)]
        [InlineData(1143, 1143)]
        [InlineData(1144, 1144)]
        [InlineData(1145, 1145)]
        [InlineData(1146, 1146)]
        [InlineData(1147, 1147)]
        [InlineData(1148, 1148)]
        [InlineData(1149, 1149)]
        public void EveryPageMatchesDotNetsOwnTable(int page, int windowsCodePage)
        {
            // A second source next to the ICU files the tables were made from. Every difference is
            // listed, so a failure shows all of them at once.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding dotNet = Encoding.GetEncoding(windowsCodePage);
            Assert.True(CodePage.TryGet(page, out CodePage codePage));

            StringBuilder differences = new StringBuilder();
            byte[] one = new byte[1];
            for (int b = 0x41; b <= 0xFE; b++)
            {
                one[0] = (byte)b;
                char expected = dotNet.GetString(one)[0];
                char actual = codePage.ToUnicode((byte)b);
                if (expected != actual)
                {
                    differences.Append(" X'").Append(b.ToString("X2", CultureInfo.InvariantCulture))
                        .Append("' .NET U+").Append(((int)expected).ToString("X4", CultureInfo.InvariantCulture))
                        .Append(" ours U+").Append(((int)actual).ToString("X4", CultureInfo.InvariantCulture));
                }
            }

            Assert.True(differences.Length == 0, "Code page " + page + ":" + differences);
        }

        [Fact]
        public void TypingUsesTheChosenPage()
        {
            // The euro sign is X'5A' on 1142 (Denmark, Norway) and X'9F' on 1140 (USA).
            Assert.True(CodePage.Cp1142.TryFromUnicode('€', out byte norway));
            Assert.Equal(0x5A, norway);
            Assert.True(CodePage.Cp1140.TryFromUnicode('€', out byte usa));
            Assert.Equal(0x9F, usa);
            Assert.False(CodePage.Cp037.TryFromUnicode('€', out _));

            var d = new Display3270();
            d.CodePage = CodePage.Cp1142;
            d.ProcessOutbound(new byte[] { Command3270.EraseWrite, 0x02 });
            Assert.True(d.Type('€'));
            Assert.Equal(0x5A, d.GetByte(0));
        }

        [Fact]
        public void Page310DefinesExactlyTheRegistryBytesAndMarksTheUnderscoredLetters()
        {
            // 137 bytes with a character, plus X'81' and X'82' which the renderer draws (checked
            // against IBM's CP00310.txt when the table was made).
            int defined = 0;
            for (int b = 0x40; b <= 0xFE; b++)
            {
                if (CodePage.Cp310.ToUnicode((byte)b) != '\0')
                {
                    defined++;
                }
            }

            Assert.Equal(137, defined);
            Assert.Equal('\0', CodePage.Cp310.ToUnicode(0x81));
            Assert.Equal('┌', CodePage.Cp310.ToUnicode(0xC5));
            Assert.True(CodePage.Cp310.IsUnderscored(0x41));
            Assert.Equal('A', CodePage.Cp310.ToUnicode(0x41));
            Assert.False(CodePage.Cp037.IsUnderscored(0xC1));
            Assert.Equal(963, CodePage.Cp310.CharacterSet);
        }

        [Fact]
        public void Page310CannotBeTheBasePage()
        {
            var d = new Display3270();
            Assert.Throws<System.ArgumentException>(() => d.CodePage = CodePage.Cp310);
        }
    }
}
