//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using RetroIO.Link.Framing;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// <see cref="XotSwitchPort"/> gives every virtual circuit its own TCP connection, as RFC 1613
    /// section 5 requires, and keeps the circuits of different connections apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The C emulator's switch does this, and its trunk could not call into a port that took one
    /// connection at a time: the port refused the second connection, and a second call never
    /// arrived. The tests here use raw sockets as the peer so that they decide exactly what is on the
    /// wire, in particular the channel number each peer puts on its packets. RFC 1613 says that number
    /// "has no significance and has arbitrary values", so two connections that both use channel 5 are
    /// two different circuits.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class XotSwitchPortPerCircuitTests
    {
        private const string Called = "23420001";
        private const string Calling = "23400001";

        private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (!condition())
            {
                if (Environment.TickCount64 > deadline)
                {
                    return false;
                }

                Thread.Sleep(10);
            }

            return true;
        }

        private static XotSwitchPort NewDce(ushort channels = 16)
        {
            XotSwitchPort port = new XotSwitchPort(new PortConfig { Id = 0, Mode = PortMode.Dce, Port = 0, LciBase = 1, LciCount = channels });
            Assert.That(port.Start(), Is.True);
            return port;
        }

        /// <summary>
        /// A peer made of a raw socket and a codec: it sends the packets the test hands it and keeps
        /// every packet it receives, with the channel number as it came.
        /// </summary>
        private sealed class RawPeer : IDisposable
        {
            private readonly TcpClient _client;
            private readonly XotFrameCodec _codec;
            private readonly Thread _reader;
            private readonly List<byte[]> _received = new List<byte[]>();

            public RawPeer(ushort port)
            {
                _client = new TcpClient { NoDelay = true };
                _client.Connect("127.0.0.1", port);
                _codec = new XotFrameCodec("raw", new Sink(_client.GetStream()));
                _codec.FrameReceived += (id, frame) =>
                {
                    lock (_received)
                    {
                        _received.Add(frame.ToArray());
                    }
                };

                _reader = new Thread(Read) { IsBackground = true };
                _reader.Start();
            }

            private sealed class Sink : IFrameTransport
            {
                private readonly NetworkStream _stream;

                public Sink(NetworkStream stream)
                {
                    _stream = stream;
                }

                public void Send(ReadOnlySpan<byte> bytes)
                {
                    _stream.Write(bytes);
                }
            }

            private void Read()
            {
                byte[] buffer = new byte[4096];
                try
                {
                    NetworkStream stream = _client.GetStream();
                    while (true)
                    {
                        int n = stream.Read(buffer, 0, buffer.Length);
                        if (n <= 0)
                        {
                            return;
                        }

                        _codec.ProcessBytes(new ReadOnlySpan<byte>(buffer, 0, n));
                    }
                }
                catch (Exception)
                {
                    // Closed by the test or by the port.
                }
            }

            public void Send(byte[] packet)
            {
                _codec.SendFrame(packet);
            }

            public bool WaitForPacket(byte type, out byte[] packet)
            {
                byte[]? found = null;
                bool ok = WaitFor(() =>
                {
                    lock (_received)
                    {
                        for (int i = 0; i < _received.Count; i++)
                        {
                            if (_received[i].Length >= 3 && _received[i][2] == type)
                            {
                                found = _received[i];
                                return true;
                            }
                        }
                    }

                    return false;
                });

                packet = found ?? Array.Empty<byte>();
                return ok;
            }

            public int DataCount
            {
                get
                {
                    lock (_received)
                    {
                        int count = 0;
                        for (int i = 0; i < _received.Count; i++)
                        {
                            if (_received[i].Length >= 3 && (_received[i][2] & 0x01) == 0)
                            {
                                count++;
                            }
                        }

                        return count;
                    }
                }
            }

            public bool WaitForData(out byte[] packet)
            {
                byte[]? found = null;
                bool ok = WaitFor(() =>
                {
                    lock (_received)
                    {
                        for (int i = 0; i < _received.Count; i++)
                        {
                            if (_received[i].Length >= 3 && (_received[i][2] & 0x01) == 0)
                            {
                                found = _received[i];
                                return true;
                            }
                        }
                    }

                    return false;
                });

                packet = found ?? Array.Empty<byte>();
                return ok;
            }

            public void Dispose()
            {
                _client.Close();
            }
        }

        /// <summary>
        /// Two peers on two connections that both call on channel 5 are two circuits, and each keeps
        /// its own data.
        /// </summary>
        [Test]
        public void TwoConnections_UsingTheSameChannelNumber_AreTwoCircuits()
        {
            using XotSwitchPort dce = NewDce();
            object gate = new object();
            List<ushort> channels = new List<ushort>();
            Dictionary<ushort, byte[]> received = new Dictionary<ushort, byte[]>();

            dce.IncomingCall += (port, lci, called, calling, cud) =>
            {
                lock (gate)
                {
                    channels.Add(lci);
                }

                port.AcceptCall(lci);
            };

            dce.DataReceived += (port, lci, data, length, q, m, d) =>
            {
                byte[] copy = new byte[length];
                Buffer.BlockCopy(data, 0, copy, 0, length);
                lock (gate)
                {
                    received[lci] = copy;
                }
            };

            using RawPeer one = new RawPeer(dce.BoundPort);
            using RawPeer two = new RawPeer(dce.BoundPort);

            one.Send(X25PacketBuilder.CreateCallRequest(5, Called, Calling));
            Assert.That(one.WaitForPacket(0x0F, out byte[] acceptedOne), Is.True, "the first peer should get Call Accepted");
            two.Send(X25PacketBuilder.CreateCallRequest(5, Called, Calling));
            Assert.That(two.WaitForPacket(0x0F, out byte[] acceptedTwo), Is.True, "the second peer should get Call Accepted");

            Assert.That(acceptedOne[1], Is.EqualTo(5), "the peer is answered on the channel number it used");
            Assert.That(acceptedTwo[1], Is.EqualTo(5), "the other peer is answered on the channel number it used too");

            ushort first;
            ushort second;
            lock (gate)
            {
                Assert.That(channels.Count, Is.EqualTo(2), "two calls reached the switch");
                first = channels[0];
                second = channels[1];
            }

            Assert.That(first, Is.Not.EqualTo(second), "the engine must see two different channels");

            one.Send(X25PacketBuilder.CreateDataPacket(5, 0, 0, new byte[] { 0x11, 0x11 }));
            two.Send(X25PacketBuilder.CreateDataPacket(5, 0, 0, new byte[] { 0x22, 0x22, 0x22 }));

            Assert.That(WaitFor(() => { lock (gate) { return received.Count == 2; } }), Is.True);
            lock (gate)
            {
                Assert.That(received[first], Is.EqualTo(new byte[] { 0x11, 0x11 }));
                Assert.That(received[second], Is.EqualTo(new byte[] { 0x22, 0x22, 0x22 }));
            }

            // Data for the second circuit goes to the second peer only, on the channel it knows.
            Assert.That(dce.SendData(second, new byte[] { 0x99 }, false, false, false), Is.EqualTo(PortSendResult.Sent));
            Assert.That(two.WaitForData(out byte[] dataToTwo), Is.True);
            Assert.That(dataToTwo[1], Is.EqualTo(5));
            Assert.That(dataToTwo[dataToTwo.Length - 1], Is.EqualTo(0x99));
            Thread.Sleep(200);
            Assert.That(one.DataCount, Is.EqualTo(0), "the first peer must not receive the second circuit's data");
        }

        /// <summary>
        /// When one connection is lost only its own circuit ends; the other circuit and the port stay up.
        /// </summary>
        [Test]
        public void LosingOneConnection_EndsOnlyItsCircuit()
        {
            using XotSwitchPort dce = NewDce();
            object gate = new object();
            List<ushort> channels = new List<ushort>();
            List<ushort> cleared = new List<ushort>();
            int stateChanges = 0;

            dce.IncomingCall += (port, lci, called, calling, cud) =>
            {
                lock (gate)
                {
                    channels.Add(lci);
                }

                port.AcceptCall(lci);
            };

            dce.CallCleared += (port, lci, cause, diagnostic) =>
            {
                lock (gate)
                {
                    cleared.Add(lci);
                }
            };

            dce.StateChanged += (port, active) =>
            {
                Interlocked.Increment(ref stateChanges);
            };

            RawPeer one = new RawPeer(dce.BoundPort);
            using RawPeer two = new RawPeer(dce.BoundPort);

            one.Send(X25PacketBuilder.CreateCallRequest(5, Called, Calling));
            Assert.That(one.WaitForPacket(0x0F, out _), Is.True);
            two.Send(X25PacketBuilder.CreateCallRequest(6, Called, Calling));
            Assert.That(two.WaitForPacket(0x0F, out _), Is.True);

            ushort first;
            ushort second;
            lock (gate)
            {
                first = channels[0];
                second = channels[1];
            }

            one.Dispose();

            Assert.That(WaitFor(() => { lock (gate) { return cleared.Count >= 1; } }), Is.True, "the lost connection's circuit should end");
            Thread.Sleep(300);
            lock (gate)
            {
                Assert.That(cleared, Is.EqualTo(new List<ushort> { first }), "only the circuit on the lost connection ends");
            }

            Assert.That(dce.PeerConnected, Is.True, "another connection is still up");
            Assert.That(dce.ConnectionCount, Is.EqualTo(1));
            Assert.That(Volatile.Read(ref stateChanges), Is.EqualTo(0), "the port did not go down, so the switch must not be told it did");
            Assert.That(dce.SendData(second, new byte[] { 0x42 }, false, false, false), Is.EqualTo(PortSendResult.Sent), "the other circuit still carries data");
            Assert.That(two.WaitForData(out _), Is.True);
        }

        /// <summary>
        /// A call placed by a dialling port gets a connection of its own, and the connection is closed
        /// when the call ends; the spare stays.
        /// </summary>
        [Test]
        public void ADiallingPort_OpensAConnectionForEachCall_AndClosesItWhenTheCallEnds()
        {
            using XotSwitchPort dce = NewDce();
            dce.IncomingCall += (port, lci, called, calling, cud) => port.AcceptCall(lci);

            using XotSwitchPort dte = new XotSwitchPort(new PortConfig
            {
                Id = 1,
                Mode = PortMode.Dte,
                Host = "127.0.0.1",
                Port = dce.BoundPort,
                LciBase = 1,
                LciCount = 16,
            });

            int connected = 0;
            dte.CallConnected += (port, lci) => Interlocked.Increment(ref connected);

            Assert.That(dte.Start(), Is.True);
            Assert.That(WaitFor(() => dte.PeerConnected && dce.PeerConnected), Is.True);
            Assert.That(dce.ConnectionCount, Is.EqualTo(1), "only the spare before any call");

            ushort a = dte.PlaceCall(Called, Calling, null);
            ushort b = dte.PlaceCall(Called, Calling, null);
            Assert.That(a, Is.Not.EqualTo(0));
            Assert.That(b, Is.Not.EqualTo(0));
            Assert.That(a, Is.Not.EqualTo(b));

            Assert.That(WaitFor(() => Volatile.Read(ref connected) == 2), Is.True, "both calls should connect");
            Assert.That(dce.ConnectionCount, Is.EqualTo(3), "the spare and one connection for each call");
            Assert.That(dte.ConnectionCount, Is.EqualTo(3));

            dte.ClearCall(a, 0, 0);
            dte.ClearCall(b, 0, 0);

            Assert.That(WaitFor(() => dce.ConnectionCount == 1 && dte.ConnectionCount == 1), Is.True,
                "the connections dialled for the calls close with the calls; the spare stays (dce=" + dce.ConnectionCount + ", dte=" + dte.ConnectionCount + ")");
            Assert.That(dte.PeerConnected, Is.True);
        }

        /// <summary>
        /// A listening port takes a second connection while the first is still up.
        /// </summary>
        [Test]
        public void AListeningPort_AcceptsSeveralConnectionsAtOnce()
        {
            using XotSwitchPort dce = NewDce();
            using RawPeer one = new RawPeer(dce.BoundPort);
            using RawPeer two = new RawPeer(dce.BoundPort);
            using RawPeer three = new RawPeer(dce.BoundPort);

            Assert.That(WaitFor(() => dce.ConnectionCount == 3), Is.True, "connections: " + dce.ConnectionCount);
        }
    }
}
