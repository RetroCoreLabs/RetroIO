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
            private volatile bool _closedFlag;
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
                            break;
                        }

                        _codec.ProcessBytes(new ReadOnlySpan<byte>(buffer, 0, n));
                    }
                }
                catch (Exception)
                {
                    // Closed by the test or by the port.
                }

                _closedFlag = true;
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

            public bool IsClosed => _closedFlag;

            public string Dump()
            {
                lock (_received)
                {
                    List<string> lines = new List<string>();
                    for (int i = 0; i < _received.Count; i++)
                    {
                        lines.Add(BitConverter.ToString(_received[i]));
                    }

                    return string.Join(" | ", lines);
                }
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
        /// <summary>
        /// A listening port whose only connection already carries a call: the peer is connected, but has no fresh
        /// connection for a new call.
        /// </summary>
        private static XotSwitchPort DceWithNoFreshConnection(out RawPeer busy)
        {
            XotSwitchPort dce = NewDce();
            dce.IncomingCall += (port, lci, called, calling, cud) => port.AcceptCall(lci);
            busy = new RawPeer(dce.BoundPort);
            busy.Send(X25PacketBuilder.CreateCallRequest(5, Called, Calling));
            Assert.That(busy.WaitForPacket(0x0F, out _), Is.True, "the first call takes the only connection");
            return dce;
        }

        /// <summary>
        /// A call out of a listening port that has no fresh connection is HELD, and the thread placing it returns at
        /// once; the Call Request goes on the next connection the peer opens. x25emu does the same (tools/x25switch.c,
        /// conn_for_tx, the held queue). Before 2026-11-21 the placing thread - another port's reader - waited up to
        /// five seconds inside PlaceCall, and every circuit on that other port stood still meanwhile (RetroX25 PLAN
        /// H3-8).
        /// </summary>
        [Test]
        public void ACallWithNoFreshConnection_IsHeldWithoutBlocking_AndGoesOnTheNextConnection()
        {
            using XotSwitchPort dce = DceWithNoFreshConnection(out RawPeer busy);
            using (busy)
            {
                long start = Environment.TickCount64;
                ushort lci = dce.PlaceCall(Called, Calling, null);
                long took = Environment.TickCount64 - start;

                Assert.That(lci, Is.Not.EqualTo((ushort)0), "the call is placed (held), not refused");
                Assert.That(took, Is.LessThan(1000), "PlaceCall must not wait for a connection; it took " + took + " ms");

                using RawPeer fresh = new RawPeer(dce.BoundPort);
                Assert.That(fresh.WaitForPacket(0x0B, out _), Is.True, "the held Call Request goes on the new connection: " + fresh.Dump());
                Thread.Sleep(200);
                Assert.That(busy.Dump().Contains("-0B-"), Is.False, "the busy connection gets no second call: " + busy.Dump());
            }
        }

        /// <summary>
        /// A held call for which no connection comes is cleared after five seconds with Number busy #71 (X.25 Table
        /// C.3 a) item 31, no logical channel available), as x25emu clears it (SW_UNDELIVERABLE_MS).
        /// </summary>
        [Test]
        public void AHeldCall_WithNoConnectionComing_IsClearedNumberBusyAfterFiveSeconds()
        {
            using XotSwitchPort dce = DceWithNoFreshConnection(out RawPeer busy);
            using (busy)
            {
                byte cause = 0xFF;
                byte diag = 0xFF;
                ushort clearedLci = 0;
                long clearedAt = 0;
                dce.CallCleared += (port, l, c, d) =>
                {
                    clearedLci = l;
                    cause = c;
                    diag = d;
                    clearedAt = Environment.TickCount64;
                };

                long start = Environment.TickCount64;
                ushort lci = dce.PlaceCall(Called, Calling, null);
                Assert.That(lci, Is.Not.EqualTo((ushort)0));
                Assert.That(WaitFor(() => clearedLci == lci, 9000), Is.True, "the held call is cleared");
                Assert.That(cause, Is.EqualTo(0x01), "Number busy");
                Assert.That(diag, Is.EqualTo(71), "#71, no logical channel available");
                Assert.That(clearedAt - start, Is.GreaterThanOrEqualTo(4500), "held for the full five seconds");
                Assert.That(busy.Dump().Contains("-0B-"), Is.False, "nothing was sent on the busy connection");
            }
        }

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
        /// A port has no use for more connections than it has channels. Like the C emulator's switch,
        /// it does not accept the extra one: it waits in the TCP backlog, open, and is taken when a
        /// connection closes. Until 2026-11-18 this test expected the port to accept it and close it,
        /// which is not what x25emu does (tools/x25switch.c only polls its listener while a slot is free).
        /// </summary>
        [Test]
        public void AConnectionBeyondThePortsChannels_Waits_AndIsTakenWhenOneCloses()
        {
            using XotSwitchPort dce = NewDce(channels: 2);
            dce.IncomingCall += (port, lci, called, calling, cud) => port.AcceptCall(lci);

            using RawPeer one = new RawPeer(dce.BoundPort);
            using RawPeer two = new RawPeer(dce.BoundPort);
            one.Send(X25PacketBuilder.CreateCallRequest(7, Called, Calling));
            Assert.That(one.WaitForPacket(0x0F, out _), Is.True);
            two.Send(X25PacketBuilder.CreateCallRequest(7, Called, Calling));
            Assert.That(two.WaitForPacket(0x0F, out _), Is.True);

            using RawPeer three = new RawPeer(dce.BoundPort);
            three.Send(X25PacketBuilder.CreateCallRequest(7, Called, Calling));
            Assert.That(WaitFor(() => three.IsClosed), Is.False, "the third connection must not be closed by the port");
            Assert.That(dce.ConnectionCount, Is.EqualTo(2));
            Assert.That(one.IsClosed, Is.False);
            Assert.That(two.IsClosed, Is.False);

            // The first call's connection closes: the third connection gets the slot and its call is answered.
            one.Dispose();
            Assert.That(three.WaitForPacket(0x0F, out _), Is.True, "the waiting connection's call is accepted once a slot frees");
            Assert.That(two.IsClosed, Is.False);
        }

        /// <summary>
        /// A peer that puts several circuits on one connection is served while the port has channels;
        /// a call beyond the port's channels is cleared with cause 5 (network congestion), and the
        /// others stay up.
        /// </summary>
        /// <remarks>
        /// This used to expect a Diagnostic (type 0xF1). A Diagnostic is not an answer to a Call
        /// Request: a caller waits for the call to be accepted or cleared, so the refused call hung
        /// (found with x25traffic, which sent 200 calls to a port of 64 channels and heard nothing for
        /// 136 of them). X.25 Table 5-7 has the network clear the call with cause 5, network congestion.
        /// </remarks>
        [Test]
        public void ACallOnOneConnection_WithNoChannelLeft_IsClearedWithNetworkCongestion()
        {
            using XotSwitchPort dce = NewDce(channels: 2);
            int incoming = 0;
            dce.IncomingCall += (port, lci, called, calling, cud) =>
            {
                Interlocked.Increment(ref incoming);
                port.AcceptCall(lci);
            };

            using RawPeer peer = new RawPeer(dce.BoundPort);
            peer.Send(X25PacketBuilder.CreateCallRequest(7, Called, Calling));
            peer.Send(X25PacketBuilder.CreateCallRequest(8, Called, Calling));
            Assert.That(WaitFor(() => Volatile.Read(ref incoming) == 2), Is.True, "two circuits fit in a port of two channels");

            peer.Send(X25PacketBuilder.CreateCallRequest(9, Called, Calling));
            Assert.That(peer.WaitForPacket(0x13, out byte[] clear), Is.True, "the third call should be cleared; received: " + peer.Dump());
            Assert.That(clear[1], Is.EqualTo(9), "the clear is on the channel the caller used");
            Assert.That(clear[3], Is.EqualTo(0x05), "cause 5: network congestion");
            Assert.That(peer.Dump(), Does.Not.Contain("F1"), "no Diagnostic is written for the refused call");

            // The caller confirms the clear; the port takes it without answering it as a stray packet.
            peer.Send(X25PacketBuilder.CreateClearConfirmation(9));
            Thread.Sleep(200);
            Assert.That(peer.Dump(), Does.Not.Contain("F1"), "the caller's clear confirmation is not answered with a Diagnostic");
            Assert.That(Volatile.Read(ref incoming), Is.EqualTo(2), "the third call never reached the switch");
            Assert.That(dce.ConnectionCount, Is.EqualTo(1));
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
        /// <summary>
        /// RFC 1613 section 6: the channel number in an XOT packet "has no significance and has arbitrary values". A peer such as
        /// x25emu answers a call on a connection dialled for it on ITS OWN channel number (our call went out on channel 16, the
        /// Call Accepted came back on channel 1). The call must still be connected, and the data and the clear must follow on
        /// the channel the peer chose. Found 2026-11-13 against x25emu: the port's packet layer answered the Call Accepted
        /// with Clear Request, diagnostic 20, and the call died.
        /// </summary>
        [Test]
        public void ADiallingPort_ConnectsTheCall_WhenThePeerAnswersOnAnotherChannelNumber()
        {
            TcpListener listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int peerPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            List<byte[]> seen = new List<byte[]>();
            bool stop = false;
            Thread accept = new Thread(() =>
            {
                while (!stop)
                {
                    TcpClient connection;
                    try
                    {
                        connection = listener.AcceptTcpClient();
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    NetworkStream stream = connection.GetStream();
                    XotFrameCodec codec = new XotFrameCodec("answering-peer", new RawSink(stream));
                    codec.FrameReceived += (id, frame) =>
                    {
                        byte[] copy = frame.ToArray();
                        lock (seen)
                        {
                            seen.Add(copy);
                        }

                        if (copy.Length >= 3 && copy[2] == 0x0B)
                        {
                            // Answer on channel 1, whatever channel the call came in on.
                            codec.SendFrame(X25PacketBuilder.CreateCallAccepted(1, null));
                        }
                    };
                    Thread read = new Thread(() =>
                    {
                        byte[] buffer = new byte[4096];
                        try
                        {
                            while (true)
                            {
                                int n = stream.Read(buffer, 0, buffer.Length);
                                if (n <= 0)
                                {
                                    break;
                                }

                                codec.ProcessBytes(new ReadOnlySpan<byte>(buffer, 0, n));
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }) { IsBackground = true };
                    read.Start();
                }
            }) { IsBackground = true };
            accept.Start();
            try
            {
                using XotSwitchPort dte = new XotSwitchPort(new PortConfig { Id = 1, Mode = PortMode.Dte, Host = "127.0.0.1", Port = (ushort)peerPort, LciBase = 1, LciCount = 16 });
                int connected = 0;
                dte.CallConnected += (port, lci) => Interlocked.Increment(ref connected);
                Assert.That(dte.Start(), Is.True);
                Assert.That(WaitFor(() => dte.PeerConnected), Is.True);
                ushort lci = dte.PlaceCall(Called, Calling, null);
                Assert.That(lci, Is.Not.EqualTo(0));
                Assert.That(WaitFor(() => Volatile.Read(ref connected) == 1), Is.True, "the call was not connected; the peer saw: " + Dump(seen));

                // Our clear goes out on the channel the peer chose, not on ours.
                dte.ClearCall(lci, 0, 0);
                Assert.That(WaitFor(() =>
                {
                    lock (seen)
                    {
                        for (int i = 0; i < seen.Count; i++)
                        {
                            if (seen[i].Length >= 3 && seen[i][2] == 0x13)
                            {
                                return seen[i][1] == 1;
                            }
                        }
                    }

                    return false;
                }), Is.True, "the clear should use the peer's channel 1; the peer saw: " + Dump(seen));
            }
            finally
            {
                stop = true;
                listener.Stop();
            }
        }

        /// <summary>
        /// The same as the dialling case, on a LISTENING port: the peer opened the connection, the switch places a
        /// call on it, and the peer answers on another channel number. RFC 1613 sections 5 and 6: one connection
        /// carries one circuit, and the number "has no significance", whoever opened the connection.
        /// </summary>
        /// <remarks>
        /// Found 2026-11-17: SINTRAN's X.29 PAD called x25emu's x25tadsrv through this port. The call went out on
        /// channel 2 of the connection x25tadsrv had opened; x25tadsrv (the x25emu client renumbers every arriving
        /// call) answered with Call Accepted on channel 1 on that same connection. The rebinding existed only for a
        /// connection the port had dialled itself, so the acceptance was dropped and the caller waited for ever.
        /// </remarks>
        [Test]
        public void AListeningPort_ConnectsTheCall_WhenThePeerAnswersOnAnotherChannelNumber()
        {
            using XotSwitchPort dce = NewDce();
            int connected = 0;
            dce.CallConnected += (port, lci) => Interlocked.Increment(ref connected);

            using RawPeer peer = new RawPeer(dce.BoundPort);
            Assert.That(WaitFor(() => dce.PeerConnected), Is.True, "the peer's connection never reached the port");

            ushort lci = dce.PlaceCall(Called, Calling, null);
            Assert.That(lci, Is.Not.EqualTo(0), "the port did not place the call");
            Assert.That(peer.WaitForPacket(0x0B, out byte[] call), Is.True, "the Call Request never reached the peer");

            byte answer = (byte)(call[1] == 7 ? 9 : 7);
            peer.Send(X25PacketBuilder.CreateCallAccepted(answer, null));
            Assert.That(WaitFor(() => Volatile.Read(ref connected) == 1), Is.True,
                "the call was not connected; the peer saw: " + peer.Dump());

            // Later packets and the clear use the peer's number.
            Assert.That(dce.SendData(lci, new byte[] { 0x41 }, false, false, false), Is.EqualTo(PortSendResult.Sent));
            Assert.That(peer.WaitForData(out byte[] data), Is.True);
            Assert.That(data[1], Is.EqualTo(answer), "data must use the channel the peer answered on");

            dce.ClearCall(lci, 0, 0);
            Assert.That(peer.WaitForPacket(0x13, out byte[] clear), Is.True, "no clear reached the peer: " + peer.Dump());
            Assert.That(clear[1], Is.EqualTo(answer), "the clear must use the peer's channel");
        }

        /// <summary>
        /// A connection the peer opened is closed by the port once the call on it has ended, the same as a
        /// connection the port dialled. It is never used for another call.
        /// </summary>
        /// <remarks>
        /// RFC 1613 section 5: "A separate TCP connection MUST be used for each X.25 virtual circuit." x25emu
        /// (2026-11-18) follows this both ways: a connection that has carried a call is closed when the call ends and
        /// never carries another. Until 2026-11-18 this port kept a peer-opened connection after its call and put the
        /// next call on it; when the peer had already closed its end, that call was lost.
        /// </remarks>
        [Test]
        public void AListeningPort_ClosesAConnectionThePeerOpened_WhenItsCallEnds()
        {
            using XotSwitchPort dce = NewDce();
            int connected = 0;
            dce.CallConnected += (port, lci) => Interlocked.Increment(ref connected);

            using RawPeer peer = new RawPeer(dce.BoundPort);
            Assert.That(WaitFor(() => dce.PeerConnected), Is.True, "the peer's connection never reached the port");

            ushort lci = dce.PlaceCall(Called, Calling, null);
            Assert.That(lci, Is.Not.EqualTo(0), "the port did not place the call");
            Assert.That(peer.WaitForPacket(0x0B, out byte[] call), Is.True, "the Call Request never reached the peer");
            peer.Send(X25PacketBuilder.CreateCallAccepted(call[1], null));
            Assert.That(WaitFor(() => Volatile.Read(ref connected) == 1), Is.True, "the call was not connected: " + peer.Dump());

            // The peer ends the call; the port confirms and then closes the connection.
            peer.Send(X25PacketBuilder.CreateClearRequest(call[1], 0, 0));
            Assert.That(peer.WaitForPacket(0x17, out _), Is.True, "no Clear Confirmation reached the peer: " + peer.Dump());
            Assert.That(WaitFor(() => peer.IsClosed), Is.True, "the port kept the used connection open");
            Assert.That(WaitFor(() => dce.ConnectionCount == 0), Is.True, "the port still counts " + dce.ConnectionCount + " connection(s)");
        }

        /// <summary>
        /// A call going out of a listening port goes only on a connection that has never carried a call. A connection
        /// that carries a call already is not used for a second one: the call waits for no connection and is refused,
        /// and goes out as soon as the peer opens a fresh connection.
        /// </summary>
        [Test]
        public void AListeningPort_NeverPutsASecondCallOnAConnectionThatCarriesOne()
        {
            using XotSwitchPort dce = NewDce();
            dce.IncomingCall += (port, lci, called, calling, cud) => port.AcceptCall(lci);

            using RawPeer busy = new RawPeer(dce.BoundPort);
            busy.Send(X25PacketBuilder.CreateCallRequest(5, Called, Calling));
            Assert.That(busy.WaitForPacket(0x0F, out _), Is.True, "the peer's call was not accepted: " + busy.Dump());

            // With no fresh connection the call is held (see ACallWithNoFreshConnection_IsHeldWithoutBlocking_...),
            // never put on the busy one. Before 2026-11-21 it was refused here after a five-second wait.
            ushort held = dce.PlaceCall(Called, Calling, null);
            Assert.That(held, Is.Not.EqualTo(0), "the call is held, not refused");
            Thread.Sleep(200);

            // Read the dump, not WaitForPacket: that waits five seconds for a packet that must not come, and the held
            // call would be cleared meanwhile.
            Assert.That(busy.Dump().Contains("-0B-"), Is.False, "a Call Request went out on the busy connection: " + busy.Dump());

            using RawPeer spare = new RawPeer(dce.BoundPort);
            Assert.That(WaitFor(() => dce.ConnectionCount == 2), Is.True, "the spare never reached the port");
            Assert.That(spare.WaitForPacket(0x0B, out _), Is.True, "the held Call Request did not go out on the fresh connection: " + spare.Dump());
            Assert.That(busy.Dump().Contains("-0B-"), Is.False, "a Call Request went out on the busy connection: " + busy.Dump());
        }

        private static string Dump(List<byte[]> packets)
        {
            lock (packets)
            {
                List<string> lines = new List<string>();
                for (int i = 0; i < packets.Count; i++)
                {
                    lines.Add(BitConverter.ToString(packets[i]));
                }

                return string.Join(" | ", lines);
            }
        }

        private sealed class RawSink : IFrameTransport
        {
            private readonly NetworkStream _stream;

            public RawSink(NetworkStream stream)
            {
                _stream = stream;
            }

            public void Send(ReadOnlySpan<byte> bytes)
            {
                _stream.Write(bytes);
            }
        }
    }
}
