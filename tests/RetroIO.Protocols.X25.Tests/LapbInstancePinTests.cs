using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Link;
using RetroIO.Link.Lapb;
using RetroIO.Link.Models;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Pins for <see cref="LapbInstance"/>, the LAPB behind <c>CodecLink</c>, <c>SerialLink</c>,
    /// <c>TcpLapbLink</c> and <c>XotLink</c>. The one-stack plan (Phase D) replaces it with the
    /// <c>Xmsg.Live</c> <c>LapbLayer</c> plus an ITU variant flag; these tests record what
    /// <c>LapbInstance</c> does today so that the replacement is measured against something. Where
    /// today's behaviour breaks ITU-T X.25 clause 2 (LAPB), the name says <c>_WHICH_IS_WRONG</c> and
    /// the comment quotes the clause.
    /// </summary>
    /// <remarks>
    /// <see cref="LapbInstance"/> runs its T1 on a <see cref="System.Threading.Timer"/>, so the
    /// retransmission tests wait on real time (short T1) and collect transmitted frames under a lock.
    /// Frames are raw LAPB bodies: address, control, information - no flags, no FCS.
    /// </remarks>
    [TestFixture]
    public class LapbInstancePinTests
    {
        private const byte Address = 0x01;
        private const byte Ua = 0x73;           // UA with F=1

        private readonly object _lock = new object();
        private readonly List<byte[]> _sent = new List<byte[]>();

        [SetUp]
        public void SetUp()
        {
            lock (_lock)
            {
                _sent.Clear();
            }
        }

        private LapbInstance NewConnected(int t1Ms)
        {
            LapbInstance lapb = new LapbInstance(NullLogger.Instance, "pin", windowSize: 7, maxRetries: 10,
                t1TimeoutMs: t1Ms, t2TimeoutMs: 0, t3TimeoutMs: 0, isPrimary: true);
            lapb.FrameToTransmit += (s, e) =>
            {
                lock (_lock)
                {
                    byte[] copy = new byte[e.Length];
                    Array.Copy(e.FrameData, copy, e.Length);
                    _sent.Add(copy);
                }
            };

            lapb.Connect();                                   // SABM out
            lapb.ReceiveFrame(new byte[] { Address, Ua }, 2); // UA in
            Assert.That(lapb.State, Is.EqualTo(LapbState.DataTransfer));
            lock (_lock)
            {
                _sent.Clear();
            }

            return lapb;
        }

        private int Count(Func<byte, bool> controlMatches)
        {
            int n = 0;
            lock (_lock)
            {
                for (int i = 0; i < _sent.Count; i++)
                {
                    if (_sent[i].Length >= 2 && controlMatches(_sent[i][1]))
                    {
                        n++;
                    }
                }
            }

            return n;
        }

        private static bool IsIFrame(byte c) => (c & 0x01) == 0;
        private static bool IsIFrameWithP(byte c) => (c & 0x11) == 0x10;
        private static bool IsRej(byte c) => (c & 0x0F) == 0x09;
        private static bool IsSupervisoryWithP(byte c) => (c & 0x13) == 0x11;

        private static byte IControl(int ns, int nr, bool p = false) =>
            (byte)((nr << 5) | (p ? 0x10 : 0) | (ns << 1));

        private static byte RnrControl(int nr) => (byte)((nr << 5) | 0x05);

        // ================================================================== peer busy
        //
        // X.25 2.4.5.7 "Receiving an RNR frame": after an RNR that leaves frames outstanding the
        // station "will not transmit or retransmit any I frame ... In any case, the DCE will not
        // transmit any other I frames before receiving an RR or REJ frame"; on T1 it may enquire
        // with a supervisory command with P=1, or (2.4.5.9) retransmit ONE I frame with P=1.

        [Test]
        public void T1UnderPeerRnr_RetransmitsTheWholeWindowWithoutAPoll_WHICH_IS_WRONG()
        {
            using LapbInstance lapb = NewConnected(t1Ms: 100);

            Assert.That(lapb.SendData(new byte[] { 1 }), Is.True);   // N(S)=0
            Assert.That(lapb.SendData(new byte[] { 2 }), Is.True);   // N(S)=1
            Assert.That(lapb.SendData(new byte[] { 3 }), Is.True);   // N(S)=2
            lapb.ReceiveFrame(new byte[] { Address, RnrControl(0) }, 2);   // peer busy, nothing acked
            lock (_lock)
            {
                _sent.Clear();
            }

            Thread.Sleep(450);   // several T1 periods

            int iFrames = Count(IsIFrame);
            int iFramesWithP = Count(IsIFrameWithP);
            int enquiries = Count(IsSupervisoryWithP);

            // MEASURED 2026-09-06: OnT1Elapsed in DataTransfer calls RetransmitFromNR(_va), which
            // pushes every outstanding I frame again, none with the P bit, and never sends an RR
            // P=1 enquiry. Into a peer that has just said it cannot receive. The C emulator has the
            // same defect (audit F-04-02) and it exhausts N2 in 30 s.
            Assert.That(iFrames, Is.GreaterThanOrEqualTo(3),
                "behaviour changed: not retransmitting I frames into an RNR is the FIX (plan D3) - rewrite this test");
            Assert.That(iFramesWithP, Is.EqualTo(0), "no retransmission carries P=1");
            Assert.That(enquiries, Is.EqualTo(0), "and no RR/RNR P=1 enquiry is made");
        }

        // ================================================================== REJ condition
        //
        // X.25 2.3.4.9 / 2.4.5.x: "Only one REJ exception condition for a given direction of
        // information transfer may be established at any time. The REJ exception condition is
        // cleared (reset) upon the receipt of an I frame with an N(S) equal to the N(R) of the REJ
        // frame."

        [Test]
        public void OutOfSequenceRun_DrawsExactlyOneRej()
        {
            using LapbInstance lapb = NewConnected(t1Ms: 60_000);

            // Expected N(S) is 0; frame 0 was lost and 1, 2, 3 arrive.
            lapb.ReceiveFrame(new byte[] { Address, IControl(ns: 1, nr: 0), 0xAA }, 3);
            lapb.ReceiveFrame(new byte[] { Address, IControl(ns: 2, nr: 0), 0xBB }, 3);
            lapb.ReceiveFrame(new byte[] { Address, IControl(ns: 3, nr: 0), 0xCC }, 3);

            // MEASURED 2026-09-06: ONE REJ. LapbInstance keeps a reject condition
            // (_rejOutstanding) and clears it when N(S) == V(R) arrives, exactly as the clause
            // says. Recorded here because the one-stack plan first claimed the opposite from a grep
            // hit on the REJ send site - reading the function, not the grep, is what settled it.
            // The C emulator DOES have the defect (audit F-04-07); this engine does not.
            Assert.That(Count(IsRej), Is.EqualTo(1));
            Assert.That(lapb.VR, Is.EqualTo(0), "nothing was accepted");

            // The retransmission arrives: the condition clears and a fresh gap draws a fresh REJ.
            lapb.ReceiveFrame(new byte[] { Address, IControl(ns: 0, nr: 0), 0x00 }, 3);
            Assert.That(lapb.VR, Is.EqualTo(1));
            lapb.ReceiveFrame(new byte[] { Address, IControl(ns: 3, nr: 0), 0xCC }, 3);
            Assert.That(Count(IsRej), Is.EqualTo(2));
        }

        [Test]
        public void InSequenceFrames_AreAcceptedAndVRAdvances()
        {
            using LapbInstance lapb = NewConnected(t1Ms: 60_000);
            int delivered = 0;
            lapb.DataReceived += (s, d) => delivered++;

            lapb.ReceiveFrame(new byte[] { Address, IControl(ns: 0, nr: 0), 0xAA }, 3);
            lapb.ReceiveFrame(new byte[] { Address, IControl(ns: 1, nr: 0), 0xBB }, 3);

            Assert.That(delivered, Is.EqualTo(2));
            Assert.That(lapb.VR, Is.EqualTo(2));
            Assert.That(Count(IsRej), Is.EqualTo(0));
        }

        // ================================================================== threading model
        //
        // The audit's Critical F-09-02 in the C switch is "the X.25 state machine runs on two
        // threads with no lock". LapbInstance has the same shape one layer down: T1 fires on a
        // thread-pool thread (System.Threading.Timer) and RetransmitFromNR rewrites _vs while the
        // owning link may be inside ProcessFrame on its receive thread. There is a _timerLock
        // around starting and stopping timers, and nothing around the state. A race cannot be
        // pinned deterministically, so this pins the STRUCTURE the plan (D3) removes: pool-thread
        // timers instead of an injected clock.

        [Test]
        public void T1IsAThreadPoolTimer_NotAnInjectedClock_WHICH_IS_WRONG()
        {
            FieldInfo? t1 = typeof(LapbInstance).GetField("_t1Timer", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(t1, Is.Not.Null, "behaviour changed: the _t1Timer field is gone - if LapbInstance now ticks from an injected clock that is the FIX - rewrite this test");
            Assert.That(t1!.FieldType, Is.EqualTo(typeof(Timer)));

            // And the retransmission path it drives rewrites the send state variable.
            MethodInfo? retransmit = typeof(LapbInstance).GetMethod("RetransmitFromNR", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(retransmit, Is.Not.Null);
        }
    }
}
