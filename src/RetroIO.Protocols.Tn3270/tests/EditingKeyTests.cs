//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Xunit;

namespace RetroIO.Protocols.Tn3270.Tests
{
    // The rules are GA23-0061-2 Appendix C, "Key Functions" (p. C-3 to C-8).
    public class EditingKeyTests
    {
        // EW restore; protected label at 0 "AB"; input field attribute at 3, input 4..9 holding
        // "XYZ" then nulls; protected stop attribute at 10.
        private static Display3270 Screen()
        {
            var d = new Display3270();
            d.ProcessOutbound(new byte[]
            {
                Command3270.EraseWrite, 0x02,
                Order3270.StartField, 0x60, 0xC1, 0xC2,
                Order3270.StartField, 0x40, Order3270.InsertCursor, 0xE7, 0xE8, 0xE9,
                Order3270.SetBufferAddress, 0x40, 0x4A, Order3270.StartField, 0x60,
            });
            return d;
        }

        private static string Field(Display3270 d)
        {
            char[] c = new char[6];
            for (int i = 0; i < 6; i++)
            {
                byte b = d.GetByte(4 + i);
                c[i] = b == 0 ? '.' : CodePage.Cp037.ToUnicode(b);
            }

            return new string(c);
        }

        [Fact]
        public void InsertShiftsUpToTheFirstNullAndSetsTheMdt()
        {
            Display3270 d = Screen();
            Assert.Equal(4, d.CursorAddress);

            d.ToggleInsert();
            Assert.True(d.Type('Q'));

            Assert.Equal("QXYZ..", Field(d));
            Assert.Equal(5, d.CursorAddress);
            Assert.Equal(FieldAttribute.Mdt, d.GetByte(3) & FieldAttribute.Mdt);
        }

        [Fact]
        public void InsertWithNoNullLeftIsTooMuchDataAndResetEndsIt()
        {
            Display3270 d = Screen();
            d.ToggleInsert();
            Assert.True(d.Type('1'));
            Assert.True(d.Type('2'));
            d.MoveCursor(4);
            Assert.True(d.Type('3'));
            Assert.Equal("312XYZ", Field(d));

            d.MoveCursor(4);
            Assert.False(d.Type('4'));
            Assert.Equal(KeyboardLock.TooMuchData, d.Lock);
            Assert.Equal("312XYZ", Field(d));

            d.Reset();
            Assert.Equal(KeyboardLock.None, d.Lock);
            Assert.False(d.InsertMode);
        }

        [Fact]
        public void InsertOnANullDoesNotShift()
        {
            Display3270 d = Screen();
            d.MoveCursor(8);
            d.ToggleInsert();

            Assert.True(d.Type('Q'));

            Assert.Equal("XYZ.Q.", Field(d));
        }

        [Fact]
        public void DeleteClosesTheGapAndLeavesTheCursor()
        {
            Display3270 d = Screen();
            d.MoveCursor(5);

            Assert.True(d.Delete());

            Assert.Equal("XZ....", Field(d));
            Assert.Equal(5, d.CursorAddress);
            Assert.Equal(FieldAttribute.Mdt, d.GetByte(3) & FieldAttribute.Mdt);
        }

        [Fact]
        public void EraseEofNullsToTheEndOfTheFieldOnly()
        {
            Display3270 d = Screen();
            d.MoveCursor(5);

            Assert.True(d.EraseEndOfField());

            Assert.Equal("X.....", Field(d));
            Assert.Equal(5, d.CursorAddress);
            Assert.True(d.IsFieldAttribute(10));
            Assert.Equal(FieldAttribute.Mdt, d.GetByte(3) & FieldAttribute.Mdt);
        }

        [Fact]
        public void KeysInAProtectedFieldLockTheKeyboard()
        {
            Display3270 d = Screen();
            d.MoveCursor(1);

            Assert.False(d.EraseEndOfField());
            Assert.Equal(KeyboardLock.OperatorError, d.Lock);
            Assert.Equal(0xC1, d.GetByte(1));

            d.Reset();
            Assert.False(d.Delete());
            d.Reset();
            Assert.False(d.Dup());
            d.Reset();
            Assert.False(d.FieldMark());
        }

        [Fact]
        public void EraseInputNullsEveryInputFieldResetsItsMdtAndHomesTheCursor()
        {
            Display3270 d = Screen();
            Assert.True(d.Type('Q'));
            d.MoveCursor(15);

            Assert.True(d.EraseInput());

            Assert.Equal("......", Field(d));
            Assert.Equal(0, d.GetByte(3) & FieldAttribute.Mdt);
            Assert.Equal(0xC1, d.GetByte(1));
            Assert.Equal(4, d.CursorAddress);
        }

        [Fact]
        public void EraseInputOnAnUnformattedScreenErasesEverything()
        {
            var d = new Display3270();
            d.ProcessOutbound(new byte[] { Command3270.EraseWrite, 0x02, 0xC1, 0xC2 });
            d.MoveCursor(5);

            Assert.True(d.EraseInput());

            Assert.Equal(0, d.GetByte(0));
            Assert.Equal(0, d.CursorAddress);
        }

