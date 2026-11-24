//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;

using Xunit;

namespace RetroIO.Protocols.Tn3270.Tests
{
    // GA23-0059-4 p. 4-21 to 4-23.
    public class FieldValidationTests
    {
        // EW restore; SFE at 0: unprotected, validation as given; input 1..4; SFE at 5:
        // unprotected, no validation; input 6..; protected stop at 20.
        private static (Display3270 Display, List<byte[]> Sent, List<string> Errors) Screen(byte validation)
        {
            var d = new Display3270();
            var sent = new List<byte[]>();
            var errors = new List<string>();
            d.InboundRecord += r => sent.Add(r.ToArray());
            d.ProtocolError += errors.Add;
            d.ProcessOutbound(new byte[]
            {
                Command3270.EraseWrite, 0x02,
                Order3270.StartFieldExtended, 0x02, 0xC0, 0x40, 0xC1, validation,
                Order3270.InsertCursor,
                Order3270.SetBufferAddress, 0x40, 0x45, Order3270.StartField, 0x40,
                Order3270.SetBufferAddress, 0x40, 0xD4, Order3270.StartField, 0x60,
            });
            return (d, sent, errors);
        }

        [Fact]
        public void APartlyFilledMandatoryFillFieldKeepsTheCursor()
        {
            var (d, _, errors) = Screen(FieldValidation.MandatoryFill);
            Assert.Empty(errors);
            Assert.Equal(FieldValidation.MandatoryFill, d.GetFieldValidation(0));
            Assert.Equal(1, d.CursorAddress);

            Assert.True(d.Type('A'));
            d.Tab();

            Assert.Equal(KeyboardLock.MandatoryFill, d.Lock);
            Assert.Equal(2, d.CursorAddress);

            d.Reset();
            Assert.True(d.Type('B'));
            Assert.True(d.Type('C'));
            Assert.True(d.Type('D'));

            // The field is full; the last character moves the cursor out and validation passes.
            Assert.Equal(KeyboardLock.None, d.Lock);
            Assert.Equal(6, d.CursorAddress);
        }

        [Fact]
        public void AnUntouchedMandatoryFillFieldCanBeLeft()
        {
            var (d, _, _) = Screen(FieldValidation.MandatoryFill);

            d.Tab();

            Assert.Equal(KeyboardLock.None, d.Lock);
            Assert.Equal(6, d.CursorAddress);
        }

        [Fact]
        public void DupSatisfiesMandatoryFill()
        {
            var (d, _, _) = Screen(FieldValidation.MandatoryFill);

            Assert.True(d.Type('A'));
            Assert.True(d.Dup());

            Assert.Equal(KeyboardLock.None, d.Lock);
            Assert.Equal(6, d.CursorAddress);
        }

        [Fact]
        public void EnterInAPrimedFillFieldIsHeldBack()
        {
            var (d, sent, _) = Screen(FieldValidation.MandatoryFill);
            Assert.True(d.Type('A'));

            Assert.False(d.Attention(Aid.Enter));

            Assert.Empty(sent);
            Assert.Equal(KeyboardLock.MandatoryFill, d.Lock);
        }

        [Fact]
        public void AWriteUnprimesTheFillField()
        {
            var (d, _, _) = Screen(FieldValidation.MandatoryFill);
            Assert.True(d.Type('A'));

            d.ProcessOutbound(new byte[] { Command3270.Write, 0x02 });
            d.Tab();

            Assert.Equal(KeyboardLock.None, d.Lock);
            Assert.Equal(6, d.CursorAddress);
        }

        [Fact]
        public void AnEmptyMandatoryEntryFieldStopsEnterAndGetsTheCursor()
        {
            var (d, sent, _) = Screen(FieldValidation.MandatoryEntry);
            d.MoveCursor(8);

            Assert.False(d.Attention(Aid.Enter));

            Assert.Empty(sent);
            Assert.Equal(KeyboardLock.MandatoryEntry, d.Lock);
            Assert.Equal(1, d.CursorAddress);

            d.Reset();
            Assert.True(d.Type('X'));
            Assert.True(d.Attention(Aid.Enter));
            Assert.Single(sent);
        }

        [Fact]
        public void MandatoryEntryDoesNotStopAShortRead()
        {
            var (d, sent, _) = Screen(FieldValidation.MandatoryEntry);

            Assert.True(d.Attention(Aid.PA1));

            Assert.Single(sent);
        }

        [Fact]
        public void TriggerIsTakenAndReservedBitsAreReported()
        {
            // The Field Validation reply now lists trigger (p. 6-52), so the bit is taken.
            var (d, _, trigger) = Screen(FieldValidation.Trigger);
            Assert.Empty(trigger);
            Assert.Equal(FieldValidation.Trigger, d.GetFieldValidation(0));

            var (_, _, reserved) = Screen(0x80);
            Assert.Single(reserved);
        }
    }
}
