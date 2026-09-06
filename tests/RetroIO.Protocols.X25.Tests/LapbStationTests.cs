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
    /// are the behaviours <see cref="LapbInstancePinTests"/> pinned as wrong on the implementation
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

        private LapbStation NewStation(int t1Ms = 60_000)
        {
            LapbStation station = new LapbStation(NullLogger.Instance, "test", windowSize: 7, maxRetries: 10, t1TimeoutMs: t1Ms);
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
        public void AnUndecodableFrame_IsDiscardedWithoutThrowing()
        {
            using LapbStation station = NewStation();
            Assert.DoesNotThrow(() => station.ReceiveFrame(new byte[] { 0x01 }, 1));
            Assert.That(Sent().Count, Is.EqualTo(0));
        }
    }
}