        [Fact]
        public void DupStoresItsCodeAndTabsFieldMarkStepsOn()
        {
            Display3270 d = Screen();
            d.MoveCursor(7);

            Assert.True(d.FieldMark());
            Assert.Equal(FormatControl.FieldMark, d.GetByte(7));
            Assert.Equal(8, d.CursorAddress);

            Assert.True(d.Dup());
            Assert.Equal(FormatControl.Dup, d.GetByte(8));

            // The only input field is this one, so Tab comes back to its start.
            Assert.Equal(4, d.CursorAddress);
        }

        [Fact]
        public void AnAttentionKeyEndsInsertMode()
        {
            Display3270 d = Screen();
            d.ToggleInsert();

            d.Attention(Aid.Enter);

            Assert.False(d.InsertMode);
        }

        [Fact]
        public void ClearGoesBackToTheDefaultSize()
        {
            var d = new Display3270(24, 80, 32, 80);
            d.ProcessOutbound(new byte[] { Command3270.EraseWriteAlternate, 0x02 });
            Assert.Equal(32, d.Rows);

            d.Attention(Aid.Clear);

            Assert.Equal(24, d.Rows);
        }

        [Fact]
        public void BackTabGoesToTheFieldStartThenToThePreviousField()
        {
            var d = new Display3270();

            // Two input fields: attribute at 0 (input 1..9), attribute at 10 (input 11..).
            d.ProcessOutbound(new byte[]
            {
                Command3270.EraseWrite, 0x02,
                Order3270.StartField, 0x40,
                Order3270.SetBufferAddress, 0x40, 0x4A, Order3270.StartField, 0x40,
            });
            d.MoveCursor(14);

            d.BackTab();
            Assert.Equal(11, d.CursorAddress);

            d.BackTab();
            Assert.Equal(1, d.CursorAddress);
        }

        [Fact]
        public void NewLineFindsTheFirstInputPositionOnALaterLine()
        {
            var d = new Display3270();

            // Input field from address 161 (row 2, column 1).
            d.ProcessOutbound(new byte[]
            {
                Command3270.EraseWrite, 0x02,
                Order3270.StartField, 0x60,
                Order3270.SetBufferAddress, 0xC2, 0x60, Order3270.StartField, 0x40,
            });

            d.NewLine();

            Assert.Equal(161, d.CursorAddress);
        }

        [Fact]
        public void ArrowsWrapAtTheEdges()
        {
            var d = new Display3270();
            d.ProcessOutbound(new byte[] { Command3270.EraseWrite, 0x02 });

            d.MoveCursorBy(-1, 0);
            Assert.Equal(1919, d.CursorAddress);

            d.MoveCursorBy(0, 1);
            Assert.Equal(79, d.CursorAddress);

            d.MoveCursorBy(1, 0);
            Assert.Equal(80, d.CursorAddress);
        }

        [Fact]
        public void EraseMovesLeftAndDeletesThatCharacter()
        {
            // "XYZ" at 4-6, cursor after the Y: erase takes the Y and pulls the Z left.
            Display3270 d = Screen();
            d.MoveCursor(6);

            Assert.True(d.Erase());

            Assert.Equal("XZ....", Field(d));
            Assert.Equal(5, d.CursorAddress);
            Assert.Equal(FieldAttribute.Mdt, d.GetByte(3) & FieldAttribute.Mdt);
        }

        [Fact]
        public void EraseAtTheFirstPositionOfAFieldDoesNothing()
        {
            // x3270 do_erase: when the position to the left is the field attribute, nothing.
            Display3270 d = Screen();

            Assert.True(d.Erase());

            Assert.Equal("XYZ...", Field(d));
            Assert.Equal(4, d.CursorAddress);
            Assert.Equal(KeyboardLock.None, d.Lock);
            Assert.Equal(0, d.GetByte(3) & FieldAttribute.Mdt);
        }

        [Fact]
        public void EraseInAProtectedFieldIsAnOperatorError()
        {
            Display3270 d = Screen();
            d.MoveCursor(2);

            Assert.False(d.Erase());

            Assert.Equal(KeyboardLock.OperatorError, d.Lock);
        }

        [Fact]
        public void ARefusedFunctionLocksWithMinusFunctionUntilReset()
        {
            Display3270 d = Screen();

            d.RefuseFunction();

            Assert.Equal(KeyboardLock.MinusFunction, d.Lock);
            Assert.Equal("X -f", StatusLine3270.Message(d, StatusLineConnection.Connected));
            Assert.False(d.Type('Q'));
            Assert.Equal("XYZ...", Field(d));

            d.Reset();

            Assert.Equal(KeyboardLock.None, d.Lock);
            Assert.True(d.Type('Q'));
        }
    }
}