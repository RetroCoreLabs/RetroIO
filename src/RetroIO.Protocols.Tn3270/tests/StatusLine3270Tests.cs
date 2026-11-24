//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using Xunit;

namespace RetroIO.Protocols.Tn3270.Tests
{
    /// <summary>
    /// Line 25 against the layout wc3270 draws (x3270 source, wc3270\screen.c, draw_oia): on an
    /// 80-column screen rmargin is 79, so the message starts at 7, insert is at 48, the LU name at
    /// 54 and the cursor at 72.
    /// </summary>
    public class StatusLine3270Tests
    {
        // EW with keyboard restore, SBA(0) SF(unprotected) IC: one input field from address 1.
        private static readonly byte[] OneInputField =
        {
            Command3270.EraseWrite, 0x02,
            Order3270.SetBufferAddress, 0x40, 0x40,
            Order3270.StartField, 0x40,
            Order3270.InsertCursor,
        };

        private static string Line(Display3270 d, StatusLineConnection connection, string? lu = null)
        {
            Span<char> line = stackalloc char[80];
            StatusLine3270.Build(d, connection, lu, line);
            return new string(line);
        }

        private static string Expected(string box, string message, string cursor, bool insert = false, string lu = "")
        {
            char[] line = new string(' ', 80).ToCharArray();
            line[0] = '4';
            line[1] = 'A';
            line[2] = box[0];
            message.CopyTo(0, line, 7, message.Length);
            if (insert)
            {
                line[48] = 'I';
            }

            lu.CopyTo(0, line, 54, lu.Length);
            cursor.CopyTo(0, line, 72, cursor.Length);
            return new string(line);
        }

        [Fact]
        public void UnderTn3270eColumnOneIsBAndTheLuNameIsShown()
        {
            // wc3270 draw_oia: addch(IN_E? 'B': 'A'), and the LU at rmargin-25.
            var d = new Display3270();
            d.ProcessOutbound(OneInputField);
            Span<char> line = stackalloc char[80];

            StatusLine3270.Build(d, StatusLineConnection.Connected, "TCP00034", tn3270e: true, line);

            char[] expected = Expected(" ", string.Empty, "001/002", lu: "TCP00034").ToCharArray();
            expected[1] = 'B';
            Assert.Equal(new string(expected), new string(line));
        }

        [Fact]
        public void WhileTheSscpOwnsTheScreenColumnTwoShowsTheSystemOperatorSymbol()
        {
            // GA23-0061-2 p. 5-35: when ownership passes to the SSCP-SLU session "the System
            // Operator symbol appears in column 3"; Appendix B p. B-2 draws it.
            var d = new Display3270();
            Assert.True(d.ProcessSscpLuData(new byte[] { 0xC9, 0xD2, 0xD1 }));
            Span<char> line = stackalloc char[80];

            StatusLine3270.Build(d, StatusLineConnection.Connected, null, tn3270e: true, line);

            Assert.Equal(StatusLine3270.SystemOperatorSymbol, line[StatusLine3270.BoxColumn]);

            // 3270 data gives the screen back to the application: My Job, the solid box.
            d.LeaveSscpLuMode();
            StatusLine3270.Build(d, StatusLineConnection.Connected, null, tn3270e: true, line);
            Assert.Equal(' ', line[StatusLine3270.BoxColumn]);
        }

        [Fact]
        public void AnUnlockedConnectedScreenShowsTheReadySymbolsAndTheCursor()
        {
            var d = new Display3270();
            d.ProcessOutbound(OneInputField);

            // Cursor at address 1: row 1, column 2.
            Assert.Equal(Expected(" ", string.Empty, "001/002"), Line(d, StatusLineConnection.Connected));
        }

        [Fact]
        public void EnterShowsXWaitThenAWriteWithoutRestoreShowsXSystem()
        {
            var d = new Display3270();
            d.ProcessOutbound(OneInputField);

            d.Attention(Aid.Enter);
            Assert.Equal("X Wait", StatusLine3270.Message(d, StatusLineConnection.Connected));

            d.ProcessOutbound(new byte[] { Command3270.Write, Wcc.ResetMdt });
            Assert.Equal("X SYSTEM", StatusLine3270.Message(d, StatusLineConnection.Connected));
            Assert.Equal(Expected(" ", "X SYSTEM", "001/002"), Line(d, StatusLineConnection.Connected));
        }

        [Fact]
        public void TypingOnAProtectedPositionShowsXProtected()
        {
            var d = new Display3270();
            d.ProcessOutbound(OneInputField);
            d.MoveCursor(0); // the field attribute itself

            Assert.False(d.Type('x'));

            Assert.Equal("X Protected", StatusLine3270.Message(d, StatusLineConnection.Connected));
        }

        [Fact]
        public void InsertModeShowsIInColumn48()
        {
            var d = new Display3270();
            d.ProcessOutbound(OneInputField);
            d.ToggleInsert();

            Assert.Equal(Expected(" ", string.Empty, "001/002", insert: true), Line(d, StatusLineConnection.Connected));
        }

        [Fact]
        public void NotConnectedShowsAQuestionMarkBoxAndNoCursor()
        {
            var d = new Display3270();

            Assert.Equal(Expected("?", "X Not Connected", string.Empty), Line(d, StatusLineConnection.NotConnected));
            Assert.Equal(Expected("?", "X [TCP]", string.Empty), Line(d, StatusLineConnection.Connecting));
        }

        [Fact]
        public void TheLuNameIsShownAtColumn54CutToEightCharacters()
        {
            var d = new Display3270();
            d.ProcessOutbound(OneInputField);

            Assert.Equal(Expected(" ", string.Empty, "001/002", lu: "TCP00001"), Line(d, StatusLineConnection.Connected, "TCP000012345"));
        }
    }
}
