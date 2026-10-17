//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Live;

using Xunit;

using static RetroIO.Protocols.Xmsg.Live.Tests.LapbTestKit;

namespace RetroIO.Protocols.Xmsg.Live.Tests
{
    /// <summary>
    /// Pins the window in which the link is CONNECTED but not yet UP, because that window is where
    /// a frame handed down gets thrown away.
    /// </summary>
    /// <remarks>
    /// <para><b>The same conflation appeared twice</b></para>
    /// <para>
    /// <c>LapbLayerAdapter</c> derived its Active status from <c>State == Connected</c>, and the
    /// runner independently gated its transfer pumps on the same expression. Fixing the adapter
    /// alone just moved the symptom one layer up: the connect letter was still handed down 20 ms
    /// early, <c>SendData</c> correctly refused it as not-Active, and the push then failed saying
    /// the peer "answered none of 4 connect letters" - true, because none had been sent.
    /// </para>
    /// <para><b>What this file is for</b></para>
    /// <para>
    /// Not to re-prove the gating - <c>LapbLinkUpGatingTests</c> does that. It is here to state
    /// that the CONNECTED-but-not-UP window is real and observable, so any future caller that
    /// reaches for <c>State</c> as a send gate has a test naming the trap.
    /// </para>
    /// </remarks>
    public sealed class LapbIsUpIsTheSendGateTests
    {
        private const ushort OurNode = 19999;

        private const byte LinkAddress = 0x01;
        private const byte SabmControl = 0x3F;
        private const byte UaControl = 0x73;

        /// <summary>
        /// There is a real window where State says Connected and IsUp says otherwise.
        /// </summary>
        /// <remarks>
        /// A caller gating on <c>State</c> transmits inside this window. A caller gating on
        /// <c>IsUp</c> waits. The window is not hypothetical: it was 20 ms wide against a real
        /// ND-100 on 2026-08-20, and wide enough to lose the frame that mattered.
        /// </remarks>
        [Fact]
        public void ThereIsAWindowWhereConnectedIsNotYetUp()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewLink(OurNode, sent);

            link.Connect(currentTicks: 0);

            // The peer's SABM arrives first and takes the state to CONNECTED.
            Deliver(link, LinkAddress, SabmControl, 0x00, 0x64);

            // THE WINDOW. Both of these are true at the same moment.
            Assert.Equal(LapbLayerState.Connected, link.State);
            Assert.False(link.IsUp);

            // The UA answering our own SABM closes it.
            Deliver(link, LinkAddress, UaControl, 0x00, 0x64);
            Assert.True(link.IsUp);
        }

        /// <summary>
        /// Once both halves are done, State and IsUp agree - so gating on IsUp costs nothing on a
        /// healthy link.
        /// </summary>
        /// <remarks>
        /// Worth stating because the obvious worry about a stricter gate is that it delays or
        /// blocks ordinary traffic. It does not: the difference exists only during establishment.
        /// </remarks>
        [Fact]
        public void OnceUpTheTwoAgree()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewLink(OurNode, sent);

            link.Connect(currentTicks: 0);
            Deliver(link, LinkAddress, SabmControl, 0x00, 0x64);
            Deliver(link, LinkAddress, UaControl, 0x00, 0x64);

            Assert.Equal(LapbLayerState.Connected, link.State);
            Assert.True(link.IsUp);

            // And a mid-session SABM - which resets the sequence - leaves both agreeing, because
            // the ND rule is that the link STAYS up across it.
            Deliver(link, LinkAddress, SabmControl, 0x00, 0x64);

            Assert.Equal(LapbLayerState.Connected, link.State);
            Assert.True(link.IsUp);
        }
    }
}
