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
    /// Timer proof for the LAPB state machine (spec section 5): T1 retransmission/poll, N2 exhaustion
    /// with auto re-establish, and the T3 idle keepalive, all driven by injected time. Also covers
    /// <see cref="LapbOptions"/> validation and that custom timer values change the timing.
    /// </summary>
    public sealed class LapbTimerTests
    {
        // Fast timers so the injected clock stays readable: T1 = 100, T3 = 1000, N2 = 3.
        private static LapbOptions Fast()
        {
            return new LapbOptions(t1: 100, t3: 1000, n2: 3);
        }

        // The same fast timers, but with the spec's automatic re-establish turned back ON. It is OFF
        // by default because that SABM kills a real XMSG gateway - see LapbOptions.
        private static LapbOptions FastReestablishing()
        {
            return new LapbOptions(t1: 100, t3: 1000, n2: 3, windowSize: 7, reestablishOnLinkFailure: true);
        }

        /// <summary>
        /// T1 does not fire before its deadline and, on expiry with a frame outstanding, polls the peer
        /// with RR (P=1) (spec 5.1 poll-first).
        /// </summary>
        [Fact]
        public void T1_ExpiryWithOutstanding_PollsRrP1()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null, Fast());
            link.SendInformation(new byte[] { 0x55 }, currentTicks: 0);   // arms T1 at deadline 100
            sent.Clear();

            Assert.False(link.Tick(99));    // before the deadline: nothing
            Assert.Empty(sent);

            Assert.True(link.Tick(100));    // at the deadline: poll
            Assert.Single(sent);
            Assert.Equal(new byte[] { 0x09, 0x11, Node102Hi, Node102Lo }, sent[0]);   // RR P=1 N(R)=0
        }

        /// <summary>
        /// T1 expiry while in SABM_SENT retransmits the SABM (P=1) (spec 5.1).
        /// </summary>
        [Fact]
        public void T1_ExpiryInSabmSent_RetransmitsSabm()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewLink(102, sent, Fast());
            link.Connect(currentTicks: 0);   // SABM, arms T1 at 100
            sent.Clear();

            Assert.True(link.Tick(100));
            Assert.Single(sent);
            Assert.Equal(new byte[] { 0x01, 0x3F, Node102Hi, Node102Lo }, sent[0]);   // resent SABM P=1
        }

        /// <summary>
        /// Exceeding N2 re-establishes from CONNECTED with a fresh SABM when the caller opts in
        /// (spec 5.1 step 1, S4).
        /// </summary>
        [Fact]
        public void N2_Exhaustion_AutoReEstablishes_WhenOptedIn()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null, FastReestablishing());   // N2 = 3
            link.SendInformation(new byte[] { 0x55 }, currentTicks: 0);
            sent.Clear();

            // Each expiry increments the retry counter and restarts T1 (deadline now + 100).
            Assert.True(link.Tick(100));   // retry 1
            Assert.True(link.Tick(200));   // retry 2
            Assert.True(link.Tick(300));   // retry 3
            sent.Clear();
            Assert.True(link.Tick(400));   // retry 4 > N2 -> re-establish

            Assert.Equal(LapbLayerState.SabmSent, link.State);
            Assert.Equal(0, link.SendVariable);   // Reset cleared outstanding
            Assert.Contains(new byte[] { 0x01, 0x3F, Node102Hi, Node102Lo }, sent);   // fresh SABM P=1
        }

        /// <summary>
        /// BY DEFAULT, exceeding N2 on a live link does NOT send a SABM - it reports the failure.
        /// </summary>
        /// <remarks>
        /// This is the regression guard for the thing that took D100 down. The re-establish is
        /// correct LAPB and it killed a real XMSG gateway twice on 2026-08-21, 147 ms and 201 ms
        /// after our SABM, with <c>XMSG ERROR CODE: 27</c> both times. A SABM MUST NOT leave this
        /// layer as the automatic consequence of a stalled window.
        /// </remarks>
        [Fact]
        public void N2_Exhaustion_ByDefault_ReportsFailureAndSendsNoSabm()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null, Fast());   // N2 = 3, no re-establish
            List<string> failures = new List<string>();
            link.OnLinkFailure += delegate (string reason) { failures.Add(reason); };

            link.SendInformation(new byte[] { 0x55 }, currentTicks: 0);

            Assert.True(link.Tick(100));   // retry 1
            Assert.True(link.Tick(200));   // retry 2
            Assert.True(link.Tick(300));   // retry 3
            sent.Clear();
            Assert.True(link.Tick(400));   // retry 4 > N2 -> declare dead, say so

            Assert.Equal(LapbLayerState.Disconnected, link.State);
            Assert.Single(failures);
            Assert.Contains("N2", failures[0]);

            // THE POINT OF THE TEST: nothing that goes out may be a SABM. Address 0x01 with the
            // SABM control byte 0x3F is the frame that kills the peer.
            for (int i = 0; i < sent.Count; i++)
            {
                byte[] frame = sent[i];
                bool isSabm = frame.Length >= 2 && frame[0] == 0x01 && (frame[1] & 0xEF) == 0x2F;
                Assert.False(isSabm, "a SABM was emitted on N2 exhaustion; that kills the peer's XMSG gateway");
            }
        }

        /// <summary>
        /// T3 expiry while connected and idle sends an RR (P=1) keepalive poll (spec 5.2).
        /// </summary>
        [Fact]
        public void T3_ExpiryWhenIdle_SendsRrP1()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null, Fast());   // T3 armed at 1000 on connect
            sent.Clear();

            Assert.False(link.Tick(999));   // before T3
            Assert.Empty(sent);

            Assert.True(link.Tick(1000));   // T3 fires
            Assert.Single(sent);
            Assert.Equal(new byte[] { 0x09, 0x11, Node102Hi, Node102Lo }, sent[0]);   // RR P=1
        }

        /// <summary>
        /// Custom T1 changes the timing: a link built with the default T1 (3000) does not fire at 100,
        /// while a fast link does.
        /// </summary>
        [Fact]
        public void CustomOptions_ChangeTiming()
        {
            List<byte[]> sentFast = new List<byte[]>();
            LapbLayer fast = NewConnected(102, sentFast, null, Fast());
            fast.SendInformation(new byte[] { 0x55 }, currentTicks: 0);
            sentFast.Clear();
            Assert.True(fast.Tick(100));    // fast T1 fires
            Assert.NotEmpty(sentFast);

            List<byte[]> sentSlow = new List<byte[]>();
            LapbLayer slow = NewConnected(102, sentSlow, null);   // default T1 = 3000
            slow.SendInformation(new byte[] { 0x55 }, currentTicks: 0);
            sentSlow.Clear();
            Assert.False(slow.Tick(100));   // default T1 has not elapsed
            Assert.Empty(sentSlow);
        }

        /// <summary>
        /// <see cref="LapbOptions"/> rejects non-positive timers, a sub-1 N2, and a window outside 1..7.
        /// </summary>
        [Fact]
        public void Options_ValidateRanges()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LapbOptions(t1: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LapbOptions(t3: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LapbOptions(n2: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LapbOptions(windowSize: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LapbOptions(windowSize: 8));
        }

        /// <summary>
        /// The default options carry the spec section 5 values.
        /// </summary>
        [Fact]
        public void Default_CarriesSpecValues()
        {
            LapbOptions d = LapbOptions.Default;
            Assert.Equal(3000, d.T1);
            Assert.Equal(30000, d.T3);
            Assert.Equal(10, d.N2);
            Assert.Equal(7, d.WindowSize);
        }
    }
}
