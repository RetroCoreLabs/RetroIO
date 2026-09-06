using System;
using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Pins for the two switch behaviours the one-stack plan (Phase A5, Phase E2) will change:
    /// what <see cref="SwitchCore"/> does when the egress port refuses a packet, and what it does
    /// when one leg of a stitch is reset. Both are WRONG today and the test names say so; when
    /// Phase E lands these go red and are rewritten on purpose.
    /// </summary>
    /// <remarks>
    /// The silent-discard shape pinned here is the one that lost packets in the C switch five
    /// times over (x25switch.c, commit 9e26791 in x25emu): a send that can be refused and a caller
    /// that does not look at the answer.
    /// </remarks>
    [TestFixture]
    public class SwitchCorePinTests
    {
        /// <summary>
        /// An egress port whose <see cref="SendData"/> can be told to refuse - the shape of a real
        /// port whose window is full or whose peer has sent RNR.
        /// </summary>
        private sealed class RefusingPort : ISwitchPort
        {
            private bool _active;
            private ushort _nextLci;

            public bool RefuseSends;
            public int Refused;
            public readonly List<(ushort Lci, byte[] Data)> Sent = new List<(ushort, byte[])>();
            public readonly List<(string Called, string Calling)> PlacedCalls = new List<(string, string)>();
            public readonly List<(ushort Lci, byte Cause, byte Diag)> ClearedCalls = new List<(ushort, byte, byte)>();

            public RefusingPort(PortConfig config)
            {
                Config = config;
                Id = config.Id;
                _nextLci = config.LciBase;
            }

            public int Id { get; }
            public PortConfig Config { get; }
            public bool Active => _active;
            public bool PeerConnected => _active;
            public PortCounters Counters { get; } = new PortCounters();

            public event PortIncomingCall? IncomingCall;
            public event PortCallConnected? CallConnected;
            public event PortDataReceived? DataReceived;
            public event PortCallCleared? CallCleared;
            public event PortStateChanged? StateChanged;

            public bool Start()
            {
                _active = true;
                StateChanged?.Invoke(this, true);
                return true;
            }

            public void Stop()
            {
                _active = false;
                StateChanged?.Invoke(this, false);
            }

            public ushort PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData)
            {
                PlacedCalls.Add((calledAddress, callingAddress));
                return _nextLci++;
            }

            public void AcceptCall(ushort lci)
            {
            }

            public bool SendData(ushort lci, ReadOnlySpan<byte> data, bool qBit, bool mBit, bool dBit)
            {
                if (RefuseSends)
                {
                    Refused++;
                    return false;
                }

                Sent.Add((lci, data.ToArray()));
                return true;
            }

            public void ClearCall(ushort lci, byte cause, byte diagnostic)
            {
                ClearedCalls.Add((lci, cause, diagnostic));
            }

            /// <summary>Feeds data as if the far DTE on this port had sent it.</summary>
            public void InjectData(ushort lci, byte[] data)
            {
                DataReceived?.Invoke(this, lci, data, data.Length, false, false, false);
            }

            /// <summary>Keeps the compiler quiet about events this fake never raises.</summary>
            public void RaiseUnused()
            {
                IncomingCall?.Invoke(this, 0, "", "", Array.Empty<byte>());
                CallConnected?.Invoke(this, 0);
                CallCleared?.Invoke(this, 0, 0, 0);
            }
        }

        private SwitchNode _node = null!;
        private SwitchCore _core = null!;
        private LoopbackSwitchPort _ingress = null!;
        private RefusingPort _egress = null!;

        [SetUp]
        public void SetUp()
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340" };
            cfg.Routing.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = "2341", PrefixLen = 4, DestPort = 1 });

            _node = new SwitchNode(cfg);
            _ingress = new LoopbackSwitchPort(new PortConfig { Id = 0, LciBase = 1 });
            _egress = new RefusingPort(new PortConfig { Id = 1, LciBase = 1 });
            _node.AddPort(_ingress);
            _node.AddPort(_egress);
            _core = new SwitchCore(_node);
        }

        [Test]
        public void EgressRefusesThePacket_ItIsLostAndStillCountedAsForwarded_WHICH_IS_WRONG()
        {
            ushort inLci = _ingress.InjectIncomingCall("23410001", "23400001");
            Assert.That(_node.ActiveStitchCount, Is.EqualTo(1));
            VcStitch stitch = _node.SnapshotStitches()[0];

            _egress.RefuseSends = true;
            _ingress.InjectData(inLci, new byte[] { 1, 2, 3, 4 });

            // MEASURED 2026-09-06. SwitchCore.OnDataReceived calls ingress.OutPort.SendData(...)
            // as a statement; the bool comes back false and nobody looks. The packet is gone, the
            // egress counter says it went out, and the stitch's forward count went up. There is no
            // queue, no RNR toward the ingress DTE, and no clear. The far DTE will see a P(S) gap
            // on the next packet that does get through and reset the circuit (X.25 4.4.1.3).
            //
            // Phase E2: queue it, send RNR to the ingress leg while the egress window is shut,
            // count on Sent only, and reset the stitch on a permanent failure.
            Assert.That(_egress.Refused, Is.EqualTo(1), "the port did refuse");
            Assert.That(_egress.Sent.Count, Is.EqualTo(0), "nothing left the egress port");
            Assert.That(_egress.Counters.PacketsOut, Is.EqualTo(1u),
                "behaviour changed: counting only accepted packets is the FIX - rewrite this test");
            Assert.That(stitch.PacketsForward, Is.EqualTo(1));
            Assert.That(_ingress.ClearedCalls.Count, Is.EqualTo(0), "no clear toward the ingress DTE either");
        }

        [Test]
        public void ThePortContractHasNoWayToReportAReset_WHICH_IS_WRONG()
        {
            // X.25 4.4.3: a reset on one leg of a switched call must reach the other leg; queued
            // data for the circuit is discarded. MEASURED 2026-09-06: ISwitchPort has events for
            // IncomingCall, CallConnected, DataReceived, CallCleared and StateChanged - and nothing
            // for a reset or an interrupt - so SwitchCore cannot forward either even if a port
            // wanted to tell it. This pins the contract by enumerating the interface's events; when
            // Phase E1 adds Reset/Interrupt this count changes and the test must be rewritten.
            System.Reflection.EventInfo[] events = typeof(ISwitchPort).GetEvents();
            Assert.That(events.Length, Is.EqualTo(5),
                "behaviour changed: ISwitchPort gained an event - if it is Reset/Interrupt that is the FIX - rewrite this test");

            bool hasReset = false;
            for (int i = 0; i < events.Length; i++)
            {
                if (events[i].Name.IndexOf("Reset", StringComparison.Ordinal) >= 0)
                {
                    hasReset = true;
                }
            }

            Assert.That(hasReset, Is.False);
        }

        [Test]
        public void EgressAcceptsThePacket_ItIsForwardedAndCounted()
        {
            // The positive path, pinned so the E2 rewrite cannot break what works.
            ushort inLci = _ingress.InjectIncomingCall("23410001", "23400001");
            VcStitch stitch = _node.SnapshotStitches()[0];

            _ingress.InjectData(inLci, new byte[] { 9, 8, 7 });

            Assert.That(_egress.Sent.Count, Is.EqualTo(1));
            Assert.That(_egress.Sent[0].Lci, Is.EqualTo(stitch.OutLci));
            Assert.That(_egress.Sent[0].Data, Is.EqualTo(new byte[] { 9, 8, 7 }));
            Assert.That(_egress.Counters.PacketsOut, Is.EqualTo(1u));
            Assert.That(stitch.PacketsForward, Is.EqualTo(1));

            // And back the other way.
            _egress.InjectData(stitch.OutLci, new byte[] { 5 });
            Assert.That(_ingress.SentData.Count, Is.EqualTo(1));
            Assert.That(_ingress.SentData[0].Lci, Is.EqualTo(inLci));
            Assert.That(stitch.PacketsReverse, Is.EqualTo(1));
        }
    }
}
