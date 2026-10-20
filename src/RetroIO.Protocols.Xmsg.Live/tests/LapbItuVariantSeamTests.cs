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
    /// <see cref="LapbLayer"/> speaking each of its two dialects. The frame engine, the timers and
    /// the state table are shared; what differs is the address byte and what may ride in an
    /// information field.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ITU-T X.25 (10/96) clause 2.4.2: address A = 0x03 on commands from the DCE and responses from
    /// the DTE, address B = 0x01 on commands from the DTE and responses from the DCE. Clause 2.3.4:
    /// SABM, UA, DISC, DM and the supervisory frames have no information field.
    /// </para>
    /// <para>
    /// The ND dialect (see the remarks on <see cref="LapbLayer.AddressData"/>) uses 0x01 for link
    /// setup and 0x09/0x89 for data transfer, and carries a two-octet node number in SABM, UA, DISC,
    /// DM and the supervisory frames. That is what the ND-100 machines on the bench speak, and it is
    /// the default.
    /// </para>
    /// <para>
    /// Written 2026-09-06 pinning ND-only behaviour before the variant existed; rewritten the same
    /// day when Phase D1 added it.
    /// </para>
    /// </remarks>
    public class LapbItuVariantSeamTests
    {
        private static LapbOptions Itu(bool isDte = true) =>
            new LapbOptions(variant: LapbVariant.Itu, isDte: isDte);

        [Fact]
        public void ItuDte_AnswersASabmWithAUaCarryingAddressAAndNoInformation()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent, Itu());

            // An ITU DCE's SABM: address A (a command from the DCE), SABM with P=1, no information.
            LapbTestKit.Deliver(link, LapbLayer.AddressItuA, 0x3F);

            Assert.Equal(LapbLayerState.Connected, link.State);

            // The UA is a response from a DTE, so address A (2.4.2), and it carries nothing (2.3.4).
            // In the ND dialect this same exchange answers 01 73 00 66 and follows it with an RR
            // carrying the node number - an ITU peer would FRMR both.
            Assert.Equal(new byte[] { LapbLayer.AddressItuA, 0x73 }, sent[0]);
            for (int i = 0; i < sent.Count; i++)
            {
                Assert.True(sent[i].Length <= 2, "no ITU frame in this exchange may carry an information field");
            }
        }

        [Fact]
        public void ItuDce_AnswersASabmWithAUaCarryingAddressB()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent, Itu(isDte: false));

            // A DTE's SABM carries address B; the DCE's UA response carries B as well (2.4.2).
            LapbTestKit.Deliver(link, LapbLayer.AddressItuB, 0x3F);

            Assert.Equal(LapbLayerState.Connected, link.State);
            Assert.Equal(new byte[] { LapbLayer.AddressItuB, 0x73 }, sent[0]);
        }

        [Fact]
        public void ItuDte_CommandsCarryAddressB()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent, Itu());

            link.Connect(0);

            // Our own SABM is a command from the DTE: address B, no information field.
            Assert.Equal(new byte[] { LapbLayer.AddressItuB, 0x3F }, sent[0]);
        }

        [Fact]
        public void ItuInformationFrames_CarryAddressBAndNoOddLengthMarker()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent, Itu());
            LapbTestKit.Deliver(link, LapbLayer.AddressItuA, 0x3F);
            sent.Clear();

            link.SendInformation(new byte[] { 0xAA, 0xBB, 0xCC }, 0);   // odd length

            // An I frame is a command from the DTE: address B. The ND dialect would send 0x89 here,
            // because the ND-100's word DMA needs the odd-length marker; ITU has no such bit and a
            // peer would read 0x89 as an address it must discard (2.4.2 note).
            Assert.Equal(LapbLayer.AddressItuB, sent[0][0]);
            Assert.Equal(3 + 2, sent[0].Length);
        }

        [Fact]
        public void ItuStationThatSentTheSabm_IsUpOnTheUaAlone()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent, Itu());

            link.Connect(0);
            Assert.False(link.IsUp);

            // X.25 2.4.4.1: on the UA to its SABM the station considers the data link set up. The
            // DCE's UA is a response from the DCE: address B (2.4.2). There is no SABM from the peer.
            LapbTestKit.Deliver(link, LapbLayer.AddressItuB, 0x73);

            Assert.Equal(LapbLayerState.Connected, link.State);
            Assert.True(link.IsUp);
        }

        [Fact]
        public void NdStationThatSentTheSabm_IsStillNotUpOnTheUaAlone()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent);   // ND dialect

            link.Connect(0);
            LapbTestKit.Deliver(link, LapbLayer.AddressLinkSetup, 0x73, LapbTestKit.Node100Hi, LapbTestKit.Node100Lo);

            // ND spec 3.1: both directions must have completed a SABM -> UA exchange.
            Assert.False(link.IsUp);
        }

        [Fact]
        public void NdDialect_IsUnchangedAndStaysTheDefault()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent);   // no options: ND

            LapbTestKit.Deliver(link, LapbLayer.AddressLinkSetup, 0x3F, LapbTestKit.Node100Hi, LapbTestKit.Node100Lo);

            Assert.Equal(LapbLayerState.Connected, link.State);
            Assert.Equal(new byte[] { 0x01, 0x73, 0x00, 0x66 }, sent[0]);
            Assert.Equal(new byte[] { 0x09, 0x01, 0x00, 0x66 }, sent[1]);
        }

        [Fact]
        public void NdOddLengthMarker_IsStillSetOnOddInformationFrames()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent);
            LapbTestKit.Deliver(link, LapbLayer.AddressLinkSetup, 0x3F, LapbTestKit.Node100Hi, LapbTestKit.Node100Lo);
            sent.Clear();

            link.SendInformation(new byte[] { 0xAA, 0xBB, 0xCC }, 0);
            Assert.Equal((byte)(LapbLayer.AddressData | LapbLayer.AddressOddLengthBit), sent[0][0]);

            sent.Clear();
            link.SendInformation(new byte[] { 0xAA, 0xBB }, 0);
            Assert.Equal(LapbLayer.AddressData, sent[0][0]);
        }
    }
}
