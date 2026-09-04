using RetroIO.Protocols.Xmsg;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Checks the generated SINTRAN error table against codes established independently.
    /// </summary>
    /// <remarks>
    /// <para><b>What this can and cannot prove</b></para>
    /// The table is generated from ND's <c>systemerrors.h</c>, so a test cannot show it is "right" -
    /// it would only be comparing the file to itself. What it CAN do is confirm the generator read
    /// the file correctly, by checking the handful of codes whose meaning was established from
    /// somewhere else entirely: captures of a real machine, matched against what its terminal
    /// printed.
    /// <para>
    /// Those few are the anchors. If the parser mis-indexed the table - an off-by-one between the
    /// <c>#define</c> comments and the row list would be the obvious way - the anchors move and
    /// these fail.
    /// </para>
    /// </remarks>
    public sealed class SintranErrorsTests
    {
        /// <summary>
        /// The codes read off real captures land on the text the terminal actually printed.
        /// </summary>
        [Theory]
        [InlineData(46, "NO SUCH FILE NAME")]
        [InlineData(48, "WRONG PASSWORD")]
        [InlineData(197, "OBJECT INDEX TOO LARGE")]
        public void CapturedCodesCarryTheTextTheTerminalPrinted(int code, string expected)
        {
            Assert.Equal(expected, SintranErrors.Describe(code));
        }

        /// <summary>
        /// The message a real terminal printed at us when we refused an unimplemented operation.
        /// </summary>
        /// <remarks>
        /// This one is the strongest anchor in the file. While the server had no
        /// <c>Set-block-size</c>, a live client gave up with this exact wording - so the table's
        /// entry for 180 is confirmed by a machine we did not consult when generating it.
        /// </remarks>
        [Fact]
        public void Code180IsTheAbortMessageWeSawOnALiveTerminal()
        {
            Assert.Equal(
                "NO ANSWER FROM REMOTE SYSTEM; FILE-ACCESS CONNECTION ABORTED",
                SintranErrors.Describe(180));
        }

        /// <summary>
        /// The codes this server chooses to send name the condition they stand for.
        /// </summary>
        [Theory]
        [InlineData(129, "ILLEGAL FUNCTION CODE")]
        [InlineData(211, "FILE-ACCESS INTERNAL ERROR; INVALID PARAMETER VALUE")]
        [InlineData(97, "TRANSFER ERROR")]
        public void TheCodesWeSendSayWhatWeMean(int code, string expected)
        {
            Assert.Equal(expected, SintranErrors.Describe(code));
        }

        /// <summary>
        /// Code 3 is END OF FILE, which is why it must never carry a refusal.
        /// </summary>
        /// <remarks>
        /// Recorded as a test because this was a live defect: <c>BadRequest</c> was 3, so a client
        /// whose request we failed to parse was told its read had finished normally.
        /// </remarks>
        [Fact]
        public void CodeThreeIsEndOfFile()
        {
            Assert.Equal("END OF FILE", SintranErrors.Describe(3));
        }

        /// <summary>
        /// The table covers the whole byte range a reply can carry.
        /// </summary>
        [Fact]
        public void TheTableCoversEveryByteValue()
        {
            Assert.Equal(256, SintranErrors.Count);
        }

        /// <summary>
        /// A code outside the table is reported as such rather than guessed at.
        /// </summary>
        [Theory]
        [InlineData(-1)]
        [InlineData(256)]
        [InlineData(9999)]
        public void OutsideTheTableIsSaidPlainly(int code)
        {
            Assert.Equal("(outside SINTRAN's error table)", SintranErrors.Describe(code));
            Assert.Equal(XmsgErrorDisposition.Unknown, SintranErrors.DispositionOf(code));
            Assert.False(SintranErrors.IsMeaningful(code));
        }

        /// <summary>
        /// ND's filler codes are flagged, so one is never chosen to send.
        /// </summary>
        /// <remarks>
        /// ND fills the unused numbers with "ERROR CODE OUTSIDE RANGE". Sending one would make a
        /// real machine print exactly that, which tells a user nothing at all.
        /// </remarks>
        [Fact]
        public void FillerCodesAreNotMarkedMeaningful()
        {
            // 0 and 1 are ND's own filler entries.
            Assert.False(SintranErrors.IsMeaningful(0));
            Assert.False(SintranErrors.IsMeaningful(1));

            // While the ones we actually use are real.
            Assert.True(SintranErrors.IsMeaningful(46));
            Assert.True(SintranErrors.IsMeaningful(129));
            Assert.True(SintranErrors.IsMeaningful(211));
            Assert.True(SintranErrors.IsMeaningful(97));
        }

        /// <summary>
        /// Dispositions come through, and 46 is the give-up ND says it is.
        /// </summary>
        /// <remarks>
        /// Most SINTRAN codes are <c>SIII_RETRY</c>, so a give-up is the useful thing to assert -
        /// it would survive a bug that defaulted everything to retry.
        /// </remarks>
        [Fact]
        public void DispositionsAreCarriedThrough()
        {
            Assert.Equal(XmsgErrorDisposition.GiveUp, SintranErrors.DispositionOf(46));
            Assert.Equal(XmsgErrorDisposition.Retry, SintranErrors.DispositionOf(197));
        }
    }
}
