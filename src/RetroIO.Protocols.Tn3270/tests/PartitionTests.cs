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
    /// Explicit partitions: Create, Destroy, Activate and Reset Partition, Outbound 3270DS and Read
    /// Partition per partition, and 16-bit addressing (GA23-0059-4 chapter 2, p. 3-4, 4-4, 5-8 to
    /// 5-15, 5-40, 5-47 to 5-51, 6-7, 6-18).
    /// </summary>
    public class PartitionTests
    {
        private static (Display3270 Display, List<byte[]> Sent, List<string> Errors) Create(int rows = 24, int columns = 80, int alternateRows = 24, int alternateColumns = 80)
        {
            var display = new Display3270(rows, columns, alternateRows, alternateColumns);
            var sent = new List<byte[]>();
            var errors = new List<string>();
            display.InboundRecord += record => sent.Add(record.ToArray());
            display.ProtocolError += errors.Add;
            return (display, sent, errors);
        }

        // WSF with one Create Partition of 18 bytes, up to WV (p. 5-11): PID, UOM/A-MODE, flags,
        // H, W, RV, CV, HV, WV. The rest takes its defaults (p. 5-12).
        private static byte[] CreatePartition(byte pid, int h, int w, int rv, int cv, byte modes = 0x00, byte flags = 0x00, int hv = -1, int wv = -1)
        {
            if (hv < 0)
            {
                hv = h;
            }

            if (wv < 0)
            {
                wv = w;
            }

            return new byte[]
            {
                Command3270.WriteStructuredField, 0x00, 0x12, 0x0C, pid, modes, flags,
                (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w,
                (byte)(rv >> 8), (byte)rv, (byte)(cv >> 8), (byte)cv,
                (byte)(hv >> 8), (byte)hv, (byte)(wv >> 8), (byte)wv,
            };
        }

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

        private static byte[] Destroy(byte pid) => new byte[] { Command3270.WriteStructuredField, 0x00, 0x04, 0x0D, pid };

        private static byte[] Activate(byte pid) => new byte[] { Command3270.WriteStructuredField, 0x00, 0x04, 0x0E, pid };

        private static byte[] ReadPartition(byte pid, byte type) => new byte[] { Command3270.WriteStructuredField, 0x00, 0x05, 0x01, pid, type };

        [Fact]
        public void TheFirstCreatePartitionDestroysImplicitPartitionZeroAndMakesTheNewOneActive()
        {
            // p. 5-10: "implicit partition 0 is destroyed ... The display state is changed from
            // implicit partition state to explicit partitioned state and the created partition is
            // made active."
            var (d, _, errors) = Create();
            d.ProcessOutbound(new byte[] { Command3270.EraseWrite, 0x02, 0xC1 });

            d.ProcessOutbound(CreatePartition(1, 10, 40, 2, 20));

            Assert.Empty(errors);
            Assert.True(d.ExplicitPartitioned);
            Assert.Equal(1, d.ActivePartitionId);
            Assert.Null(d.GetPartition(0));
            Partition3270? p = d.GetPartition(1);
            Assert.NotNull(p);
            Assert.Equal(10, p.Rows);
            Assert.Equal(40, p.Columns);
            Assert.Equal(2, p.ViewportRow);
            Assert.Equal(20, p.ViewportColumn);
            Assert.False(p.SixteenBitAddressing);

            // The new presentation space is empty and the cursor at the window origin (p. 5-10).
            Assert.Equal(0, d.CursorAddress);
            Assert.Equal(0, d.GetByte(0));
            Assert.Equal(10, d.Rows);
            Assert.Equal(24, d.ScreenRows);
            Assert.Equal(80, d.ScreenColumns);
        }

        [Fact]
        public void ACreatePartitionWithOnlyItsPidTakesTheDefaultsOfPage5To12()
        {
            // H and W: the default screen size; RV, CV 0; HV, WV the smaller of H, W and the
            // usable area (p. 5-12).
            var (d, _, errors) = Create(24, 80, 32, 80);

            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x04, 0x0C, 0x03 });

            Assert.Empty(errors);
            Partition3270? p = d.GetPartition(3);
            Assert.NotNull(p);
            Assert.Equal(24, p.Rows);
            Assert.Equal(80, p.Columns);
            Assert.Equal(0, p.ViewportRow);
            Assert.Equal(32, d.ScreenRows);
        }

        [Fact]
        public void Outbound3270DSWritesOnlyTheNamedPartitionWithAddressesFromItsOwnOrigin()
        {
            // SBA "is relative to the origin of the buffer of the partition to which the order and
            // data are directed" (p. 4-3). Partition 1 at rows 0-11, partition 2 at rows 12-23.
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 12, 80, 0, 0));
            d.ProcessOutbound(CreatePartition(2, 12, 80, 12, 0));

            // To partition 2: SBA 12-bit coded address 5 (X'40' X'C5'), "A".
            d.ProcessOutbound(Outbound(2, Command3270.Write, 0x02, Order3270.SetBufferAddress, 0x40, 0xC5, 0xC1));

            Assert.Empty(errors);
            Partition3270? p1 = d.GetPartition(1);
            Partition3270? p2 = d.GetPartition(2);
            Assert.NotNull(p1);
            Assert.NotNull(p2);
            Assert.Equal(0xC1, p2.GetByte(5));
            Assert.Equal(0x00, p1.GetByte(5));

            // On the screen that is row 12, column 5.
            Assert.True(d.TryGetScreenPosition(12, 5, out Partition3270? at, out int address));
            Assert.Same(p2, at);
            Assert.Equal(5, address);
        }

        [Fact]
        public void ANewPartitionIsMadeActiveOnlyWhenItIsTheOnlyOne()
        {
            // p. 5-10: a new partition "is made active if it is the only partition with a window
            // of nonzero extent".
            var (d, _, _) = Create();
            d.ProcessOutbound(CreatePartition(1, 12, 80, 0, 0));
            d.ProcessOutbound(CreatePartition(2, 12, 80, 12, 0));

            Assert.Equal(1, d.ActivePartitionId);
        }

        [Fact]
        public void ActivatePartitionMovesTheOperatorToThatPartition()
        {
            // p. 5-8: the named partition is made active and the cursor shows at its cursor.
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 12, 80, 0, 0));
            d.ProcessOutbound(CreatePartition(2, 12, 80, 12, 0));
            d.ProcessOutbound(Outbound(2, Command3270.Write, 0x02, Order3270.SetBufferAddress, 0x40, 0xC5, Order3270.InsertCursor));

            d.ProcessOutbound(Activate(2));

            Assert.Empty(errors);
            Assert.Equal(2, d.ActivePartitionId);
            Assert.Equal(5, d.CursorAddress);
            Assert.True(d.Type('A'));
            Partition3270? p2 = d.GetPartition(2);
            Assert.NotNull(p2);
            Assert.Equal(0xC1, p2.GetByte(5));
        }

        [Fact]
        public void ActivatingAPartitionThatDoesNotExistIsRejected()
        {
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 12, 80, 0, 0));

            Assert.False(d.ProcessOutbound(Activate(4)));

            Assert.Single(errors);
            Assert.Equal(1, d.ActivePartitionId);
        }

        [Fact]
        public void ReadPartitionOfANonzeroPartitionAnswersInAnInbound3270DSWithAid61()
        {
            // p. 6-7 case 2 and p. 3-16: X'88', then L, X'80', PID, AID X'61', cursor, data.
            var (d, sent, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 12, 80, 0, 0));
            d.ProcessOutbound(CreatePartition(2, 2, 2, 12, 0));
            d.ProcessOutbound(Outbound(2, Command3270.EraseWrite, 0x02, 0xC1));

            d.ProcessOutbound(ReadPartition(2, Command3270.ReadBuffer));

            Assert.Empty(errors);
            byte[] r = Assert.Single(sent);

            // 2 x 2 = 4 cells, nulls included in a Read Buffer (p. 3-11): A, 0, 0, 0. Cursor 0 in
            // 12-bit form is X'40' X'40'. Length 2 + 1 + 1 + 1 + 2 + 4 = 11.
            Assert.Equal(new byte[] { 0x88, 0x00, 0x0B, 0x80, 0x02, 0x61, 0x40, 0x40, 0xC1, 0x00, 0x00, 0x00 }, r);
            Assert.Equal(2, d.InboundPartitionId);
        }

        [Fact]
        public void AReadModifiedAfterReadPartitionSendsTheSameDataAgain()
        {
            // p. 5-49 step 7: retry-read state; p. 3-18: there a Read Modified command
            // "retransmit[s] data from command INPID".
            var (d, sent, _) = Create();
            d.ProcessOutbound(CreatePartition(1, 2, 2, 0, 0));
            d.ProcessOutbound(Outbound(1, Command3270.EraseWrite, 0x02, 0xC1));
            d.ProcessOutbound(ReadPartition(1, Command3270.ReadBuffer));

            d.ProcessOutbound(new byte[] { Command3270.ReadModified });

            Assert.Equal(2, sent.Count);
            Assert.Equal(sent[0], sent[1]);
        }

        [Fact]
        public void EnterInANonzeroActivePartitionSendsAnInbound3270DSWithTheEnterAid()
        {
            // p. 6-7 case 3, p. 3-9 note 1 ("It contains an AID of X'7D' if sent inbound as a
            // result of the ENTER key"), p. 3-21 step 6 (INPID is the active partition's PID).
            var (d, sent, errors) = Create();
            d.ProcessOutbound(CreatePartition(5, 2, 4, 0, 0));
            d.ProcessOutbound(Outbound(5, Command3270.EraseWrite, 0x02));

            Assert.True(d.Type('A'));
            Assert.True(d.Attention(Aid.Enter));

            Assert.Empty(errors);
            byte[] r = Assert.Single(sent);

            // Unformatted: AID, cursor (1 in 12-bit form: X'40' X'C1'), then the data "A".
            Assert.Equal(new byte[] { 0x88, 0x00, 0x08, 0x80, 0x05, Aid.Enter, 0x40, 0xC1, 0xC1 }, r);
            Assert.Equal(5, d.InboundPartitionId);
        }

        [Fact]
        public void SixteenBitAddressingSendsTheCursorAsBinary()
        {
            // p. 4-4: "When 16-bit addressing is specified, the partition will always generate
            // 16-bit addresses in the inbound data stream"; p. D-1: all 16 bits, 3100 is X'0C1C'.
            // A 12/14-bit partition of this size sends 12-bit coded addresses instead.
            var (d, sent, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 12, 80, 0, 0, modes: 0x01));
            d.ProcessOutbound(CreatePartition(2, 12, 80, 12, 0, modes: 0x00));
            Partition3270? p1 = d.GetPartition(1);
            Assert.NotNull(p1);
            Assert.True(p1.SixteenBitAddressing);

            // Cursor to 100 in each: X'0064' as 16-bit binary; as 12/14-bit that is 14-bit form.
            d.ProcessOutbound(Outbound(1, Command3270.Write, 0x02, Order3270.SetBufferAddress, 0x00, 0x64, Order3270.InsertCursor));
            d.ProcessOutbound(Outbound(2, Command3270.Write, 0x02, Order3270.SetBufferAddress, 0x00, 0x64, Order3270.InsertCursor));
            d.ProcessOutbound(ReadPartition(1, Command3270.ReadModified));
            d.ProcessOutbound(ReadPartition(2, Command3270.ReadModified));

            Assert.Empty(errors);

            // 88, L, 80, PID, 61, then the cursor at bytes 6 and 7.
            Assert.Equal(new byte[] { 0x00, 0x64 }, sent[0].AsSpan(6, 2).ToArray());

            // 100 = 1 x 64 + 36: Figure D-1 gives X'C1' and X'E4' (p. D-2).
            Assert.Equal(new byte[] { 0xC1, 0xE4 }, sent[1].AsSpan(6, 2).ToArray());
        }

        [Fact]
        public void SixteenBitAddressingReadsAllSixteenBitsOutbound()
        {
            // X'40' X'C5' is 12-bit coded address 5 in a 12/14-bit partition (p. 4-4), but in a
            // 16-bit partition it is X'40C5' = 16581, outside the partition: the write stops with
            // an error (p. 4-3).
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 12, 80, 0, 0, modes: 0x01));

            Assert.False(d.ProcessOutbound(Outbound(1, Command3270.Write, 0x02, Order3270.SetBufferAddress, 0x40, 0xC5, 0xC1)));

            Assert.Single(errors);
            Assert.Equal(0x00, d.GetByte(5));
        }

        [Fact]
        public void DestroyingTheActivePartitionActivatesTheNextHigherPid()
        {
            // p. 5-14: "activates the next partition"; the next is the smallest PID greater than
            // this one, else the lowest (p. 2-5).
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(3, 8, 80, 0, 0));
            d.ProcessOutbound(CreatePartition(1, 8, 80, 8, 0));
            d.ProcessOutbound(CreatePartition(7, 8, 80, 16, 0));

            d.ProcessOutbound(Destroy(3));
            Assert.Equal(7, d.ActivePartitionId);

            d.ProcessOutbound(Destroy(7));
            Assert.Equal(1, d.ActivePartitionId);
            Assert.Empty(errors);
            Assert.Null(d.GetPartition(3));
        }

        [Fact]
        public void DestroyingTheLastPartitionGoesBackToImplicitPartitionZero()
        {
            // p. 5-14: "Resets the display to implicit partition state and creates the default
            // Implicit Partition zero"; INPID back to 0.
            var (d, sent, _) = Create(24, 80, 32, 80);
            d.ProcessOutbound(CreatePartition(2, 4, 4, 0, 0));
            d.ProcessOutbound(ReadPartition(2, Command3270.ReadBuffer));
            Assert.Equal(2, d.InboundPartitionId);

            d.ProcessOutbound(Destroy(2));

            Assert.False(d.ExplicitPartitioned);
            Assert.Equal(0, d.InboundPartitionId);
            Assert.Equal(24, d.Rows);
            Assert.Equal(80, d.Columns);
            Assert.NotNull(d.GetPartition(0));
            Assert.Single(sent);
        }

        [Fact]
        public void DestroyingAPartitionThatDoesNotExistHasNoEffect()
        {
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 4, 4, 0, 0));

            Assert.True(d.ProcessOutbound(Destroy(9)));

            Assert.Empty(errors);
            Assert.True(d.ExplicitPartitioned);
        }

        [Fact]
        public void ACreateWhoseViewportOverlapsAnotherIsRejected()
        {
            // p. 5-10 condition 5.
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 12, 80, 0, 0));

            Assert.False(d.ProcessOutbound(CreatePartition(2, 12, 80, 11, 0)));

            Assert.Single(errors);
            Assert.Null(d.GetPartition(2));
        }

        [Fact]
        public void ACreateWhoseViewportDoesNotFitTheUsableAreaIsRejected()
        {
            // p. 5-10 conditions 3 and 4: RV + HV > screen height, CV + WV > screen width.
            var (d, _, errors) = Create();

            Assert.False(d.ProcessOutbound(CreatePartition(1, 10, 10, 20, 0)));
            Assert.False(d.ProcessOutbound(CreatePartition(1, 10, 10, 0, 75)));

            Assert.Equal(2, errors.Count);
            Assert.False(d.ExplicitPartitioned);
        }

        [Fact]
        public void APresentationSpaceLargerThanItsViewportIsRefusedBecauseScrollingIsNotBuilt()
        {
            // H > HV is a scrolling partition (p. 2-3); the Alphanumeric Partitions reply says
            // SCROLL 0 (p. 6-19), so it is refused, not shown cut off.
            var (d, _, errors) = Create();

            Assert.False(d.ProcessOutbound(CreatePartition(1, 40, 80, 0, 0, hv: 12, wv: 80)));

            Assert.Single(errors);
            Assert.False(d.ExplicitPartitioned);
        }

        [Theory]
        [InlineData(0x02, 0x00, 1)] // A-MODE text: no text partitions (p. 5-11)
        [InlineData(0x03, 0x00, 1)] // A-MODE reserved (Table B-1, Invalid A-MODE)
        [InlineData(0x10, 0x00, 1)] // UOM B'0001': only cells (p. 5-12)
        [InlineData(0x00, 0x01, 1)] // BASE 1: one base character set (Table B-1)
        [InlineData(0x00, 0x40, 1)] // PROT: partition protection not supported (p. 6-19)
        [InlineData(0x00, 0x80, 1)] // reserved flag bit 0
        [InlineData(0x01, 0x00, 0)] // A-MODE 16-bit: taken
        public void CreatePartitionModesAndFlagsAreCheckedAgainstWhatTheDisplayReports(byte modes, byte flags, int expectedErrors)
        {
            var (d, _, errors) = Create();

            d.ProcessOutbound(CreatePartition(1, 4, 4, 0, 0, modes, flags));

            Assert.Equal(expectedErrors, errors.Count);
            Assert.Equal(expectedErrors == 0, d.ExplicitPartitioned);
        }

        [Fact]
        public void APidAboveTheReportedCountIsRejected()
        {
            // Alphanumeric Partitions NA = 16, so PIDs 0 to 15 (p. 6-18); Table B-1, PID > maximum.
            var (d, _, errors) = Create();

            Assert.False(d.ProcessOutbound(CreatePartition(16, 4, 4, 0, 0)));

            Assert.Single(errors);
        }

        [Fact]
        public void AWriteCommandWithNoExplicitPartitionZeroIsRejected()
        {
            // Table 3-3 (p. 3-4), row 2a, explicit partitioned state: "Execute the command against
            // explicit partition 0; if no explicit partition 0, reject the command."
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 4, 4, 0, 0));

            Assert.False(d.ProcessOutbound(new byte[] { Command3270.Write, 0x02, 0xC1 }));

            Assert.Single(errors);
            Assert.True(d.ExplicitPartitioned);
        }

        [Fact]
        public void AWriteCommandGoesToExplicitPartitionZero()
        {
            // Table 3-3 row 2a; p. 2-6: commands go to partition 0 "as the first byte of the data
            // stream". Partition 1 is active, so the write must not land there.
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 4, 80, 0, 0));
            d.ProcessOutbound(CreatePartition(0, 4, 80, 4, 0));

            d.ProcessOutbound(new byte[] { Command3270.EraseWrite, 0x02, 0xC1 });

            Assert.Empty(errors);
            Partition3270? p0 = d.GetPartition(0);
            Partition3270? p1 = d.GetPartition(1);
            Assert.NotNull(p0);
            Assert.NotNull(p1);
            Assert.Equal(0xC1, p0.GetByte(0));
            Assert.Equal(0x00, p1.GetByte(0));

            // Erase/Write in explicit partitioned state keeps the partition's size (p. 3-6 step 1).
            Assert.Equal(4, p0.Rows);
            Assert.Equal(1, d.ActivePartitionId);
        }

        [Fact]
        public void EraseWriteWithWccResetGoesBackToImplicitPartitionState()
        {
            // Table 3-3 row 1a: "Rest the display to the implicit partition state; execute the
            // command"; p. 3-6 step 6b.
            var (d, _, errors) = Create(24, 80, 32, 80);
            d.ProcessOutbound(CreatePartition(1, 4, 4, 0, 0));

            d.ProcessOutbound(new byte[] { Command3270.EraseWriteAlternate, 0x42, 0xC1 });

            Assert.Empty(errors);
            Assert.False(d.ExplicitPartitioned);
            Assert.Equal(32, d.Rows);
            Assert.Equal(0xC1, d.GetByte(0));
        }

        [Fact]
        public void EraseResetDestroysEveryPartition()
        {
            // p. 5-15: "destroying all existing (implicit or explicit) partitions".
            var (d, _, _) = Create();
            d.ProcessOutbound(CreatePartition(1, 4, 4, 0, 0));
            d.ProcessOutbound(CreatePartition(2, 4, 4, 4, 0));

            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x04, 0x03, 0x00 });

            Assert.False(d.ExplicitPartitioned);
            Assert.Null(d.GetPartition(1));
            Assert.Equal(24, d.Rows);
        }

        [Fact]
        public void TheClearKeyGoesBackToImplicitPartitionZero()
        {
            // p. 3-16: implicit partition 0 occurs "After the CLEAR key is pressed"; the short
            // read then comes from partition 0 as the AID alone (p. 3-13).
            var (d, sent, _) = Create();
            d.ProcessOutbound(CreatePartition(1, 4, 4, 0, 0));
            d.ProcessOutbound(Outbound(1, Command3270.EraseWrite, 0x02));

            Assert.True(d.Attention(Aid.Clear));

            Assert.False(d.ExplicitPartitioned);
            Assert.Equal(new byte[] { Aid.Clear }, Assert.Single(sent));
        }

        [Fact]
        public void SetReplyModeAndResetPartitionBelongToTheNamedPartition()
        {
            // Set Reply Mode names a partition (p. 5-61); Reset Partition puts its reply mode back
            // to Field (p. 5-51).
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 4, 4, 0, 0));
            d.ProcessOutbound(CreatePartition(2, 4, 4, 4, 0));

            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x05, 0x09, 0x02, 0x01 });

            Partition3270? p1 = d.GetPartition(1);
            Partition3270? p2 = d.GetPartition(2);
            Assert.NotNull(p1);
            Assert.NotNull(p2);
            Assert.Equal(ReplyMode.ExtendedField, p2.ReplyMode);
            Assert.Equal(ReplyMode.Field, p1.ReplyMode);

            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x04, 0x00, 0x02 });

            Assert.Equal(ReplyMode.Field, p2.ReplyMode);
            Assert.Empty(errors);
        }

        [Fact]
        public void Outbound3270DSToAPartitionThatDoesNotExistIsRejected()
        {
            // p. 5-40: "The specified partition must be defined or a data stream error occurs."
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 4, 4, 0, 0));

            Assert.False(d.ProcessOutbound(Outbound(2, Command3270.Write, 0x02, 0xC1)));

            Assert.Single(errors);
        }

        [Fact]
        public void TheFieldOutliningReplyCarriesTheRenderersLinePositions()
        {
            // p. 6-49, 6-50: Query List naming X'8C'.
            var (d, sent, errors) = Create();
            d.OutlineVerticalPosition = 1;
            d.OutlineHorizontalPosition = 14;

            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x07, 0x01, 0xFF, 0x03, 0x00, 0x8C });

            Assert.Empty(errors);
            Assert.Equal(new byte[] { 0x88, 0x00, 0x0A, 0x81, 0x8C, 0x00, 0x00, 0x01, 0x0E, 0x00, 0x00 }, Assert.Single(sent));
        }
    }
}
