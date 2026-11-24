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
    /// Input inhibit per partition: PWAIT and System Lock belong to a partition, Enter Inhibit
    /// and the read states to the display, and the jump-partition key (GA23-0059-4 p. 2-5, 2-9,
    /// 3-14 to 3-23).
    /// </summary>
    public class PartitionInputInhibitTests
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

        // Create Partition, 18 bytes up to WV (p. 5-11), 16-bit addressing.
        private static byte[] CreatePartition(byte pid, int h, int rv) => new byte[]
        {
            Command3270.WriteStructuredField, 0x00, 0x12, 0x0C, pid, 0x01, 0x00,
            0x00, (byte)h, 0x00, 80, 0x00, (byte)rv, 0x00, 0x00, 0x00, (byte)h, 0x00, 80,
        };

        // WSF with one Outbound 3270DS: PID, command, then the WCC and data (p. 5-40).
        private static byte[] Outbound(byte pid, byte command, params byte[] wccAndData)
        {
            int length = 5 + wccAndData.Length;
            var record = new byte[1 + length];
            record[0] = Command3270.WriteStructuredField;
            record[1] = (byte)(length >> 8);
            record[2] = (byte)length;
            record[3] = 0x40;
            record[4] = pid;
            record[5] = command;
            Array.Copy(wccAndData, 0, record, 6, wccAndData.Length);
            return record;
        }

        private static byte[] ReadPartition(byte pid, byte type) => new byte[] { Command3270.WriteStructuredField, 0x00, 0x05, 0x01, pid, type };

        // Two partitions: 1 on rows 0-11 (made active as the first), 2 on rows 12-23.
        private static (Display3270 Display, List<byte[]> Sent, List<string> Errors) TwoPartitions()
        {
            var (d, sent, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 12, 0));
            d.ProcessOutbound(CreatePartition(2, 12, 12));
            Assert.Empty(errors);
            Assert.Equal(1, d.ActivePartition.Pid);
            return (d, sent, errors);
        }

        [Fact]
        public void AnEnterWithMoreThanOnePartitionIsPwaitOnThatPartitionOnly()
        {
            // p. 3-21 steps 5 to 9: Enter Inhibit, INPID, PWAIT and System Lock on INPID.
            // p. 3-20: PWAIT, not TWAIT, when there is more than one partition.
            var (d, sent, _) = TwoPartitions();

            Assert.True(d.Attention(Aid.Enter));

            Assert.Single(sent);
            Assert.Equal(KeyboardLock.PartitionWait, d.Lock);
            Assert.True(d.EnterInhibit);
            Partition3270? p1 = d.GetPartition(1);
            Partition3270? p2 = d.GetPartition(2);
            Assert.NotNull(p1);
            Assert.NotNull(p2);
            Assert.True(p1.PartitionWait);
            Assert.True(p1.SystemLock);
            Assert.False(p2.PartitionWait);
            Assert.False(p2.SystemLock);
        }

        [Fact]
        public void AJumpLeavesTheWaitBehindAndTypingGoesOnInTheOtherPartition()
        {
            // p. 2-9: the conditions are remembered with their partition; p. 3-15: Enter
            // Inhibit stops an AID from any partition but not typing.
            var (d, sent, _) = TwoPartitions();
            d.Attention(Aid.Enter);

            Assert.True(d.JumpPartition());

            Assert.Equal(2, d.ActivePartition.Pid);
            Assert.Equal(KeyboardLock.None, d.Lock);
            Assert.True(d.Type('A'));
            Assert.Equal(0xC1, d.GetByte(0));

            // An enter from partition 2 is rejected: Enter Inhibit (p. 3-21 step 1).
            Assert.False(d.Attention(Aid.Enter));
            Assert.Single(sent);

            // Back on partition 1 its PWAIT shows again.
            Assert.True(d.JumpPartition());
            Assert.Equal(1, d.ActivePartition.Pid);
            Assert.Equal(KeyboardLock.PartitionWait, d.Lock);
        }

        [Fact]
        public void ResetDoesNotEndAWait()
        {
            // p. 3-20: PWAIT is ended only by the acknowledgment; System Lock "will not be
            // displayed while TWAIT or PWAIT exists" (p. 3-21) and Reset does not reach it there.
            var (d, _, _) = TwoPartitions();
            d.Attention(Aid.Enter);

            d.Reset();

            Assert.Equal(KeyboardLock.PartitionWait, d.Lock);
            Assert.True(d.GetPartition(1)!.SystemLock);
        }

        [Fact]
        public void AWriteWithoutRestoreAcknowledgesAndLeavesSystemLockForReset()
        {
            // p. 3-23 acknowledgment ends PWAIT and Enter Inhibit; System Lock stays until a
            // keyboard restore or RESET while its partition is active (p. 2-9).
            var (d, _, errors) = TwoPartitions();
            d.Attention(Aid.Enter);

            d.ProcessOutbound(Outbound(1, Command3270.Write, 0x00));

            Assert.Empty(errors);
            Assert.False(d.EnterInhibit);
            Assert.Equal(KeyboardLock.SystemLock, d.Lock);

            d.Reset();
            Assert.Equal(KeyboardLock.None, d.Lock);
        }

        [Fact]
        public void AWriteWithKeyboardRestoreToThePartitionEndsBoth()
        {
            var (d, _, _) = TwoPartitions();
            d.Attention(Aid.Enter);

            d.ProcessOutbound(Outbound(1, Command3270.Write, Wcc.KeyboardRestore));

            Assert.Equal(KeyboardLock.None, d.Lock);
            Assert.False(d.EnterInhibit);
            Assert.False(d.GetPartition(1)!.SystemLock);
        }

        [Fact]
        public void AReadPartitionInARetryStateIsRejectedWithX0871()
        {
            // p. 3-18: "If the device is in retry state, the Read Partition structured field is
            // rejected with sense code X'0871'".
            var (d, sent, errors) = TwoPartitions();
            d.Attention(Aid.Enter);

            Assert.False(d.ProcessOutbound(ReadPartition(2, Command3270.ReadModified)));

            Assert.Single(errors);
            Assert.Contains("0871", errors[0], StringComparison.Ordinal);
            Assert.Single(sent);
        }

        [Fact]
        public void AReadModifiedInRetryEnterStateSendsTheEnterAgainFromTheInboundPartition()
        {
            // p. 3-17: Read Modified in retry-enter state "performs a retry of the inbound
            // operation on INPID", even though the command as the first byte would otherwise go
            // to partition 0 (Table 3-3) - which does not exist here.
            var (d, sent, errors) = TwoPartitions();
            d.JumpPartition();
            d.Type('Z');
            d.Attention(Aid.Enter);

            Assert.True(d.ProcessOutbound(new byte[] { Command3270.ReadModified }));

            Assert.Empty(errors);
            Assert.Equal(2, sent.Count);
            Assert.Equal(sent[0], sent[1]);

            // And it is still not acknowledged.
            Assert.True(d.EnterInhibit);
        }

        [Fact]
        public void WithOnePartitionTheWaitIsShownAsTwait()
        {
            // p. 3-20: "TWAIT will be used when in the implicit partition state or when there is
            // only one explicit partition".
            var (d, _, _) = Create();
            d.ProcessOutbound(CreatePartition(1, 24, 0));

            d.Attention(Aid.Enter);

            Assert.Equal(KeyboardLock.WaitingForHost, d.Lock);
            Assert.True(d.GetPartition(1)!.PartitionWait);
        }

        [Fact]
        public void TheJumpKeyGoesToTheNextHigherPidAndWrapsToTheLowest()
        {
            // p. 2-5: the smallest PID greater than N, else the lowest.
            var (d, _, _) = Create();
            d.ProcessOutbound(CreatePartition(5, 8, 0));
            d.ProcessOutbound(CreatePartition(1, 8, 8));
            d.ProcessOutbound(CreatePartition(3, 8, 16));
            Assert.Equal(5, d.ActivePartition.Pid);

            Assert.True(d.JumpPartition());
            Assert.Equal(1, d.ActivePartition.Pid);
            Assert.True(d.JumpPartition());
            Assert.Equal(3, d.ActivePartition.Pid);
            Assert.True(d.JumpPartition());
            Assert.Equal(5, d.ActivePartition.Pid);
        }

        [Fact]
        public void TheJumpKeyDoesNothingUnderTwait()
        {
            // p. 3-20: "When the TWAIT condition exists, the Jump key does nothing". Implicit
            // partition state and a single partition are always TWAIT cases.
            var (d, _, _) = Create();
            Assert.False(d.JumpPartition());

            d.ProcessOutbound(CreatePartition(1, 24, 0));
            Assert.False(d.JumpPartition());
        }

        [Fact]
        public void AnAcknowledgedReadPartitionLetsTheNextOneThrough()
        {
            // Retry-read state after a Read Partition (p. 5-49), ended by the host's next
            // non-read transmission (p. 3-23), after which a Read Partition is taken again.
            var (d, sent, errors) = TwoPartitions();

            d.ProcessOutbound(ReadPartition(1, Command3270.ReadModified));
            Assert.True(d.EnterInhibit);

            d.ProcessOutbound(Outbound(2, Command3270.Write, 0x00));
            Assert.False(d.EnterInhibit);

            Assert.True(d.ProcessOutbound(ReadPartition(2, Command3270.ReadModified)));
            Assert.Empty(errors);
            Assert.Equal(2, sent.Count);
        }
    }
}
