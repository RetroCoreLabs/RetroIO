//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Protocols.Xmsg.Qform;
using Xunit;
using static RetroIO.Protocols.Xmsg.TestSupport.TestHex;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// The QFORM reader, checked against bytes captured from a live SINTRAN file-access session.
    /// </summary>
    public sealed class QformReaderTests
    {
        /// <summary>
        /// The field-3 string from the captured request parses to exactly the 13 characters that
        /// were on the wire.
        /// </summary>
        /// <remarks>
        /// Tag 0xBD is class 3 with a low nibble of 13. "BAK03  SYSTEM" is 13 characters including
        /// the two spaces, so this pins the class-1-to-7 length rule against known content.
        /// </remarks>
        [Fact]
        public void ByteStringField_ParsesToItsExactCapturedLength()
        {
            byte[] body = FromHex("F20003 BD 42414B3033202053595354454D");

            IReadOnlyList<QformField> fields = QformReader.Read(body);

            Assert.Equal(2, fields.Count);

            Assert.Equal(QformClass.Selector, fields[0].Class);
            Assert.Equal(2, fields[0].ValueLength);

            Assert.Equal(QformClass.ByteString, fields[1].Class);
            Assert.Equal(13, fields[1].ValueLength);

            string text = System.Text.Encoding.ASCII.GetString(body, fields[1].ValueOffset, fields[1].ValueLength);
            Assert.Equal("BAK03  SYSTEM", text);
        }

        /// <summary>
        /// A class-3 tag with a zero length nibble takes its length from the following byte.
        /// </summary>
        /// <remarks>
        /// Captured as 0xB0 0x10 introducing 16 bytes holding "SECRET" plus NUL padding. Without the
        /// escape a 4-bit nibble could never carry a 16-byte value.
        /// </remarks>
        [Fact]
        public void EscapedLength_TakesTheLengthFromTheFollowingByte()
        {
            byte[] body = FromHex("B0 10 53454352455427000000000000000000");

            IReadOnlyList<QformField> fields = QformReader.Read(body);

            Assert.Single(fields);
            Assert.Equal(QformClass.ByteString, fields[0].Class);
            Assert.Equal(16, fields[0].ValueLength);
        }

        /// <summary>
        /// Tag 0x8C is CONSTRUCTED: its low nibble is a subtype, its length is escaped, and its
        /// content is itself tagged.
        /// </summary>
        /// <remarks>
        /// This is the tag that defeated the earlier flat reader. Captured as
        /// <c>F2 0002  8C 06  92 0001 92 0001</c>: field 2 holds a 6-byte constructed value whose
        /// content is two more tagged integers. A flat walk reads those inner bytes as top-level
        /// tags and desynchronises for the rest of the message.
        /// </remarks>
        [Fact]
        public void ConstructedField_IsLengthDelimitedAndItsContentIsTagged()
        {
            byte[] body = FromHex("F20002 8C 06 920001 920001");

            IReadOnlyList<QformField> fields = QformReader.Read(body);

            // selector, the constructed value, and the two integers nested inside it
            Assert.Equal(4, fields.Count);

            Assert.Equal(QformClass.Selector, fields[0].Class);

            Assert.Equal(QformClass.Constructed, fields[1].Class);
            Assert.True(fields[1].IsConstructed);
            Assert.Equal(4, fields[1].ConstructedSubtype);   // 0x8C & 0x17
            Assert.Equal(6, fields[1].ValueLength);
            Assert.Equal(0, fields[1].Depth);

            // The nested integers are reported one level deeper, not as top-level fields.
            Assert.Equal(QformClass.Integer, fields[2].Class);
            Assert.Equal(1, fields[2].Depth);
            Assert.Equal(QformClass.Integer, fields[3].Class);
            Assert.Equal(1, fields[3].Depth);
        }

        /// <summary>
        /// A tag byte with bit 7 clear ends the stream, so trailing padding terminates a body
        /// instead of being read as a tag.
        /// </summary>
        /// <remarks>
        /// This is the reader's own first test in the binary (BSKP 7 at ram:0x7d14), not a
        /// convention invented here.
        /// </remarks>
        [Fact]
        public void TagWithBit7Clear_EndsTheStream()
        {
            byte[] body = FromHex("920001 00 920002");

            IReadOnlyList<QformField> fields = QformReader.Read(body);

            Assert.Single(fields);
        }

        /// <summary>
        /// The rejection reply from the capture parses, and the error field is reachable.
        /// </summary>
        /// <remarks>
        /// The wrong-password reply carried <c>F2 0001 A2 0030</c> where the accepted one had
        /// nothing. 0x0030 is 48 decimal, which the SINTRAN III Reference Manual lists as 060 octal
        /// "Wrong password".
        /// </remarks>
        [Fact]
        public void RejectionReply_CarriesTheSintranErrorNumber()
        {
            byte[] body = FromHex("920002 920001 F20001 A20030 F200FF");

            IReadOnlyList<QformField> fields = QformReader.Read(body);

            int errorNumber = -1;
            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i].Class == QformClass.TypedInteger)
                {
                    errorNumber = (body[fields[i].ValueOffset] << 8) | body[fields[i].ValueOffset + 1];
                }
            }

            Assert.Equal(48, errorNumber);
        }

        /// <summary>
        /// The end-of-list selector is the last field, and it carries 0x00FF.
        /// </summary>
        [Fact]
        public void EndOfListSelector_IsTheDocumentedSentinel()
        {
            byte[] body = FromHex("920002 F200FF");

            IReadOnlyList<QformField> fields = QformReader.Read(body);

            QformField last = fields[fields.Count - 1];
            Assert.Equal(QformClass.Selector, last.Class);

            int selector = (body[last.ValueOffset] << 8) | body[last.ValueOffset + 1];
            Assert.Equal(QformReader.EndOfListSelector, selector);
        }

        /// <summary>
        /// A length byte of 0x80 is an escape marker: the real length is the byte after it.
        /// </summary>
        /// <remarks>
        /// Previously this case was refused as undecoded. Frame 23 of
        /// <c>fa-access-secret-102-to-100-2026-07-29.pcapng</c> resolved it.
        /// </remarks>
        [Fact]
        public void EscapeMarker_TakesTheLengthFromTheByteAfterIt()
        {
            byte[] body = FromHex("B0 80 04 00000000");

            IReadOnlyList<QformField> fields = QformReader.Read(body);

            Assert.Single(fields);
            Assert.Equal(4, fields[0].ValueLength);
        }

        /// <summary>
        /// The captured constructed value whose escaped length is confirmed by arithmetic.
        /// </summary>
        /// <remarks>
        /// Frame 23 carries <c>8C 80 46</c>: an escaped length of 0x46 = 70, and the contents
        /// account for exactly 70 bytes - a 62-byte string under <c>B0 3E</c> (2 + 62) plus two
        /// three-byte <c>A2</c> integers. Had 0x80 meant anything else the inner fields could not
        /// close on the declared boundary.
        /// </remarks>
        [Fact]
        public void CapturedEscapedConstructedValue_ContentsAccountForItsDeclaredLength()
        {
            byte[] body = FromHex(
                "8C 80 46"
                + "B0 3E 28534543524554292745542853454352455429292E285345435245542927000048FF"
                + "00000000000000000000000000000000000000000000000000000000"
                + "A2 0000"
                + "A2 FFFF");

            IReadOnlyList<QformField> fields = QformReader.Read(body);

            Assert.Equal(4, fields.Count);

            Assert.True(fields[0].IsConstructed);
            Assert.Equal(70, fields[0].ValueLength);

            // The three nested fields sit one level down and consume the constructed value exactly.
            Assert.Equal(1, fields[1].Depth);
            Assert.Equal(62, fields[1].ValueLength);
            Assert.Equal(1, fields[2].Depth);
            Assert.Equal(1, fields[3].Depth);

            int consumed = (2 + 62) + (1 + 2) + (1 + 2);
            Assert.Equal(fields[0].ValueLength, consumed);
        }

        /// <summary>
        /// A value whose length runs past the end of the body is rejected.
        /// </summary>
        [Fact]
        public void OverlongValue_IsRejected()
        {
            byte[] body = FromHex("BD 4142");

            Assert.Throws<QformFormatException>(() => QformReader.Read(body));
        }
    }
}
