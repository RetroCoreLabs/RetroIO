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

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// RFC 1613 section 5: a separate TCP connection is used for each circuit. <see cref="X25Client"/> in XOT mode places
    /// each call on a connection of its own (the first call may use the connection Open made) and closes that
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
            Assert.That(peer.Accepted, Is.EqualTo(Calls), "one TCP connection for each of the " + Calls + " calls (the first call uses the one Open made)");

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

            Assert.That(peer.Accepted, Is.EqualTo(Calls));
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
            Assert.That(peer.Accepted, Is.EqualTo(2));

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
