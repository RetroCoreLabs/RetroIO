//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Live;

using Xunit;

using static RetroIO.Protocols.Xmsg.Live.Tests.LapbTestKit;

namespace RetroIO.Protocols.Xmsg.Live.Tests
{
    /// <summary>
    /// The ND LAPB spec section 8.2 byte-level acceptance vectors S1-S8, asserted frame-for-frame.
    /// Local node = 100 (<c>00 64</c>), peer node = 102 (<c>00 66</c>), exactly as the spec fixes them.
    /// </summary>
    /// <remarks>
    /// Our implementation emits one spec-permitted extra RR keepalive when it enters CONNECTED (spec
    /// 3.4, live-necessary so the ND peer advances to RUN); the vectors below account for it where it
    /// appears and otherwise assert the required frames byte-for-byte. S8 is a framing-layer vector
    /// (byte-stuffing / FCS) and is covered by the HDLC tests, not here.
    /// </remarks>
    public sealed class LapbConformanceVectorsTests
    {
        private const byte L100Hi = 0x00;   // local node 100
        private const byte L100Lo = 0x64;
        private const byte P102Hi = 0x00;   // peer node 102
        private const byte P102Lo = 0x66;

        /// <summary>
        /// S1 - balanced bring-up: TX SABM, then on RX SABM answer UA, then RX UA completes with the
        /// variables zeroed both directions.
        /// </summary>
        [Fact]
        public void S1_BalancedBringUp()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewLink(100, sent);

            link.Connect(currentTicks: 0);
            Assert.Equal(new byte[] { 0x01, 0x3F, L100Hi, L100Lo }, sent[0]);   // TX SABM P1 + node 100
            sent.Clear();

            Deliver(link, 0x01, 0x3F, P102Hi, P102Lo);                         // RX SABM P1 + node 102
            Assert.Equal(new byte[] { 0x01, 0x73, L100Hi, L100Lo }, sent[0]);   // TX UA F1 + node 100
            Assert.Equal(new byte[] { 0x09, 0x01, L100Hi, L100Lo }, sent[1]);   // (permitted) RR keepalive
            Assert.Equal(0x0066, link.PeerNode);
            sent.Clear();

            Deliver(link, 0x01, 0x73, P102Hi, P102Lo);                         // RX UA F1 + node 102
            Assert.Equal(LapbLayerState.Connected, link.State);
            Assert.Equal(0, link.SendVariable);
            Assert.Equal(0, link.ReceiveVariable);
            Assert.Equal(0, link.AcknowledgeVariable);
        }

        /// <summary>
        /// S2 - REJ go-back-N: two outstanding I-frames are both retransmitted from N(R)=0.
        /// </summary>
        [Fact]
        public void S2_RejGoBackN()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnectedInitiator(100, P102Hi, P102Lo, sent, null);
            sent.Clear();

            link.SendInformation(new byte[] { 0xD0 }, currentTicks: 0);
            link.SendInformation(new byte[] { 0xD1 }, currentTicks: 0);
            Assert.Equal(new byte[] { 0x89, 0x00, 0xD0 }, sent[0]);   // TX I N(S)=0 N(R)=0
            Assert.Equal(new byte[] { 0x89, 0x02, 0xD1 }, sent[1]);   // TX I N(S)=1 N(R)=0
            sent.Clear();

            Deliver(link, 0x09, 0x09, P102Hi, P102Lo);               // RX REJ N(R)=0

