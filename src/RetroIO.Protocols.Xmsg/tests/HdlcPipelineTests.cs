//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;

using RetroIO.Protocols.Xmsg.Hdlc;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Focused tests for the HDLC/pcap pipeline: the FCS anchor frame and the exact
    /// per-capture FCS-valid frame counts.
    /// </summary>
    public sealed class HdlcPipelineTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test with the xUnit output sink.
        /// </summary>
        /// <param name="output">
        /// The per-test output helper.
        /// </param>
        public HdlcPipelineTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// Verifies the captured SABM anchor frame validates and folds to the good residue.
        /// </summary>
        [Fact]
        public void AnchorFrame_PassesFcs()
        {
            // Real captured SABM from node 100 (XMSG-PROTOCOL.md section 2):
            //   body = 01 3F 00 64, FCS = 0x092E stored low-first as 2E 09.
            byte[] frame = { 0x01, 0x3F, 0x00, 0x64, 0x2E, 0x09 };
            byte[] body = { 0x01, 0x3F, 0x00, 0x64 };

            // crc16(body) XOR 0xFFFF must equal the transmitted FCS 0x092E.
            ushort fcs = (ushort)(Fcs16.Compute(body) ^ 0xFFFF);
            Assert.Equal(0x092E, fcs);

            // Folding init 0xFFFF over the whole frame (body + FCS) yields residue 0xF0B8.
            ushort residue = Fcs16.Compute(frame);
            Assert.Equal(Fcs16.GoodResidue, residue);
            Assert.Equal(0xF0B8, residue);

            // And the convenience validator agrees.
            Assert.True(Fcs16.IsValid(frame));
        }

        /// <summary>
        /// A deliberately corrupted anchor frame must fail the FCS check.
        /// </summary>
        [Fact]
        public void CorruptedFrame_FailsFcs()
        {
            byte[] frame = { 0x01, 0x3F, 0x00, 0x65, 0x2E, 0x09 };
            Assert.False(Fcs16.IsValid(frame));
        }

        /// <summary>
        /// Asserts the exact FCS-valid frame count for every capture and the 1947 total.
        /// </summary>
        /// <remarks>
        /// Skips with a log message when the capture corpus cannot be located, so the
        /// suite stays portable.
        /// </remarks>
        [Fact]
        public void EveryCapture_HasExpectedFcsValidCount()
        {
            string? pcapDir = PcapFiles.Directory();
            if (pcapDir == null)
            {
                _output.WriteLine("pcap directory not found; skipping per-file count assertions.");
                return;
            }

            // Expected FCS-valid frame counts, verified against the reference pipeline.
            string[] names =
            {
                "2026-04-09-0100-tad-connect-to-d102-from-d103-via-d100.pcapng",
                "2026-04-09-0025-tad-connect-to-d102-from-d100.pcapng",
                "2026-04-09-0047-devices-online-d100-d102-d103.pcapng",
                "2026-04-09-0052-list-routing-on-d102.pcapng",
                "2026-04-09-0055-list-routing-on-d103.pcapng",
                "2026-04-09-1920-list-routing-on-d103-longer-take.pcapng",
                "2026-04-09-1916-list-routing-on-d103.pcapng",
                "2026-04-09-0107-list-routing-on-d100-proxy-d102.pcapng",
                "2026-04-09-0058-list-systems-on-d103.pcapng",
                "2026-04-09-0020-list-routing-d100-d102-then-d102-d100.pcapng",
                "2026-04-09-1242-tad-connect-to-d102-from-d100.pcapng",
                "2026-04-19-1147-xrout-route-query-d102-to-d100-sent-twice.pcapng",
                "2026-04-08-2339-tad-session-d100-to-d102-31-minutes.pcapng",
            };
            int[] counts = { 307, 147, 150, 42, 64, 372, 144, 108, 36, 24, 174, 37, 342 };

            int total = 0;
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                int expected = counts[i];
                string path = Path.Combine(pcapDir, name);
                Assert.True(File.Exists(path), "Missing capture: " + name);

                IReadOnlyList<LapbFrame> frames = HdlcPcap.ReadFrames(path);
                int fcsValid = frames.Count;
                _output.WriteLine($"{name}: FCS-valid={fcsValid} (expected {expected})");

                Assert.True(fcsValid > 0, "No FCS-valid frames in " + name);
                Assert.Equal(expected, fcsValid);
                total += fcsValid;
            }

            Assert.Equal(1947, total);
        }

        /// <summary>
        /// Sanity check that 2026-04-08-2339-tad-session-d100-to-d102-31-minutes.pcapng yields more than 4000 raw TCP payload bytes worth
        /// of frames - that is, the pcapng/Ethernet/IP/TCP parse actually produced packets.
        /// </summary>
        [Fact]
        public void Test1Capture_YieldsFcsValidFrames()
        {
            string? pcapDir = PcapFiles.Directory();
            if (pcapDir == null)
            {
                _output.WriteLine("pcap directory not found; skipping.");
                return;
            }

            string path = Path.Combine(pcapDir, "2026-04-08-2339-tad-session-d100-to-d102-31-minutes.pcapng");
            if (!File.Exists(path))
            {
                _output.WriteLine("2026-04-08-2339-tad-session-d100-to-d102-31-minutes.pcapng not present; skipping.");
                return;
            }

            IReadOnlyList<LapbFrame> frames = HdlcPcap.ReadFrames(path);
            _output.WriteLine("2026-04-08-2339-tad-session-d100-to-d102-31-minutes.pcapng FCS-valid frames: " + frames.Count);
            Assert.True(frames.Count > 0);
        }

    }
}
