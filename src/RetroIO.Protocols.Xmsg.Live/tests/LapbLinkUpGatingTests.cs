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
    /// Replays the 2026-08-19 failure: the link reported itself usable after only ONE direction of
    /// the handshake, so the layer above transmitted into a window that was about to be reset.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the existing suite did not catch this</b></para>
    /// <para>
    /// 1166 tests were green on the day this failed. None of them replayed a peer that answers our
    /// SABM LATE while its own SABM arrives early - which is what a queued backlog produces. The
    /// bug had been latent for months and only appeared after a 3.5 hour gap between runs let the
    /// peer's queued output grow.
    /// </para>
    /// <para><b>What the spec says, and it is a table cell rather than prose</b></para>
    /// <para>
    /// The state table (section 8) carries "notify up" as an action SEPARATE from the transition:
    /// <c>rx SABM</c> in SABM_SENT is <c>send UA; reset -> CONNECTED</c> with NO notify up,
    /// while <c>rx UA</c> in SABM_SENT is <c>reset; notify up -> CONNECTED</c>. Section 3.1 says
    /// the same in prose - the link is up when BOTH directions have completed SABM -> UA.
    /// </para>
    /// <para><b>Relationship to LapbAnnounceOrderingTests</b></para>
    /// <para>
    /// That file pins what happens once a frame HAS been sent between two resets - the FRMR is
    /// required and must not be weakened. This file pins the thing that stops us getting there.
    /// </para>
    /// </remarks>
    public sealed class LapbLinkUpGatingTests
    {
        // Our relay node, 19999 = 0x4E1F, and the peer D100 = 0x0064.
        private const ushort OurNode = 19999;

        private const byte LinkAddress = 0x01;
        private const byte SabmControl = 0x3F;
        private const byte UaControl = 0x73;

        /// <summary>
        /// Answering the peer's SABM does NOT make the link usable while our own SABM is still
        /// unacknowledged.
        /// </summary>
        /// <remarks>
        /// This is the exact shape of the live failure. We sent SABM at T+0, the peer's SABM
        /// arrived at T+8 ms and we answered it, and the UA for OUR SABM did not arrive until
        /// T+201 ms. In between, the link reported itself Active and an I-frame went out.
        /// </remarks>
        [Fact]
        public void PeerSabmAloneDoesNotBringTheLinkUp()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewLink(OurNode, sent);

            link.Connect(currentTicks: 0);
            Assert.Equal(LapbLayerState.SabmSent, link.State);
            Assert.False(link.IsUp);

            // The peer's SABM arrives first. We answer it and the state machine moves to CONNECTED
            // - correctly, per the table - but our own direction is NOT done.
            Deliver(link, LinkAddress, SabmControl, 0x00, 0x64);

            Assert.Equal(LapbLayerState.Connected, link.State);
            Assert.False(link.IsUp);   // <-- the whole point: CONNECTED is not UP
        }

        /// <summary>
        /// The UA answering our own SABM is what brings the link up.
        /// </summary>
        [Fact]
        public void TheUaForOurOwnSabmBringsTheLinkUp()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewLink(OurNode, sent);

            link.Connect(currentTicks: 0);
            Deliver(link, LinkAddress, SabmControl, 0x00, 0x64);
            Assert.False(link.IsUp);

            // Both directions have now completed SABM -> UA (spec 3.1).
            Deliver(link, LinkAddress, UaControl, 0x00, 0x64);

            Assert.Equal(LapbLayerState.Connected, link.State);
            Assert.True(link.IsUp);
        }

        /// <summary>
        /// A mid-session SABM resets the sequence but leaves the link UP.
        /// </summary>
        /// <remarks>
        /// The table is explicit for <c>rx SABM</c> in CONNECTED: "send UA; reset (mid-session,
        /// stay up)". Dropping the link on a mid-session SABM would be the standard-LAPB reading
        /// and is the single most important ND deviation - it must not creep back in as a
        /// side-effect of gating the up state.
        /// </remarks>
        [Fact]
        public void AMidSessionSabmResetsTheSequenceButStaysUp()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewLink(OurNode, sent);

            link.Connect(currentTicks: 0);
            Deliver(link, LinkAddress, SabmControl, 0x00, 0x64);
            Deliver(link, LinkAddress, UaControl, 0x00, 0x64);
            Assert.True(link.IsUp);

            link.SendInformation(new byte[] { 0xAA, 0xBB }, currentTicks: 0);
            Assert.Equal(1, link.SendVariable);

            // Mid-session SABM: sequence hard-zeroed (3.2), link still up.
            Deliver(link, LinkAddress, SabmControl, 0x00, 0x64);

            Assert.Equal(0, link.SendVariable);
            Assert.Equal(LapbLayerState.Connected, link.State);
            Assert.True(link.IsUp);
        }

        /// <summary>
        /// A late UA is honoured even though it arrives in CONNECTED rather than SABM_SENT.
        /// </summary>
        /// <remarks>
        /// This is the ordering the live failure actually had, and the reason this fix tracks two
        /// HALVES rather than the state table's single "notify up" action. The table has no cell
        /// for a UA arriving in CONNECTED, because it assumes SABM_SENT is left exactly one way.
        /// The first version of this fix honoured the UA only in SABM_SENT and left the link
        /// permanently half-up - caught by these tests, not by the machine.
        /// </remarks>
        [Fact]
        public void ALateUaArrivingInConnectedStillCompletesOurHalf()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewLink(OurNode, sent);

            link.Connect(currentTicks: 0);

            // The peer's SABM lands FIRST and takes us out of SABM_SENT.
            Deliver(link, LinkAddress, SabmControl, 0x00, 0x64);
            Assert.Equal(LapbLayerState.Connected, link.State);
            Assert.False(link.IsUp);

            // The UA answering OUR SABM arrives late, in CONNECTED.
            Deliver(link, LinkAddress, UaControl, 0x00, 0x64);

            Assert.True(link.IsUp);
        }

        /// <summary>
        /// A station that never sent a SABM comes up on the peer's SABM alone.
        /// </summary>
        /// <remarks>
        /// The table's "rx SABM in DISCONNECTED -> notify up" case. A passive answerer has no
        /// outstanding direction of its own, so requiring an acknowledgement it never asked for
        /// would leave it permanently down. This is why the our-half flag starts TRUE and is
        /// cleared by Connect() rather than starting false.
        /// </remarks>
        [Fact]
        public void APassiveStationComesUpOnThePeerSabmAlone()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewLink(OurNode, sent);

            // No Connect() - we never send a SABM of our own.
            Deliver(link, LinkAddress, SabmControl, 0x00, 0x64);

            Assert.Equal(LapbLayerState.Connected, link.State);
            Assert.True(link.IsUp);
        }
    }
}
