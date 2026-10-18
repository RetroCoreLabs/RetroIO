//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Protocols.Xmsg.Fa;
using RetroIO.Protocols.Xmsg.Hdlc;
using RetroIO.Protocols.Xmsg.Tests;
using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Establishes how a file name travels in a CREATE-FILE request, and how the string field
    /// declares its own length.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The experiment. Two CREATE-FILE commands were run from node 102 against node 100 on
    /// 2026-07-30, changing exactly one thing - whether the directory was named in the file
    /// specification:
    /// </para>
    /// <code>
    /// d100(system).ronny-r2:txt
    /// d100(system).(pack-one:system)ronny-r3:txt
    /// </code>
    /// <para>
    /// Everything else was held constant: same command, same page count, same user, same server.
    /// So any difference in the bytes is attributable to that one change.
    /// </para>
    /// <para>
    /// Result. The directory name is carried verbatim inside the SAME field as the file name.
    /// There is no separate directory field, so the server parses the specification string itself.
    /// </para>
    /// </remarks>
    public sealed class FaFileNameFieldTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public FaFileNameFieldTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// CREATE-FILE with a bare file name.
        /// </summary>
        private const string PlainCapture = "2026-07-30-0957-fa-create-file-d102-to-d100.pcapng";

        /// <summary>
        /// CREATE-FILE with the directory named in the file specification.
        /// </summary>
        private const string DirectoryCapture = "2026-07-30-1041-fa-create-file-dir-in-spec-d102-to-d100.pcapng";

        /// <summary>
        /// Frame offset at which the message body begins.
        /// </summary>
        private const int BodyOffsetFullHeader = 28;

        /// <summary>
        /// The tag that introduces a byte string.
        /// </summary>
        private const byte ByteStringTagBase = 0xB0;

        /// <summary>
        /// Requires the directory name to travel inside the file-name field, and the string field to
        /// declare its length two different ways depending on how long it is.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The encoding rule this establishes. A byte string is tagged <c>0xB0 | n</c>. When
        /// <c>n</c> is 1 to 15 it IS the length. When <c>n</c> is 0 the tag is a bare <c>0xB0</c> and
        /// the length is the byte that follows.
        /// </para>
        /// <para>
        /// The short name is 14 bytes and arrives as <c>0xBE</c>. Adding the directory pushes it to 31
        /// bytes, which does not fit in a nibble, and it arrives as <c>0xB0 0x1F</c>. That is what makes
        /// this a real test of the rule rather than of one case - the change crosses the 15-byte
        /// boundary where the encoding has to switch form.
        /// </para>
        /// <para>
        /// It also explains the <c>B0 3E</c> (62) and <c>B0 40</c> (64) forms seen in the directory
        /// listing recordings, which had been read as an unrelated tag.
        /// </para>
        /// </remarks>
        [Fact]
        public void DirectoryName_TravelsInsideTheFileNameField()
        {
            byte[]? plain = FindCreateRequest(PlainCapture);
            byte[]? withDirectory = FindCreateRequest(DirectoryCapture);

            if (plain == null || withDirectory == null)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            string plainName;
            int plainDeclared;
            Assert.True(TryReadNameField(plain, out plainName, out plainDeclared));

            string directoryName;
            int directoryDeclared;
            Assert.True(TryReadNameField(withDirectory, out directoryName, out directoryDeclared));

            _output.WriteLine("no directory  : declared=" + plainDeclared + " text=" + plainName);
            _output.WriteLine("with directory: declared=" + directoryDeclared + " text=" + directoryName);

            // The directory appears in the same field, spelled exactly as it was typed.
            Assert.Contains("(PACK-ONE:SYSTEM)", directoryName);
            Assert.DoesNotContain("(PACK-ONE:SYSTEM)", plainName);

            // Both still carry the file name and the SINTRAN string terminator.
            Assert.Contains("RONNY-R2:TXT", plainName);
            Assert.Contains("RONNY-R3:TXT", directoryName);
            Assert.Contains("'", plainName);
            Assert.Contains("'", directoryName);

            // The short form fits in the tag's low nibble; the long one cannot and must use the
            // explicit-length form. This is the part that tests the encoding rather than one case.
            Assert.Equal(14, plainDeclared);
            Assert.Equal(31, directoryDeclared);
            Assert.True(plainDeclared <= 15, "the short name should fit the nibble form");
            Assert.True(directoryDeclared > 15, "the long name should not fit the nibble form");
        }

        /// <summary>
        /// Requires the name field to be sized from the name, and records that it always carries one
        /// byte too many.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The experiment. Three more files were created on 2026-07-30 with deliberately
        /// different name lengths - <c>A:T</c>, <c>QQQQ:T</c> and the 16-character
        /// <c>ABCDEFGHIJKLMNOP:TXT</c> - to test whether the field is fixed width with rubbish after
        /// the terminator.
        /// </para>
        /// <para>
        /// It is not fixed width. The declared length tracks the name: 5, 8, 14, 22 and 31
        /// across the recordings. So a client must compute it, not pad to a constant.
        /// </para>
        /// <para>
        /// But it is always one byte too long. In every recording the declared length is
        /// (characters + the <c>0x27</c> terminator) + 1, and that final byte differs every time
        /// without tracking the name:
        /// </para>
        /// <code>
        /// A:T'                            declared 5   extra 0x28 '('
        /// QQQQ:T'                         declared 8   extra 0x53 'S'
        /// RONNY-R2:TXT'                   declared 14  extra 0x52 'R'
        /// ABCDEFGHIJKLMNOP:TXT'           declared 22  extra 0x49 'I'
        /// (PACK-ONE:SYSTEM)RONNY-R3:TXT'  declared 31  extra 0x52 'R'
        /// XFERTEST:DATA'                  declared 15  extra 0x46 'F'
        /// </code>
        /// <para>
        /// That looks like the sender measuring one past the terminator and shipping whatever sits
        /// next in its buffer. Its meaning is UNKNOWN and it must not be treated as a field.
        /// </para>
        /// <para>
        /// Every one of those requests succeeded with a different value there, which suggests the
        /// server ignores it - but that is INFERRED from six samples the real client produced, not
        /// tested. Sending a deliberately chosen byte would test it; that has not been done.
        /// </para>
        /// </remarks>
        [Fact]
        public void NameField_IsSizedFromTheNameAndCarriesOneByteTooMany()
        {
            string[] captures = new string[]
            {
                PlainCapture,
                DirectoryCapture,
                "2026-07-30-1049-fa-create-file-name-lengths-d102-to-d100.pcapng",
            };

            int checkedFields = 0;

            for (int c = 0; c < captures.Length; c++)
            {
                List<byte[]> requests = FindAllCreateRequests(captures[c]);
                if (requests.Count == 0) { continue; }

                for (int r = 0; r < requests.Count; r++)
                {
                    string text;
                    int declared;
                    if (!TryReadNameField(requests[r], out text, out declared)) { continue; }

                    int terminator = text.IndexOf('\'');
                    Assert.True(terminator >= 0, "no SINTRAN string terminator in: " + text);

                    // Characters, plus the terminator, plus exactly one more.
                    int expected = terminator + 1 + 1;

                    _output.WriteLine("declared=" + declared + " expected=" + expected
                        + " name=" + text.Substring(0, terminator)
                        + " extra='" + text.Substring(text.Length - 1) + "'");

                    Assert.Equal(expected, declared);
                    checkedFields++;
                }
            }

            if (checkedFields == 0)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            // Five creates across three recordings, with five different name lengths.
            Assert.True(checkedFields >= 5, "expected at least five create requests, saw " + checkedFields);
        }

        /// <summary>
        /// Reads the byte-string field that follows selector 2 in a create request.
        /// </summary>
        /// <param name="body">
        /// The message body.
        /// </param>
        /// <param name="text">
        /// The field contents rendered as text.
        /// </param>
        /// <param name="declaredLength">
        /// The length the field declares, however it encoded it.
        /// </param>
        /// <returns>
        /// <c>true</c> when the field was found.
        /// </returns>
        private static bool TryReadNameField(byte[] body, out string text, out int declaredLength)
        {
            text = string.Empty;
            declaredLength = 0;

            for (int i = 0; i + 2 < body.Length; i++)
            {
                // F2 0002 introduces the field carrying the name.
                if (body[i] != 0xF2 || body[i + 1] != 0x00 || body[i + 2] != 0x02) { continue; }

                int at = i + 3;
                if (at >= body.Length) { return false; }

                byte tag = body[at];
                if ((tag & 0xF0) != ByteStringTagBase) { continue; }

                int low = tag & 0x0F;
                int valueAt;
                if (low == 0)
                {
                    declaredLength = body[at + 1];
                    valueAt = at + 2;
                }
                else
                {
                    declaredLength = low;
                    valueAt = at + 1;
                }

                if (valueAt + declaredLength > body.Length) { return false; }

                System.Text.StringBuilder builder = new System.Text.StringBuilder(declaredLength);
                for (int j = 0; j < declaredLength; j++)
                {
                    byte b = body[valueAt + j];
                    builder.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                }

                text = builder.ToString();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Finds the create request in a recording.
        /// </summary>
        /// <param name="captureName">
        /// The recording to read.
        /// </param>
        /// <returns>
        /// The request body, or <c>null</c> when the recording is absent.
        /// </returns>
        private static byte[]? FindCreateRequest(string captureName)
        {
            List<byte[]> all = FindAllCreateRequests(captureName);
            return all.Count == 0 ? null : all[0];
        }

        /// <summary>
        /// Finds every create request in a recording.
        /// </summary>
        /// <param name="captureName">
        /// The recording to read.
        /// </param>
        /// <returns>
        /// The request bodies, empty when the recording is absent.
        /// </returns>
        private static List<byte[]> FindAllCreateRequests(string captureName)
        {
            List<byte[]> found = new List<byte[]>();

            string? path = PcapFiles.File(captureName);
            if (path == null) { return found; }

            IReadOnlyList<LapbFrame> frames = HdlcPcap.ReadFramesInCaptureOrder(path);
            for (int i = 0; i < frames.Count; i++)
            {
                LapbFrame frame = frames[i];
                if (frame.Kind != LapbFrameKind.Information
                    || frame.Info.Length <= BodyOffsetFullHeader)
                {
                    continue;
                }

                ReadOnlySpan<byte> info = frame.Info.Span;
                if (info[3] != (byte)SintranPacketSubtype.Data) { continue; }

                ReadOnlySpan<byte> body = info.Slice(BodyOffsetFullHeader);

                FaOperation operation;
                ushort sequence;
                if (!FaExchangeCodec.TryReadOperation(body, out operation, out sequence)) { continue; }
                if (operation != FaOperation.CreateFile) { continue; }

                found.Add(body.ToArray());
            }

            return found;
        }
    }
}
