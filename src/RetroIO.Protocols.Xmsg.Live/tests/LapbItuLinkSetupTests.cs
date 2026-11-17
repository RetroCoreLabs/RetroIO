//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using Xunit;

namespace RetroIO.Protocols.Xmsg.Live.Tests
{
    /// <summary>
    /// Link set-up in the ITU dialect of <see cref="LapbLayer"/>, by the X.25 (10/96) rules, when the
    /// station's own SABM is refused or crosses the peer's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 2.4.4.1: a station that answers a SABM with UA resets V(S) and V(R) and the link is set up.
    /// 2.4.5.11.1 (both stations send the same command): each sends the UA, then enters the
    /// information transfer phase when it receives the UA or when its T1 runs out. Neither rule
    /// ever sends the SABM again into a link that is set up.
    /// </para>
    /// <para>
    /// Measured 2026-11-16, x25switch (DTE) against the SINTRAN K COSMOS X.25 link on port 2362: the
    /// switch's SABM was refused with DM, SINTRAN then sent its own SABM, the switch answered UA,
    /// the link came up and the X.25 restart completed - and 3 s later the switch's T1 sent its old
    /// SABM again without resetting its own counters, so its next poll carried N(R)=1 into a link
    /// SINTRAN had just reset, and SINTRAN answered FRMR (Z, invalid N(R)).
    /// </para>
    /// </remarks>
    public class LapbItuLinkSetupTests
    {
        // ITU DTE (the switch's role): commands carry B, responses A; the DCE's commands carry A,
        // its responses B (2.4.2).
        private const byte FromDceCommand = LapbLayer.AddressItuA;
        private const byte FromDceResponse = LapbLayer.AddressItuB;

        private static LapbOptions ItuDte() => new LapbOptions(variant: LapbVariant.Itu, isDte: true);

        private static bool IsSabm(byte[] frame) => frame.Length >= 2 && (frame[1] & 0xEF) == 0x2F;

        private static int CountSabms(List<byte[]> sent)
        {
            int n = 0;
            for (int i = 0; i < sent.Count; i++)
            {
                if (IsSabm(sent[i]))
                {
                    n++;
                }
            }

            return n;
        }

        [Fact]
        public void OurSabmRefusedWithDm_ThenThePeersSabm_SetsTheLinkUp_AndNoSabmIsSentAgain()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent, ItuDte());

            link.Connect(0);
            LapbTestKit.DeliverAt(link, 100, FromDceResponse, 0x1F);   // DM, F=1: our SABM is refused
            LapbTestKit.DeliverAt(link, 200, FromDceCommand, 0x2F);    // the peer's SABM, P=0

            // 2.4.4.1: the UA answers the peer's SABM and the link is set up. No SABM of ours is
            // outstanding any more: the DM answered it.
            Assert.Equal(LapbLayerState.Connected, link.State);
            Assert.True(link.IsUp);

            sent.Clear();
            link.Tick(3500);
            link.Tick(7000);
            Assert.Equal(0, CountSabms(sent));
        }

        [Fact]
        public void CrossingSabms_TheUaToOursSetsTheLinkUp()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent, ItuDte());

            link.Connect(0);
            LapbTestKit.DeliverAt(link, 100, FromDceCommand, 0x3F);    // the peer's SABM crosses ours

            // 2.4.5.11.1: send the UA, then wait for the UA to our SABM (or T1).
            Assert.Equal(new byte[] { LapbLayer.AddressItuA, 0x73 }, sent[1]);
            Assert.False(link.IsUp);

            LapbTestKit.DeliverAt(link, 200, FromDceResponse, 0x73);   // the UA to ours
            Assert.True(link.IsUp);
        }

        [Fact]
        public void CrossingSabms_WithNoUa_T1SetsTheLinkUp_AndDoesNotSendTheSabmAgain()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent, ItuDte());

            link.Connect(0);
            LapbTestKit.DeliverAt(link, 100, FromDceCommand, 0x3F);
            sent.Clear();

            // 2.4.5.11.1: "or after timeout of T1" - enter information transfer, no new SABM.
            link.Tick(3500);

            Assert.True(link.IsUp);
            Assert.Equal(0, CountSabms(sent));
        }
    }
}
