using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Keeps <see cref="XmsgErrorDispositions"/> honest against ND's own tables.
    /// </summary>
    /// <remarks>
    /// The lookup is a hand transcription of two 1988 header files. A transcription rots the
    /// moment somebody regenerates <see cref="XmsgError"/> from the constants JSON and a new code
    /// appears, so the accounting is asserted rather than trusted: every declared code must be
    /// either classified or named here as one ND does not classify.
    /// </remarks>
    public sealed class XmsgErrorDispositionTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public XmsgErrorDispositionTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// The XMSG codes ND's release-3 table does not list.
        /// </summary>
        /// <remarks>
        /// <c>XENIR</c>, <c>XENCE</c> and <c>XEICM</c> have no row in <c>xmsgerrors.h</c>, and
        /// <c>XKXXX</c> is the range base rather than an error. If this list ever needs a new
        /// member, that is a real finding - a code was added and nobody decided what it means.
        /// </remarks>
        private static readonly XmsgError[] NotClassifiedByNd =
        {
            XmsgError.XENIR,
            XmsgError.XENCE,
            XmsgError.XEICM,
            XmsgError.XKXXX,
        };

        /// <summary>
        /// Every declared XMSG error is either classified, or listed as one ND left out.
        /// </summary>
        [Fact]
        public void EveryXmsgErrorIsEitherClassifiedOrKnownUnclassified()
        {
            List<XmsgError> unexpected = new List<XmsgError>();
            Array values = Enum.GetValues(typeof(XmsgError));
            int classified = 0;

            for (int i = 0; i < values.Length; i++)
            {
                XmsgError error = (XmsgError)values.GetValue(i)!;
                XmsgErrorDisposition disposition = XmsgErrorDispositions.Of(error);

                if (disposition != XmsgErrorDisposition.Unknown)
                {
                    classified++;
                    continue;
                }

                if (!Contains(NotClassifiedByNd, error))
                {
                    unexpected.Add(error);
                }
            }

            _output.WriteLine("classified " + classified + " of " + values.Length);

            // 42 of the 45 codes carry a disposition; the other three are in NotClassifiedByNd.
            Assert.Equal(42, classified);
            Assert.Empty(unexpected);
        }

        /// <summary>
        /// The dispositions split the way ND's table does: 29 retry, 11 give up, one each of
        /// suspend and sleep.
        /// </summary>
        /// <remarks>
        /// Counting the arms catches a copy-paste that lands a code in the wrong block, which a
        /// spot check of a handful of codes would sail straight past.
        /// </remarks>
        [Fact]
        public void TheDispositionCountsMatchNdsTable()
        {
            int retry = 0;
            int giveUp = 0;
            int suspend = 0;
            int sleep = 0;
            Array values = Enum.GetValues(typeof(XmsgError));

            for (int i = 0; i < values.Length; i++)
            {
                switch (XmsgErrorDispositions.Of((XmsgError)values.GetValue(i)!))
                {
                    case XmsgErrorDisposition.Retry: retry++; break;
                    case XmsgErrorDisposition.GiveUp: giveUp++; break;
                    case XmsgErrorDisposition.Suspend: suspend++; break;
                    case XmsgErrorDisposition.Sleep: sleep++; break;
                    default: break;
                }
            }

            _output.WriteLine(
                "retry " + retry + ", give up " + giveUp
                + ", suspend " + suspend + ", sleep " + sleep);

            Assert.Equal(29, retry);
            Assert.Equal(11, giveUp);
            Assert.Equal(1, suspend);
            Assert.Equal(1, sleep);
        }

        /// <summary>
        /// XEIMA is a retry, which is why the FA close that answers with it has never done harm.
        /// </summary>
        /// <remarks>
        /// This is the case the whole import was worth doing for. A real D100 answers our FA close
        /// with <c>XEIMA</c> (-19) and the file transfer is nonetheless byte-correct. ND classify
        /// it <c>SIII_RETRY</c>: the conversation is already gone, not the request malformed.
        /// </remarks>
        [Fact]
        public void XeimaIsRetryable()
        {
            Assert.Equal(XmsgErrorDisposition.Retry, XmsgErrorDispositions.Of(XmsgError.XEIMA));
            Assert.True(XmsgErrorDispositions.IsWorthRetrying(XmsgError.XEIMA));
        }

        /// <summary>
        /// XENRU sleeps and XERNA suspends, and they are the only ones that do.
        /// </summary>
        /// <remarks>
        /// The two are easy to conflate: both mean "wait". They differ in what you are waiting
        /// for - <c>XENRU</c> is our own kernel being down, <c>XERNA</c> is the far end being
        /// unreachable - and only one of them is fixed by starting XMSG locally.
        /// </remarks>
        [Fact]
        public void SleepAndSuspendAreDistinctAndSingular()
        {
            Assert.Equal(XmsgErrorDisposition.Sleep, XmsgErrorDispositions.Of(XmsgError.XENRU));
            Assert.Equal(XmsgErrorDisposition.Suspend, XmsgErrorDispositions.Of(XmsgError.XERNA));
        }

        /// <summary>
        /// An unclassified code is never reported as worth retrying.
        /// </summary>
        [Fact]
        public void UnknownIsNotRetryable()
        {
            Assert.Equal(XmsgErrorDisposition.Unknown, XmsgErrorDispositions.Of(XmsgError.XENCE));
            Assert.False(XmsgErrorDispositions.IsWorthRetrying(XmsgError.XENCE));
        }

        /// <summary>
        /// XROUT is a give-up protocol: only XRSOK and XRNRO are anything else.
        /// </summary>
        /// <remarks>
        /// Codes 47 and above (<c>XRAMB</c> onward) postdate NDIX release 3 and have no row, so
        /// they must come back unknown rather than being swept into the give-up range.
        /// </remarks>
        [Fact]
        public void XroutErrorsAreGiveUpExceptTheTwoNamedOnes()
        {
            Assert.Equal(XmsgErrorDisposition.Ok, XmsgErrorDispositions.Of(XroutError.XRSOK));
            Assert.Equal(XmsgErrorDisposition.Suspend, XmsgErrorDispositions.Of(XroutError.XRNRO));

            Assert.Equal(XmsgErrorDisposition.GiveUp, XmsgErrorDispositions.Of(XroutError.XRISN));
            Assert.Equal(XmsgErrorDisposition.GiveUp, XmsgErrorDispositions.Of(XroutError.XRUNN));
            Assert.Equal(XmsgErrorDisposition.GiveUp, XmsgErrorDispositions.Of(XroutError.XRNCO));

            // Later additions, and the range base - neither is in ND's table.
            Assert.Equal(XmsgErrorDisposition.Unknown, XmsgErrorDispositions.Of(XroutError.XRAMB));
            Assert.Equal(XmsgErrorDisposition.Unknown, XmsgErrorDispositions.Of(XroutError.XRILX));
            Assert.Equal(XmsgErrorDisposition.Unknown, XmsgErrorDispositions.Of(XroutError.XRXXX));
        }

        /// <summary>
        /// Reports whether a code appears in a list.
        /// </summary>
        /// <param name="list">
        /// The list to search.
        /// </param>
        /// <param name="error">
        /// The code to look for.
        /// </param>
        /// <returns>
        /// <c>true</c> when <paramref name="error"/> is in <paramref name="list"/>.
        /// </returns>
        private static bool Contains(XmsgError[] list, XmsgError error)
        {
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] == error)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
