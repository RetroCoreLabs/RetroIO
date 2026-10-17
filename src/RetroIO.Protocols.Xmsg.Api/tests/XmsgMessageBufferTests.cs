//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Api.Tests
{
    /// <summary>
    /// Proves the message buffer reproduces the displacement, length and whole-message-read rules
    /// the COSMOS Programmer Guide specifies for XFWRI, XFREA, XFWHD and XFRHD.
    /// </summary>
    public sealed class XmsgMessageBufferTests
    {
        /// <summary>
        /// A fresh buffer has its capacity but no content and a zero cursor.
        /// </summary>
        [Fact]
        public void NewBuffer_HasSizeButNoLength()
        {
            XmsgMessageBuffer buffer = new XmsgMessageBuffer(64);

            Assert.Equal(64, buffer.Size);
            Assert.Equal(0, buffer.Length);
            Assert.Equal(0, buffer.Displacement);
            Assert.False(buffer.WholeMessageRead);
        }

        /// <summary>
        /// A write advances the displacement and grows the length to match.
        /// </summary>
        [Fact]
        public void Write_AdvancesDisplacementAndLength()
        {
            XmsgMessageBuffer buffer = new XmsgMessageBuffer(32);
            byte[] payload = new byte[] { 1, 2, 3, 4 };

            int written;
            XmsgStatus status = buffer.Write(payload, 0, false, out written);

            Assert.False(status.IsError);
            Assert.Equal(4, written);
            Assert.Equal(4, buffer.Displacement);
            Assert.Equal(4, buffer.Length);
        }

        /// <summary>
        /// A displacement of -1 appends at the current cursor, which is how repeated writes build
        /// a message up.
        /// </summary>
        [Fact]
        public void Write_MinusOneDisplacement_Appends()
        {
            XmsgMessageBuffer buffer = new XmsgMessageBuffer(32);
            int written;

            buffer.Write(new byte[] { 0xAA, 0xBB }, 0, false, out written);
            buffer.Write(new byte[] { 0xCC, 0xDD }, -1, false, out written);

            Assert.Equal(4, buffer.Length);
            byte[] data = buffer.ToArray();
            Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, data);
        }

        /// <summary>
        /// An odd write displacement is rounded up and the skipped byte is left zero - the manual's
        /// "garbage byte".
        /// </summary>
        [Fact]
        public void Write_OddDisplacement_RoundsUpAndLeavesZeroByte()
        {
            XmsgMessageBuffer buffer = new XmsgMessageBuffer(32);
            int written;

            buffer.Write(new byte[] { 0x11 }, 0, false, out written);
            buffer.Write(new byte[] { 0x22 }, 1, false, out written);

            // Byte 1 was skipped by the rounding, so it is still zero and the payload sits at 2.
            Assert.Equal(3, buffer.Length);
            Assert.Equal(new byte[] { 0x11, 0x00, 0x22 }, buffer.ToArray());
        }

        /// <summary>
        /// A write past the end of the buffer fails with the illegal-displacement error.
        /// </summary>
        [Fact]
        public void Write_PastEnd_Fails()
        {
            XmsgMessageBuffer buffer = new XmsgMessageBuffer(4);
            int written;

            XmsgStatus status = buffer.Write(new byte[] { 1, 2, 3, 4, 5 }, 0, false, out written);

            Assert.True(status.IsError);
            Assert.Equal(XmsgError.XEIDP, status.Error);
            Assert.Equal(0, written);
        }

        /// <summary>
        /// Reading the final byte zeroes the displacement and raises the whole-message-read flag.
        /// </summary>
        [Fact]
        public void Read_ToEnd_SetsWholeMessageReadAndZeroesDisplacement()
        {
            XmsgMessageBuffer buffer = new XmsgMessageBuffer(16);
            int transferred;
            buffer.Write(new byte[] { 1, 2, 3, 4 }, 0, false, out transferred);

            byte[] destination = new byte[4];
            XmsgStatus status = buffer.Read(destination, 0, out transferred);

            Assert.False(status.IsError);
            Assert.Equal(4, transferred);
            Assert.Equal(0, buffer.Displacement);
            Assert.True(buffer.WholeMessageRead);
        }

        /// <summary>
        /// After the whole message has been read, the next write restarts the length at zero, which
        /// is what recycles a received buffer into a reply.
        /// </summary>
        [Fact]
        public void Write_AfterWholeMessageRead_ResetsLength()
        {
            XmsgMessageBuffer buffer = new XmsgMessageBuffer(16);
            int transferred;
            buffer.Write(new byte[] { 1, 2, 3, 4 }, 0, false, out transferred);
            buffer.Read(new byte[4], 0, out transferred);

            buffer.Write(new byte[] { 9 }, 0, false, out transferred);

            Assert.False(buffer.WholeMessageRead);
            Assert.Equal(1, buffer.Length);
            Assert.Equal(new byte[] { 9 }, buffer.ToArray());
        }

        /// <summary>
        /// A partial read leaves the cursor mid-message so the next read resumes from there.
        /// </summary>
        [Fact]
        public void Read_Partial_LeavesCursorForResume()
        {
            XmsgMessageBuffer buffer = new XmsgMessageBuffer(16);
            int transferred;
            buffer.Write(new byte[] { 1, 2, 3, 4, 5, 6 }, 0, false, out transferred);

            byte[] first = new byte[2];
            buffer.Read(first, 0, out transferred);
            Assert.Equal(2, buffer.Displacement);
            Assert.False(buffer.WholeMessageRead);

            byte[] second = new byte[2];
            buffer.Read(second, -1, out transferred);

            Assert.Equal(new byte[] { 3, 4 }, second);
            Assert.Equal(4, buffer.Displacement);
        }

        /// <summary>
        /// Reading more than the message holds returns only what exists.
        /// </summary>
        [Fact]
        public void Read_MoreThanLength_ReturnsOnlyWhatExists()
        {
            XmsgMessageBuffer buffer = new XmsgMessageBuffer(16);
            int transferred;
            buffer.Write(new byte[] { 7, 8 }, 0, false, out transferred);

            byte[] destination = new byte[10];
            buffer.Read(destination, 0, out transferred);

            Assert.Equal(2, transferred);
        }

        /// <summary>
        /// A zero-byte read leaves the cursor exactly where it was.
        /// </summary>
        [Fact]
        public void Read_ZeroBytes_DoesNotMoveCursor()
        {
            XmsgMessageBuffer buffer = new XmsgMessageBuffer(16);
            int transferred;
            buffer.Write(new byte[] { 1, 2, 3, 4 }, 0, false, out transferred);
            buffer.Seek(2);

            buffer.Read(Array.Empty<byte>(), -1, out transferred);

            Assert.Equal(0, transferred);
            Assert.Equal(2, buffer.Displacement);
        }

        /// <summary>
        /// The six-byte user header round-trips and leaves the cursor at six.
        /// </summary>
        [Fact]
        public void Header_RoundTrips_AndLeavesDisplacementAtSix()
        {
            XmsgMessageBuffer buffer = new XmsgMessageBuffer(32);
            byte[] header = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };

            XmsgStatus written = buffer.WriteHeader(header);
            Assert.False(written.IsError);
            Assert.Equal(6, buffer.Displacement);
            Assert.Equal(6, buffer.Length);

            byte[] readBack = new byte[6];
            XmsgStatus read = buffer.ReadHeader(readBack);

            Assert.False(read.IsError);
            Assert.Equal(header, readBack);
            Assert.Equal(6, buffer.Displacement);
        }

        /// <summary>
        /// A buffer smaller than six bytes cannot carry a user header.
        /// </summary>
        [Fact]
        public void WriteHeader_BufferTooSmall_Fails()
        {
            XmsgMessageBuffer buffer = new XmsgMessageBuffer(4);

            XmsgStatus status = buffer.WriteHeader(new byte[] { 1, 2, 3, 4, 5, 6 });

            Assert.True(status.IsError);
            Assert.Equal(XmsgError.XEILM, status.Error);
        }

        /// <summary>
        /// The XFRES option truncates the message before writing.
        /// </summary>
        [Fact]
        public void Write_WithResetLength_TruncatesFirst()
        {
            XmsgMessageBuffer buffer = new XmsgMessageBuffer(16);
            int transferred;
            buffer.Write(new byte[] { 1, 2, 3, 4, 5, 6 }, 0, false, out transferred);

            buffer.Write(new byte[] { 0xFF }, 0, true, out transferred);

            Assert.Equal(1, buffer.Length);
            Assert.Equal(new byte[] { 0xFF }, buffer.ToArray());
        }
    }
}
