using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Api.Tests
{
    /// <summary>
    /// Keeps the claim "all 48 XMSG function codes are accounted for" honest.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a test and not just a comment</b></para>
    /// <see cref="IXmsgKernel"/> lists every function code in one of four groups - implemented, not
    /// applicable to a typed API, driver or privileged, obsolete, or blocked on evidence. A list
    /// like that is worth nothing once it drifts, and prose drifts silently.
    /// <para>
    /// These tests fail the moment ND's own enum and our accounting disagree, which is the only way
    /// the claim stays true after somebody adds a function or regenerates
    /// <c>XmsgFunction</c> from the constants JSON.
    /// </para>
    /// </remarks>
    public sealed class XmsgFunctionCoverageTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public XmsgFunctionCoverageTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// The codes this kernel implements.
        /// </summary>
        /// <remarks>
        /// Kept as a literal list rather than derived from the interface, so that adding a method
        /// without deciding which group it belongs to is caught here instead of passing quietly.
        /// </remarks>
        private static readonly XmsgFunction[] Implemented =
        {
            XmsgFunction.XFDUM, XmsgFunction.XFDCT, XmsgFunction.XFGET, XmsgFunction.XFREL,
            XmsgFunction.XFRHD, XmsgFunction.XFWHD, XmsgFunction.XFREA, XmsgFunction.XFWRI,
            XmsgFunction.XFSCM, XmsgFunction.XFMST, XmsgFunction.XFOPN, XmsgFunction.XFCLS,
            XmsgFunction.XFSND, XmsgFunction.XFRCV, XmsgFunction.XFPST, XmsgFunction.XFGST,
            XmsgFunction.XFM2P, XmsgFunction.XFP2M, XmsgFunction.XFPRV, XmsgFunction.XFRTN,
            XmsgFunction.XFRRH, XmsgFunction.XFDMM, XmsgFunction.XFALM, XmsgFunction.XFFRM,
            XmsgFunction.XFLMP, XmsgFunction.XFRRE, XmsgFunction.XFCPV,

            // Unblocked 2026-08-07: documented in the version-L release description section 6.3,
            // not in Appendix A, which is why it spent months in BlockedOnEvidence.
            XmsgFunction.XFGSM,
        };

        /// <summary>
        /// Codes deliberately not implemented, each for a reason recorded on
        /// <see cref="IXmsgKernel"/>.
        /// </summary>
        private static readonly XmsgFunction[] DeliberatelyAbsent =
        {
            // Batches raw register sets to save monitor calls; there is no monitor call here.
            XmsgFunction.XFSMC,

            // Driver, privileged, or physical memory - none of which exists for a task on this
            // side of the wire.
            XmsgFunction.XFSIN, XmsgFunction.XFABR, XmsgFunction.XFCRD, XmsgFunction.XFSTD,
            XmsgFunction.XFDUB, XmsgFunction.XFDBK, XmsgFunction.XFWDF,

            // Obsolete in ND's own include, or private to COSROUT.
            XmsgFunction.XFSRL, XmsgFunction.XFABW, XmsgFunction.XFMLK, XmsgFunction.XFMUL,
            XmsgFunction.XFRIN, XmsgFunction.XFDIB, XmsgFunction.XFRIB, XmsgFunction.XFWIB,
            XmsgFunction.XFMRT, XmsgFunction.XFSFM, XmsgFunction.XFCRR,
        };

        /// <summary>
        /// Codes we would implement but cannot, because their parameters are undocumented.
        /// </summary>
        /// <remarks>
        /// Both are named in <c>XMSG-PL-VALUES-M.INCL</c> and neither has a section in Appendix A
        /// of the COSMOS Programmer Guide. They wait on a capture or a carve, not on effort. If
        /// this list ever empties, the accounting on <see cref="IXmsgKernel"/> must say so too.
        /// </remarks>
        private static readonly XmsgFunction[] BlockedOnEvidence =
        {
            XmsgFunction.XFWRT,
        };

        /// <summary>
        /// Every function ND declares falls into exactly one group.
        /// </summary>
        /// <remarks>
        /// The end marker is excluded deliberately: <c>X5FUN=48</c> is where ND's table stops, not
        /// a function - a distinction that has already caused one wrong "missing function" report.
        /// </remarks>
        [Fact]
        public void EveryDeclaredFunctionIsAccountedForExactlyOnce()
        {
            Dictionary<XmsgFunction, string> seen = new Dictionary<XmsgFunction, string>();

            Add(seen, Implemented, "implemented");
            Add(seen, DeliberatelyAbsent, "deliberately absent");
            Add(seen, BlockedOnEvidence, "blocked on evidence");

            List<XmsgFunction> unaccounted = new List<XmsgFunction>();
            Array values = Enum.GetValues(typeof(XmsgFunction));

            for (int i = 0; i < values.Length; i++)
            {
                XmsgFunction function = (XmsgFunction)values.GetValue(i)!;

                // X5FUN is the table's END MARKER, not a function. See XmsgFunction's source JSON.
                if ((int)function >= 48)
                {
                    continue;
                }

                if (!seen.ContainsKey(function))
                {
                    unaccounted.Add(function);
                }
            }

            _output.WriteLine(
                "accounted for: " + seen.Count
                + " (implemented " + Implemented.Length
                + ", absent " + DeliberatelyAbsent.Length
                + ", blocked " + BlockedOnEvidence.Length + ")");

            Assert.Empty(unaccounted);
        }

        /// <summary>
        /// No function is claimed by two groups.
        /// </summary>
        [Fact]
        public void NoFunctionAppearsInTwoGroups()
        {
            Dictionary<XmsgFunction, string> seen = new Dictionary<XmsgFunction, string>();

            // Add throws on a duplicate, which is the assertion.
            Add(seen, Implemented, "implemented");
            Add(seen, DeliberatelyAbsent, "deliberately absent");
            Add(seen, BlockedOnEvidence, "blocked on evidence");

            Assert.Equal(Implemented.Length + DeliberatelyAbsent.Length + BlockedOnEvidence.Length, seen.Count);
        }

        /// <summary>
        /// Records the group each function belongs to, refusing duplicates.
        /// </summary>
        /// <param name="seen">
        /// The accumulating map.
        /// </param>
        /// <param name="functions">
        /// The group's members.
        /// </param>
        /// <param name="group">
        /// The group's name, used in the failure message.
        /// </param>
        private static void Add(
            Dictionary<XmsgFunction, string> seen, XmsgFunction[] functions, string group)
        {
            for (int i = 0; i < functions.Length; i++)
            {
                Assert.False(
                    seen.ContainsKey(functions[i]),
                    functions[i] + " is in two groups: " + group + " and " + (seen.ContainsKey(functions[i]) ? seen[functions[i]] : "?"));

                seen.Add(functions[i], group);
            }
        }
    }
}
