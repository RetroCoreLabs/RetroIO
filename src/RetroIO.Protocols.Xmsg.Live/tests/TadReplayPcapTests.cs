using System;
using System.Collections.Generic;
using System.IO;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Hdlc;
using RetroIO.Protocols.Xmsg.Node.Tad;

using RetroIO.Protocols.Xmsg.Tests;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Live.Tests
{
    /// <summary>
    /// Replays the captured connect-to (<c>new-conn-to-102-from-100.pcapng</c>) through the TAD
    /// session state machine and asserts the OBSERVED phase order and negotiated parameters.
    /// </summary>
    /// <remarks>
    /// When the capture cannot be located (set <c>XMSG_PCAP_DIR</c> or place <c>X25Emulator</c>
    /// next to <c>NDInsight</c>) the test passes with a log message rather than failing, so it
    /// is portable to machines without the corpus - matching the existing pcap test policy.
    /// </remarks>
    public sealed class TadReplayPcapTests
    {
        private const string ConnectCapture = "new-conn-to-102-from-100.pcapng";
        private const ushort ClientNode = 100;

        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test with the xUnit output sink.
        /// </summary>
        /// <param name="output">
        /// The per-test output helper.
        /// </param>
        public TadReplayPcapTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// Driving the client-originated data frames in capture order makes the session pass
        /// through XROUT setup, then negotiation, then the data phase, in that order, and
        /// capture the negotiated parameters.
        /// </summary>
        [Fact]
        public void Replay_ClientConnectTo_FollowsObservedPhaseOrder()
        {
            string? dir = PcapFiles.Directory();
            if (dir == null)
            {
                _output.WriteLine("pcap directory not found; skipping replay test.");
                return;
            }

            string path = Path.Combine(dir, ConnectCapture);
            if (!File.Exists(path))
            {
                _output.WriteLine("capture " + ConnectCapture + " not found; skipping replay test.");
                return;
            }

            TadSession client = new TadSession(TadSessionRole.Client);

            IReadOnlyList<LapbFrame> frames = HdlcPcap.ReadFrames(path);
            int observed = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                LapbFrame frame = frames[i];
                if (frame.Kind != LapbFrameKind.Information || !frame.IsSintranInfo)
                {
                    continue;
                }

                ReadOnlyMemory<byte> info = frame.Info;
                if (info.Length < SintranHeader.Size)
                {
                    continue;
                }

                XmsgFrame decoded = XmsgFrame.Parse(info.Span);

                // Drive only the client-originated data frames (the connect-to driver side).
                if (decoded.Header.Subtype != SintranPacketSubtype.Data
                    || decoded.Header.SourceNode != ClientNode)
                {
                    continue;
                }

                client.Observe(decoded);
                observed++;
            }

            Assert.True(observed > 0, "expected at least one client data frame in the capture.");

            // The three connect-to phases must have been entered in the observed order.
            int setupAt = IndexOf(client.PhaseHistory, TadSessionState.XroutSetup);
            int negAt = IndexOf(client.PhaseHistory, TadSessionState.Negotiating);
            int dataAt = IndexOf(client.PhaseHistory, TadSessionState.DataPhase);

            Assert.True(setupAt >= 0, "XROUT setup phase was never entered.");
            Assert.True(negAt > setupAt, "Negotiating must follow XROUT setup.");
            Assert.True(dataAt > negAt, "DataPhase must follow Negotiating.");

            // Negotiated parameters observed on this single connect-to (OBSERVED, single capture).
            Assert.True(client.Parameters.TerminalType.HasValue);
            Assert.Equal((ushort)0x0000, client.Parameters.TerminalType!.Value);
            Assert.Equal(new byte[] { 0x4C, 0x01, 0x04 }, client.Parameters.GetOsVersionCopy());
            Assert.True(client.Parameters.EscapeCharacter.HasValue);
            Assert.Equal((byte)0x1B, client.Parameters.EscapeCharacter!.Value);

            _output.WriteLine("client data frames observed: " + observed);
            _output.WriteLine("final phase: " + client.State);
        }

        /// <summary>
        /// Finds the first index of a phase in the phase history.
        /// </summary>
        /// <param name="history">
        /// The session phase history.
        /// </param>
        /// <param name="state">
        /// The phase to locate.
        /// </param>
        /// <returns>
        /// The zero-based index, or -1 when the phase was never entered.
        /// </returns>
        private static int IndexOf(IReadOnlyList<TadSessionState> history, TadSessionState state)
        {
            for (int i = 0; i < history.Count; i++)
            {
                if (history[i] == state)
                {
                    return i;
                }
            }

            return -1;
        }

    }
}