            Assert.Equal(2, sent.Count);
            Assert.Equal(new byte[] { 0x89, 0x00, 0xD0 }, sent[0]);   // resent N(S)=0
            Assert.Equal(new byte[] { 0x89, 0x02, 0xD1 }, sent[1]);   // resent N(S)=1
        }

        /// <summary>
        /// S3 - RNR hold and resume: RNR sets peer-busy and acknowledges; a later RR clears busy.
        /// </summary>
        [Fact]
        public void S3_RnrHoldAndResume()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnectedInitiator(100, P102Hi, P102Lo, sent, null);
            sent.Clear();

            link.SendInformation(new byte[] { 0xC0 }, currentTicks: 0);   // TX I N(S)=0
            Assert.Equal(new byte[] { 0x89, 0x00, 0xC0 }, sent[0]);   // 1-byte info = odd -> address 0x89

            Deliver(link, 0x09, 0x25, P102Hi, P102Lo);   // RX RNR N(R)=1
            Assert.True(link.PeerBusy);
            Assert.Equal(1, link.AcknowledgeVariable);

            Deliver(link, 0x09, 0x21, P102Hi, P102Lo);   // RX RR N(R)=1
            Assert.False(link.PeerBusy);
        }

        /// <summary>
        /// S4 - T1 x N2 -> link down and re-establish with a fresh SABM (uses fast timers).
        /// </summary>
        /// <remarks>
        /// This vector asserts the SPEC behaviour, so it opts in with
        /// <see cref="LapbOptions.ReestablishOnLinkFailure"/>. The live default is the opposite: the
        /// re-establish SABM kills a real XMSG gateway (measured twice on D100, 2026-08-21, fatal
        /// code 27 within 200 ms), so the runner reports the failure and stops instead. The guard for
        /// that default lives in <c>LapbTimerTests.N2_Exhaustion_ByDefault_ReportsFailureAndSendsNoSabm</c>.
        /// </remarks>
        [Fact]
        public void S4_T1TimesN2_ReEstablishes()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbOptions fast = new LapbOptions(t1: 100, t3: 1000, n2: 3, windowSize: 7, reestablishOnLinkFailure: true);
            LapbLayer link = NewConnectedInitiator(100, P102Hi, P102Lo, sent, null, fast);
            link.SendInformation(new byte[] { 0xB0 }, currentTicks: 0);   // arms T1
            sent.Clear();

            link.Tick(100);   // retry 1
            link.Tick(200);   // retry 2
            link.Tick(300);   // retry 3
            sent.Clear();
            link.Tick(400);   // retry 4 > N2 -> re-establish

            Assert.Equal(LapbLayerState.SabmSent, link.State);
            Assert.Contains(new byte[] { 0x01, 0x3F, L100Hi, L100Lo }, sent);   // TX SABM P1 + node 100
        }

        /// <summary>
        /// S5 - gap -> a single REJ; a further ahead frame is dropped silently.
        /// </summary>
        [Fact]
        public void S5_GapSingleRej()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnectedInitiator(100, P102Hi, P102Lo, sent, null);   // V(R)=0
            sent.Clear();

            Deliver(link, 0x09, 0x02, 0x21, 0x13);   // RX I N(S)=1 (gap)
            Assert.Equal(new byte[] { 0x09, 0x09, L100Hi, L100Lo }, sent[0]);   // TX REJ N(R)=0
            sent.Clear();

            Deliver(link, 0x09, 0x04, 0x21, 0x13);   // RX I N(S)=2 (still ahead) -> silent
            Assert.Empty(sent);
        }

        /// <summary>
        /// S6 - a received P=1 is answered with an RR carrying F=1 and the current N(R).
        /// </summary>
        [Fact]
        public void S6_PollAnsweredWithFinal()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnectedInitiator(100, P102Hi, P102Lo, sent, null);   // V(R)=0
            sent.Clear();

            Deliver(link, 0x09, 0x11, P102Hi, P102Lo);   // RX RR P=1 N(R)=0

            Assert.Equal(new byte[] { 0x09, 0x11, L100Hi, L100Lo }, sent[0]);   // TX RR F=1 N(R)=0
        }

        /// <summary>
        /// S7 - mid-session SABM: reset the variables and queue but STAY connected, then the next
        /// I-frame N(S)=0 is delivered afresh.
        /// </summary>
        [Fact]
        public void S7_MidSessionSabmReset()
        {
            List<byte[]> sent = new List<byte[]>();
            List<byte[]> got = new List<byte[]>();
            LapbLayer link = NewConnectedInitiator(100, P102Hi, P102Lo, sent, got);

            Deliver(link, 0x09, 0x00, 0x21, 0x13);   // deliver N(S)=0 -> V(R)=1
            Deliver(link, 0x09, 0x02, 0x21, 0x13);   // deliver N(S)=1 -> V(R)=2
            link.SendInformation(new byte[] { 0x55 }, currentTicks: 0);   // V(S)=1
            link.SendInformation(new byte[] { 0x56 }, currentTicks: 0);   // V(S)=2
            link.SendInformation(new byte[] { 0x57 }, currentTicks: 0);   // V(S)=3
            Assert.Equal(3, link.SendVariable);
            Assert.Equal(2, link.ReceiveVariable);
            sent.Clear();
            got.Clear();

            Deliver(link, 0x01, 0x3F, P102Hi, P102Lo);   // RX SABM P1 + node 102

            Assert.Equal(LapbLayerState.Connected, link.State);   // stayed up
            Assert.Equal(0, link.SendVariable);
            Assert.Equal(0, link.ReceiveVariable);
            Assert.Equal(0, link.AcknowledgeVariable);
            Assert.Equal(new byte[] { 0x01, 0x73, L100Hi, L100Lo }, sent[0]);   // TX UA F1 + node 100

            Deliver(link, 0x09, 0x00, 0x21, 0x13);   // TX next I N(S)=0 delivered afresh
            Assert.Single(got);
            Assert.Equal(1, link.ReceiveVariable);
        }
    }
}
