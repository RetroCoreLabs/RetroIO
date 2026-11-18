//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Client;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// RFC 1613 section 5: a separate TCP connection is used for each circuit. <see cref="X25Client"/> in XOT mode places
    /// each call on a connection of its own (the connection Open made is kept as a spare for calls that arrive) and closes that
    /// connection when the circuit is cleared.
    /// </summary>
    [TestFixture]
    public class X25ClientConnectionPerCallTests
    {
        /// <summary>
        /// A bare XOT peer: counts TCP connections, answers every Call Request with Call Accepted on the same channel and
        /// every Clear Request with Clear Confirmation, and notes when a connection ends.
        /// </summary>
        private sealed class CountingPeer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Thread _accept;
            private readonly object _lock = new object();
            private int _accepted;
            private int _callRequests;
            private int _ended;
            private volatile bool _stop;
            private readonly List<TcpClient> _clients = new List<TcpClient>();

            public CountingPeer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "counting-peer" };
                _accept.Start();
            }

            public int Port { get; }

            public int Accepted { get { lock (_lock) { return _accepted; } } }

            public int CallRequests { get { lock (_lock) { return _callRequests; } } }

            public int Ended { get { lock (_lock) { return _ended; } } }

            private void AcceptLoop()
            {
                while (!_stop)
                {
                    TcpClient client;
                    try
                    {
                        client = _listener.AcceptTcpClient();
                    }
                    catch (SocketException)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    lock (_lock)
                    {
                        _accepted++;
                        _clients.Add(client);
                    }

                    Thread t = new Thread(() => Serve(client)) { IsBackground = true, Name = "counting-peer-conn" };
                    t.Start();
                }
            }

            private void Serve(TcpClient client)
            {
                try
                {
                    NetworkStream s = client.GetStream();
                    byte[] header = new byte[4];
                    while (ReadFully(s, header, 4))
                    {
                        int length = (header[2] << 8) | header[3];
                        byte[] packet = new byte[length];
                        if (!ReadFully(s, packet, length))
                        {
                            break;
                        }

                        byte lcnHi = (byte)(packet[0] & 0x0F);
                        byte lcnLo = packet[1];
                        byte type = packet[2];
                        if (type == 0x0B)
                        {
                            lock (_lock)
                            {
                                _callRequests++;
                            }

                            Send(s, new byte[] { (byte)(0x10 | lcnHi), lcnLo, 0x0F });
                        }
                        else if (type == 0x13)
                        {
                            Send(s, new byte[] { (byte)(0x10 | lcnHi), lcnLo, 0x17 });
                        }
                    }
                }
                catch (Exception)
                {
                    // The client went away: the connection ended.
                }
                finally
                {
                    lock (_lock)
                    {
                        _ended++;
                    }
                }
            }

            private static void Send(NetworkStream s, byte[] packet)
            {
                byte[] frame = new byte[4 + packet.Length];
                frame[2] = (byte)(packet.Length >> 8);
                frame[3] = (byte)packet.Length;
                Array.Copy(packet, 0, frame, 4, packet.Length);
                s.Write(frame, 0, frame.Length);
            }

            private static bool ReadFully(NetworkStream s, byte[] buffer, int count)
            {
                int got = 0;
                while (got < count)
                {
                    int n = s.Read(buffer, got, count - got);
                    if (n <= 0)
                    {
                        return false;
                    }

                    got += n;
                }

                return true;
            }

            /// <summary>Drops every connection now, as a peer that refuses to keep them does.</summary>
            public void DropAll()
            {
                lock (_lock)
                {
                    for (int i = 0; i < _clients.Count; i++)
                    {
                        _clients[i].Close();
                    }
                }
            }

            public void Dispose()
            {
                _stop = true;
                _listener.Stop();
                lock (_lock)
                {
                    for (int i = 0; i < _clients.Count; i++)
                    {
                        _clients[i].Close();
                    }
                }
            }
        }

        private static X25ClientConfig ConfigFor(CountingPeer peer)
        {
            return new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = (ushort)peer.Port,
                LocalAddress = "23400001",
                Framing = X25ClientFraming.Xot
            };
        }

        [Test]
        public void EachCall_OpensItsOwnTcpConnection()
        {
            using CountingPeer peer = new CountingPeer();
            using X25Client client = new X25Client { ServiceName = "per-call" };
            client.Open(ConfigFor(peer), NullLogger.Instance);

            const int Calls = 8;
            X25Vc?[] vcs = new X25Vc?[Calls];
            for (int i = 0; i < Calls; i++)
            {
                vcs[i] = client.CallWait("23410001", 3000);
                Assert.That(vcs[i], Is.Not.Null, "call " + i + " was not answered");
            }

            Assert.That(peer.CallRequests, Is.EqualTo(Calls));

            // The first call takes the connection Open made, and the client dials a NEW spare for arriving calls on a
            // pool thread (X25Client.OpenSpare), so that ninth connection arrives some time after the first call. Until
            // 2026-11-18 this counted at once and failed now and then under load with 8: the spare had not been
            // accepted yet. Wait for it, then check no further connection follows.
            Assert.That(SpinWait.SpinUntil(() => peer.Accepted >= Calls + 1, 5000), Is.True, "the new spare never arrived (connections: " + peer.Accepted + ")");
            Assert.That(peer.Accepted, Is.EqualTo(Calls + 1), "one TCP connection for each of the " + Calls + " calls, plus the spare Open made, which is kept for calls that arrive");

            // The circuit numbers the application sees are all different, whatever connection carries them.
            HashSet<ushort> numbers = new HashSet<ushort>();
            for (int i = 0; i < Calls; i++)
            {
                numbers.Add(vcs[i]!.Lcn);
            }

            Assert.That(numbers.Count, Is.EqualTo(Calls));
        }

        [Test]
        public void SimultaneousCalls_OpenOneConnectionEach()
        {
            using CountingPeer peer = new CountingPeer();
            using X25Client client = new X25Client { ServiceName = "per-call-parallel" };
            client.Open(ConfigFor(peer), NullLogger.Instance);

            const int Calls = 8;
            X25Vc?[] vcs = new X25Vc?[Calls];
            Thread[] threads = new Thread[Calls];
            for (int i = 0; i < Calls; i++)
            {
                int index = i;
                threads[i] = new Thread(() => vcs[index] = client.CallWait("23410001", 5000));
                threads[i].Start();
            }

            for (int i = 0; i < Calls; i++)
            {
                Assert.That(threads[i].Join(10000), Is.True);
                Assert.That(vcs[i], Is.Not.Null, "call " + i + " was not answered");
            }

            // The new spare is dialled on a pool thread after a call takes the first one (see EachCall_OpensItsOwnTcpConnection).
            Assert.That(SpinWait.SpinUntil(() => peer.Accepted >= Calls + 1, 5000), Is.True, "the new spare never arrived (connections: " + peer.Accepted + ")");
            Assert.That(peer.Accepted, Is.EqualTo(Calls + 1), "one connection for each call, plus the spare Open made");
        }

        [Test]
        public void ClearingACall_ClosesItsConnection_ButNotTheOneOpenMade()
        {
            using CountingPeer peer = new CountingPeer();
            using X25Client client = new X25Client { ServiceName = "per-call-clear" };
            client.Open(ConfigFor(peer), NullLogger.Instance);

            X25Vc? first = client.CallWait("23410001", 3000);
            X25Vc? second = client.CallWait("23410001", 3000);
            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Not.Null);
            // The new spare is dialled on a pool thread after a call takes the first one (see EachCall_OpensItsOwnTcpConnection).
            Assert.That(SpinWait.SpinUntil(() => peer.Accepted >= 3, 5000), Is.True, "the new spare never arrived (connections: " + peer.Accepted + ")");
            Assert.That(peer.Accepted, Is.EqualTo(3), "one connection for each call, plus the spare Open made");

            client.Clear(second!);
            Assert.That(SpinWait.SpinUntil(() => peer.Ended == 1, 5000), Is.True, "the connection of the cleared call is closed");
            Assert.That(peer.Ended, Is.EqualTo(1), "the connection Open made stays up");
            Assert.That(client.IsOpen, Is.True);
        }

        [Test]
        public void ConnectionDropped_EveryCircuitOnItIsReportedCleared_WithCauseOutOfOrder()
        {
            using CountingPeer peer = new CountingPeer();
            using X25Client client = new X25Client { ServiceName = "per-call-drop" };
            object gate = new object();
            List<(ushort Lcn, byte Cause)> cleared = new List<(ushort, byte)>();
            client.SetOnClear((c, vc, cause, diag) =>
            {
                lock (gate)
                {
                    cleared.Add((vc.Lcn, cause));
                }
            });
            client.Open(ConfigFor(peer), NullLogger.Instance);

            X25Vc? first = client.CallWait("23410001", 3000);
            X25Vc? second = client.CallWait("23410001", 3000);
            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Not.Null);

            peer.DropAll();

            Assert.That(SpinWait.SpinUntil(() => { lock (gate) { return cleared.Count == 2; } }, 5000), Is.True, "both circuits are reported cleared");
            lock (gate)
            {
                Assert.That(cleared[0].Cause, Is.EqualTo((byte)0x09), "cause 9: out of order");
                Assert.That(cleared[1].Cause, Is.EqualTo((byte)0x09));
            }

            Assert.That(first!.Connected, Is.False);
            Assert.That(second!.Connected, Is.False);
        }

        /// <summary>
        /// Calls that arrive come in on a spare connection the client keeps open for them, one call per connection.
        /// When a call arrives on the spare the client opens a new spare; when the call ends its connection is
        /// closed. The next call arrives on the new spare.
        /// </summary>
        /// <remarks>
        /// RFC 1613 section 5: "A separate TCP connection MUST be used for each X.25 virtual circuit." The switch on
        /// the other end (ours and x25emu's, both since 2026-11-18) puts a call only on a connection that has never
        /// carried one and closes a connection when its call ends. Until 2026-11-18 this client took every arriving
        /// call on the one connection Open made and kept it after the call; x25emu then had nowhere to put the next
        /// call.
        /// </remarks>
        [Test]
        public void ArrivingCalls_EachTakeAFreshConnection_AndItClosesWhenTheCallEnds()
        {
            using XotSwitchPort dce = new XotSwitchPort(
                new PortConfig { Id = 0, Mode = PortMode.Dce, Port = 0, LciBase = 1, LciCount = 16 });
            Assert.That(dce.Start(), Is.True);
            int connected = 0;
            dce.CallConnected += (port, lci) => Interlocked.Increment(ref connected);

            using X25Client client = new X25Client { ServiceName = "spare" };
            client.SetOnIncoming((c, vc) => c.Accept(vc));
            client.Open(new X25ClientConfig { Host = "127.0.0.1", Port = dce.BoundPort, LocalAddress = "23430001", Framing = X25ClientFraming.Xot, NumVcs = 16 }, NullLogger.Instance);
            Assert.That(SpinWait.SpinUntil(() => dce.ConnectionCount == 1, 5000), Is.True, "the client's first connection never arrived");

            ushort first = dce.PlaceCall("23430001", "23400001", null);
            Assert.That(first, Is.Not.EqualTo(0), "the switch had no fresh connection for the first call");
            Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref connected) == 1, 5000), Is.True, "the first call was not answered");
            Assert.That(SpinWait.SpinUntil(() => dce.ConnectionCount == 2, 5000), Is.True,
                "the client did not open a new spare after a call took the old one (connections: " + dce.ConnectionCount + ")");

            dce.ClearCall(first, 0, 0);
            Assert.That(SpinWait.SpinUntil(() => dce.ConnectionCount == 1, 5000), Is.True,
                "the connection of the ended call was not closed (connections: " + dce.ConnectionCount + ")");
            // The new spare is dialled on a pool thread (X25Client.OpenSpare), and the switch counts the connection as
            // soon as TCP has it, a moment BEFORE the client records it as its spare. Until 2026-11-18 this was checked
            // at once and failed now and then (IsOpen false: the first call's connection was gone and the spare was not
            // recorded yet). Wait for the client to record it.
            Assert.That(SpinWait.SpinUntil(() => client.IsOpen, 5000), Is.True, "the client still has its spare");

            ushort second = dce.PlaceCall("23430001", "23400001", null);
            Assert.That(second, Is.Not.EqualTo(0), "the switch had no fresh connection for the second call");
            Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref connected) == 2, 5000), Is.True, "the second call was not answered");
        }

        /// <summary>
        /// A call can arrive on a new spare before the client has recorded the connection as its spare: the connection is
        /// up, its read thread runs, and the switch puts the waiting call on it at once. The client must still open the
        /// next spare. Until 2026-11-18 it made the taken connection its spare and never opened another, so the next
        /// arriving call found no connection (seen as SwitchPortFullTests.MoreCallsThanChannels failing now and then).
        /// The seam holds the dial at exactly that point while the call arrives.
        /// </summary>
        [Test]
        public void ACallThatTakesTheNewSpareBeforeItIsRecorded_StillGetsAnotherSpareOpened()
        {
            TcpListener sw = new TcpListener(IPAddress.Loopback, 0);
            sw.Start();
            List<TcpClient> accepted = new List<TcpClient>();
            try
            {
                int port = ((IPEndPoint)sw.LocalEndpoint).Port;
                using X25Client client = new X25Client { ServiceName = "spare-race" };
                client.SetOnIncoming((c, vc) => c.Accept(vc));

                // In the seam: take the new spare's connection, put a call on it and wait for the answer, all before the
                // client goes on to record the connection.
                // The seam runs on the client's pool thread; the test thread waits for it to finish before it accepts
                // the next connection, so the two never take each other's connection.
                bool answeredInSeam = false;
                Exception? seamFailure = null;
                using ManualResetEventSlim seamDone = new ManualResetEventSlim(false);
                client.SpareConnectedForTest = () =>
                {
                    client.SpareConnectedForTest = null;
                    try
                    {
                        TcpClient second = sw.AcceptTcpClient();
                        lock (accepted)
                        {
                            accepted.Add(second);
                        }

                        WriteXot(second.GetStream(), RetroIO.Protocols.X25.Models.X25PacketBuilder.CreateCallRequest(1, "23430001", "23400002"));
                        answeredInSeam = ReadXotType(second.GetStream()) == 0x0F;
                    }
                    catch (Exception ex)
                    {
                        seamFailure = ex;
                    }
                    finally
                    {
                        seamDone.Set();
                    }
                };

                client.Open(new X25ClientConfig { Host = "127.0.0.1", Port = (ushort)port, LocalAddress = "23430001", Framing = X25ClientFraming.Xot }, NullLogger.Instance);
                TcpClient first = sw.AcceptTcpClient();
                lock (accepted)
                {
                    accepted.Add(first);
                }

                // The first call takes the connection Open made; the client dials a new spare (and the seam runs).
                WriteXot(first.GetStream(), RetroIO.Protocols.X25.Models.X25PacketBuilder.CreateCallRequest(1, "23430001", "23400001"));
                Assert.That(ReadXotType(first.GetStream()), Is.EqualTo((byte)0x0F), "the first call was not accepted");

                Assert.That(seamDone.Wait(5000), Is.True, "the client never dialled a new spare");
                Assert.That(seamFailure, Is.Null, "the call on the new spare: " + seamFailure?.Message);

                // The next spare must come: a third connection.
                System.Threading.Tasks.Task<TcpClient> third = sw.AcceptTcpClientAsync();
                Assert.That(third.Wait(5000), Is.True, "no new spare was opened after a call took the last one before it was recorded");
                lock (accepted)
                {
                    accepted.Add(third.Result);
                }

                Assert.That(answeredInSeam, Is.True, "the call put on the new spare was not accepted");
            }
            finally
            {
                lock (accepted)
                {
                    for (int i = 0; i < accepted.Count; i++)
                    {
                        accepted[i].Close();
                    }
                }

                sw.Stop();
            }
        }

        private static void WriteXot(NetworkStream s, byte[] packet)
        {
            byte[] frame = new byte[4 + packet.Length];
            frame[2] = (byte)(packet.Length >> 8);
            frame[3] = (byte)packet.Length;
            Array.Copy(packet, 0, frame, 4, packet.Length);
            s.Write(frame, 0, frame.Length);
        }

        private static byte ReadXotType(NetworkStream s)
        {
            s.ReadTimeout = 5000;
            byte[] header = ReadExactly(s, 4);
            byte[] packet = ReadExactly(s, (header[2] << 8) | header[3]);
            return packet[2];
        }

        private static byte[] ReadExactly(NetworkStream s, int count)
        {
            byte[] buffer = new byte[count];
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buffer, got, count - got);
                if (n <= 0)
                {
                    throw new InvalidOperationException("the connection closed after " + got + " of " + count + " octets");
                }

                got += n;
            }

            return buffer;
        }

        [Test]
        public void ADeadPeer_MakesTheCallFail_NotThrow()
        {
            CountingPeer peer = new CountingPeer();
            using X25Client client = new X25Client { ServiceName = "per-call-dead" };
            client.Open(ConfigFor(peer), NullLogger.Instance);
            X25Vc? first = client.CallWait("23410001", 3000);
            Assert.That(first, Is.Not.Null);

            peer.Dispose();

            // The second call needs a new connection; nothing listens any more.
            Assert.That(client.CallWait("23410001", 1000), Is.Null);
        }
    }
}
