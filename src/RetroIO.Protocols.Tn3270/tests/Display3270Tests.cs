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
    public class Display3270Tests
    {
        private static (Display3270 Display, List<byte[]> Sent, List<string> Errors) Create()
        {
            var display = new Display3270();
            var sent = new List<byte[]>();
            var errors = new List<string>();
            display.InboundRecord += record => sent.Add(record.ToArray());
            display.ProtocolError += errors.Add;
            return (display, sent, errors);
        }

        // EW, WCC restore+resetMDT, SBA(0) SF(protected) "NAME", SF(unprotected) IC,
        // SBA(20) SF(protected autoskip). Field 1 is the protected label at 0, the input field's
        // attribute is at 5, input runs 6..19, a stop attribute is at 20.
        private static readonly byte[] LogonScreen =
        {
            Command3270.EraseWrite, 0x03,
            Order3270.SetBufferAddress, 0x40, 0x40,
            Order3270.StartField, 0x60, 0xD5, 0xC1, 0xD4, 0xC5, // protected, "NAME"
            Order3270.StartField, 0x40,                         // unprotected
            Order3270.InsertCursor,
            Order3270.SetBufferAddress, 0x40, 0xD4,             // address 20
            Order3270.StartField, 0xF0,                         // protected + numeric = autoskip
        };

        [Fact]
        public void TheMilestoneTypeIntoAFieldAndPressEnterGivesTheRightReply()
        {
            var (d, sent, errors) = Create();

            d.ProcessOutbound(LogonScreen);

            Assert.Empty(errors);
            Assert.Equal(KeyboardLock.None, d.Lock);
            Assert.Equal(6, d.CursorAddress);
            Assert.True(d.IsFieldAttribute(0));
            Assert.True(d.IsFieldAttribute(5));
            Assert.Equal(0xD5, d.GetByte(1)); // 'N'

            Assert.True(d.Type('h'));
            Assert.True(d.Type('i'));
            Assert.Equal(8, d.CursorAddress);

            Assert.True(d.Attention(Aid.Enter));

            // AID, cursor 8 = 0x40 0xC8, SBA to 6 = 0x11 0x40 0xC6, "hi" = 0x88 0x89. Nulls after
            // the typed text are left out (GA23-0059-4 p. 3-13).
            Assert.Single(sent);
            Assert.Equal(new byte[] { 0x7D, 0x40, 0xC8, 0x11, 0x40, 0xC6, 0x88, 0x89 }, sent[0]);
            Assert.Equal(KeyboardLock.WaitingForHost, d.Lock);
        }

        [Fact]
        public void TypingIntoAProtectedFieldLocksUntilReset()
        {
            var (d, sent, _) = Create();
            d.ProcessOutbound(LogonScreen);

            d.MoveCursor(2);
            Assert.False(d.Type('x'));
            Assert.Equal(KeyboardLock.OperatorError, d.Lock);
            Assert.Equal(0xC1, d.GetByte(2)); // unchanged 'A'

            d.Reset();
            Assert.Equal(KeyboardLock.None, d.Lock);
            Assert.Empty(sent);
        }

        [Fact]
        public void ResetDoesNotUnlockAKeyboardWaitingForTheHost()
        {
            var (d, _, _) = Create();
            d.ProcessOutbound(LogonScreen);
            d.Attention(Aid.Enter);

            d.Reset();

            Assert.Equal(KeyboardLock.WaitingForHost, d.Lock);
            Assert.False(d.Type('x'));

            // A Write with keyboard restore unlocks it.
            d.ProcessOutbound(new byte[] { Command3270.Write, Wcc.KeyboardRestore });
            Assert.Equal(KeyboardLock.None, d.Lock);
        }

        [Fact]
        public void ThePaKeysAndClearSendTheAidAlone()
        {
            var (d, sent, _) = Create();
            d.ProcessOutbound(LogonScreen);
            d.Type('x');

            d.Attention(Aid.PA1);

            Assert.Equal(new byte[] { 0x6C }, sent[0]);
        }

        [Fact]
        public void ClearEmptiesTheScreenAndSendsTheAidAlone()
        {
            var (d, sent, _) = Create();
            d.ProcessOutbound(LogonScreen);

            d.Attention(Aid.Clear);

            Assert.Equal(new byte[] { 0x6D }, sent[0]);
            Assert.False(d.IsFormatted);
            Assert.Equal(0, d.CursorAddress);
        }

        [Fact]
        public void TypingTheLastPositionSkipsTheAutoskipFieldToTheNextInputField()
        {
            var (d, _, _) = Create();
            d.ProcessOutbound(LogonScreen);
            d.MoveCursor(19);

            d.Type('z');

            // Past the autoskip at 20 the next unprotected field is the one at 5 (wrapping), so 6.
            Assert.Equal(6, d.CursorAddress);
        }

        [Fact]
        public void TabGoesToTheNextInputFieldAndHomeToTheFirst()
        {
            var (d, _, _) = Create();
            d.ProcessOutbound(LogonScreen);
            d.MoveCursor(100);

            d.Tab();
            Assert.Equal(6, d.CursorAddress);

            d.MoveCursor(1000);
            d.Home();
            Assert.Equal(6, d.CursorAddress);
        }

        [Fact]
        public void ResetMdtInTheWccClearsTheTypedFlagSoTheFieldIsNotSentAgain()
        {
            var (d, sent, _) = Create();
            d.ProcessOutbound(LogonScreen);
            d.Type('a');
            d.Attention(Aid.Enter);

            // W with restore and reset MDT.
            d.ProcessOutbound(new byte[] { Command3270.Write, 0x03 });
            d.Attention(Aid.Enter);

            Assert.Equal(new byte[] { 0x7D, 0x40, 0xC7 }, sent[1]);
        }

        [Fact]
        public void RepeatToAddressFillsUpToButNotIncludingTheStopAddress()
        {
            var (d, _, errors) = Create();

            // EW, WCC, RA to 10 with 'A'.
            d.ProcessOutbound(new byte[] { Command3270.EraseWrite, 0x02, Order3270.RepeatToAddress, 0x40, 0x4A, 0xC1 });

            Assert.Empty(errors);
            Assert.Equal(0xC1, d.GetByte(0));
            Assert.Equal(0xC1, d.GetByte(9));
            Assert.Equal(0x00, d.GetByte(10));
        }

        [Fact]
        public void EraseUnprotectedToAddressLeavesProtectedTextAlone()
        {
            var (d, _, _) = Create();
            d.ProcessOutbound(LogonScreen);
            d.Type('q');

            // W, WCC, SBA 0, EUA to 20.
            d.ProcessOutbound(new byte[] { Command3270.Write, 0x00, Order3270.SetBufferAddress, 0x40, 0x40, Order3270.EraseUnprotectedToAddress, 0x40, 0xD4 });

            Assert.Equal(0xD5, d.GetByte(1));
            Assert.Equal(0x00, d.GetByte(6));
        }

        [Fact]
        public void AnAddressOutsideTheScreenIsReportedAndNotSkipped()
        {
            var (d, _, errors) = Create();

            // 14-bit address 2000 on a 1920-cell screen.
            d.ProcessOutbound(new byte[] { Command3270.EraseWrite, 0x00, Order3270.SetBufferAddress, 0x07, 0xD0, 0xC1 });

            Assert.Single(errors);
        }

        [Fact]
        public void WriteStructuredFieldIsReportedAsNotBuilt()
        {
            var (d, _, errors) = Create();

            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x05, 0x01, 0xFF, 0x02 });

            Assert.Single(errors);
        }

        [Fact]
        public void ReadBufferSendsEveryPositionWithStartFieldBeforeEachAttribute()
        {
            var (d, sent, _) = Create();
            d.ProcessOutbound(LogonScreen);

            d.ProcessOutbound(new byte[] { Command3270.ReadBuffer });

            byte[] r = sent[0];
            Assert.Equal(Aid.None, r[0]);
            Assert.Equal(new byte[] { 0x40, 0xC6 }, r.AsSpan(1, 2).ToArray()); // cursor 6

            // Position 0 is the protected attribute X'20', sent as SF + X'60' (Figure D-1).
            Assert.Equal(Order3270.StartField, r[3]);
            Assert.Equal(0x60, r[4]);
            Assert.Equal(0xD5, r[5]);

            // 1920 positions, 3 field attributes each one byte longer.
            Assert.Equal(3 + 1920 + 3, r.Length);
        }
    }
}
