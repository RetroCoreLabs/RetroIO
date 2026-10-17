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
    /// The connect letter, compared against the letter a real machine put on the wire.
    /// </summary>
    /// <remarks>
    /// Captured from node 103 to node 102 in
    /// <c>DOC/captures/ND-TO-ND-2026-08-08/nd-to-nd.pcapng</c>, and the same shape in every other
    /// recording we hold. A RECORDING, so a builder that is confidently wrong fails here.
    /// </remarks>
    public sealed class FaConnectLetterTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Creates the fixture.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public FaConnectLetterTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// The captured letter body, byte for byte.
        /// </summary>
        /// <remarks>
        /// <code>
        /// 1B        serial
        /// 41        XSLET
        /// 0012      declared length 18 - the two strings only
        /// FF 0A     "*FA-SERVER"
        /// FE 04     "D102"    the DESTINATION, node 102 - the letter came FROM 103
        /// 07E2 0000 0002 6400 A200 FF00   extras, past the declared length
        /// </code>
        /// <para>
        /// Thirty-four bytes, which is exactly what that frame's sub-header declares (0x0022).
        /// </para>
        /// </remarks>
        private const string Captured =
            "1B410012"
            + "FF0A2A46412D534552564552"
            + "FE0444313032"
            + "07E2000000026400A200FF00";

        private static string Hex(byte[] value)
        {
            return Convert.ToHexString(value);
        }

        /// <summary>
        /// The builder reproduces the captured letter exactly.
        /// </summary>
        [Fact]
        public void TheLetterMatchesTheCapture()
        {
            byte[] body = FaConnectLetter.BuildBody(0x1B, "*FA-SERVER", "D102", 0x0002);

            _output.WriteLine("built    : " + Hex(body));
            _output.WriteLine("captured : " + Captured);

            Assert.Equal(Captured, Hex(body));

            // The sub-header of the captured frame declared 0x0022 for this body. The letter is
            // built once and measured twice, which is what catches a body that is a byte short.
            Assert.Equal(0x0022, body.Length);
        }

        /// <summary>
        /// The declared length covers only the two strings, NOT the extras.
        /// </summary>
        /// <remarks>
        /// This is the part a careful implementer gets wrong by being careful: the bytes after
        /// the declared length are carried anyway, and the server reads the word the
        /// confirmation echoes out of them. A letter truncated at its declared length cannot be
        /// answered properly.
        /// </remarks>
        [Fact]
        public void TheDeclaredLengthExcludesTheExtras()
        {
            byte[] body = FaConnectLetter.BuildBody(0x1B, "*FA-SERVER", "D100", 0x0002);

            int declared = (body[2] << 8) | body[3];

            Assert.Equal(18, declared);
            Assert.Equal(2 + 10 + 2 + 4, declared);

            // The body is longer than the header plus the declared length - by the extras.
            Assert.Equal(XroutMessage.HeaderSize + declared + 12, body.Length);

            // And the whole body is a whole number of words. A machine that counts in words
            // cannot carry an odd body, which is how the missing pad byte was found.
            Assert.Equal(0, body.Length % 2);
        }

        /// <summary>
        /// The echoed word is the caller's choice and is NOT fixed at 0x0002.
        /// </summary>
        /// <remarks>
        /// Treating it as a constant is what hung a live terminal; see
        /// FaServerConversation.ResponderConversation. A client picks it, and the server stamps
        /// that same word on everything it sends back.
        /// </remarks>
        [Fact]
        public void TheEchoedWordIsChosenByTheClient()
        {
            byte[] a = FaConnectLetter.BuildBody(0x1B, "*FA-SERVER", "D100", 0x0002);
            byte[] b = FaConnectLetter.BuildBody(0x1B, "*FA-SERVER", "D100", 0x000C);

            // The word sits in the extras, after 07E2 0000.
            int at = XroutMessage.HeaderSize + 18 + 4;

            Assert.Equal(0x0002, (a[at] << 8) | a[at + 1]);
            Assert.Equal(0x000C, (b[at] << 8) | b[at + 1]);
        }

        /// <summary>
        /// The trailing constant is the same one the confirmation carries.
        /// </summary>
        [Fact]
        public void TheLetterCarriesTheTrailingConstant()
        {
            byte[] body = FaConnectLetter.BuildBody(0x1B, "*FA-SERVER", "D100", 0x0002);

            int at = XroutMessage.HeaderSize + 18 + 6;
            Assert.Equal(
                FaExchangeCodec.ConfirmTrailingWord, (ushort)((body[at] << 8) | body[at + 1]));
        }

        /// <summary>
        /// The DESTINATION system's name goes in parameter 2, and it varies in length.
        /// </summary>
        /// <remarks>
        /// The letter names the machine being asked, never the sender. Every recording agrees:
        /// 102 to 100 says <c>D100</c>, 100 to 102 says <c>D102</c>, 103 to 102 says
        /// <c>D102</c>, 100 to 19999 says <c>D19999</c>. We read the one 102-to-100 frame as
        /// "the sender names itself", which fits that frame and none of the others, and D100
        /// answered the resulting push with a network error.
        /// </remarks>
        [Fact]
        public void TheSystemNameIsTheDestinations()
        {
            byte[] body = FaConnectLetter.BuildBody(0x01, "*FA-SERVER", "D19999", 0x0002);
            string text = System.Text.Encoding.ASCII.GetString(body);

            Assert.Contains("*FA-SERVER", text);
            Assert.Contains("D19999", text);

            // Six characters now, so the declared length grows by two.
            Assert.Equal(2 + 10 + 2 + 6, (body[2] << 8) | body[3]);

            // And the body grows with it, to the 0x0024 that D100's letter to us declared.
            Assert.Equal(0x0024, body.Length);
        }

        /// <summary>
        /// The serial is echoed by the reply, so it is the caller's to set.
        /// </summary>
        [Fact]
        public void TheSerialIsTheCallers()
        {
            Assert.Equal(0x1B, FaConnectLetter.BuildBody(0x1B, "*FA-SERVER", "D100", 2)[0]);
            Assert.Equal(0x07, FaConnectLetter.BuildBody(0x07, "*FA-SERVER", "D100", 2)[0]);
            Assert.Equal(FaConnectLetter.XsletService,
                FaConnectLetter.BuildBody(0x1B, "*FA-SERVER", "D100", 2)[1]);
        }

        /// <summary>
        /// Empty and over-long names are refused.
        /// </summary>
        [Fact]
        public void BadNamesAreRefused()
        {
            Assert.Throws<ArgumentNullException>(
                () => FaConnectLetter.BuildBody(1, null!, "D100", 2));
            Assert.Throws<ArgumentNullException>(
                () => FaConnectLetter.BuildBody(1, "*FA-SERVER", null!, 2));
            Assert.Throws<ArgumentException>(
                () => FaConnectLetter.BuildBody(1, string.Empty, "D100", 2));
            Assert.Throws<ArgumentException>(
                () => FaConnectLetter.BuildBody(1, "*FA-SERVER", new string('X', 256), 2));
        }
    }
}
