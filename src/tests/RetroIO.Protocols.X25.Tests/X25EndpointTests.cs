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
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.PacketLayer;
using RetroIO.Protocols.X25.Runtime;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// <see cref="X25Endpoint"/>: one engine over one connection, with the blocking send the
    /// desktop runtime's PAD needs. Two endpoints are wired back to back through a queue that a
    /// second thread drains, so replies are asynchronous the way a socket's are.
    /// </summary>
    [TestFixture]
    public class X25EndpointTests
    {
        private sealed class Wire : IDisposable
        {
            private readonly Queue<(X25Endpoint To, byte[] Packet)> _queue = new Queue<(X25Endpoint, byte[])>();
            private readonly AutoResetEvent _work = new AutoResetEvent(false);
            private readonly Thread _pump;
            private volatile bool _running = true;
            public volatile bool Deliver = true;

            public X25Endpoint A = null!;
            public X25Endpoint B = null!;

            public Wire()
            {
                _pump = new Thread(Pump) { IsBackground = true, Name = "wire" };
                _pump.Start();
            }

            public bool Send(X25Endpoint to, byte[] packet)
            {
                if (!Deliver)
                {
                    return false;
                }

                lock (_queue)
                {
                    _queue.Enqueue((to, packet));
                }

                _work.Set();
                return true;
            }

            private void Pump()
            {
                while (_running)
                {
                    _work.WaitOne(50);
                    while (true)
                    {
                        (X25Endpoint To, byte[] Packet) item;
                        lock (_queue)
                        {
                            if (_queue.Count == 0)
                            {
                                break;
                            }

                            item = _queue.Dequeue();
                        }

                        item.To.Receive(item.Packet);
                    }
                }
            }

            public void Dispose()
            {
                _running = false;
                _work.Set();
                _pump.Join(1000);
            }
        }

        private static PacketLayer.X25Config Config(string address, bool dte) =>
            new PacketLayer.X25Config { LocalAddress = address, LciBase = 1, LciCount = 8, DefaultWindowSize = 2, DefaultPacketSize = 128, IsDte = dte };

        private static bool WaitFor(Func<bool> condition, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(5);
            }

            return condition();
        }

        [Test]
        public void CallDataAndClear_FlowThroughTwoEndpoints()
        {
            using Wire wire = new Wire();
            wire.A = new X25Endpoint("A", p => wire.Send(wire.B, p), Config("1", true), NullLogger.Instance);
            wire.B = new X25Endpoint("B", p => wire.Send(wire.A, p), Config("2", false), NullLogger.Instance);

            List<byte[]> received = new List<byte[]>();
            wire.B.Layer.IncomingCall += (l, lci, called, calling, cud) => l.AcceptCall(lci);
            wire.B.Layer.DataReceived += (l, lci, d, n, q, m, db) => { lock (received) received.Add(d); };
            int aConnected = 0;
            wire.A.Layer.CallConnected += (l, lci) => Interlocked.Increment(ref aConnected);

            ushort lci;
            lock (wire.A.Sync)
            {
                lci = wire.A.Layer.PlaceCall("2");
            }

            Assert.That(WaitFor(() => Volatile.Read(ref aConnected) == 1, 2000), Is.True, "call not connected");

            Assert.That(wire.A.SendBlocking(lci, new byte[] { 1, 2, 3 }, 1000), Is.True);
            Assert.That(WaitFor(() => { lock (received) return received.Count == 1; }, 2000), Is.True);
            Assert.That(received[0], Is.EqualTo(new byte[] { 1, 2, 3 }));

            int bCleared = 0;
            wire.B.Layer.Cleared += (l, c, cause, diag) => Interlocked.Increment(ref bCleared);
            lock (wire.A.Sync)
            {
                wire.A.Layer.ClearCall(lci);
            }

            Assert.That(WaitFor(() => Volatile.Read(ref bCleared) == 1, 2000), Is.True, "peer not told of the clear");
            Assert.That(WaitFor(() => { lock (wire.A.Sync) return wire.A.Layer.Circuits[lci].State == X25VcState.Ready; }, 2000), Is.True, "clear not confirmed");
        }

        [Test]
        public void SendBlocking_WaitsForTheWindowAndThenSends()
        {
            using Wire wire = new Wire();
            wire.A = new X25Endpoint("A", p => wire.Send(wire.B, p), Config("1", true), NullLogger.Instance);
            wire.B = new X25Endpoint("B", p => wire.Send(wire.A, p), Config("2", false), NullLogger.Instance);
            wire.B.Layer.IncomingCall += (l, lci, called, calling, cud) => l.AcceptCall(lci);
            int delivered = 0;
            wire.B.Layer.DataReceived += (l, lci, d, n, q, m, db) => Interlocked.Increment(ref delivered);
            int aConnected = 0;
            wire.A.Layer.CallConnected += (l, lci) => Interlocked.Increment(ref aConnected);

            ushort lci;
            lock (wire.A.Sync)
            {
                lci = wire.A.Layer.PlaceCall("2");
            }

            Assert.That(WaitFor(() => Volatile.Read(ref aConnected) == 1, 2000), Is.True);

            // Ten packets through a window of two: each blocked send waits for the RR that opens
            // the window, none is refused and none is lost.
            for (int i = 0; i < 10; i++)
            {
                Assert.That(wire.A.SendBlocking(lci, new byte[] { (byte)i }, 2000), Is.True, "send " + i);
            }

            Assert.That(WaitFor(() => Volatile.Read(ref delivered) == 10, 3000), Is.True, "delivered " + delivered);
        }

        [Test]
        public void SendBlocking_TimesOutWhenThePeerStaysBusy_AndDoesNotSpendPS()
        {
            using Wire wire = new Wire();
            wire.A = new X25Endpoint("A", p => wire.Send(wire.B, p), Config("1", true), NullLogger.Instance);
            wire.B = new X25Endpoint("B", p => wire.Send(wire.A, p), Config("2", false), NullLogger.Instance);
            wire.B.Layer.IncomingCall += (l, lci, called, calling, cud) => l.AcceptCall(lci);
            int aConnected = 0;
            wire.A.Layer.CallConnected += (l, lci) => Interlocked.Increment(ref aConnected);

            ushort lci;
            lock (wire.A.Sync)
            {
                lci = wire.A.Layer.PlaceCall("2");
            }

            Assert.That(WaitFor(() => Volatile.Read(ref aConnected) == 1, 2000), Is.True);

            // The peer goes busy on its side of the same channel.
            lock (wire.B.Sync)
            {
                Assert.That(wire.B.Layer.SetBusy(lci, true), Is.True);
            }

            Assert.That(WaitFor(() => { lock (wire.A.Sync) return wire.A.Layer.Circuits[lci].PeerBusy; }, 2000), Is.True, "RNR not seen");

            long t0 = Environment.TickCount64;
            Assert.That(wire.A.SendBlocking(lci, new byte[] { 9 }, 300), Is.False, "a send into a busy peer must time out, not go");
            Assert.That(Environment.TickCount64 - t0, Is.GreaterThanOrEqualTo(250), "it waited for the window");
            lock (wire.A.Sync)
            {
                Assert.That(wire.A.Layer.Circuits[lci].SendSeq, Is.EqualTo(0), "no P(S) spent on a packet that did not leave");
            }

            // The peer comes back; the next send goes at once.
            lock (wire.B.Sync)
            {
                wire.B.Layer.SetBusy(lci, false);
            }

            Assert.That(WaitFor(() => { lock (wire.A.Sync) return !wire.A.Layer.Circuits[lci].PeerBusy; }, 2000), Is.True);
            Assert.That(wire.A.SendBlocking(lci, new byte[] { 9 }, 300), Is.True);
        }

        [Test]
        public void SendBlocking_DoesNotWaitForReasonsWaitingCannotFix()
        {
            using Wire wire = new Wire();
            wire.A = new X25Endpoint("A", p => wire.Send(wire.B, p), Config("1", true), NullLogger.Instance);
            wire.B = new X25Endpoint("B", p => wire.Send(wire.A, p), Config("2", false), NullLogger.Instance);

            long t0 = Environment.TickCount64;
            Assert.That(wire.A.SendBlocking(7, new byte[] { 1 }, 2000), Is.False, "no such circuit");
            Assert.That(Environment.TickCount64 - t0, Is.LessThan(500), "returned at once, no wait");
        }

        [Test]
        public void LinkDown_MakesTheSinkReportLinkDownAndCallsFailCleanly()
        {
            using Wire wire = new Wire();
            wire.A = new X25Endpoint("A", p => wire.Send(wire.B, p), Config("1", true), NullLogger.Instance);
            wire.B = new X25Endpoint("B", p => wire.Send(wire.A, p), Config("2", false), NullLogger.Instance);

            wire.A.MarkLinkDown();
            ushort lci;
            lock (wire.A.Sync)
            {
                lci = wire.A.Layer.PlaceCall("2");
            }

            Assert.That(lci, Is.EqualTo((ushort)0), "a call on a dead link is refused, not left dangling");
            Assert.That(wire.A.Layer.SendFailures, Is.EqualTo(1));
        }
    }
}
