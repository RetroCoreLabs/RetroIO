using System;
using System.Collections.Generic;
using System.IO;
using RetroIO.Protocols.Xmsg.Hdlc;
using RetroIO.Protocols.Xmsg.Packet;
using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Diagnostic for the one failing conformance test: it tabulates the channel offset actually seen
    /// on the wire against the value the envelope model derives, split by the low byte of Flags 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hypothesis under test. <c>XmsgEnvelope</c> derives the channel as
    /// <c>0xDE - (XMCSM >> 24) - epoch</c>, where the epoch comes from
    /// <c>baseLow = seed - (Flags2 AND 0xFF)</c>. That was verified on a corpus in which the low byte
    /// of Flags 2 was a small class marker - <c>0x00</c> for control, <c>0x08</c> for terminal data.
    /// </para>
    /// <para>
    /// In the file-server captures the low byte of Flags 2 is not a class marker at all: Flags 2 is the
    /// message body length (see the LIST-FILES doc, the <c>length - 28</c> rule). So <c>baseLow</c>
    /// becomes a function of how long the message happened to be, and the epoch computed from it is not
    /// a wrap count of anything. If the hypothesis is right, every mismatch has a Flags-2 low byte
    /// outside the class-marker set, and the class-marker frames are all still correct.
    /// </para>
    /// <para>
    /// This test asserts only what it can prove and reports the rest. It does NOT invent a replacement
    /// formula - three hand-checked frames are not enough to justify one, and the whole point of the
    /// exercise is to stop publishing rules fitted to one case.
    /// </para>
    /// </remarks>
    public sealed class ChannelOffsetDiagnosticTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public ChannelOffsetDiagnosticTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// The Flags-2 low bytes that acted as class markers in the corpus the channel rule was
        /// verified against.
        /// </summary>
        private static readonly byte[] ClassMarkerLowBytes = new byte[] { 0x00, 0x08 };

        /// <summary>
        /// Splits every captured data frame by its Flags-2 low byte and reports how the wire channel
        /// compares with the derived one in each group.
        /// </summary>
        [Fact]
        public void ChannelMismatches_AllHaveANonClassMarkerFlagsTwo()
        {
            string? dir = PcapFiles.Directory();
            if (dir == null)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            string[] files = Directory.GetFiles(dir, "*.pcapng");

            int classMarkerChecked = 0;
            int classMarkerWrong = 0;
            int otherChecked = 0;
            int otherWrong = 0;

            // Offset actually observed on the wire, counted per value, so the shape of the error is
            // visible rather than just its size.
            Dictionary<int, int> offsetCensus = new Dictionary<int, int>();
            Dictionary<int, int> derivedCensus = new Dictionary<int, int>();

            for (int f = 0; f < files.Length; f++)
            {
                IReadOnlyList<LapbFrame> frames = HdlcPcap.ReadFrames(files[f]);

                for (int i = 0; i < frames.Count; i++)
                {
                    LapbFrame frame = frames[i];
                    if (frame.Kind != LapbFrameKind.Information || !frame.IsSintranInfo) { continue; }
                    if (frame.Info.Length < SintranHeader.Size) { continue; }

                    XmsgFrame decoded = XmsgFrame.Parse(frame.Info.Span);
                    if (decoded.Header.Subtype != SintranPacketSubtype.Data
                        || decoded.SubHeader == null
                        || decoded.Header.Flags1 == 0xFFFF)
                    {
                        continue;
                    }

                    ushort flags1 = decoded.Header.Flags1;
                    ushort flags2 = decoded.Header.Flags2;
                    byte counter = decoded.Header.Counter;
                    uint xmcsm = decoded.ControlService;

                    byte seed = XmsgEnvelope.LearnSeed(flags1, counter, flags2);

                    // What the wire says the offset from the anchor is, and what the model derives.
                    int wireOffset = XmsgEnvelope.ChannelAnchor
                        - (byte)(xmcsm >> 24)
                        - (byte)decoded.Header.ProtocolId;
                    int derivedOffset = XmsgEnvelope.ComputeEpoch(seed, flags1, flags2);

                    Add(offsetCensus, wireOffset);
                    Add(derivedCensus, derivedOffset);

                    bool isClassMarker = false;
                    for (int c = 0; c < ClassMarkerLowBytes.Length; c++)
                    {
                        if ((byte)(flags2 & 0xFF) == ClassMarkerLowBytes[c]) { isClassMarker = true; break; }
                    }

                    if (isClassMarker)
                    {
                        classMarkerChecked++;
                        if (wireOffset != derivedOffset) { classMarkerWrong++; }
                    }
                    else
                    {
                        otherChecked++;
                        if (wireOffset != derivedOffset) { otherWrong++; }
                    }
                }
            }

            _output.WriteLine("Flags2 low byte IS a class marker (0x00 / 0x08):");
            _output.WriteLine("  frames=" + classMarkerChecked + " channel wrong=" + classMarkerWrong);
            _output.WriteLine("Flags2 low byte is NOT a class marker:");
            _output.WriteLine("  frames=" + otherChecked + " channel wrong=" + otherWrong);
            _output.WriteLine("wire channel offset census:    " + Census(offsetCensus));
            _output.WriteLine("derived channel offset census: " + Census(derivedCensus));

            Assert.True(classMarkerChecked > 0, "no class-marker frames were examined");
            Assert.True(otherChecked > 0, "no non-class-marker frames were examined");

            // The claim: the derivation is sound exactly where Flags 2 carries a class marker.
            Assert.Equal(0, classMarkerWrong);
        }

        /// <summary>
        /// Increments a census bucket.
        /// </summary>
        /// <param name="census">
        /// The census.
        /// </param>
        /// <param name="key">
        /// The bucket.
        /// </param>
        private static void Add(Dictionary<int, int> census, int key)
        {
            int existing;
            census[key] = census.TryGetValue(key, out existing) ? existing + 1 : 1;
        }

        /// <summary>
        /// Renders a census as "value=count" pairs, ascending by value.
        /// </summary>
        /// <param name="census">
        /// The census.
        /// </param>
        private static string Census(Dictionary<int, int> census)
        {
            List<int> keys = new List<int>(census.Keys);
            keys.Sort();

            System.Text.StringBuilder text = new System.Text.StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) { text.Append("  "); }
                text.Append(keys[i]).Append('=').Append(census[keys[i]]);
            }

            return text.ToString();
        }

    }
}
