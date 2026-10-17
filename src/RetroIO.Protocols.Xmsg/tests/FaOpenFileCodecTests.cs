//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Fa;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Covers the <c>OpenFile</c> and <c>CloseFile</c> bodies against the captured exchanges.
    /// </summary>
    /// <remarks>
    /// The bytes here are lifted from
    /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-read.txt</c>,
    /// <c>capture-write.txt</c> and <c>capture-open-error.txt</c>, so a wrong reading fails against
    /// a real ND machine's traffic rather than against our own encoder.
    /// </remarks>
    public sealed class FaOpenFileCodecTests
    {
        /// <summary>
        /// The captured read open is parsed: name, sequence, and no write access.
        /// </summary>
        /// <remarks>
        /// From <c>capture-read.txt</c>. The string's declared length is <c>0x10</c> = 16 for a
        /// 14-character name, because the terminator and one junk byte are counted in.
        /// </remarks>
        [Fact]
        public void TheCapturedReadOpenIsParsed()
        {
            byte[] body = Hex.ToBytes("920005920002F20002B01050415443482D46494C453A4F55542754F200FF");

            ushort serial;
            string name;
            bool forWrite;
            Assert.True(FaOpenFileCodec.TryReadRequest(body, out serial, out name, out forWrite));

            Assert.Equal(2, serial);
            Assert.Equal("PATCH-FILE:OUT", name);
            Assert.False(forWrite);
        }

        /// <summary>
        /// The captured write open is parsed, and its selector 3 marks it as a write.
        /// </summary>
        /// <remarks>
        /// From <c>capture-write.txt</c>. The quote characters are part of the name on the wire -
        /// quoting is how a SINTRAN caller asks for the file to be created - so they survive here.
        /// </remarks>
        [Fact]
        public void TheCapturedWriteOpenIsParsedAsAWrite()
        {
            // BF is class 3 with the length in the nibble: 15 bytes = 13 characters, the 0x27
            // terminator, and the one junk byte the protocol leaves after a string.
            byte[] body = Hex.ToBytes("920005920002F20002BF22575254455354313A4F5554222757F20003920001F200FF");

            ushort serial;
            string name;
            bool forWrite;
            Assert.True(FaOpenFileCodec.TryReadRequest(body, out serial, out name, out forWrite));

            Assert.Equal("\"WRTEST1:OUT\"", name);
            Assert.True(forWrite);
        }

        /// <summary>
        /// The reply reproduces the captured success byte for byte.
        /// </summary>
        /// <remarks>
        /// The capture answered the read open with file number <c>0x0040</c> and size
        /// <c>0x000045F1</c> = 17905.
        /// </remarks>
        [Fact]
        public void TheReplyReproducesTheCapturedBytes()
        {
            byte[] expected = Hex.ToBytes("920005920002F20002A20040F20003A4000045F1F200FF");

            byte[] actual = FaOpenFileCodec.BuildReply(serial: 2, fileNumber: 0x0040, byteLength: 0x45F1);

            Assert.Equal(expected, actual);
        }

        /// <summary>
        /// An empty file gets no size selector, which is what the capture does.
        /// </summary>
        /// <remarks>
        /// The write open's reply on a freshly created file carries selector 2 and then ends. The
        /// directory entry expresses the same thing as <c>0xFFFFFFFF</c>, the index of the last byte
        /// of a file with none.
        /// </remarks>
        [Fact]
        public void AnEmptyFileGetsNoSizeSelector()
        {
            byte[] expected = Hex.ToBytes("920005920002F20002A20040F200FF");

            byte[] actual = FaOpenFileCodec.BuildReply(serial: 2, fileNumber: 0x0040, byteLength: 0);

            Assert.Equal(expected, actual);
        }

        /// <summary>
        /// The close reply carries nothing but the echo.
        /// </summary>
        [Fact]
        public void TheCloseReplyIsJustTheEcho()
        {
            byte[] expected = Hex.ToBytes("92000692000EF200FF");

            byte[] actual = FaOpenFileCodec.BuildCloseReply(serial: 0x000E);

            Assert.Equal(expected, actual);
        }
    }
}
