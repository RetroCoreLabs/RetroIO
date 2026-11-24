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
    /// Trigger fields: priming, the trigger action, the keystroke queue, and the host's
    /// affirmative and negative replies (GA23-0059-4 p. 4-23 to 4-26).
    /// </summary>
    public class TriggerFieldTests
    {
        // As FieldValidationTests: EW restore; SFE at 0 unprotected with the validation given,
        // input 1..4, cursor 1; SF at 5 unprotected, input 6..19; protected stop at 20.
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
            Assert.Empty(errors);
            return (d, sent, errors);
        }

        private static byte[] Address(int address)
        {
            var a = new byte[2];
            BufferAddress.Encode(address, 1920, a);
            return a;
        }

        [Fact]
        public void LeavingAPrimedTriggerFieldSendsItAndKeepsTheCursorThere()
        {
            // p. 4-23: a data key primes the field; trying to move the cursor out sends AID
            // X'7F', the cursor address and the field's address and data; the cursor does not
            // move. A trigger action raises no PWAIT or System Lock (p. 3-21 steps 8 and 9).
            var (d, sent, _) = Screen(FieldValidation.Trigger);
            Assert.True(d.Type('A'));

            d.Tab();

            byte[] record = Assert.Single(sent);
            byte[] cursor = Address(2);
            byte[] field = Address(1);
            Assert.Equal(new byte[] { Aid.Trigger, cursor[0], cursor[1], Order3270.SetBufferAddress, field[0], field[1], 0xC1 }, record);
            Assert.Equal(2, d.CursorAddress);
            Assert.True(d.TriggerPending);
            Assert.True(d.EnterInhibit);
            Assert.Equal(KeyboardLock.None, d.Lock);
        }

        [Fact]
        public void AnUntouchedTriggerFieldIsLeftWithoutATriggerAction()
        {
            var (d, sent, _) = Screen(FieldValidation.Trigger);

            d.Tab();

            Assert.Empty(sent);
            Assert.Equal(6, d.CursorAddress);
        }

        [Fact]
        public void KeystrokesAreQueuedAndRunAfterTheAffirmativeReply()
        {
            // p. 4-23: queued keystrokes are not shown and the cursor does not move until the
            // affirmative reply; then the cursor goes where the triggering key would have put
            // it, and the queue runs.
            var (d, _, errors) = Screen(FieldValidation.Trigger);
            d.Type('A');
            d.Tab();

            Assert.True(d.Type('B'));
            Assert.Equal(1, d.QueuedKeystrokes);
            Assert.Equal(0x00, d.GetByte(6));
            Assert.Equal(2, d.CursorAddress);

            // A Write with keyboard restore to INPID 0 is the affirmative reply (p. 4-24).
            Assert.True(d.ProcessOutbound(new byte[] { Command3270.Write, Wcc.KeyboardRestore }));

            Assert.Empty(errors);
            Assert.False(d.TriggerPending);
            Assert.False(d.EnterInhibit);
            Assert.Equal(0xC2, d.GetByte(6));
            Assert.Equal(7, d.CursorAddress);
        }

        [Fact]
        public void AnInsertCursorInTheAffirmativeReplyWins()
        {
            // p. 4-23: the cursor goes to the pending target "unless the cursor is moved by the
            // host program by means of an IC order".
            var (d, _, _) = Screen(FieldValidation.Trigger);
            d.Type('A');
            d.Tab();
            d.Type('B');

            byte[] ten = Address(10);
            d.ProcessOutbound(new byte[] { Command3270.Write, Wcc.KeyboardRestore, Order3270.SetBufferAddress, ten[0], ten[1], Order3270.InsertCursor });

            Assert.Equal(0xC2, d.GetByte(10));
            Assert.Equal(11, d.CursorAddress);
        }

        [Fact]
        public void ANegativeReplyEmptiesTheQueueAndRaisesTheInvalidFieldCondition()
        {
            // p. 4-24: any other operation is negative: the queue is purged and an input
            // inhibit is raised. A Write without keyboard restore is one.
            var (d, _, _) = Screen(FieldValidation.Trigger);
            d.Type('A');
            d.Tab();
            d.Type('B');

            d.ProcessOutbound(new byte[] { Command3270.Write, 0x00 });

            Assert.False(d.TriggerPending);
            Assert.Equal(0, d.QueuedKeystrokes);
            Assert.Equal(KeyboardLock.InvalidField, d.Lock);
            Assert.Equal(0x00, d.GetByte(6));

            d.Reset();
            Assert.Equal(KeyboardLock.None, d.Lock);
        }

        [Fact]
        public void AReadModifiedIsNotAReplyAndSendsTheTriggerFieldAgain()
        {
            // p. 4-23: "The RM command transmits the trigger field"; p. 4-24: read commands are
            // not replies.
            var (d, sent, _) = Screen(FieldValidation.Trigger);
            d.Type('A');
            d.Tab();

            d.ProcessOutbound(new byte[] { Command3270.ReadModified });

            Assert.Equal(2, sent.Count);
            Assert.Equal(sent[0], sent[1]);
            Assert.True(d.TriggerPending);
        }

        [Fact]
        public void SysReqPurgesTheQueue()
        {
            // p. 4-23: "system request purges the queue".
            var (d, _, _) = Screen(FieldValidation.Trigger);
            d.Type('A');
            d.Tab();
            d.Type('B');

            d.SysReq();

            Assert.Equal(0, d.QueuedKeystrokes);
        }

        [Fact]
        public void FillCheckingComesBeforeTheTrigger()
        {
            // p. 4-25 note 1: a field both mandatory fill and trigger is checked for fill first.
            var (d, sent, _) = Screen((byte)(FieldValidation.MandatoryFill | FieldValidation.Trigger));
            d.Type('A');

            d.Tab();

            Assert.Empty(sent);
            Assert.Equal(KeyboardLock.MandatoryFill, d.Lock);
        }

        [Fact]
        public void AWriteToThePartitionUnprimesTheField()
        {
            // p. 4-23: primed until "a write operation to that partition".
            var (d, sent, _) = Screen(FieldValidation.Trigger);
            d.Type('A');

            d.ProcessOutbound(new byte[] { Command3270.Write, Wcc.KeyboardRestore });
            d.Tab();

            Assert.Empty(sent);
            Assert.Equal(6, d.CursorAddress);
        }

        [Fact]
        public void AFullQueueRaisesAnInputInhibit()
        {
            // p. 4-25 note 4. The size, 64, is this display's choice.
            var (d, _, _) = Screen(FieldValidation.Trigger);
            d.Type('A');
            d.Tab();
            for (int i = 0; i < Display3270.KeystrokeQueueCapacity; i++)
            {
                Assert.True(d.Type('B'));
            }

            Assert.False(d.Type('B'));

            Assert.Equal(KeyboardLock.KeystrokeQueueFull, d.Lock);
        }
    }
}
