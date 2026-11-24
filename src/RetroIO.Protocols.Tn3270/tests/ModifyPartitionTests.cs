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
    /// Modify Partition, X'0F0A' (GA23-0059-4 p. 5-34, 5-35), and the windows of zero extent it
    /// and Create Partition may make now that the display supports it (p. 5-10 conditions 1 and
    /// 2, 5-8, 5-14). Each case cites its page.
    /// </summary>
    public class ModifyPartitionTests
    {
        private static (Display3270 Display, List<byte[]> Sent, List<string> Errors) Create()
        {
            var display = new Display3270(24, 80, 24, 80);
            var sent = new List<byte[]>();
            var errors = new List<string>();
            display.InboundRecord += record => sent.Add(record.ToArray());
            display.ProtocolError += errors.Add;
            return (display, sent, errors);
        }

        // Create Partition of 24 bytes, up to RS (p. 5-11): 16-bit addressing, H x 80 in a
        // viewport HV x 80 at screen row RV, window origin row RW, scroll RS rows.
        private static byte[] CreatePartition(byte pid, int h, int hv, int rv, int rw = 0, int rs = 1, int wv = 80) => new byte[]
        {
            Command3270.WriteStructuredField, 0x00, 0x18, 0x0C, pid, 0x01, 0x00,
            0x00, (byte)h, 0x00, 80, 0x00, (byte)rv, 0x00, 0x00, 0x00, (byte)hv, 0x00, (byte)wv,
            0x00, (byte)rw, 0x00, 0x00, (byte)(rs >> 8), (byte)rs,
        };

        // Modify Partition: bytes 0 to 11 (L, X'0F0A', reserved, PID, reserved, flags, four
        // reserved zero bytes), then as many two-byte parameters from RV on as are given: RV, CV,
        // HV, WV, RW, CW, RS, reserved, PW, PH (p. 5-35).
        private static byte[] Modify(byte pid, byte flags, params int[] parameters)
        {
            int length = 12 + (2 * parameters.Length);
            var b = new List<byte>
            {
                Command3270.WriteStructuredField, (byte)(length >> 8), (byte)length, 0x0F, 0x0A, 0x00, pid, 0x00, flags,
                0x00, 0x00, 0x00, 0x00,
            };
            for (int i = 0; i < parameters.Length; i++)
            {
                b.Add((byte)(parameters[i] >> 8));
                b.Add((byte)parameters[i]);
            }

            return b.ToArray();
        }

        // A raw Modify Partition field, given from byte 2 on.
        private static byte[] Raw(params byte[] fromByte2)
        {
            int length = 2 + fromByte2.Length;
            var record = new byte[1 + length];
            record[0] = Command3270.WriteStructuredField;
            record[1] = (byte)(length >> 8);
            record[2] = (byte)length;
            Array.Copy(fromByte2, 0, record, 3, fromByte2.Length);
            return record;
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

        private static byte[] Activate(byte pid) => new byte[] { Command3270.WriteStructuredField, 0x00, 0x04, 0x0E, pid };

        private static byte[] Destroy(byte pid) => new byte[] { Command3270.WriteStructuredField, 0x00, 0x04, 0x0D, pid };

        [Fact]
        public void MovingTheViewportDownShowsThePartitionThereAndNothingAbove()
        {
            // RV is "Row origin of viewport" (p. 5-35); "The specified values replace those
            // currently in effect" (p. 5-34). Only RV is sent, so CV is left off and stays.
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 10, 10, 0));

            Assert.True(d.ProcessOutbound(Modify(1, 0x00, 12)));

            Assert.Empty(errors);
            Partition3270 p = d.ActivePartition;
            Assert.Equal(12, p.ViewportRow);
            Assert.Equal(10, p.ViewportRows);
            Assert.True(d.TryGetScreenPosition(12, 5, out Partition3270? shown, out int address));
            Assert.Same(p, shown);
            Assert.Equal(5, address);
            Assert.False(d.TryGetScreenPosition(0, 0, out _, out _));
        }

        [Fact]
        public void LeftOffParametersAreNotAltered()
        {
            // "Working backward from byte 31 through byte 6, parameters may be omitted
            // progressively ... If a parameter is omitted in this way, its value is not altered"
            // (p. 5-34). Six bytes (up to the PID), seven (the reserved byte 6) and eight (the
            // flags) all change nothing.
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 40, 12, 3, rw: 5, rs: 2));

            Assert.True(d.ProcessOutbound(Raw(0x0F, 0x0A, 0x00, 0x01)));
            Assert.True(d.ProcessOutbound(Raw(0x0F, 0x0A, 0x00, 0x01, 0x00)));
            Assert.True(d.ProcessOutbound(Raw(0x0F, 0x0A, 0x00, 0x01, 0x00, 0x00)));
            Assert.True(d.ProcessOutbound(Modify(1, 0x00, 3, 0, 12, 80)));

            Assert.Empty(errors);
            Partition3270 p = d.ActivePartition;
            Assert.Equal(3, p.ViewportRow);
            Assert.Equal(12, p.ViewportRows);
            Assert.Equal(80, p.ViewportColumns);
            Assert.Equal(5, p.WindowRow);
            Assert.Equal(2, p.ScrollRows);
        }

        [Fact]
        public void ShrinkingTheViewportMovesTheWindowByTheFewestRowsToKeepTheCursor()
        {
            // "If the viewport size is changed, the window size is changed accordingly. If as a
            // result, the cursor is outside the window, the window is moved to contain it.
            // Vertically, the window is moved by the minimum number of rows" (p. 5-34). Cursor on
            // row 15 of a 20-row window from row 0; a 10-row window must start at row 6.
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 20, 20, 0, rs: 0));
            d.ProcessOutbound(Outbound(1, Command3270.Write, 0x00, Order3270.SetBufferAddress, 0x04, 0xB7, Order3270.InsertCursor));
            Assert.Equal((15 * 80) + 7, d.CursorAddress);

            Assert.True(d.ProcessOutbound(Modify(1, 0x00, 0, 0, 10)));

            Assert.Empty(errors);
            Assert.Equal(10, d.ActivePartition.ViewportRows);
            Assert.Equal(6, d.ActivePartition.WindowRow);
            Assert.Equal((15 * 80) + 7, d.CursorAddress);
        }

        [Fact]
        public void ShrinkingTheViewportLeavesTheWindowWhereItIsWhenTheCursorIsStillInIt()
        {
            // The window moves only "If as a result, the cursor is outside the window" (p. 5-34).
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 20, 20, 0, rs: 0));
            d.ProcessOutbound(Outbound(1, Command3270.Write, 0x00, Order3270.SetBufferAddress, 0x00, 0xA0, Order3270.InsertCursor));

            Assert.True(d.ProcessOutbound(Modify(1, 0x00, 0, 0, 10)));

            Assert.Empty(errors);
            Assert.Equal(0, d.ActivePartition.WindowRow);
            Assert.Equal(160, d.CursorAddress);
        }

        [Fact]
        public void ANewWindowOriginMovesTheCursorToTheNearestRowOfTheWindow()
        {
            // "If the window origin is changed and the cursor is outside the new window, the
            // cursor is moved by the minimum number of rows and/or columns so that it is within
            // the new window" (p. 5-34). Cursor on row 0, column 7; window to rows 20 to 31.
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 40, 12, 0));
            d.ProcessOutbound(Outbound(1, Command3270.Write, 0x00, Order3270.SetBufferAddress, 0x00, 0x07, Order3270.InsertCursor));

            Assert.True(d.ProcessOutbound(Modify(1, 0x00, 0, 0, 12, 80, 20)));

            Assert.Empty(errors);
            Assert.Equal(20, d.ActivePartition.WindowRow);
            Assert.Equal((20 * 80) + 7, d.CursorAddress);
        }

        [Fact]
        public void RsIsReplaced()
        {
            // RS, "Vertical scroll units" (p. 5-35), is one of the values that "replace those
            // currently in effect" (p. 5-34).
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 40, 12, 0, rs: 1));

            Assert.True(d.ProcessOutbound(Modify(1, 0x00, 0, 0, 12, 80, 0, 0, 4)));

            Assert.Empty(errors);
            Assert.Equal(4, d.ActivePartition.ScrollRows);
        }

        [Fact]
        public void EveryRejectionOfPage534LeavesThePartitionAsItWas()
        {
            // p. 5-34 conditions 1 to 6, Table B-1 p. B-2 (Modify Partition and Structured
            // Fields (All)). Partition 1 at rows 0 to 9, partition 2 at rows 12 to 21.
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 10, 10, 0));
            d.ProcessOutbound(CreatePartition(2, 10, 10, 12));
            Assert.Empty(errors);

            byte[][] rejected =
            {
                Modify(3, 0x00, 0),                                 // 1. the partition does not exist
                Modify(0x10, 0x00, 0),                              // PID > maximum allowed
                Modify(1, 0x80, 0),                                 // 2. reserved flag bit 0
                Modify(1, 0x01, 0),                                 // 2. reserved flag bit 7
                Modify(1, 0x00, 5),                                 // 3. rows 5 to 14 overlap partition 2
                Modify(1, 0x00, 20),                                // 4. rows 20 to 29 do not fit 24 rows
                Modify(1, 0x00, 0, 1),                              // 4. CV 1 with 80 columns does not fit 80
                Modify(1, 0x00, 0, 0, 10, 80, 1),                   // 5. window rows 1 to 10 of a 10-row space
                Modify(1, 0x00, 0, 0, 10, 80, 0, 0, 1, 7),          // reserved bytes 26-27 not zero
                Raw(0x0F, 0x0A, 0x01, 0x01),                        // reserved byte 4 not zero
                Raw(0x0F, 0x0A, 0x00, 0x01, 0x02),                  // reserved byte 6 not zero
                Raw(0x0F, 0x0A, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00), // 6. bytes 8-11
                Raw(0x0F, 0x0A, 0x00, 0x01, 0x00, 0x00, 0x00),      // a two-byte parameter cut in half
                Raw(0x0F, 0x0A, 0x00),                              // shorter than byte 5, the PID
            };

            for (int i = 0; i < rejected.Length; i++)
            {
                int before = errors.Count;
                Assert.False(d.ProcessOutbound(rejected[i]), "case " + i);
                Assert.Equal(before + 1, errors.Count);
            }

            Partition3270? p = d.GetPartition(1);
            Assert.NotNull(p);
            Assert.Equal(0, p.ViewportRow);
            Assert.Equal(10, p.ViewportRows);
            Assert.Equal(0, p.WindowRow);
            Assert.Equal(1, d.ActivePartitionId);
        }

        [Fact]
        public void ProtectionCellSizeAndHorizontalWindowsAreRefusedByName()
        {
            // The replies say the display lacks them: PROT 0 (Alphanumeric Partitions, p. 6-19),
            // one cell size (Usable Area VCP 0, p. 6-88), no horizontal windowing (Table B-1
            // p. B-2, Invalid windowing parameters).
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 10, 10, 0));

            Assert.False(d.ProcessOutbound(Modify(1, 0x40, 0)));
            Assert.Contains("PROT", errors[^1], StringComparison.Ordinal);

            Assert.False(d.ProcessOutbound(Modify(1, 0x00, 0, 0, 10, 80, 0, 0, 1, 0, 8, 16)));
            Assert.Contains("one cell size", errors[^1], StringComparison.Ordinal);

            Assert.False(d.ProcessOutbound(Modify(1, 0x00, 0, 0, 10, 80, 0, 3)));
            Assert.Contains("horizontal windowing", errors[^1], StringComparison.Ordinal);

            Assert.False(d.ProcessOutbound(Modify(1, 0x00, 0, 0, 10, 40)));
            Assert.Contains("horizontal windowing", errors[^1], StringComparison.Ordinal);

            // The cell size this display has is taken: it changes nothing.
            Assert.True(d.ProcessOutbound(Modify(1, 0x00, 0, 0, 10, 80, 0, 0, 1, 0, d.CellWidth, d.CellHeight)));
            Assert.Equal(4, errors.Count);
        }

        [Fact]
        public void ModifyInImplicitPartitionStateIsRefusedByName()
        {
            // p. 5-34 speaks of partitions made by Create Partition; what modifying the implicit
            // partition 0 means is not said, so it is refused out loud.
            var (d, _, errors) = Create();

            Assert.False(d.ProcessOutbound(Modify(0, 0x00, 0)));

            Assert.Single(errors);
            Assert.Contains("implicit partition state", errors[0], StringComparison.Ordinal);
        }

        [Fact]
        public void AZeroViewportOnCreateIsTakenNowThatModifyIsSupported()
        {
            // p. 5-10 rejects WV = 0 or HV = 0 only when "modify partition is not supported".
            // The new zero-extent partition is not made active: it is not "the only partition
            // with a window of nonzero extent" (p. 5-10). It overlaps nothing.
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 10, 10, 0));

            Assert.True(d.ProcessOutbound(CreatePartition(2, 10, 0, 5)));

            Assert.Empty(errors);
            Partition3270? p2 = d.GetPartition(2);
            Assert.NotNull(p2);
            Assert.True(p2.ZeroWindow);
            Assert.Equal(1, d.ActivePartitionId);
        }

        [Fact]
        public void AZeroExtentWindowCannotBeActivated()
        {
            // "If the named partition does not exist or has a window of zero extent, the
            // structured field is rejected" (p. 5-8 step 1; Table B-1, Activate Partition).
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 10, 10, 0));
            d.ProcessOutbound(CreatePartition(2, 10, 0, 12));

            Assert.False(d.ProcessOutbound(Activate(2)));

            Assert.Single(errors);
            Assert.Equal(1, d.ActivePartitionId);
        }

        [Fact]
        public void VaryingTheActiveWindowToZeroActivatesTheNextWithANonzeroWindow()
        {
            // "If the size of the window is varied to zero and this partition is Active, the next
            // partition (if any) with a nonzero window is activated" (p. 5-34). Partitions 1, 2
            // and 3; 2 has a zero window, so the next after 1 is 3.
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 5, 5, 0));
            d.ProcessOutbound(CreatePartition(2, 5, 0, 6));
            d.ProcessOutbound(CreatePartition(3, 5, 5, 12));
            Assert.Equal(1, d.ActivePartitionId);

            Assert.True(d.ProcessOutbound(Modify(1, 0x00, 0, 0, 0)));

            Assert.Empty(errors);
            Assert.Equal(3, d.ActivePartitionId);
            Assert.False(d.CursorOnIndicatorRow);
        }

        [Fact]
        public void WithEveryWindowZeroThePartitionStaysActiveButCannotBeTypedInto()
        {
            // "If every partition has a zero extent window, this partition remains active but the
            // operator cannot type data into it, and the screen cursor is placed at the
            // right-hand end of the indicator row" (p. 5-34). And at the end of the transmission
            // "all partitions have zero extent windows" is an error (p. 5-13 note 3; Table B-1,
            // sense X'0883').
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 5, 5, 0));
            d.ProcessOutbound(Outbound(1, Command3270.Write, Wcc.KeyboardRestore, Order3270.StartField, 0x00, Order3270.InsertCursor));
            Assert.True(d.Type('A'));
            Assert.Empty(errors);

            d.ProcessOutbound(Modify(1, 0x00, 0, 0, 0));

            Assert.Single(errors);
            Assert.Contains("X'0883'", errors[0], StringComparison.Ordinal);
            Assert.Equal(1, d.ActivePartitionId);
            Assert.True(d.CursorOnIndicatorRow);
            Assert.False(d.Type('B'));
            Assert.False(d.Delete());
        }

        [Fact]
        public void TheOnlyPartitionWithANonzeroWindowIsActivated()
        {
            // "If this is the only partition with a window of nonzero extent, then it is
            // Activated" (p. 5-34). Partition 2 is made with a zero window; partition 1 is
            // destroyed, which activates 2 though its window is zero ("If all other partitions
            // have zero-extent windows, Destroy Partition activates the next partition", p. 5-14);
            // then 2 is given rows and stays the active one, now with a cursor to type at.
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreatePartition(1, 5, 5, 0));
            d.ProcessOutbound(CreatePartition(2, 5, 0, 12));
            d.ProcessOutbound(CreatePartition(3, 5, 0, 18));

            d.ProcessOutbound(Destroy(1));
            Assert.Equal(2, d.ActivePartitionId);
            Assert.True(d.CursorOnIndicatorRow);
            errors.Clear();

            Assert.True(d.ProcessOutbound(Modify(3, 0x00, 18, 0, 5)));

            Assert.Empty(errors);
            Assert.Equal(3, d.ActivePartitionId);
            Assert.False(d.CursorOnIndicatorRow);
        }
    }
}
