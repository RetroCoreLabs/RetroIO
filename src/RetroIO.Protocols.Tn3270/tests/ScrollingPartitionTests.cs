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
    /// Vertical scrolling: a presentation space with more rows than its viewport, the window
    /// origin, Set Window Origin, the scroll keys and automatic scrolling (GA23-0059-4 p. 2-3,
    /// 2-4, 5-10 to 5-13, 5-63, 7-16, 7-17).
    /// </summary>
    public class ScrollingPartitionTests
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

        // Create Partition, 24 bytes up to RS (p. 5-11): PID 1, 16-bit addressing, H x 80 in a
        // viewport HV x 80 at row RV, window origin row RW, scroll RS rows.
        private static byte[] CreateScrolling(int h, int hv, int rv, int rw, int rs) => new byte[]
        {
            Command3270.WriteStructuredField, 0x00, 0x18, 0x0C, 0x01, 0x01, 0x00,
            0x00, (byte)h, 0x00, 80, 0x00, (byte)rv, 0x00, 0x00, 0x00, (byte)hv, 0x00, 80,
            0x00, (byte)rw, 0x00, 0x00, (byte)(rs >> 8), (byte)rs,
        };

        private static byte[] SetWindowOrigin(byte pid, int rw, int cw) => new byte[]
        {
            Command3270.WriteStructuredField, 0x00, 0x08, 0x0B, pid, (byte)(rw >> 8), (byte)rw, (byte)(cw >> 8), (byte)cw,
        };

        // WSF with one Outbound 3270DS to partition 1.
        private static byte[] Outbound(byte command, params byte[] wccAndData)
        {
            int length = 5 + wccAndData.Length;
            var record = new byte[1 + length];
            record[0] = Command3270.WriteStructuredField;
            record[1] = (byte)(length >> 8);
            record[2] = (byte)length;
            record[3] = 0x40;
            record[4] = 0x01;
            record[5] = command;
            Array.Copy(wccAndData, 0, record, 6, wccAndData.Length);
            return record;
        }

        [Fact]
        public void TheCursorOfANewPartitionStartsAtTheWindowOrigin()
        {
            // p. 5-10 item 8: "The current cursor position for the new partition is set to the
            // window origin".
            var (d, _, errors) = Create();

            Assert.True(d.ProcessOutbound(CreateScrolling(40, 12, 0, 5, 3)));

            Assert.Empty(errors);
            Assert.Equal(5, d.ActivePartition.WindowRow);
            Assert.Equal(400, d.CursorAddress);
        }

        [Fact]
        public void TheScreenPositionIsWhatTheWindowShows()
        {
            // p. 2-3: the window is shown "in the corresponding row and column of the viewport".
            // Viewport at screen row 4, window from presentation space row 5: screen row 5 shows
            // presentation space row 6.
            var (d, _, _) = Create();
            d.ProcessOutbound(CreateScrolling(40, 12, 4, 5, 3));

            Assert.True(d.TryGetScreenPosition(5, 3, out Partition3270? p, out int address));
            Assert.NotNull(p);
            Assert.Equal((6 * 80) + 3, address);

            // Below the 12-row viewport there is nothing, though the presentation space goes on.
            Assert.False(d.TryGetScreenPosition(16, 0, out _, out _));
        }

        [Fact]
        public void SetWindowOriginMovesTheWindowAndTheCursorToTheNearestRow()
        {
            // p. 5-63: the cursor is changed only when it falls outside the new window, and then
            // goes to the nearest row; its column stays.
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreateScrolling(40, 12, 0, 0, 1));
            d.ProcessOutbound(Outbound(Command3270.Write, 0x00, Order3270.SetBufferAddress, 0x00, 0x07, Order3270.InsertCursor));

            Assert.True(d.ProcessOutbound(SetWindowOrigin(1, 20, 0)));

            Assert.Empty(errors);
            Assert.Equal(20, d.ActivePartition.WindowRow);
            Assert.Equal((20 * 80) + 7, d.CursorAddress);
        }

        [Fact]
        public void SetWindowOriginPastTheEndOrWithAColumnIsRejected()
        {
            // p. 5-63: RW + HW > H is rejected and the window is not moved; CW > 0 needs
            // horizontal windowing, not supported (Table B-1 p. B-5).
            var (d, _, errors) = Create();
            d.ProcessOutbound(CreateScrolling(40, 12, 0, 2, 1));

            Assert.False(d.ProcessOutbound(SetWindowOrigin(1, 29, 0)));
            Assert.False(d.ProcessOutbound(SetWindowOrigin(1, 0, 1)));

            Assert.Equal(2, errors.Count);
            Assert.Equal(2, d.ActivePartition.WindowRow);
        }

        [Fact]
        public void ScrollUpMovesTheWindowByRsAndDragsTheCursorAlong()
        {
            // p. 7-16, 7-17: the window moves RS rows; a cursor in the top row stays in the top
            // row of the window.
            var (d, _, _) = Create();
            d.ProcessOutbound(CreateScrolling(40, 12, 0, 0, 3));
            Assert.Equal(0, d.CursorAddress);

            Assert.True(d.ScrollUp());

            Assert.Equal(3, d.ActivePartition.WindowRow);
            Assert.Equal(3 * 80, d.CursorAddress);

            // Back down: the cursor at row 3 is inside the window 0-11, so it stays.
            Assert.True(d.ScrollDown());
            Assert.Equal(0, d.ActivePartition.WindowRow);
            Assert.Equal(3 * 80, d.CursorAddress);
        }

        [Fact]
        public void AtTheEndOfThePresentationSpaceScrollingHasNoEffect()
        {
            // p. 7-17: "scrolling up has no effect", with no input inhibit.
            var (d, _, _) = Create();
            d.ProcessOutbound(CreateScrolling(40, 12, 0, 28, 3));

            Assert.False(d.ScrollUp());

            Assert.Equal(28, d.ActivePartition.WindowRow);
            Assert.Equal(KeyboardLock.None, d.Lock);
        }

        [Fact]
        public void TheCursorKeysWrapAtTheViewport()
        {
            // p. 7-16, 7-17: "The cursor movement keys ... wrap at the boundary of the viewport".
            var (d, _, _) = Create();
            d.ProcessOutbound(CreateScrolling(40, 12, 0, 10, 1));
            Assert.Equal(800, d.CursorAddress);

            d.MoveCursorBy(0, -1);

            // Up from the top row of the window goes to its bottom row, 21, not to row 9.
            Assert.Equal(21 * 80, d.CursorAddress);
            Assert.Equal(10, d.ActivePartition.WindowRow);
        }

        [Fact]
        public void AnInsertCursorOutsideTheWindowScrollsByTheFewestRows()
        {
            // p. 7-17: an automatic scroll brings the cursor into the window with the minimum
            // movement: cursor at row 25, window 12 high, so the window starts at row 14.
            var (d, _, _) = Create();
            d.ProcessOutbound(CreateScrolling(40, 12, 0, 0, 1));

            d.ProcessOutbound(Outbound(Command3270.Write, 0x00, Order3270.SetBufferAddress, 0x07, 0xD0, Order3270.InsertCursor));

            Assert.Equal(2000, d.CursorAddress);
            Assert.Equal(14, d.ActivePartition.WindowRow);
        }

        [Fact]
        public void AnEraseWriteBringsTheWindowBackToTheTop()
        {
            // p. 3-6 step 5: the cursor to 0, and with it the window.
            var (d, _, _) = Create();
            d.ProcessOutbound(CreateScrolling(40, 12, 0, 20, 1));

            d.ProcessOutbound(Outbound(Command3270.EraseWrite, 0x00));

            Assert.Equal(0, d.CursorAddress);
            Assert.Equal(0, d.ActivePartition.WindowRow);
        }

        [Fact]
        public void AWindowOriginPastThePresentationSpaceIsRejectedAtCreate()
        {
            // p. 5-10 item 10; Table B-1, Window spec outside presentation space.
            var (d, _, errors) = Create();

            Assert.False(d.ProcessOutbound(CreateScrolling(40, 12, 0, 29, 1)));

            Assert.Single(errors);
            Assert.False(d.ExplicitPartitioned);
        }
    }
}
