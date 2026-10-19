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
    /// Proof for the T2 acknowledgement delay timer (ITU-T X.25 (10/96) 2.4.9.2 and 2.4.6.2 a) ii)).
    /// All time is injected through the tick values; nothing sleeps.
    /// </summary>
    public sealed class LapbT2Tests
    {
        // T1 = 1000, T2 = 200: T2 is well inside T1, as 2.4.9.2 requires (T2 less than T1).
        private static LapbOptions WithT2()
        {
            return new LapbOptions(t1: 1000, t3: 100000, n2: 3, t2: 200);
        }

        // RR response with N(R)=1 as this layer builds it (ND dialect): address, control, node number.
        private static readonly byte[] RrNr1 = new byte[] { 0x09, 0x21, Node102Hi, Node102Lo };

        /// <summary>
        /// With T2 above zero an in-sequence I frame is NOT acknowledged at once; when T2 runs out
        /// the layer sends the RR itself and counts one T2 expiry.
        /// </summary>
        [Fact]
        public void T2_Expiry_SendsTheAcknowledgementAndCountsIt()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null, WithT2());
            sent.Clear();

            DeliverAt(link, 1000, 0x09, 0x00, 0x21, 0x13, 0xAA);   // I N(S)=0, P=0
            Assert.Empty(sent);                                     // held, not sent
            Assert.Equal(1, link.ReceiveVariable);

            Assert.False(link.Tick(1199));                          // one tick before T2
            Assert.Empty(sent);
            Assert.Equal(0, link.T2Timeouts);

            Assert.True(link.Tick(1200));                           // T2 runs out
            Assert.Single(sent);
            Assert.Equal(RrNr1, sent[0]);
            Assert.Equal(1, link.T2Timeouts);

            Assert.False(link.Tick(5000));                          // no second RR for the same frame
            Assert.Single(sent);
            Assert.Equal(1, link.T2Timeouts);
        }

        /// <summary>
        /// An I frame that goes out before T2 runs out carries the acknowledgement in its N(R)
        /// (2.4.6.2 a) ii)); T2 is cancelled, no RR is sent and nothing is counted.
        /// </summary>
        [Fact]
        public void OutgoingIFrame_CarriesTheAcknowledgement_AndCancelsT2()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null, WithT2());
            sent.Clear();

            DeliverAt(link, 1000, 0x09, 0x00, 0x21, 0x13, 0xAA);   // I N(S)=0: ack is held
            Assert.Empty(sent);

            link.SendInformation(new byte[] { 0x55, 0x66 }, currentTicks: 1100);   // even length: address stays 0x09
            Assert.Single(sent);
            Assert.Equal(0x09, sent[0][0]);
            Assert.Equal(0x20, sent[0][1]);   // I frame, N(S)=0, N(R)=1: the acknowledgement rides along

            sent.Clear();
            Assert.False(link.Tick(1200));    // T2 deadline passes: nothing to send
            Assert.Empty(sent);
            Assert.Equal(0, link.T2Timeouts);
        }

        /// <summary>
        /// With T2 = 0 (the default) the acknowledgement goes out at once, exactly as before, and
        /// T2 is never counted.
        /// </summary>
        [Fact]
        public void T2Zero_AcknowledgesAtOnce_AndNeverCounts()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null, new LapbOptions(t1: 1000, t3: 100000, n2: 3));
            sent.Clear();

            DeliverAt(link, 1000, 0x09, 0x00, 0x21, 0x13, 0xAA);
            Assert.Single(sent);
            Assert.Equal(RrNr1, sent[0]);

            sent.Clear();
            link.Tick(5000);
            Assert.Empty(sent);
            Assert.Equal(0, link.T2Timeouts);
        }

        /// <summary>
        /// An I frame with P=1 must be answered at once with F=1 (2.4.6.2 a) i)); T2 does not apply.
        /// </summary>
        [Fact]
        public void PollBitSet_IsAnsweredAtOnce_EvenWithT2()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null, WithT2());
            sent.Clear();

            DeliverAt(link, 1000, 0x09, 0x10, 0x21, 0x13, 0xAA);   // I N(S)=0, P=1
            Assert.Single(sent);
            Assert.Equal(0x31, sent[0][1]);                         // RR F=1 N(R)=1
            link.Tick(5000);
            Assert.Single(sent);
            Assert.Equal(0, link.T2Timeouts);
        }

        /// <summary>
        /// A second I frame arriving while T2 runs does not push the deadline out: one RR with the
        /// latest V(R) goes out when the first frame's T2 ends.
        /// </summary>
        [Fact]
        public void SecondIFrame_DoesNotRestartT2_AndOneRrCoversBoth()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null, WithT2());
            sent.Clear();

            DeliverAt(link, 1000, 0x09, 0x00, 0x21, 0x13, 0xAA);   // N(S)=0
            DeliverAt(link, 1150, 0x09, 0x02, 0x21, 0x13, 0xBB);   // N(S)=1
            Assert.Empty(sent);

            Assert.True(link.Tick(1200));
            Assert.Single(sent);
            Assert.Equal(0x41, sent[0][1]);                         // RR N(R)=2
            Assert.Equal(1, link.T2Timeouts);
        }

        /// <summary>
        /// T2 must be less than T1 (2.4.9.2); an equal or larger value is refused.
        /// </summary>
        [Theory]
        [InlineData(1000, 1000)]
        [InlineData(1000, 1500)]
        [InlineData(1000, -1)]
        public void T2NotLessThanT1_IsRefused(long t1, long t2)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LapbOptions(t1: t1, t2: t2));
        }

        /// <summary>
        /// A value just under T1 is accepted.
        /// </summary>
        [Fact]
        public void T2JustBelowT1_IsAccepted()
        {
            LapbOptions options = new LapbOptions(t1: 1000, t2: 999);
            Assert.Equal(999, options.T2);
        }

        /// <summary>
        /// The default is zero: acknowledge at once.
        /// </summary>
        [Fact]
        public void DefaultT2_IsZero()
        {
            Assert.Equal(0, new LapbOptions().T2);
            Assert.Equal(0, LapbOptions.Default.T2);
        }
    }
}
