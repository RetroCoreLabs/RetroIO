using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Node.Services;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Covers <see cref="XmsgServerHost.ResyncOriginationDown"/> - the recovery for a XENSE reject
    /// of a frame WE ORIGINATED, as opposed to the connect-accept that
    /// <c>ResyncAcceptDown</c> already handled.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect these tests stand for, measured on a live machine</b></para>
    /// <para>
    /// Flags 1 is persisted ON SEND, which is right - it is what the SINTRAN kernel does. But a
    /// frame the peer refuses with XENSE has already spent and persisted its number, and until
    /// 2026-08-24 nothing stepped it back for an originated frame. So each rejection left us one
    /// FURTHER ahead of the peer than the last, and the retry was refused for the same reason at a
    /// worse number - the opposite of converging.
    /// </para>
    /// <para>
    /// Measured against D100 on 2026-08-24: four connect-letter attempts walked the stored sequence
    /// 0x007B -> 0x007C -> 0x007E -> ... -> 0x008C while not one frame was ever accepted. The same
    /// signature was recorded on 2026-08-11 (0x003B -> 0x0040). It was a known hole for two weeks.
    /// </para>
    /// <para><b>What a test here can and cannot prove</b></para>
    /// <para>
    /// These prove the counter now moves the RIGHT WAY and lands on the right values. They cannot
    /// prove a real SINTRAN then accepts the frame - only a live run does that, and the live result
    /// belongs in the notes, not here.
    /// </para>
    /// </remarks>
    public sealed class OriginationXenseResyncTests
    {
        private const ushort ServerNode = 19999;
        private const ushort PeerNode = 100;

        private readonly ITestOutputHelper _output;

        public OriginationXenseResyncTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// A XENSE against an originated frame steps the outgoing sequence DOWN, to one below the
        /// number that was refused - and persists it.
        /// </summary>
        /// <remarks>
        /// XENSE has exactly one meaning: our number is AHEAD of what the peer expects. So the
        /// largest value that is certainly acceptable is one below the refused one. Before the fix
        /// this direction was not merely wrong, it did not happen at all.
        /// </remarks>
        [Fact]
        public void AXenseOnAnOriginatedFrameStepsTheSequenceDown()
        {
            MemoryStore store = new MemoryStore();
            XmsgServerHost host = BuildHost(store);

            // Stand where the live run stood: our stored sequence has climbed well past the peer.
            host.Route(BuildLinkCreatingFrame());
            store.SaveNextFlags1(PeerNode, 0x007F);

            bool moved = host.ResyncOriginationDown(PeerNode, 0x007E);

            Assert.True(moved, "the recovery should report that it moved the sequence");
            Assert.Equal((ushort)0x007D, store.LoadNextFlags1(PeerNode));
        }

        /// <summary>
        /// The step-down is the OPPOSITE of what the defect did: the stored number must fall, never
        /// climb, across a run of rejections.
        /// </summary>
        /// <remarks>
        /// This is the regression test proper. It replays the shape of the live failure - repeated
        /// XENSE, each naming the number just refused - and asserts the counter walks DOWN. Run
        /// against the old code the stored value only ever increased, so this fails loudly.
        /// </remarks>
        [Fact]
        public void RepeatedRejectionsWalkTheSequenceDownNotUp()
        {
            MemoryStore store = new MemoryStore();
            XmsgServerHost host = BuildHost(store);

            host.Route(BuildLinkCreatingFrame());
            store.SaveNextFlags1(PeerNode, 0x0040);

            ushort refused = 0x003F;
            ushort previous = store.LoadNextFlags1(PeerNode);

            for (int i = 0; i < 4; i++)
            {
                host.ResyncOriginationDown(PeerNode, refused);

                ushort now = store.LoadNextFlags1(PeerNode);
                Assert.True(
                    now < previous,
                    "the stored sequence must fall after a XENSE; it went 0x" + previous.ToString("X4")
                    + " -> 0x" + now.ToString("X4"));

                previous = now;

                // The peer refuses whatever we send next, which is the corrected number.
                refused = now;
            }
        }

        /// <summary>
        /// When stepping down stops converging, the sequence is LEFT WHERE IT IS - it is not
        /// reset to zero.
        /// </summary>
        /// <remarks>
        /// <para><b>This test exists because the opposite was tried and measured wrong</b></para>
        /// <para>
        /// The first version of this recovery dropped to 0 on giving up, reasoning that a walk that
        /// will not converge means the peer restarted. Against a live D100 on 2026-08-24 that made
        /// things strictly worse: the machine wanted 122 (0x007A) and we were at 0x008C - eighteen
        /// ahead. Resetting put us a hundred and twenty-two BEHIND, and behind is dropped in
        /// SILENCE, so a loud refusal we could diagnose became no answer at all.
        /// </para>
        /// <para>
        /// The real peer-restart case is already handled elsewhere, by the ReachabilityRequest the
        /// peer sends. There is nothing to guess here, so nothing is guessed.
        /// </para>
        /// </remarks>
        [Fact]
        public void AWalkThatDoesNotConvergeLeavesTheSequenceAlone()
        {
            MemoryStore store = new MemoryStore();
            XmsgServerHost host = BuildHost(store);

            host.Route(BuildLinkCreatingFrame());
            store.SaveNextFlags1(PeerNode, 0x0400);

            // The peer refuses everything - the "it is not a small drift" case.
            ushort refused = 0x03FF;
            for (int i = 0; i < 32; i++)
            {
                host.ResyncOriginationDown(PeerNode, refused);
                refused = store.LoadNextFlags1(PeerNode);
            }

            // It walked down a little and then stopped. What must NOT happen is a jump to zero.
            ushort final = store.LoadNextFlags1(PeerNode);
            Assert.NotEqual((ushort)0x0000, final);
            Assert.True(
                final > 0x03F0,
                "the sequence should be left near where stepping reached, not reset; it was 0x"
                + final.ToString("X4"));
        }

        /// <summary>
        /// A peer that refuses our 0x0000 is not telling us we are ahead, so that case goes
        /// straight to the baseline instead of trying to step below zero.
        /// </summary>
        /// <remarks>
        /// Guards the underflow. Stepping down from 0 would wrap to 0xFFFF, which is the reserved
        /// "I claim no sequence" value - a wrong number AND a meaningful one, the worst pair.
        /// </remarks>
        [Fact]
        public void AXenseAtZeroDoesNotWrapBelowZero()
        {
            MemoryStore store = new MemoryStore();
            XmsgServerHost host = BuildHost(store);

            host.Route(BuildLinkCreatingFrame());
            store.SaveNextFlags1(PeerNode, 0x0000);

            host.ResyncOriginationDown(PeerNode, 0x0000);

            // The only hard rule: never 0xFFFF, which is the reserved "I claim no sequence" value.
            Assert.NotEqual((ushort)0xFFFF, store.LoadNextFlags1(PeerNode));
        }

        /// <summary>
        /// An acknowledgement clears the step count, so a later drift gets its own full walk.
        /// </summary>
        /// <remarks>
        /// An ACK proves the two sides are in step. If the count survived it, the second drift of a
        /// long session would inherit a stale total, skip the walk and jump to the zero fallback -
        /// resetting a sequence that only needed nudging by one.
        /// </remarks>
        [Fact]
        public void AnAcknowledgementClearsTheStepCount()
        {
            MemoryStore store = new MemoryStore();
            XmsgServerHost host = BuildHost(store);

            host.Route(BuildLinkCreatingFrame());
            store.SaveNextFlags1(PeerNode, 0x0100);

            // Burn most of the allowance.
            ushort refused = 0x00FF;
            for (int i = 0; i < 7; i++)
            {
                host.ResyncOriginationDown(PeerNode, refused);
                refused = store.LoadNextFlags1(PeerNode);
            }

            // The peer acknowledges something: we are back in step.
            host.ConfirmDelivered(PeerNode, refused);

            // A fresh drift must STEP, not fall back to zero.
            store.SaveNextFlags1(PeerNode, 0x0080);
            host.ResyncOriginationDown(PeerNode, 0x007F);

            Assert.Equal((ushort)0x007E, store.LoadNextFlags1(PeerNode));
        }

        /// <summary>
        /// With no link for the node there is nothing to correct, and it says so rather than
        /// inventing one.
        /// </summary>
        [Fact]
        public void AnUnknownNodeIsReportedRatherThanCorrected()
        {
            MemoryStore store = new MemoryStore();
            XmsgServerHost host = BuildHost(store);

            Assert.False(host.ResyncOriginationDown(4242, 0x0010));
        }

        private XmsgServerHost BuildHost(IResponderSequenceStore store)
        {
            XmsgServerHost host = new XmsgServerHost(ServerNode, store);
            host.Log += line => _output.WriteLine(line);
            return host;
        }

        /// <summary>
        /// A frame from the peer, purely to make the host create the per-node link that the
        /// sequence lives on. The FA connect letter is reused because it already comes from node
        /// 100 to node 19999 - the same pair these tests use - and routing it is what registers
        /// the link.
        /// </summary>
        private static XmsgFrame BuildLinkCreatingFrame()
        {
            return FaTestClient.BuildConnectLetter();
        }

        /// <summary>
        /// A sequence store that keeps everything in memory, so these tests touch no files.
        /// </summary>
        private sealed class MemoryStore : IResponderSequenceStore
        {
            private readonly Dictionary<ushort, ushort> _sequences = new Dictionary<ushort, ushort>();

            public ushort LoadNextFlags1(ushort remoteNode)
            {
                ushort value;
                return _sequences.TryGetValue(remoteNode, out value) ? value : (ushort)0;
            }

            public void SaveNextFlags1(ushort remoteNode, ushort nextFlags1)
            {
                _sequences[remoteNode] = nextFlags1;
            }
        }
    }
}
