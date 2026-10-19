//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Link;
using RetroIO.Link.Lapb;
using RetroIO.Link.Models;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// <see cref="LapbStation"/>: the link layer's event surface running on the shared engine. These
    /// are the behaviours <c>LapbInstancePinTests</c> pinned as wrong on the implementation
    /// this replaces, plus the surface the four links depend on.
    /// </summary>
    [TestFixture]
    public class LapbStationTests
    {
        private const byte AddressA = 0x03;   // X.25 2.4.2: DCE commands, DTE responses
        private const byte AddressB = 0x01;   // DTE commands, DCE responses

        private readonly object _lock = new object();
        private List<byte[]> _sent = null!;

        [SetUp]
        public void SetUp()
        {
            _sent = new List<byte[]>();
        }

        private LapbStation NewStation(int t1Ms = 60_000, int t2Ms = 0)
        {
            LapbStation station = new LapbStation(NullLogger.Instance, "test", windowSize: 7, maxRetries: 10, t1TimeoutMs: t1Ms, t2TimeoutMs: t2Ms);
            station.FrameToTransmit += (s, e) =>
            {
                lock (_lock)
                {
                    byte[] copy = new byte[e.Length];
                    Array.Copy(e.FrameData, copy, e.Length);
                    _sent.Add(copy);
                }
            };

            return station;
        }

        private List<byte[]> Sent()
        {
            lock (_lock)
            {
                return new List<byte[]>(_sent);
            }
        }

        private int CountControl(Func<byte, bool> match)
        {
            int n = 0;
            List<byte[]> sent = Sent();
            for (int i = 0; i < sent.Count; i++)
            {
                if (sent[i].Length >= 2 && match(sent[i][1]))
                {
                    n++;
                }
            }

            return n;
        }

        private static bool IsIFrame(byte c) => (c & 0x01) == 0;
        private static bool IsSabm(byte c) => (c & 0xEF) == 0x2F;
        private static bool IsRr(byte c) => (c & 0x0F) == 0x01;

        private static byte IControl(int ns, int nr) => (byte)((nr << 5) | (ns << 1));

        [Test]
        public void Connect_SendsSabmWithTheDteAddress_AndUaBringsItUp()
        {
            using LapbStation station = NewStation();
            List<LapbState> states = new List<LapbState>();
            station.StateChanged += (s, e) => states.Add(e.NewState);

            station.Connect();

            // X.25 2.4.2: our SABM is a command from the DTE, so address B; 2.3.4: no information.
            Assert.That(Sent().Count, Is.EqualTo(1));
            Assert.That(Sent()[0][0], Is.EqualTo(AddressB));
            Assert.That(IsSabm(Sent()[0][1]), Is.True);
            Assert.That(Sent()[0].Length, Is.EqualTo(2));
            Assert.That(station.State, Is.EqualTo(LapbState.Connecting));

            // The DCE's UA is a response, so address B as well.
            station.ReceiveFrame(new byte[] { AddressB, 0x73 }, 2);

            Assert.That(station.State, Is.EqualTo(LapbState.DataTransfer));
            Assert.That(states, Does.Contain(LapbState.DataTransfer));
        }

        [Test]
        public void DataFlowsBothWays_AndSequenceNumbersAdvance()
        {
            using LapbStation station = NewStation();
            station.Connect();
            station.ReceiveFrame(new byte[] { AddressB, 0x73 }, 2);
            lock (_lock) { _sent.Clear(); }

            byte[]? delivered = null;
            station.DataReceived += (s, d) => delivered = d;

            Assert.That(station.SendData(new byte[] { 1, 2, 3 }), Is.True);
            Assert.That(CountControl(IsIFrame), Is.EqualTo(1));
            Assert.That(station.VS, Is.EqualTo(1));

            station.ReceiveFrame(new byte[] { AddressA, IControl(ns: 0, nr: 1), 0xAA }, 3);

            Assert.That(delivered, Is.EqualTo(new byte[] { 0xAA }));
            Assert.That(station.VR, Is.EqualTo(1));
            Assert.That(station.VA, Is.EqualTo(1), "our I-frame was acknowledged by the peer's N(R)");
        }

        [Test]
        public void SendData_IsRefusedWhenNotConnected()
        {
            using LapbStation station = NewStation();
            Assert.That(station.SendData(new byte[] { 1 }), Is.False);
            Assert.That(Sent().Count, Is.EqualTo(0));
        }

        [Test]
        public void T1UnderPeerRnr_DoesNotRetransmitIntoTheBusyPeer()
        {
            // The defect LapbInstancePinTests pinned on the implementation this replaces: on T1 with
            // the peer in RNR it pushed the whole outstanding window again, into a station that had
            // just said it cannot receive. X.25 2.4.5.7 forbids it - "the DCE will not transmit or
            // retransmit any I frame" - and 2.4.5.9 allows one I frame with P=1 or a supervisory
            // poll instead.
            using LapbStation station = NewStation(t1Ms: 100);
            station.Connect();
            station.ReceiveFrame(new byte[] { AddressB, 0x73 }, 2);

            station.SendData(new byte[] { 1 });
            station.SendData(new byte[] { 2 });
            station.SendData(new byte[] { 3 });
            station.ReceiveFrame(new byte[] { AddressA, 0x05 }, 2);   // RNR, N(R)=0: nothing acknowledged
            lock (_lock) { _sent.Clear(); }

            Thread.Sleep(500);   // several T1 periods

            int iFrames = CountControl(IsIFrame);
            Assert.That(iFrames, Is.LessThanOrEqualTo(1),
                "at most one I frame with P=1 may be sent to probe a busy peer, never the whole window");
        }

        [Test]
        public void Reset_DropsTheLinkWithoutTellingThePeer()
        {
            using LapbStation station = NewStation();
            station.Connect();
            station.ReceiveFrame(new byte[] { AddressB, 0x73 }, 2);
            station.SendData(new byte[] { 1 });
            lock (_lock) { _sent.Clear(); }

            station.Reset();

            // The transport is gone, so a DISC would be a frame into a hole; and the next connection
            // must start at V(S)=0 rather than mid-sequence.
            Assert.That(station.State, Is.EqualTo(LapbState.Disconnected));
            Assert.That(Sent().Count, Is.EqualTo(0));
            Assert.That(station.VS, Is.EqualTo(0));
            Assert.That(station.VR, Is.EqualTo(0));
        }

        [Test]
        public void SendFrame_PutsABuiltFrameOnTheWireUntouched()
        {
            using LapbStation station = NewStation();
            byte[] raw = { 0x01, 0x2F, 0xDE, 0xAD };

            station.SendFrame(raw, raw.Length);

            Assert.That(Sent().Count, Is.EqualTo(1));
            Assert.That(Sent()[0], Is.EqualTo(raw));
        }

        [Test]
        public void ReceiveFrame_RaisesFrameReceivedWithAddressAndControl()
        {
            using LapbStation station = NewStation();
            LapbFrameEventArgs? seen = null;
            station.FrameReceived += (s, e) => seen = e;

            station.ReceiveFrame(new byte[] { AddressA, 0x3F }, 2);   // an incoming SABM

            Assert.That(seen, Is.Not.Null);
            Assert.That(seen!.Address, Is.EqualTo(AddressA));
            Assert.That(seen.Control, Is.EqualTo(0x3F));
            Assert.That(seen.Direction, Is.EqualTo(FrameDirection.Receive));
        }

        [Test]
        public void AnIncomingSabm_IsAnsweredAndBringsTheLinkUp()
        {
            using LapbStation station = NewStation();

            station.ReceiveFrame(new byte[] { AddressA, 0x3F }, 2);

            Assert.That(station.State, Is.EqualTo(LapbState.DataTransfer));
            // Our UA is a response from the DTE: address A (2.4.2), no information field (2.3.4).
            Assert.That(Sent()[0][0], Is.EqualTo(AddressA));
            Assert.That(Sent()[0][1] & 0xEF, Is.EqualTo(0x63));
            Assert.That(Sent()[0].Length, Is.EqualTo(2));
        }

        [Test]
        public void Statistics_CountFramesInBothDirections()
        {
            using LapbStation station = NewStation();
            station.Connect();
            station.ReceiveFrame(new byte[] { AddressB, 0x73 }, 2);
            station.SendData(new byte[] { 1 });
            station.ReceiveFrame(new byte[] { AddressA, IControl(ns: 0, nr: 1), 0xAA }, 3);

            LapbStatistics stats = station.GetStatistics();
            Assert.That(stats.UFramesSent, Is.GreaterThanOrEqualTo(1), "the SABM");
            Assert.That(stats.UFramesReceived, Is.GreaterThanOrEqualTo(1), "the UA");
            Assert.That(stats.IFramesSent, Is.EqualTo(1));
            Assert.That(stats.IFramesReceived, Is.EqualTo(1));
            Assert.That(stats.VS, Is.EqualTo(station.VS));
        }

        [Test]
        public void Statistics_StartAtZero_AndCountNothingOnAQuietLink()
        {
            // The five error counters count real events only: a link that connected and moved one
            // frame each way has had no REJ, no timeout and no retransmission.
            using LapbStation station = NewStation();
            station.Connect();
            station.ReceiveFrame(new byte[] { AddressB, 0x73 }, 2);
            station.SendData(new byte[] { 1 });
            station.ReceiveFrame(new byte[] { AddressA, IControl(ns: 0, nr: 1), 0xAA }, 3);

            LapbStatistics stats = station.GetStatistics();
            Assert.That(stats.Retransmissions, Is.EqualTo(0));
            Assert.That(stats.RejectionsSent, Is.EqualTo(0));
            Assert.That(stats.RejectionsReceived, Is.EqualTo(0));
            Assert.That(stats.T1Timeouts, Is.EqualTo(0));
        }

        [Test]
        public void Statistics_ARejFromThePeer_CountsOneRejectionAndEachResentIFrame()
        {
            using LapbStation station = NewStation();
            station.Connect();
            station.ReceiveFrame(new byte[] { AddressB, 0x73 }, 2);
            station.SendData(new byte[] { 1 });
            station.SendData(new byte[] { 2 });
            station.SendData(new byte[] { 3 });
            lock (_lock) { _sent.Clear(); }

            // REJ with N(R)=1: the peer got frame 0 and wants 1 and 2 again (go-back-N).
            station.ReceiveFrame(new byte[] { AddressA, (byte)(0x09 | (1 << 5)) }, 2);

            LapbStatistics stats = station.GetStatistics();
            Assert.That(CountControl(IsIFrame), Is.EqualTo(2), "frames 1 and 2 went out again");
            Assert.That(stats.RejectionsReceived, Is.EqualTo(1));
            Assert.That(stats.Retransmissions, Is.EqualTo(2));
            Assert.That(stats.RejectionsSent, Is.EqualTo(0));
        }

        [Test]
        public void Statistics_AGapInTheReceivedFrames_CountsOneRejectionSentNotOnePerFrame()
        {
            using LapbStation station = NewStation();
            station.Connect();
            station.ReceiveFrame(new byte[] { AddressB, 0x73 }, 2);

            // N(S)=1 arrives while V(R)=0: frame 0 was lost. One REJ goes out (X.25 2.4.6.1); the
            // second out-of-sequence frame must not draw another one.
            station.ReceiveFrame(new byte[] { AddressA, IControl(ns: 1, nr: 0), 0xAA }, 3);
            station.ReceiveFrame(new byte[] { AddressA, IControl(ns: 2, nr: 0), 0xBB }, 3);

            LapbStatistics stats = station.GetStatistics();
            Assert.That(stats.RejectionsSent, Is.EqualTo(1));
            Assert.That(stats.RejectionsReceived, Is.EqualTo(0));
            Assert.That(stats.Retransmissions, Is.EqualTo(0));
        }

        [Test]
        public void Statistics_AnUnansweredIFrame_CountsT1Timeouts()
        {
            using LapbStation station = NewStation(t1Ms: 100);
            station.Connect();
            station.ReceiveFrame(new byte[] { AddressB, 0x73 }, 2);
            station.SendData(new byte[] { 1 });

            // Poll with a deadline, not a fixed sleep: T1 fires on the station's own timer.
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (station.GetStatistics().T1Timeouts < 2 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }

            Assert.That(station.GetStatistics().T1Timeouts, Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public void T2_HoldsTheAcknowledgement_ThenSendsItAndCountsIt()
        {
            // X.25 2.4.9.2 and 2.4.6.2 a) ii): with P=0 the acknowledgement of a received I frame may be
            // held up to T2. The station used to ignore its T2 argument and acknowledge at once, so
            // T2Timeouts could never move.
            using LapbStation station = NewStation(t1Ms: 60_000, t2Ms: 300);
            station.Connect();
            station.ReceiveFrame(new byte[] { AddressB, 0x73 }, 2);
            lock (_lock) { _sent.Clear(); }

            station.ReceiveFrame(new byte[] { AddressA, IControl(ns: 0, nr: 0), 0xAA }, 3);

            Assert.That(CountControl(IsRr), Is.EqualTo(0), "the acknowledgement is held, not sent at once");
            Assert.That(station.GetStatistics().T2Timeouts, Is.EqualTo(0));

            // Poll with a deadline, not a fixed sleep: T2 fires on the station's own timer.
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (CountControl(IsRr) == 0 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }

            Assert.That(CountControl(IsRr), Is.EqualTo(1), "one RR when T2 runs out");
            Assert.That(station.GetStatistics().T2Timeouts, Is.EqualTo(1));
        }

        /// <summary>
        /// A held acknowledgement rides on the next outgoing I frame: the frame carries N(R)=1, no RR
        /// is sent for the received frame, and the station counts no T2 expiry. T2 is long here so it
        /// cannot run out during the test.
        /// </summary>
        [Test]
        public void T2_HeldAcknowledgement_RidesOnTheNextOutgoingIFrame()
        {
            using LapbStation station = NewStation(t1Ms: 60_000, t2Ms: 30_000);
            station.Connect();
            station.ReceiveFrame(new byte[] { AddressB, 0x73 }, 2);
            lock (_lock) { _sent.Clear(); }

            station.ReceiveFrame(new byte[] { AddressA, IControl(ns: 0, nr: 0), 0xAA }, 3);
            Assert.That(CountControl(IsRr), Is.EqualTo(0), "the acknowledgement is held");

            Assert.That(station.SendData(new byte[] { 1, 2, 3 }), Is.True);

            List<byte[]> sent = Sent();
            Assert.That(sent.Count, Is.EqualTo(1), "only the I frame went out");
            Assert.That(sent[0][1], Is.EqualTo(IControl(ns: 0, nr: 1)), "the I frame acknowledges the received frame through N(R)");
            Assert.That(CountControl(IsRr), Is.EqualTo(0));
            Assert.That(station.GetStatistics().T2Timeouts, Is.EqualTo(0));
        }

        [Test]
        public void T2Zero_AcknowledgesAtOnce_AndNeverCounts()
        {
            using LapbStation station = NewStation(t1Ms: 60_000, t2Ms: 0);
            station.Connect();
            station.ReceiveFrame(new byte[] { AddressB, 0x73 }, 2);
            lock (_lock) { _sent.Clear(); }

            station.ReceiveFrame(new byte[] { AddressA, IControl(ns: 0, nr: 0), 0xAA }, 3);

            Assert.That(CountControl(IsRr), Is.EqualTo(1));
            Assert.That(station.GetStatistics().T2Timeouts, Is.EqualTo(0));
        }

        [Test]
        public void T2NotLessThanT1_IsRefused()
        {
            // X.25 2.4.9.2: T2 is less than T1. The engine refuses it and the station passes that on.
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new LapbStation(NullLogger.Instance, "test", t1TimeoutMs: 1000, t2TimeoutMs: 1000));
        }

        [Test]
        public void AnUndecodableFrame_IsDiscardedWithoutThrowing()
        {
            using LapbStation station = NewStation();
            Assert.DoesNotThrow(() => station.ReceiveFrame(new byte[] { 0x01 }, 1));
            Assert.That(Sent().Count, Is.EqualTo(0));
        }
    }
}
