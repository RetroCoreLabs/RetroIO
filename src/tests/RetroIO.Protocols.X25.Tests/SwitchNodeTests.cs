using System;
using NUnit.Framework;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Tests for <see cref="SwitchNode"/>'s port registry and virtual-circuit stitch table.
    /// </summary>
    [TestFixture]
    public class SwitchNodeTests
    {
        /// <summary>
        /// A minimal <see cref="ISwitchPort"/> used only to exercise the node's bookkeeping.
        /// </summary>
        private sealed class StubPort : ISwitchPort
        {
            public StubPort(int id)
            {
                Id = id;
                Config = new PortConfig { Id = id };
                Counters = new PortCounters();
            }

            public int Id { get; }
            public PortConfig Config { get; }
            public bool Active => true;
            public bool PeerConnected => true;
            public PortCounters Counters { get; }

#pragma warning disable CS0067 // events are part of the seam but unused by these tests
            public event PortIncomingCall? IncomingCall;
            public event PortCallConnected? CallConnected;
            public event PortDataReceived? DataReceived;
            public event PortCallCleared? CallCleared;
            public event PortStateChanged? StateChanged;
            public event PortReset? CircuitReset;
            public event PortInterrupt? InterruptReceived;
            public event PortReadyToSend? ReadyToSend;
#pragma warning restore CS0067

            public bool Start() => true;
            public void Stop() { }
            public ushort PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData) => 1;
            public void AcceptCall(ushort lci) { }
            public PortSendResult SendData(ushort lci, ReadOnlySpan<byte> data, bool qBit, bool mBit, bool dBit) => PortSendResult.Sent;
            public void ClearCall(ushort lci, byte cause, byte diagnostic) { }
            public void ResetCircuit(ushort lci, byte cause, byte diagnostic) { }
            public void SendInterrupt(ushort lci, byte[] userData) { }
            public void SetLocalBusy(ushort lci, bool busy) { }
        }

        private static SwitchNode NewNode()
        {
            SwitchConfig cfg = new SwitchConfig();
            cfg.Accounts.Add(new AccountConfig { Name = "acct", AddrStart = "2340", AddrEnd = "2341" });
            return new SwitchNode(cfg);
        }

        [Test]
        public void Accounts_BuiltFromConfig()
        {
            SwitchNode node = NewNode();

            Assert.That(node.Accounts.Count, Is.EqualTo(1));
            Assert.That(node.Accounts[0].Name, Is.EqualTo("acct"));
            Assert.That(node.Accounts[0].Covers("23405"), Is.True);
            Assert.That(node.Accounts[0].Covers("2342"), Is.False);
        }

        [Test]
        public void Ports_AddedAndFoundById()
        {
            SwitchNode node = NewNode();
            node.AddPort(new StubPort(0));
            node.AddPort(new StubPort(3));

            Assert.That(node.Ports.Count, Is.EqualTo(2));
            Assert.That(node.GetPort(3), Is.Not.Null);
            Assert.That(node.GetPort(3)!.Id, Is.EqualTo(3));
            Assert.That(node.GetPort(9), Is.Null);
        }

        [Test]
        public void Stitch_CreateFindDestroy()
        {
            SwitchNode node = NewNode();
            StubPort a = new StubPort(0);
            StubPort b = new StubPort(1);
            node.AddPort(a);
            node.AddPort(b);

            VcStitch st = node.CreateStitch(a, 5, b, 9);
            Assert.That(st.Active, Is.True);
            Assert.That(node.ActiveStitchCount, Is.EqualTo(1));

            // Find by either side.
            Assert.That(node.FindIngress(a, 5), Is.SameAs(st));
            Assert.That(node.FindEgress(b, 9), Is.SameAs(st));

            // The other directions do not match this stitch's sides.
            Assert.That(node.FindIngress(b, 9), Is.Null);
            Assert.That(node.FindEgress(a, 5), Is.Null);

            node.DestroyStitch(st);
            Assert.That(st.Active, Is.False);
            Assert.That(node.ActiveStitchCount, Is.EqualTo(0));
            Assert.That(node.FindIngress(a, 5), Is.Null);
        }

        [Test]
        public void SnapshotStitches_ReturnsOnlyActive()
        {
            SwitchNode node = NewNode();
            StubPort a = new StubPort(0);
            StubPort b = new StubPort(1);

            VcStitch s1 = node.CreateStitch(a, 1, b, 1);
            VcStitch s2 = node.CreateStitch(a, 2, b, 2);
            node.DestroyStitch(s1);

            System.Collections.Generic.List<VcStitch> snap = node.SnapshotStitches();
            Assert.That(snap.Count, Is.EqualTo(1));
            Assert.That(snap[0], Is.SameAs(s2));
        }
    }
}
