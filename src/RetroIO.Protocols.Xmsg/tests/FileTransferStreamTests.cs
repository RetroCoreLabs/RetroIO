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
    /// The first captured COSMOS file transfer that actually carried file content, analysed at the
    /// LAPB layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Capture: <c>claude-transfer-file-COMPLETE-102-to-100-2026-07-29.pcapng</c>. Produced on node
    /// 102 inside <c>COS-FILE-TRA-E02</c> with <c>DEBUGPRINT-ON</c>:
    /// </para>
    /// <code>
    /// TRANSFER-FILE d100(system)."xfertest:data",DUMMY:DATA
    ///   -> Completed. Transfer rate: 3 Kbytes/sec
    /// </code>
    /// <para>
    /// <b>Why this test exists.</b> A first pass over this capture counted raw TCP payload sizes and
    /// concluded "the wire unit is a 256-byte LAPB I-frame". That was wrong: 256 is the TCP segment
    /// size of the HDLC-over-TCP bridge, and a single LAPB frame spans several segments. Every
    /// measurement here therefore goes through <see cref="HdlcPcap.ReadFrames"/>, which reassembles
    /// frames across segments, rather than through segment sizes.
    /// </para>
    /// <para>
    /// The test is deliberately written to REPORT as well as assert, so the numbers behind each
    /// claim are visible in the test output rather than only in a document.
    /// </para>
    /// </remarks>
    public sealed class FileTransferStreamTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink, used to report the measured numbers.
        /// </param>
        public FileTransferStreamTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private const string CaptureName = "claude-transfer-file-COMPLETE-102-to-100-2026-07-29.pcapng";

        /// <summary>
        /// Reassembles the transfer capture and reports the frame-level shape of the stream.
        /// </summary>
        /// <remarks>
        /// Answers, from the wire rather than from the client's own API:
        /// how big a data-carrying frame really is, whether the LAPB send window is genuinely used,
        /// and whether the receiving node ever sends anything other than supervisory frames.
        /// </remarks>
        [Fact]
        public void TransferStream_FrameLevelShape()
        {
            string? path = PcapFiles.File(CaptureName);
            if (path == null)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            IReadOnlyList<CapturedFrame> frames = HdlcPcap.ReadCapturedFrames(path);
            _output.WriteLine("total LAPB frames: " + frames.Count);

            // Split by direction. The sender of the file is whichever side emits information frames
            // carrying a SINTRAN info field; we identify directions by StreamKey rather than assuming.
            Dictionary<string, int> infoByDirection = new Dictionary<string, int>();
            Dictionary<string, int> supervisoryByDirection = new Dictionary<string, int>();
            Dictionary<string, long> infoBytesByDirection = new Dictionary<string, long>();
            List<int> infoSizes = new List<int>();

            for (int i = 0; i < frames.Count; i++)
            {
                LapbFrame frame = frames[i].Frame;
                string dir = frames[i].Key.SourcePort.ToString() + "->" + frames[i].Key.DestinationPort.ToString();

                if (frame.Kind == LapbFrameKind.Information)
                {
                    Increment(infoByDirection, dir);
                    if (!infoBytesByDirection.ContainsKey(dir)) { infoBytesByDirection[dir] = 0; }
                    infoBytesByDirection[dir] += frame.Info.Length;
                    infoSizes.Add(frame.Info.Length);
                }
                else if (frame.Kind == LapbFrameKind.Supervisory)
                {
                    Increment(supervisoryByDirection, dir);
                }
            }

            _output.WriteLine("--- information frames by direction");
            string[] infoKeys = new string[infoByDirection.Count];
            infoByDirection.Keys.CopyTo(infoKeys, 0);
            for (int k = 0; k < infoKeys.Length; k++)
            {
                string key = infoKeys[k];
                int value = infoByDirection[key];
                long bytes = infoBytesByDirection[key];
                _output.WriteLine("  " + key + " : " + value + " frames, " + bytes + " info bytes");
            }

            _output.WriteLine("--- supervisory frames by direction");
            string[] supKeys = new string[supervisoryByDirection.Count];
            supervisoryByDirection.Keys.CopyTo(supKeys, 0);
            for (int k = 0; k < supKeys.Length; k++)
            {
                _output.WriteLine("  " + supKeys[k] + " : " + supervisoryByDirection[supKeys[k]]);
            }

            // The distribution of information-field sizes is what settles the block size question.
            infoSizes.Sort();
            Dictionary<int, int> sizeHistogram = new Dictionary<int, int>();
            for (int i = 0; i < infoSizes.Count; i++)
            {
                Increment(sizeHistogram, infoSizes[i]);
            }

            _output.WriteLine("--- information-field size histogram (size : count)");
            int[] sizeKeys = new int[sizeHistogram.Count];
            sizeHistogram.Keys.CopyTo(sizeKeys, 0);
            for (int k = 0; k < sizeKeys.Length; k++)
            {
                _output.WriteLine("  " + sizeKeys[k] + " : " + sizeHistogram[sizeKeys[k]]);
            }

            Assert.True(frames.Count > 0, "The capture produced no LAPB frames.");
        }

        /// <summary>
        /// Determines whether the LAPB send window is genuinely used, from the acknowledgement
        /// progression rather than from frame interleaving.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why it is measured this way.</b> The obvious approach - walk the capture in time order
        /// and count frames sent but not yet acknowledged - does NOT work here, because
        /// <see cref="HdlcPcap.ReadFrames"/> returns frames grouped by directional flow rather than
        /// interleaved in capture order. An earlier version of this test did exactly that and
        /// reported 16 unacknowledged frames, which is impossible: LAPB's modulo-8 sequence space
        /// allows at most 7.
        /// </para>
        /// <para>
        /// Order WITHIN a flow is preserved, though, so the receiver's N(R) progression is reliable.
        /// N(R) means "I have everything below this number". If N(R) ever advances by more than one
        /// between consecutive frames from the receiver, then more than one information frame was
        /// outstanding when it was sent - the window was in use. If every advance is exactly one,
        /// each frame was acknowledged individually.
        /// </para>
        /// </remarks>
        [Fact]
        public void TransferStream_IsTheSendWindowActuallyUsed()
        {
            string? path = PcapFiles.File(CaptureName);
            if (path == null)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            IReadOnlyList<CapturedFrame> frames = HdlcPcap.ReadCapturedFrames(path);

            // Group every frame that carries N(R) by the direction it travels in, in flow order.
            Dictionary<string, List<int>> receiveSequences = new Dictionary<string, List<int>>();
            for (int i = 0; i < frames.Count; i++)
            {
                LapbFrame frame = frames[i].Frame;
                if (frame.Kind != LapbFrameKind.Information && frame.Kind != LapbFrameKind.Supervisory)
                {
                    continue;
                }

                string dir = frames[i].Key.SourcePort.ToString() + "->" + frames[i].Key.DestinationPort.ToString();
                if (!receiveSequences.ContainsKey(dir)) { receiveSequences[dir] = new List<int>(); }
                receiveSequences[dir].Add(frame.ReceiveSequence);
            }

            string[] seqKeys = new string[receiveSequences.Count];
            receiveSequences.Keys.CopyTo(seqKeys, 0);
            for (int k = 0; k < seqKeys.Length; k++)
            {
                string key = seqKeys[k];
                List<int> nrs = receiveSequences[key];
                int maxAdvance = 0;
                int advancesAboveOne = 0;

                for (int i = 1; i < nrs.Count; i++)
                {
                    int advance = ((nrs[i] - nrs[i - 1]) + 8) % 8;
                    if (advance > maxAdvance) { maxAdvance = advance; }
                    if (advance > 1) { advancesAboveOne++; }
                }

                _output.WriteLine(key + " : N(R) values " + string.Join(",", nrs));
                _output.WriteLine("    largest single advance = " + maxAdvance
                    + ", advances greater than one = " + advancesAboveOne);
            }

            Assert.NotEmpty(receiveSequences);
        }

        /// <summary>
        /// Dumps the head of every information field, so the transfer message structure can be read.
        /// </summary>
        /// <remarks>
        /// Reporting only - it makes no claims. The `LIST-VARIABLES` output of the same program shows
        /// it tracks a <c>Function</c>, a <c>page no</c> and a <c>displacement</c>, so those three
        /// should appear somewhere in these bytes; this test exists to find where.
        /// </remarks>
        [Theory]
        [InlineData(CaptureName)]
        [InlineData("claude-file-stat-102-to-100-2026-07-29.pcapng")]
        [InlineData("claude-list-files-d100-system-2026-07-29.pcapng")]
        [InlineData("claude-delete-file-102-to-100-2026-07-29.pcapng")]
        public void TransferStream_DumpInformationFieldHeads(string captureName)
        {
            string? path = PcapFiles.File(captureName);
            if (path == null)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            _output.WriteLine("=== " + captureName);

            IReadOnlyList<CapturedFrame> frames = HdlcPcap.ReadCapturedFrames(path);

            for (int i = 0; i < frames.Count; i++)
            {
                LapbFrame frame = frames[i].Frame;
                if (frame.Kind != LapbFrameKind.Information || frame.Info.Length == 0)
                {
                    continue;
                }

                string dir = frames[i].Key.SourcePort.ToString() + "->" + frames[i].Key.DestinationPort.ToString();
                ReadOnlySpan<byte> info = frame.Info.Span;

                int head = info.Length < 48 ? info.Length : 48;
                System.Text.StringBuilder hex = new System.Text.StringBuilder();
                for (int k = 0; k < head; k++)
                {
                    hex.Append(info[k].ToString("x2"));
                    if ((k % 2) == 1) { hex.Append(' '); }
                }

                _output.WriteLine(dir + "  len=" + info.Length + "  N(S)=" + frame.SendSequence
                    + "  head: " + hex.ToString());
            }

            Assert.True(frames.Count > 0);
        }

        /// <summary>
        /// Increments a counter in a dictionary.
        /// </summary>
        private static void Increment<T>(Dictionary<T, int> counts, T key)
            where T : notnull
        {
            if (counts.ContainsKey(key)) { counts[key] = counts[key] + 1; }
            else { counts[key] = 1; }
        }

    }
}
