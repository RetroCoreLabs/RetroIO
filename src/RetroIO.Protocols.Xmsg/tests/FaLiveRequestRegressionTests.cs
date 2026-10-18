//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Fa;
using RetroIO.Protocols.Xmsg.Qform;

using Xunit;
using static RetroIO.Protocols.Xmsg.TestSupport.TestHex;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Pins the parsing of requests captured from a REAL D100, byte for byte.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this file exists</b></para>
    /// The listing goldens in <c>FaListingRegressionTests</c> compare every frame we EMIT. They
    /// cannot catch a fault in what we PARSE, and one got through: on 2026-08-06 a live D100
    /// answered every <c>LIST-FILES</c> with <c>END OF FILE</c> because each request was refused
    /// with "carries no sub-function", while all 634 tests passed.
    /// <para>
    /// So the rule this file follows is: a request body taken off the wire goes in here verbatim,
    /// as hex, with the log line it came from. No hand-built equivalent - the whole point is the
    /// bytes nobody thought to write by hand.
    /// </para>
    /// </remarks>
    public sealed class FaLiveRequestRegressionTests
    {
        /// <summary>
        /// A <c>LIST-FILES</c> directory-enquiry request body, captured live.
        /// </summary>
        /// <remarks>
        /// <para>
        /// From <c>spec-capture.log</c>, 2026-08-06 08:59:27.586, frame
        /// <c>[RX] 100->103 sub=Data f1=0x004B</c>, driven by <c>LIST-FILES</c> /
        /// <c>D103(sys).</c> at the D100 terminal. This is the body only - the 14-byte SINTRAN
        /// header and the 14-byte XMSG sub-header are stripped.
        /// </para>
        /// <para>
        /// The declared body length is <c>0x0064</c> = 100 bytes and this string is exactly that
        /// long, so the final byte is genuinely sent by SINTRAN and is not framing slop.
        /// </para>
        /// </remarks>
        private const string LiveListFilesRequestHex =
            // message type 07F0, conversation 0042, session header 8100 D761
            "07F000428100D761"

            // 92 000C operation | 92 0002 serial | F2 0001 | 92 0078 sub-function | F2 0002
            + "92000C920002F20001920078F20002"

            // 8C 80 46 constructed, escaped length 0x46 = 70 bytes
            + "8C8046"

            // B0 3E - the 62-byte spec block. Printable part reads (SYS)'YS).(SYS)' - the
            // terminator 0x27 follows the user's bracket at once, so no file is named.
            + "B03E"
            + "2853595329275953292E2853595329270000024000003704"
            + "B5DC00000000000048FF0000000000000000000000000000"
            + "0000000000000000000000000000"

            // A2 0000 | A2 FFFF cursor - still inside the constructed block
            + "A20000A2FFFF"

            // F2 00FF end of list, then ONE 0xF0 byte padding the body to an even 100
            + "F200FF"
            + "F0";

        /// <summary>
        /// A <c>SetBlockSize</c> request body, captured live.
        /// </summary>
        /// <remarks>
        /// <para>
        /// From <c>read-capture.log</c>, 2026-08-06 10:42:42.456, frame
        /// <c>[RX] 100->103 sub=Data f1=0x0026</c>, driven by <c>COPY-FILE</c> reading
        /// <c>D103(sys).README:TXT</c> at the D100 terminal. Body only.
        /// </para>
        /// <para><b>Identical to the 2026-08-04 capture but for the conversation number</b></para>
        /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-read.txt</c> line 49 carries
        /// <c>07F0 0042 8200 D761 92 0007 92 0003 F2 0001 A2 0800 F2 00FF 0A</c> - the same
        /// operation, sequence, selector, value and trailing pad, from a different machine pair on a
        /// different day.
        /// </remarks>
        private const string LiveSetBlockSizeRequestHex =
            "07F000438200D761"
            + "920007"      // operation 7 = SetBlockSize
            + "920003"      // sequence 3
            + "F20001"      // selector 1
            + "A20800"      // 0x0800 = 2048 bytes
            + "F200FF"      // end of list
            + "0A";         // pad - bit 7 CLEAR here, where the listing's pad was 0xF0

        /// <summary>
        /// A <c>CreateFile</c> request body, taken off the wire.
        /// </summary>
        /// <remarks>
        /// From <c>2026-07-30-0957-fa-create-file-d102-to-d100.pcapng</c> - node 102 creating
        /// <c>RONNY-R2:TXT</c> on node 100. Body only.
        /// </remarks>
        private const string CapturedCreateFileRequestHex =
            "07F000458100D761"
            + "92000A"                          // operation 0x000A = CreateFile
            + "920002"                          // sequence 2
            + "F20002"                          // selector 2 - the name follows
            + "BE" + "524F4E4E592D52323A545854" + "27" + "52"   // "RONNY-R2:TXT" + 0x27 + 'R'
            + "F20004"                          // selector 4 - the page count follows
            + "A400000001"                      // 32-bit page count = 1
            + "F200FF"                          // end of list
            + "FF";                             // pad

        /// <summary>
        /// The reply the real server sent to that request, for the record.
        /// </summary>
        /// <remarks>
        /// <c>07F0 0002 8100 909E 92 000A 92 0002 F2 00FF</c> - the plain accepted form. It returns
        /// no value at all, not even a file number. This constant is not asserted against; it is
        /// here so the shape our server answers with can be checked against the real one by eye.
        /// </remarks>
        private const string CapturedCreateFileReplyHex =
            "07F000028100909E" + "92000A" + "920002" + "F200FF";

        /// <summary>
        /// The captured body is the length SINTRAN declared for it.
        /// </summary>
        /// <remarks>
        /// Guards the hex constant itself. If a later edit drops or adds a byte, this fails first
        /// and names the real problem instead of letting a parsing test fail for the wrong reason.
        /// </remarks>
        [Fact]
        public void TheCapturedListFilesBodyIsTheHundredBytesTheFrameDeclared()
        {
            byte[] body = FromHex(LiveListFilesRequestHex);
            Assert.Equal(100, body.Length);
        }

        /// <summary>
        /// A body padded to an even length with a high-bit-set byte still reads.
        /// </summary>
        /// <remarks>
        /// <para><b>The defect this pins</b></para>
        /// The QFORM content of this request is 91 bytes, which is odd, so SINTRAN pads the body to
        /// 92 to keep it on a word boundary. The pad byte is <c>0xF0</c>.
        /// <para>
        /// <c>QformReader</c> stops when it meets a byte with bit 7 CLEAR, which is correct and
        /// carved from the binary. <c>0xF0</c> has bit 7 SET, so the reader took it for a tag,
        /// went looking for its escaped length, ran off the end of the body and threw. Every
        /// listing request was then refused as unreadable.
        /// </para>
        /// <para><b>Why a lone trailing byte can never be a field</b></para>
        /// No QFORM field is one byte long. A tag whose length nibble is non-zero needs that many
        /// value bytes; a tag whose nibble is zero is followed by an escaped length. So a single
        /// byte left at the end is always padding, whatever its value.
        /// </remarks>
        [Fact]
        public void ABodyPaddedToAWordBoundaryWithF0StillParses()
        {
            byte[] body = FromHex(LiveListFilesRequestHex);
            ReadOnlySpan<byte> qform = new ReadOnlySpan<byte>(body, FaExchangeCodec.QformOffset, body.Length - FaExchangeCodec.QformOffset);

            System.Collections.Generic.IReadOnlyList<QformField> fields;
            bool read = QformReader.TryRead(qform, out fields);

            Assert.True(read, "the reader refused a body whose only oddity is a 0xF0 word-alignment pad");
            Assert.True(fields.Count >= 5);
        }

        /// <summary>
        /// The sub-function is read out of the live request.
        /// </summary>
        /// <remarks>
        /// <c>0x0078</c> is the value <c>FaListFilesCodec.BuildRequest</c> writes as
        /// <c>RequestSelector1Value</c>, so the wire and our own builder agree on it. Failing this
        /// is exactly what produced "carries no sub-function; answering BadRequest" in the live log.
        /// </remarks>
        [Fact]
        public void TheSubFunctionIsReadFromTheLiveListFilesRequest()
        {
            byte[] body = FromHex(LiveListFilesRequestHex);
            ReadOnlySpan<byte> qform = new ReadOnlySpan<byte>(body, FaExchangeCodec.QformOffset, body.Length - FaExchangeCodec.QformOffset);

            FaSpecialFunction function;
            bool read = FaListFilesCodec.TryReadFunction(qform, out function);

            Assert.True(read, "the live LIST-FILES request was refused as carrying no sub-function");
            Assert.Equal(0x0078, (int)function);
        }

        /// <summary>
        /// The serial and cursor are read out of the live request.
        /// </summary>
        /// <remarks>
        /// The cursor is <c>0xFFFF</c> here - the value that means "start the walk again" rather
        /// than an index. See <c>FaServerSession.WalkPosition</c>.
        /// </remarks>
        [Fact]
        public void TheSerialAndCursorAreReadFromTheLiveListFilesRequest()
        {
            byte[] body = FromHex(LiveListFilesRequestHex);
            ReadOnlySpan<byte> qform = new ReadOnlySpan<byte>(body, FaExchangeCodec.QformOffset, body.Length - FaExchangeCodec.QformOffset);

            ushort serial;
            ushort cursor;
            bool read = FaListFilesCodec.TryReadRequest(qform, out serial, out cursor);

            Assert.True(read, "the live LIST-FILES request was not readable as a directory enquiry");
            Assert.Equal(2, serial);
            Assert.Equal(0xFFFF, cursor);
        }

        /// <summary>
        /// The block size is read out of the live <c>SetBlockSize</c> request.
        /// </summary>
        /// <remarks>
        /// <para><b>What refusing this cost</b></para>
        /// A <c>COPY-FILE</c> reading from us sends this between <c>OpenFile</c> and the first
        /// <c>ReadFile</c>. While it was answered "NOT SUPPORTED" the client gave up with
        /// <c>NO ANSWER FROM REMOTE SYSTEM; FILE-ACCESS CONNECTION ABORTED</c> - it was the only
        /// thing standing between us and a real client reading a file.
        /// </remarks>
        [Fact]
        public void TheBlockSizeIsReadFromTheLiveSetBlockSizeRequest()
        {
            byte[] body = FromHex(LiveSetBlockSizeRequestHex);
            Assert.Equal(24, body.Length);

            ReadOnlySpan<byte> qform = new ReadOnlySpan<byte>(body, FaExchangeCodec.QformOffset, body.Length - FaExchangeCodec.QformOffset);

            ushort blockSize;
            bool read = FaFileDataCodec.TryReadBlockSize(qform, out blockSize);

            Assert.True(read, "the live SetBlockSize request was refused as carrying no block size");
            Assert.Equal(2048, blockSize);
        }

        /// <summary>
        /// A pad byte with bit 7 clear is tolerated too, not just <c>0xF0</c>.
        /// </summary>
        /// <remarks>
        /// The listing request's pad is <c>0xF0</c> and this one's is <c>0x0A</c>. The pad is
        /// whatever was left in the buffer, which is exactly why the rule has to be "a lone trailing
        /// byte is padding WHATEVER its value" rather than a test against one byte.
        /// </remarks>
        [Fact]
        public void ThePadByteValueVariesBetweenRequests()
        {
            byte[] listing = FromHex(LiveListFilesRequestHex);
            byte[] blockSize = FromHex(LiveSetBlockSizeRequestHex);

            Assert.Equal(0xF0, listing[listing.Length - 1]);
            Assert.Equal(0x0A, blockSize[blockSize.Length - 1]);
        }

        /// <summary>
        /// The name and page count are read out of the captured <c>CreateFile</c> request.
        /// </summary>
        /// <remarks>
        /// The name field is name + <c>0x27</c> + one more byte, the same shape <c>DeleteFile</c>
        /// uses. That trailing byte is <c>'R'</c> here and was <c>'T'</c> and <c>'W'</c> in the two
        /// captured deletes - three samples, three values, and the field is name + 2 long each time,
        /// so it is not word padding. It is not read.
        /// </remarks>
        [Fact]
        public void TheNameAndPageCountAreReadFromTheCapturedCreateFileRequest()
        {
            byte[] body = FromHex(CapturedCreateFileRequestHex);
            ReadOnlySpan<byte> qform = new ReadOnlySpan<byte>(body, FaExchangeCodec.QformOffset, body.Length - FaExchangeCodec.QformOffset);

            string named;
            uint pageCount;
            bool read = FaListFilesCodec.TryReadCreateFile(qform, out named, out pageCount);

            Assert.True(read, "the captured CreateFile request was not readable");
            Assert.Equal("RONNY-R2:TXT", named);
            Assert.Equal(1u, pageCount);
        }

        /// <summary>
        /// The captured <c>CreateFile</c> reply returns no value.
        /// </summary>
        /// <remarks>
        /// Guards the shape our server has to answer with: operation, sequence, end of list, and
        /// nothing between. <c>OpenFile</c> reports a file number; this does not.
        /// </remarks>
        [Fact]
        public void TheCapturedCreateFileReplyCarriesNothingButTheEcho()
        {
            byte[] body = FromHex(CapturedCreateFileReplyHex);
            ReadOnlySpan<byte> qform = new ReadOnlySpan<byte>(body, FaExchangeCodec.QformOffset, body.Length - FaExchangeCodec.QformOffset);

            System.Collections.Generic.IReadOnlyList<QformField> fields;
            Assert.True(QformReader.TryRead(qform, out fields));

            // operation, sequence, end-of-list selector - and nothing else.
            Assert.Equal(3, fields.Count);
        }

        /// <summary>
        /// The spec block in the live request names no file, which is how a whole-directory
        /// listing is asked for.
        /// </summary>
        /// <remarks>
        /// The captured block is <c>(SYS)'YS).(SYS)'</c> - the terminator <c>0x27</c> comes
        /// straight after the user's closing bracket, so the filespec is empty and everything
        /// after it is residue from the caller's buffer. This is the <c>LIST-FILES</c> shape, not the
        /// <c>FILE-STATISTICS</c> one.
        /// </remarks>
        [Fact]
        public void TheLiveListFilesSpecBlockNamesNoFile()
        {
            byte[] body = FromHex(LiveListFilesRequestHex);
            ReadOnlySpan<byte> qform = new ReadOnlySpan<byte>(body, FaExchangeCodec.QformOffset, body.Length - FaExchangeCodec.QformOffset);

            string named;
            bool read = FaListFilesCodec.TryReadRequestedFileName(qform, out named);

            Assert.True(read, "the spec block was not found in the live request");
            Assert.Equal(string.Empty, named);
        }
    }
}
