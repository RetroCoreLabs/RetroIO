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
    /// Fixes where the file-access message body starts inside a datagram, from a real captured
    /// frame rather than from the sum of two size constants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FaServer.SessionBody</c> took the body at <c>SintranHeader.Size + XmsgSubHeader.Size</c>
    /// = <c>13 + 19</c> = <b>32</b>. Measured, it starts at <b>28</b>, so the server was reading
    /// four bytes into every request it ever handled - which means it never once parsed one
    /// correctly, and the file service could not have worked whatever else was fixed.
    /// </para>
    /// <para>
    /// The SINTRAN header is 14 bytes, not 13: word 6 is a ones-complement sum of words 0-5 with
    /// end-around carry, carved from the XMSG kernel and verified on 3595 of 3595 frames. The byte
    /// the old model called the sub-header's <c>Counter</c> at offset 13 is that checksum's low
    /// half, so the sub-header really begins at 14 - which is why it is the marker <c>0x21 0x00</c>
    /// that appears there, and not a counter.
    /// </para>
    /// <para>
    /// This test deliberately checks the WRONG offset too. An assertion that 28 parses proves
    /// little on its own, because a tolerant parser might accept many offsets; showing that 32 does
    /// NOT parse is what makes the measurement falsifiable.
    /// </para>
    /// </remarks>
    public sealed class FaBodyOffsetTests
    {
        /// <summary>
        /// Offset of the file-access body inside a datagram: 14-byte SINTRAN header plus 14-byte
        /// XMSG sub-header.
        /// </summary>
        private const int MeasuredBodyOffset = 28;

        /// <summary>
        /// The offset the server used before this was measured (13 + 19).
        /// </summary>
        private const int OldIncorrectBodyOffset = 32;

        /// <summary>
        /// One real datagram: the file-access conversation-open that D100 sent to D102, taken from
        /// <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-open-error.txt</c>. Only the SINTRAN
        /// datagram is kept here - the Ethernet, LLC and ND link headers ahead of it are stripped,
        /// because those belong to the transport and the body offset is measured from the datagram.
        /// </summary>
        /// <remarks>
        /// Laid out:
        /// <code>
        /// abs 0-13   2113 000E 0066 0064 028E 0070 DB16   SINTRAN header, checksum DB16
        /// abs 14-27  2100 8284 0066 06B6 0064 0848 0070   sub-header, marker 2100
        /// abs 28+    07F0 0045 80 00 0001 ...             body: message type 07F0
        /// </code>
        /// </remarks>
        private const string OpenConversationDatagramHex =
            // SINTRAN header, 14 bytes.
            "2113000E00660064028E0070DB16"
            // XMSG sub-header, 14 bytes, opening with the 2100 marker.
            + "21008284006606B6006408480070"
            // File-access body.
            + "07F0004580000001920002920001F20001A207D0F200028C06920001920001"
            + "F20003BD42414B3034202053595354454D"
            + "F200048C38"
            + "B01053595354454D27000000000000000000E180"
            + "B01000000000000000000000000000000000"
            + "B01000000000000000000000000000000000"
            + "F200FF";

        /// <summary>
        /// At the measured offset the body opens with a real file-access envelope.
        /// </summary>
        [Fact]
        public void BodyAt28_ParsesAsAFileAccessEnvelope()
        {
            byte[] datagram = Convert.FromHexString(OpenConversationDatagramHex);

            bool parsed = FaExchangeCodec.TryReadEnvelope(
                new ReadOnlySpan<byte>(datagram, MeasuredBodyOffset, datagram.Length - MeasuredBodyOffset),
                out FaMessageType messageType,
                out ushort conversation,
                out byte sequenceByte,
                out ushort sessionToken);

            Assert.True(parsed, "the body did not parse at the measured offset 28");

            // 07F0 - a request carrying a body. Conversation 0x0045 is the asker's, and 0x80 is the
            // exchange counter's first value on the asking side.
            Assert.Equal(FaMessageType.Request, messageType);
            Assert.Equal(0x0045, conversation);
            Assert.Equal(0x80, sequenceByte);
        }

        /// <summary>
        /// At the offset the server used, the same datagram does NOT yield that envelope - which is
        /// what makes the 28 a measurement and not a preference.
        /// </summary>
        [Fact]
        public void BodyAt32_DoesNotParseAsTheSameEnvelope()
        {
            byte[] datagram = Convert.FromHexString(OpenConversationDatagramHex);

            bool parsed = FaExchangeCodec.TryReadEnvelope(
                new ReadOnlySpan<byte>(datagram, OldIncorrectBodyOffset, datagram.Length - OldIncorrectBodyOffset),
                out FaMessageType messageType,
                out ushort _,
                out byte _,
                out ushort _);

            // Four bytes late lands in the middle of the envelope, so either the parse fails or it
            // succeeds on nonsense. Both are wrong; what must NOT happen is reading the true message.
            bool readTheRealMessage = parsed && messageType == FaMessageType.Request;
            Assert.False(readTheRealMessage, "offset 32 must not yield the real envelope");
        }
    }
}
