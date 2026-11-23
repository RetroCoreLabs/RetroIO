//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using Xunit;

namespace RetroIO.Protocols.Tn3270.Tests
{
    /// <summary>
    /// Field End, Sys Req and Cursor Select: the three keys taken from x3270 (Common\kybd.c) and
    /// GA23-0059-4 (p. 4-22 to 4-24 for the selector pen, p. 9-3 for the test request read).
    /// </summary>
    public class Display3270KeyTests
    {
        // EW, WCC restore + reset MDT, SBA 0, SF protected "NAME", SF unprotected IC, SBA 20,
        // SF autoskip. The input field's attribute is at 5, its data runs 6 to 19.
        private static readonly byte[] LogonScreen =
        {
            Command3270.EraseWrite, 0x03,
            Order3270.SetBufferAddress, 0x40, 0x40,
            Order3270.StartField, 0x60, 0xD5, 0xC1, 0xD4, 0xC5,
            Order3270.StartField, 0x40,
            Order3270.InsertCursor,
            Order3270.SetBufferAddress, 0x40, 0xD4,
            Order3270.StartField, 0xF0,
        };

        // Five protected fields. X'E4' is protected with display bits 01 (detectable), X'60' is
        // protected and not detectable (GA23-0059-4 p. 4-22). Designators:
        //  - field at 0: "?" then "ABC".
        //  - field at 10: a space then "X".
        //  - field at 20: an ampersand.
        //  - field at 30: not detectable, "Z".
        //  - field at 40: detectable, "Q", which is not a designator.
        private static readonly byte[] PenScreen =
        {
            Command3270.EraseWrite, 0x03,
            Order3270.SetBufferAddress, 0x40, 0x40, Order3270.StartField, 0xE4, 0x6F, 0xC1, 0xC2, 0xC3,
            Order3270.SetBufferAddress, 0x40, 0x4A, Order3270.StartField, 0xE4, 0x40, 0xE7,
            Order3270.SetBufferAddress, 0x40, 0xD4, Order3270.StartField, 0xE4, 0x50,
            Order3270.SetBufferAddress, 0x40, 0x5E, Order3270.StartField, 0x60, 0xE9,
            Order3270.SetBufferAddress, 0x40, 0xE8, Order3270.StartField, 0xE4, 0xD8,
        };

        private static (Display3270 Display, List<byte[]> Sent) Create(byte[] screen)
        {
            var display = new Display3270();
            var sent = new List<byte[]>();
            display.InboundRecord += record => sent.Add(record.ToArray());
            display.ProcessOutbound(screen);
            return (display, sent);
        }

        [Fact]
        public void FieldEndGoesJustAfterTheLastCharacterTyped()
        {
            var (d, _) = Create(LogonScreen);
            Assert.True(d.Type('h'));
            Assert.True(d.Type('i'));
            d.Home();
            Assert.Equal(6, d.CursorAddress);

            d.FieldEnd();

            Assert.Equal(8, d.CursorAddress);
        }

        [Fact]
        public void FieldEndInAnEmptyFieldGoesToItsFirstPosition()
        {
            var (d, _) = Create(LogonScreen);
            d.MoveCursor(12);

            d.FieldEnd();

            Assert.Equal(6, d.CursorAddress);
        }

        [Fact]
        public void FieldEndInAFullFieldStaysOnTheLastCharacter()
        {
            // x3270 FieldEnd_action: when the position after the last character is the next
            // attribute, the cursor goes on the last character instead.
            var (d, _) = Create(LogonScreen);
            for (int i = 0; i < 14; i++)
            {
                Assert.True(d.Type('a'));
            }

            d.Home();
            d.FieldEnd();

            Assert.Equal(19, d.CursorAddress);
        }

        [Fact]
        public void FieldEndDoesNothingInAProtectedField()
        {
            var (d, _) = Create(LogonScreen);
            d.MoveCursor(2);

            d.FieldEnd();

            Assert.Equal(2, d.CursorAddress);
            Assert.Equal(KeyboardLock.None, d.Lock);
        }

        [Fact]
        public void SysReqSendsTheTestRequestHeadingThenTheModifiedFields()
        {
            // SOH % / STX (GA23-0059-4 p. 9-3), no AID and no cursor address, then SBA 6 "hi".
            var (d, sent) = Create(LogonScreen);
            Assert.True(d.Type('h'));
            Assert.True(d.Type('i'));

            Assert.True(d.SysReq());

            Assert.Single(sent);
            Assert.Equal(new byte[] { 0x01, 0x6C, 0x61, 0x02, 0x11, 0x40, 0xC6, 0x88, 0x89 }, sent[0]);
            Assert.Equal(KeyboardLock.WaitingForHost, d.Lock);
        }

        [Fact]
        public void CursorSelectOnAQuestionMarkSelectsAndASecondPressDeselects()
        {
            var (d, sent) = Create(PenScreen);
            d.MoveCursor(3);

            Assert.True(d.CursorSelect());
            Assert.Equal(0x6E, d.GetByte(1));       // ">"
            Assert.Equal(1, d.GetByte(0) & 0x01);   // MDT set

            Assert.True(d.CursorSelect());
            Assert.Equal(0x6F, d.GetByte(1));       // "?" again
            Assert.Equal(0, d.GetByte(0) & 0x01);   // MDT cleared

            Assert.Empty(sent);
            Assert.Equal(KeyboardLock.None, d.Lock);
        }

        [Fact]
        public void CursorSelectOnASpaceSendsASelectorPenAttentionWithAddressesButNoData()
        {
            // AID X'7E', cursor 11 (X'40' X'4B'), then SBA to 11 for the one modified field and
            // none of its data (p. 3-12).
            var (d, sent) = Create(PenScreen);
            d.MoveCursor(11);

            Assert.True(d.CursorSelect());

            Assert.Single(sent);
            Assert.Equal(new byte[] { 0x7E, 0x40, 0x4B, 0x11, 0x40, 0x4B }, sent[0]);
            Assert.Equal(KeyboardLock.WaitingForHost, d.Lock);
        }

        [Fact]
        public void CursorSelectOnAnAmpersandSendsEnter()
        {
            var (d, sent) = Create(PenScreen);
            d.MoveCursor(21);

            Assert.True(d.CursorSelect());

            Assert.Single(sent);
            Assert.Equal(Aid.Enter, sent[0][0]);
        }

        [Fact]
        public void CursorSelectOutsideADetectableFieldLocksTheKeyboard()
        {
            // GA27-2890-4 p. 2-22: Go Elsewhere for a cursor select outside a valid field.
            var (d, sent) = Create(PenScreen);
            d.MoveCursor(31);
            Assert.False(d.CursorSelect());
            Assert.Equal(KeyboardLock.OperatorError, d.Lock);

            d.Reset();
            d.MoveCursor(41);
            Assert.False(d.CursorSelect());
            Assert.Equal(KeyboardLock.OperatorError, d.Lock);
            Assert.Empty(sent);
        }
    }
}
