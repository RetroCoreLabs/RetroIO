//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Fa;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// The delete request we build is the one a real machine sends.
    /// </summary>
    /// <remarks>
    /// <para><b>Pinned against real traffic, not against ourselves</b></para>
    /// <para>
    /// The bytes below were taken off the wire between two real NDs - D102 deleting
    /// <c>XFERTEST:DATA</c> on D100. A round trip through our own reader would pass even if the
    /// encoding were wrong in a way both halves shared, which is exactly the trap the chat golden
    /// bytes were written to avoid. So the first test compares against the capture, and the round
    /// trip is a second, weaker check on top.
    /// </para>
    /// </remarks>
    public sealed class FaDeleteFileCodecTests
    {
        /// <summary>
        /// The QFORM body of a real delete, from the operation word onwards.
        /// </summary>
        /// <remarks>
        /// <c>DOC/captures/ARCHIVE-2026-07/2026-07-29-2047-fa-delete-file-d102-to-d100.pcapng</c>. The
        /// trailing <c>46</c> after the terminator is the byte whose meaning is unknown; ours writes
        /// a space there, so the comparison below stops short of it and the next test covers the
        /// rest.
        /// </remarks>
        private const string CapturedDeleteBody =
            "92000B"                                  // operation 0x000B, DeleteFile
            + "920002"                                // sequence 2
            + "F20001"                                // selector 1 - the name follows
            + "BF" + "58464552544553543A44415441"     // byte string, 15 bytes: "XFERTEST:DATA"
            + "27" + "46"                             // terminator, then the unknown byte
            + "F200FF";                               // end of list

        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the fixture.
        /// </summary>
        /// <param name="output">
        /// xunit's output sink.
        /// </param>
        public FaDeleteFileCodecTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// Our request matches the captured one everywhere the capture is meaningful.
        /// </summary>
        /// <remarks>
        /// Everything is compared except the single byte after the terminator, which varies across
        /// all four samples we hold and has no known rule. Comparing that byte would be asserting a
        /// value we know to be arbitrary.
        /// </remarks>
        [Fact]
        public void OurRequestMatchesTheOneARealMachineSent()
        {
            byte[] expected = Convert.FromHexString(CapturedDeleteBody);
            byte[] built = FaDeleteFileCodec.BuildRequest(2, "XFERTEST:DATA");

            _output.WriteLine("captured : " + Convert.ToHexString(expected));
            _output.WriteLine("built    : " + Convert.ToHexString(built));

            Assert.Equal(expected.Length, built.Length);

            // The unknown byte sits immediately after the terminator.
            int unknownAt = Array.IndexOf(built, FaDeleteFileCodec.NameTerminator) + 1;
            Assert.True(unknownAt > 0, "the terminator should be present");

            for (int i = 0; i < expected.Length; i++)
            {
                if (i == unknownAt)
                {
                    continue;
                }

                Assert.True(
                    expected[i] == built[i],
                    "byte " + i + ": the capture has 0x" + expected[i].ToString("X2")
                        + " and we build 0x" + built[i].ToString("X2"));
            }
        }

        /// <summary>
        /// The name comes back out through the reader that was written from the same captures.
        /// </summary>
        /// <remarks>
        /// The weaker check, and it is second on purpose: the reader and the writer could agree with
        /// each other and both be wrong about the wire. It is worth having because it covers the
        /// path a live exchange actually takes - we send, a server reads.
        /// </remarks>
        [Theory]
        [InlineData("XFERTEST:DATA")]
        [InlineData("THIRD:TXT")]
        [InlineData("NEWFILE:TXT")]
        [InlineData("A")]
        public void TheNameSurvivesOurOwnReader(string name)
        {
            byte[] body = FaDeleteFileCodec.BuildRequest(7, name);

            Assert.True(FaListFilesCodec.TryReadDeleteFileName(body, out string read));
            Assert.Equal(name, read);
        }

        /// <summary>
        /// A long name switches to the escaped length, as a real client does at sixteen bytes.
        /// </summary>
        /// <remarks>
        /// The compact tag carries its length in the low nibble and stops at fifteen. Emitting the
        /// compact form for a longer field would not merely be a size error - it would be a
        /// different encoding, and the peer would read the wrong number of bytes.
        /// </remarks>
        [Fact]
        public void ALongNameUsesTheEscapedLength()
        {
            // Fourteen characters plus terminator and the extra byte is sixteen - one past compact.
            byte[] body = FaDeleteFileCodec.BuildRequest(1, "LONGERNAME:TXT");

            // 92 000B | 92 0001 | F2 0001 | B0 10 ...
            Assert.Equal(0xB0, body[9]);
            Assert.Equal(0x10, body[10]);

            Assert.True(FaListFilesCodec.TryReadDeleteFileName(body, out string read));
            Assert.Equal("LONGERNAME:TXT", read);
        }

        /// <summary>
        /// A name of exactly fifteen bytes of field still uses the compact form.
        /// </summary>
        /// <remarks>
        /// The boundary, because an off-by-one here picks the wrong encoding for one specific
        /// length and nothing else - the hardest kind of fault to notice in the field.
        /// </remarks>
        [Fact]
        public void TheBoundaryAtFifteenStaysCompact()
        {
            byte[] body = FaDeleteFileCodec.BuildRequest(1, "XFERTEST:DATA");
            Assert.Equal(0xBF, body[9]);
        }

        /// <summary>
        /// An empty or null name is refused rather than sent.
        /// </summary>
        /// <remarks>
        /// A delete with no name is a request to destroy something unspecified. Refusing locally is
        /// the only safe answer - the server's behaviour on such a request is unknown, and finding
        /// out is not worth the risk.
        /// </remarks>
        [Fact]
        public void AnEmptyNameIsRefused()
        {
            Assert.Throws<ArgumentException>(() => FaDeleteFileCodec.BuildRequest(1, string.Empty));
            Assert.Throws<ArgumentNullException>(() => FaDeleteFileCodec.BuildRequest(1, null!));
        }
    }
}
