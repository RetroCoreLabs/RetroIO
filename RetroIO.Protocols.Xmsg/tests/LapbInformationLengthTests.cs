using System;
using System.Collections.Generic;
using System.IO;

using RetroIO.Protocols.Xmsg.Hdlc;
using RetroIO.Protocols.Xmsg.Tests;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Measures the largest LAPB information field a real ND machine actually sends, across every
    /// recorded capture.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is worth measuring</b></para>
    /// Requirement A5 in <c>DOC\LAPB-REQUIREMENTS.md</c> says "max info field 312 bytes", and
    /// <c>LapbLayer.MaxInformationLength</c> implements it: a longer received I-frame is answered
    /// with an FRMR rather than delivered. The same document names the captures as its source of
    /// truth. If the captures contain frames longer than 312, then either the limit is not what the
    /// wire does or it is not a universal one - and a receiver that FRMRs a frame the peer routinely
    /// sends will break the moment file content flows towards us.
    /// <para><b>It is a measurement, not a fix</b></para>
    /// This test does not change the limit and does not assert that 312 is wrong. It reports the
    /// distribution and fails only if the corpus turns out to be EMPTY, so the number is on the
    /// record either way. Changing a normative constant is a decision about live-link behaviour,
    /// and the authoritative spec lives in another repository.
    /// </remarks>
    public sealed class LapbInformationLengthTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink, used to report the measured numbers.
        /// </param>
        public LapbInformationLengthTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// The limit requirement A5 states, repeated here so the comparison is visible in the
        /// output rather than having to be looked up.
        /// </summary>
        /// <remarks>
        /// Deliberately a local copy: <c>RetroIO.Protocols.Xmsg.Live</c> is not on this test project's reference list,
        /// and hard-coupling to it would make this measurement move whenever the limit does.
        /// </remarks>
        private const int StatedLimit = 312;

        /// <summary>
        /// Reports the largest information field in every recorded capture, and how many frames
        /// exceed the stated limit.
        /// </summary>
        [Fact]
        public void TheLargestInformationFieldOnTheWireIsOnTheRecord()
        {
            string? directory = PcapFiles.Directory();
            if (directory == null)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            string[] captures = System.IO.Directory.GetFiles(directory, "*.pcapng");
            Array.Sort(captures, StringComparer.OrdinalIgnoreCase);

            int overallLargest = 0;
            int overallOverLimit = 0;
            int totalInformationFrames = 0;
            string largestCapture = string.Empty;

            // How many frames land in each size band, so "one odd frame" and "a whole class of
            // traffic" cannot be confused.
            Dictionary<int, int> bands = new Dictionary<int, int>();

            for (int c = 0; c < captures.Length; c++)
            {
                IReadOnlyList<LapbFrame> frames;
                try
                {
                    frames = HdlcPcap.ReadFrames(captures[c]);
                }
                catch (IOException)
                {
                    // A capture we cannot read is not what this test is about.
                    continue;
                }

                int largest = 0;
                int overLimit = 0;
                int informationFrames = 0;

                for (int i = 0; i < frames.Count; i++)
                {
                    if (frames[i].Kind != LapbFrameKind.Information)
                    {
                        continue;
                    }

                    int length = frames[i].Info.Length;
                    informationFrames++;

                    if (length > largest) { largest = length; }
                    if (length > StatedLimit) { overLimit++; }

                    int band = (length / 100) * 100;
                    bands[band] = bands.ContainsKey(band) ? bands[band] + 1 : 1;
                }

                if (informationFrames == 0)
                {
                    continue;
                }

                totalInformationFrames += informationFrames;
                overallOverLimit += overLimit;

                if (largest > overallLargest)
                {
                    overallLargest = largest;
                    largestCapture = Path.GetFileName(captures[c]);
                }

                _output.WriteLine(
                    Path.GetFileName(captures[c])
                    + ": " + informationFrames + " I-frames, largest " + largest
                    + ", over " + StatedLimit + ": " + overLimit);
            }

            _output.WriteLine(string.Empty);
            _output.WriteLine("--- size bands (lower bound : count)");

            int[] bandKeys = new int[bands.Count];
            bands.Keys.CopyTo(bandKeys, 0);
            Array.Sort(bandKeys);
            for (int i = 0; i < bandKeys.Length; i++)
            {
                _output.WriteLine("  " + bandKeys[i] + " : " + bands[bandKeys[i]]);
            }

            _output.WriteLine(string.Empty);
            _output.WriteLine("TOTAL I-frames        : " + totalInformationFrames);
            _output.WriteLine("LARGEST info field    : " + overallLargest + "  (" + largestCapture + ")");
            _output.WriteLine("OVER the stated " + StatedLimit + "  : " + overallOverLimit);
            _output.WriteLine(overallLargest > StatedLimit
                ? "=> real ND traffic EXCEEDS the limit LapbLayer enforces on receive."
                : "=> nothing recorded exceeds the limit.");

            // The only failure this test has: a corpus that measured nothing at all, which would
            // make the report above meaningless rather than reassuring.
            Assert.True(totalInformationFrames > 0, "no information frames were read from any capture");
        }
    }
}
