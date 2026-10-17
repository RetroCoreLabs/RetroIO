//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.X25.PacketLayer;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Tests for the clean <see cref="X25Layer"/>: two layers are cross-wired so one layer's outbound
    /// packets feed the other's <see cref="X25Layer.ProcessPacket"/>, exercising the full call / data /
    /// clear / restart handshake through the named-delegate up-events.
    /// </summary>
    [TestFixture]
    public class X25LayerTests
    {
        /// <summary>
        /// A packet sink that captures sent packets and forwards them into a target layer.
        /// </summary>
        private sealed class LoopSink : IX25PacketSink
        {
            public X25Layer? Target;
            public readonly List<byte[]> Sent = new List<byte[]>();

            public X25SendResult SendPacket(ReadOnlySpan<byte> packet)
            {
                byte[] bytes = packet.ToArray();
                Sent.Add(bytes);
                Target?.ProcessPacket(bytes);
                return X25SendResult.Sent;
            }
        }

        private LoopSink _sinkA = null!;
        private LoopSink _sinkB = null!;
        private X25Layer _a = null!;
        private X25Layer _b = null!;

        [SetUp]
        public void SetUp()
        {
            _sinkA = new LoopSink();
            _sinkB = new LoopSink();
            _a = new X25Layer(_sinkA, new X25Config { LocalAddress = "2340", LciBase = 1, LciCount = 16 });
            _b = new X25Layer(_sinkB, new X25Config { LocalAddress = "2341", LciBase = 1, LciCount = 16 });
            _sinkA.Target = _b;
            _sinkB.Target = _a;
        }

        [Test]
        public void FullHandshake_Call_Accept_DataBothWays_Clear()
        {
            ushort incomingLci = 0;
            string? incCalled = null;
            string? incCalling = null;
            _b.IncomingCall += (layer, lci, called, calling, cud) =>
            {
                incomingLci = lci;
                incCalled = called;
                incCalling = calling;
                layer.AcceptCall(lci);   // B accepts as soon as the call arrives
            };

            bool connected = false;
            _a.CallConnected += (layer, lci) => connected = true;

            byte[]? bReceived = null;
            _b.DataReceived += (layer, lci, data, length, q, m, d) => bReceived = Slice(data, length);

            byte[]? aReceived = null;
            _a.DataReceived += (layer, lci, data, length, q, m, d) => aReceived = Slice(data, length);

            // The party that RECEIVES the clear (B) raises Cleared; the initiator (A) just returns to ready
            // on the clear confirmation, since it already knows it asked to clear.
            bool bCleared = false;
            _b.Cleared += (layer, lci, cause, diag) => bCleared = true;

            // A places a call to B.
            ushort lciA = _a.PlaceCall("2341", "2340");
            Assert.That(lciA, Is.Not.EqualTo((ushort)0));
            Assert.That(incCalled, Is.EqualTo("2341"));
            Assert.That(incCalling, Is.EqualTo("2340"));
            Assert.That(incomingLci, Is.EqualTo(lciA));
            Assert.That(connected, Is.True, "A sees the call connected after B accepts");

            // A -> B data.
            Assert.That(_a.SendData(lciA, Encoding.ASCII.GetBytes("hi")), Is.True);
            Assert.That(bReceived, Is.EqualTo(Encoding.ASCII.GetBytes("hi")));

            // B -> A data (reverse).
            Assert.That(_b.SendData(incomingLci, Encoding.ASCII.GetBytes("yo")), Is.True);
            Assert.That(aReceived, Is.EqualTo(Encoding.ASCII.GetBytes("yo")));

            // A clears; B confirms and both circuits return to ready.
            _a.ClearCall(lciA, 0, 0);
            Assert.That(bCleared, Is.True, "B sees the incoming clear");
            Assert.That(_a.Circuits[lciA].State, Is.EqualTo(X25VcState.Ready), "A returns to ready on clear confirmation");
            Assert.That(_b.Circuits[incomingLci].State, Is.EqualTo(X25VcState.Ready));
        }

        [Test]
        public void IncomingCall_Rejected_ByClear()
        {
            _b.IncomingCall += (layer, lci, called, calling, cud) => layer.ClearCall(lci, 0x0D, 0);

            bool aCleared = false;
            _a.Cleared += (layer, lci, cause, diag) => aCleared = true;

            ushort lciA = _a.PlaceCall("9999");
            Assert.That(aCleared, Is.True, "the rejecting clear comes back to A");
            Assert.That(_a.Circuits[lciA].State, Is.EqualTo(X25VcState.Ready));
        }

        [Test]
        public void SendData_BlockedOutsideDataTransfer()
        {
            // No call placed yet: sending on an unknown / non-established circuit fails.
            Assert.That(_a.SendData(1, new byte[] { 1, 2, 3 }), Is.False);
        }

        [Test]
        public void SendData_HonoursSendWindow()
        {
            _b.IncomingCall += (layer, lci, called, calling, cud) => layer.AcceptCall(lci);
            // Stop B from acknowledging so A's window fills: drop B's outbound (RR) by unwiring the target.
            ushort lciA = _a.PlaceCall("2341");
            _sinkB.Target = null; // B's RR acks no longer reach A -> AckedSeq stays put

            // Default window is 2: two sends succeed, the third is blocked until acknowledged.
            Assert.That(_a.SendData(lciA, new byte[] { 1 }), Is.True);
            Assert.That(_a.SendData(lciA, new byte[] { 2 }), Is.True);
            Assert.That(_a.SendData(lciA, new byte[] { 3 }), Is.False, "window of 2 is full");
        }

        [Test]
        public void Restart_FreesSwitchedCircuits()
        {
            _b.IncomingCall += (layer, lci, called, calling, cud) => layer.AcceptCall(lci);
            bool restarted = false;
            _b.RestartOccurred += (layer, cause, diag) => restarted = true;

            ushort lciA = _a.PlaceCall("2341");
            Assert.That(_a.Circuits[lciA].State, Is.EqualTo(X25VcState.DataTransfer));

            _a.Restart(0, 0);
            Assert.That(restarted, Is.True, "B sees the restart");
            Assert.That(_a.Circuits[lciA].State, Is.EqualTo(X25VcState.Ready), "A frees its circuits");
        }

        private static byte[] Slice(byte[] data, int length)
        {
            byte[] r = new byte[length];
            Array.Copy(data, 0, r, 0, length);
            return r;
        }
    }
}
