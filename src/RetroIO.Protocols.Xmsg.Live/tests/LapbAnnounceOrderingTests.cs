//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Hdlc;
using RetroIO.Protocols.Xmsg.Live;

using Xunit;

using static RetroIO.Protocols.Xmsg.Live.Tests.LapbTestKit;

namespace RetroIO.Protocols.Xmsg.Live.Tests
{
    /// <summary>
    /// Replays the 2026-08-08 live capture in which our own LAPB rejected D100's frame with FRMR,
    /// and pins the ordering rule that prevents it.
    /// </summary>
    /// <remarks>
    /// <para><b>What was captured</b></para>
    /// Evidence: <c>DOC/captures/FRMR-ON-INNAK-2026-08-08/frmr-on-innak.pcapng</c>. D100
    /// retransmitted the same datagrams every 40 seconds forever and D103 never saw traffic from
    /// 100, because we answered D100 with six Frame Rejects. Every FRMR in that capture goes from
    /// OUR socket to D100 - the defect was ours, after being misread twice as peer-side.
    /// <para><b>Why it happens, given that nothing violates the spec</b></para>
    /// Two MUST rules collide, and we obey both:
    ///  - Spec 3.2 - hard-zero V(S), V(A) and V(R) on EVERY received SABM, including mid-session.
    ///  - Spec 4.3 - an N(R) outside the window from V(A) to V(S) MUST be answered with FRMR(Z).
    /// D100's queued SABM retries land as SIX SABMs in one TCP segment. Answer the first, send the
    /// announce so V(S) becomes 1, then answer the rest and each resets V(S) to 0. D100 - which
    /// never reset its own view of our announce - acknowledges it with N(R)=1, which no longer fits
    /// the window, and 4.3 forces the reject. The ORDER is the defect, not either rule.
    /// <para><b>The bytes here are the captured ones</b></para>
    /// The announce and the INNAK are the real information fields off the wire, so these tests fail
    /// against the traffic that actually broke rather than against a synthetic stand-in.
    /// </remarks>
    public sealed class LapbAnnounceOrderingTests
    {
        // Our relay node, 19999 = 0x4E1F. Every node in the corpus before this one was under 256,
        // which is why the two-byte form matters here.
        private const ushort OurNode = 19999;

        // Our reachability announce to D100, captured verbatim (frame 343).
        private static readonly byte[] Announce =
        {
            0x21, 0x13, 0x00, 0x19, 0x4E, 0x1F, 0x00, 0x64, 0xFF, 0xFF, 0x00, 0x01, 0x90, 0x4F,
        };

        // D100's INNAK, captured verbatim (frame 381) - the frame we wrongly rejected. Marker 2 is
        // 0xFE, which is why the project dissector labels it "non-SINTRAN info".
        private static readonly byte[] Innak =
        {
            0x21, 0xFE, 0x00, 0x17, 0x4E, 0x1F, 0x00, 0x64, 0xFF, 0xFF, 0xFF, 0xFD, 0x8F, 0x69,
        };

        /// <summary>
        /// Reproduces the capture: an announce sent BETWEEN two SABM resets makes the peer's correct
        /// acknowledgement fall outside the window, and we reject it with FRMR reason Z.
        /// </summary>
        /// <remarks>
        /// This asserts the behaviour the spec REQUIRES once the link is in that state - the FRMR is
        /// not the bug. It exists so the mechanism cannot be "fixed" by weakening the 4.3 range
        /// check: if someone does that, this test fails and says why.
        /// </remarks>
        [Fact]
        public void AnnounceSentBetweenTwoSabmResets_RejectsThePeersAckWithFrmrReasonZ()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnectedInitiator(OurNode, Node100Hi, Node100Lo, sent, null);

            // The announce goes out after the FIRST SABM only - V(S) advances to 1.
            link.SendInformation(Announce, currentTicks: 0);
            Assert.Equal(1, link.SendVariable);

            // The rest of D100's queued SABM burst arrives. Spec 3.2: each one hard-zeroes V(S).
            for (int i = 0; i < 5; i++)
            {
                Deliver(link, 0x01, 0x3F, Node100Hi, Node100Lo);
            }

            Assert.Equal(0, link.SendVariable);   // our announce is no longer accounted for
            sent.Clear();

            // D100 acknowledges the announce it did receive: I N(S)=0 N(R)=1 (control 0x20).
            Deliver(link, 0x09, 0x20, Innak);

            byte[]? frmr = FindFrmr(sent);
            Assert.NotNull(frmr);
            Assert.Equal(0x01, frmr![0]);                                   // link-management address
            Assert.Equal(0x20, frmr[2]);                                    // the rejected control byte
            Assert.Equal((byte)LapbFrmrReason.ReceiveSequenceInvalid, frmr[4]);   // reason Z, 0x08
        }

        /// <summary>
        /// The fix, at protocol level: draining the whole SABM burst BEFORE announcing keeps the
        /// peer's acknowledgement inside the window, so the frame is accepted and no FRMR is sent.
        /// </summary>
        /// <remarks>
        /// This is what deferring the announce out of the link's status callback buys. The runner's
        /// single-link path already did this - it arms a flag in StatusChanged and sends on the next
        /// loop tick - and the relay path did not, which is the whole difference between a link that
        /// carries traffic and one that rejects it forever.
        /// </remarks>
        [Fact]
        public void AnnounceSentAfterDrainingTheSabmBurst_AcceptsThePeersAck_NoFrmr()
        {
            List<byte[]> sent = new List<byte[]>();
            List<byte[]> got = new List<byte[]>();
            LapbLayer link = NewConnectedInitiator(OurNode, Node100Hi, Node100Lo, sent, got);

            // Drain the whole burst FIRST - every reset happens before we transmit anything.
            for (int i = 0; i < 5; i++)
            {
                Deliver(link, 0x01, 0x3F, Node100Hi, Node100Lo);
            }

            // Only now announce. V(S) advances to 1 and stays there.
            link.SendInformation(Announce, currentTicks: 0);
            Assert.Equal(1, link.SendVariable);
            sent.Clear();

            // The same acknowledgement that was rejected above is now in window and accepted.
            Deliver(link, 0x09, 0x20, Innak);

            Assert.Null(FindFrmr(sent));
            Assert.Equal(1, link.AcknowledgeVariable);   // N(R)=1 acknowledged our announce
            Assert.Single(got);
            Assert.Equal(Innak, got[0]);                 // the INNAK was delivered up, not rejected
        }

        /// <summary>
        /// Finds the first FRMR among transmitted bodies.
        /// </summary>
        /// <param name="sent">
        /// The transmitted LAPB bodies.
        /// </param>
        /// <returns>
        /// The first FRMR body, or null when none was transmitted.
        /// </returns>
        /// <remarks>
        /// The Poll/Final bit is masked off because FRMR is emitted both with and without it - the
        /// capture shows control 0x97 (P/F set) and the base encoding is 0x87.
        /// </remarks>
        private static byte[]? FindFrmr(List<byte[]> sent)
        {
            for (int i = 0; i < sent.Count; i++)
            {
                byte[] body = sent[i];
                if (body.Length >= 5 && (body[1] & ~0x10) == 0x87)
                {
                    return body;
                }
            }

            return null;
        }
    }
}
