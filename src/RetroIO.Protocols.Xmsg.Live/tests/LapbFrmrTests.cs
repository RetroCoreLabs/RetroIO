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
    /// FRMR error-path proof for the LAPB state machine (spec 2.3.3 / 4.3): a bad N(R) yields FRMR
    /// reason Z, an over-long I-field yields reason Y, and an unimplemented control yields reason W.
    /// The full 3-byte diagnostic (rejected control, the V(S)/V(R) byte, and the reason nibble) is
    /// asserted, along with the FRMR_SENT recovery behaviour.
    /// </summary>
    public sealed class LapbFrmrTests
    {
        /// <summary>
        /// An N(R) acknowledging a frame we never sent draws FRMR reason Z and enters FRMR_SENT (4.3).
        /// </summary>
        [Fact]
        public void BadReceiveSequence_SendsFrmrReasonZ_EntersFrmrSent()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null);   // V(S)=V(A)=V(R)=0, nothing outstanding
            sent.Clear();

            // RR N(R)=1 while nothing is outstanding: (1 - V(A)) mod 8 = 1 > 0 -> invalid.
            Deliver(link, 0x09, 0x21, Node100Hi, Node100Lo);

            Assert.Equal(LapbLayerState.FrmrSent, link.State);
            Assert.Single(sent);
            // FRMR: addr 0x01, ctrl 0x87 (F=0), rejected ctrl 0x21, seq byte 0x00 (V(S)=V(R)=0), reason Z.
            Assert.Equal(new byte[] { 0x01, 0x87, 0x21, 0x00, 0x08 }, sent[0]);
        }

        /// <summary>
        /// An I-field longer than the maximum draws FRMR reason Y and is not delivered (spec 2.3.2).
        /// </summary>
        [Fact]
        public void OverlongInformation_SendsFrmrReasonY_NotDelivered()
        {
            List<byte[]> sent = new List<byte[]>();
            List<byte[]> got = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, got);
            sent.Clear();

            byte[] tooLong = new byte[LapbLayer.MaxInformationLength + 1];
            Deliver(link, 0x09, 0x00, tooLong);   // I N(S)=0 N(R)=0 with an over-long info field

            Assert.Empty(got);                     // payload rejected, not delivered
            Assert.Equal(0, link.ReceiveVariable); // V(R) not advanced
            Assert.Equal(LapbLayerState.FrmrSent, link.State);
            Assert.Single(sent);
            // FRMR: rejected ctrl 0x00 (the I-frame control), seq byte 0x00, reason Y = 0x04.
            Assert.Equal(new byte[] { 0x01, 0x87, 0x00, 0x00, 0x04 }, sent[0]);
        }

        /// <summary>
        /// An unimplemented supervisory subtype draws FRMR reason W (spec 2.3.3).
        /// </summary>
        [Fact]
        public void UnknownSupervisoryControl_SendsFrmrReasonW()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null);
            sent.Clear();

            // S-frame (bits 1..0 = 01) with an undefined low nibble 0xD; N(R)=0 is valid.
            Deliver(link, 0x09, 0x0D, Node100Hi, Node100Lo);

            Assert.Equal(LapbLayerState.FrmrSent, link.State);
            Assert.Single(sent);
            // FRMR: rejected ctrl 0x0D, seq byte 0x00, reason W = 0x01.
            Assert.Equal(new byte[] { 0x01, 0x87, 0x0D, 0x00, 0x01 }, sent[0]);
        }

        /// <summary>
        /// The FRMR diagnostic's second byte encodes the current V(S) and V(R) (spec 2.3.3): V(S) in
        /// bits 1..3, V(R) in bits 5..7, bit 4 = 0.
        /// </summary>
        [Fact]
        public void FrmrDiagnostic_EncodesSendAndReceiveVariables()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null);
            Deliver(link, 0x09, 0x00, new byte[] { 0x21, 0x13 });   // deliver N(S)=0 -> V(R)=1
            link.SendInformation(new byte[] { 0x55 }, currentTicks: 0); // send -> V(S)=1 (outstanding 1)
            sent.Clear();

            // RR N(R)=5 while only one frame is outstanding: (5 - 0) mod 8 = 5 > 1 -> invalid.
            Deliver(link, 0x09, 0xA1, Node100Hi, Node100Lo);

            Assert.Equal(LapbLayerState.FrmrSent, link.State);
            Assert.Single(sent);
            // seq byte = (V(S)=1 << 1) | (V(R)=1 << 5) = 0x02 | 0x20 = 0x22.
            Assert.Equal(new byte[] { 0x01, 0x87, 0xA1, 0x22, 0x08 }, sent[0]);
        }

        /// <summary>
        /// While in FRMR_SENT, a further I/S frame causes the same FRMR to be resent with F=1 (spec 6.3).
        /// </summary>
        [Fact]
        public void FrmrSent_ResendsFrmrWithFinal_OnFurtherFrame()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null);
            Deliver(link, 0x09, 0x21, Node100Hi, Node100Lo);   // bad N(R) -> FRMR_SENT
            Assert.Equal(LapbLayerState.FrmrSent, link.State);
            sent.Clear();

            Deliver(link, 0x09, 0x01, Node100Hi, Node100Lo);   // any further S-frame

            Assert.Single(sent);
            // Same diagnostic, now with the final bit: 0x87 | 0x10 = 0x97.
            Assert.Equal(new byte[] { 0x01, 0x97, 0x21, 0x00, 0x08 }, sent[0]);
            Assert.Equal(LapbLayerState.FrmrSent, link.State);
        }

        /// <summary>
        /// Receiving a FRMR while connected re-establishes the link with a fresh SABM (spec 6.3).
        /// </summary>
        [Fact]
        public void RxFrmr_WhenConnected_ReEstablishesWithSabm()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null);
            sent.Clear();

            Deliver(link, 0x01, 0x87, 0x00, 0x00, 0x08);   // peer FRMR

            Assert.Equal(LapbLayerState.SabmSent, link.State);
            Assert.Single(sent);
            Assert.Equal(new byte[] { 0x01, 0x3F, Node102Hi, Node102Lo }, sent[0]);   // our fresh SABM P=1
        }
    }
}
