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
    /// The receive window a SINTRAN machine announces in the LAPB address of every I, RR and REJ
    /// frame: address = window * 2 + 1 (plus 0x80 on an odd-length I-frame), read from the XMSG
    /// kernel routine XHSTR at 143630 octal (NDInsight
    /// SINTRAN/XMSG/DOC/HDLC-ADDRESS-BYTE-ANALYSIS-2026-11-09.md). We must never have more
    /// unacknowledged I-frames than min(own k, that window).
    /// </summary>
    public sealed class LapbPeerWindowTests
    {
        /// <summary>
        /// Queues <paramref name="count"/> one-byte payloads on the link.
        /// </summary>
        /// <param name="link">
        /// The link under test.
        /// </param>
        /// <param name="count">
        /// How many payloads to queue.
        /// </param>
        private static void Queue(LapbLayer link, int count)
        {
            for (int i = 0; i < count; i++)
            {
                link.SendInformation(new byte[] { (byte)(0xA0 + i) }, currentTicks: 0);
            }
        }

        /// <summary>
        /// Before any I or S frame is heard only our own k limits sending: all 7 go out.
        /// </summary>
        [Fact]
        public void NoWindowHeard_OnlyOwnWindowLimits()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null);
            sent.Clear();

            Queue(link, 8);

            Assert.Equal(LapbLayer.MaxPeerWindow, link.PeerWindow);
            Assert.Equal(7, sent.Count);
            Assert.Equal(7, link.Outstanding);
        }

        /// <summary>
        /// RR with address 0x07 announces window 3: only 3 frames may be outstanding.
        /// </summary>
        [Fact]
        public void Window3_LimitsToThree()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null);
            Deliver(link, 0x07, 0x01, Node100Hi, Node100Lo);   // RR N(R)=0, window 3
            sent.Clear();

            Queue(link, 5);

            Assert.Equal(3, link.PeerWindow);
            Assert.Equal(3, sent.Count);
            Assert.Equal(3, link.Outstanding);
            Assert.True(link.WindowWaits > 0);
        }

        /// <summary>
        /// Address 0x01 on an RR is window 0: nothing is sent even with nothing outstanding, and the
        /// next RR 0x09 (window 4) reopens it without any poll from us.
        /// </summary>
        [Fact]
        public void Window0_BlocksWithNothingOutstanding_Then0x09ReopensToFour()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null);
            Deliver(link, 0x01, 0x01, Node100Hi, Node100Lo);   // RR N(R)=0, window 0
            sent.Clear();

            Queue(link, 6);

            Assert.Equal(0, link.PeerWindow);
            Assert.Empty(sent);
            Assert.Equal(0, link.Outstanding);
            Assert.Equal(6, link.WindowWaits);   // each of the 6 sends stopped on the peer window

            Deliver(link, 0x09, 0x01, Node100Hi, Node100Lo);   // RR N(R)=0, window 4

            Assert.Equal(4, link.PeerWindow);
            Assert.Equal(4, sent.Count);
            Assert.Equal(4, link.Outstanding);
        }

        /// <summary>
        /// An odd-length I-frame with address 0x87 announces window 3 (0x80 is the odd-length bit).
        /// </summary>
        [Fact]
        public void OddIFrame0x87_GivesWindowThree()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null);
            Deliver(link, 0x87, 0x00, 0x55);   // I N(S)=0 N(R)=0, 1 byte, window 3

            Assert.Equal(3, link.PeerWindow);

            sent.Clear();
            Queue(link, 5);

            Assert.Equal(3, sent.Count);
            Assert.Equal(3, link.Outstanding);
        }

        /// <summary>
        /// Our own k smaller than the peer's window wins: k = 2 against window 4.
        /// </summary>
        [Fact]
        public void OwnWindowSmallerThanPeer_Wins()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null, new LapbOptions(windowSize: 2));
            Deliver(link, 0x09, 0x01, Node100Hi, Node100Lo);   // RR window 4
            sent.Clear();

            Queue(link, 4);

            Assert.Equal(4, link.PeerWindow);
            Assert.Equal(2, sent.Count);
            Assert.Equal(0, link.WindowWaits);   // our own k stopped it, not the peer
        }

        /// <summary>
        /// An address without the SINTRAN shape (0x11) is still taken - the RR's N(R) applies - but
        /// does not change the window.
        /// </summary>
        [Fact]
        public void UnknownShapeAddress_AcceptedAndWindowUnchanged()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null);
            Deliver(link, 0x05, 0x01, Node100Hi, Node100Lo);   // RR window 2
            sent.Clear();

            Queue(link, 3);
            Assert.Equal(2, sent.Count);

            Deliver(link, 0x11, 0x41, Node100Hi, Node100Lo);   // RR N(R)=2, odd shape

            Assert.Equal(2, link.PeerWindow);
            Assert.Equal(2, link.AcknowledgeVariable);   // the frame was accepted
            Assert.Equal(3, sent.Count);                 // one slot of the 2 is now used by the third frame
            Assert.Equal(1, link.Outstanding);
        }

        /// <summary>
        /// A link reset forgets the window: after the peer's SABM only our own k limits again.
        /// </summary>
        [Fact]
        public void LinkReset_ForgetsTheWindow()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = NewConnected(102, sent, null);
            Deliver(link, 0x03, 0x01, Node100Hi, Node100Lo);   // RR window 1
            Assert.Equal(1, link.PeerWindow);

            Deliver(link, 0x01, 0x3F, Node100Hi, Node100Lo);   // peer SABM mid-session: reset

            Assert.Equal(LapbLayer.MaxPeerWindow, link.PeerWindow);
        }
    }
}
