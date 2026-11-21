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
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Client;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Runtime;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// A call that comes in on one line of the runtime and whose route points at another line is joined to a call
    /// the runtime places on that line: the two calls are tied together and carry each other's data, clears, resets
    /// and interrupts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Decided by Ronny 2026-11-21 (RetroIO BUGS 32): the runtime joins the two lines itself, instead of handing such
    /// calls to the switch or refusing them. Until then an incoming call was delivered to a local service or refused,
    /// whatever line its route named.
    /// </para>
    /// <para>
    /// The rig: line A is an XOT server entry the test calls into with <see cref="X25Client"/>; line B is an XOT peer
    /// entry that dials a plain TCP listener in this file. The listener reads and writes the XOT header (RFC 1613
    /// section 4: version 0, then the length of the packet, two octets each) and the X.25 packets as bytes written
    /// out from X.25 (10/96) clause 5 here, so the runtime is not graded against its own packet code:
    /// </para>
    ///  - Call Request, type octet 0x0B; Call Connected 0x0F (Table 5-1).
    ///  - Clear Request 0x13 with cause and diagnostic, Clear Confirmation 0x17.
    ///  - A data packet has bit 1 of the type octet clear; P(R) in bits 8 to 6, M in bit 5, P(S) in bits 4 to 2
    ///    (5.4.1, modulo 8). Receive Ready is 0x01 with P(R) in bits 8 to 6.
    /// </remarks>
    [TestFixture]
    public class X25CallJoinTests
    {
        private const string LocalAddress = "23430200";
        private const string FarAddress = "55512";

        private ProtocolRuntime? _runtime;
        private X25Client? _client;
        private TcpListener? _farListener;
        private TcpClient? _far;

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _far?.Dispose();
            _farListener?.Stop();
            _farListener?.Dispose();
            _runtime?.Dispose();
        }

        private static int FreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        /// <summary>
        /// Starts the runtime with line A (XOT server, the test calls into it) and line B (XOT peer, dials the far
        /// listener), and a route that sends calls to 555 out of line B.
        /// </summary>
        /// <returns>
        /// The port of line A.
        /// </returns>
        private int Start()
        {
            int portA = FreePort();
            _farListener = new TcpListener(IPAddress.Loopback, 0);
            _farListener.Start();
            int farPort = ((IPEndPoint)_farListener.LocalEndpoint).Port;

            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = LocalAddress;
            config.Routing.Enabled = true;
            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "A",
                Enabled = true,
                Mode = XotMode.Server,
                LocalPort = portA,
                RemoteMachineId = "23430001",
            });
            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "B",
                Enabled = true,
                Mode = XotMode.Both,
                LocalPort = FreePort(),
                RemoteIPAddress = "127.0.0.1",
                RemotePort = farPort,
                RemoteMachineId = FarAddress,
            });
            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "ToB",
                X121AddressPattern = "^555",
                Type = RetroIO.Protocols.X25.Runtime.RouteType.Interface,
                Destination = "B",
                Priority = 100,
                Enabled = true,
            });

            _runtime = new ProtocolRuntime(NullLogger.Instance, config);
            Assert.That(_runtime.StartTransportInterfaces(), Is.True, "both lines should start");

            _client = new X25Client { ServiceName = "caller" };
            _client.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = (ushort)portA,
                LocalAddress = "23430001",
                Framing = X25ClientFraming.Xot,
            }, NullLogger.Instance);
            return portA;
        }

        // ------------------------------------------------------------------ the far end, by hand

        private static void WriteXot(NetworkStream stream, byte[] packet)
        {
            byte[] frame = new byte[4 + packet.Length];
            frame[2] = (byte)(packet.Length >> 8);
            frame[3] = (byte)packet.Length;
            Array.Copy(packet, 0, frame, 4, packet.Length);
            stream.Write(frame, 0, frame.Length);
        }

        private static byte[] ReadExactly(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int got = 0;
            while (got < count)
            {
                int n = stream.Read(buffer, got, count - got);
                if (n == 0)
                {
                    throw new AssertionException("the runtime closed the connection to the far end");
                }

                got += n;
            }

            return buffer;
        }

        private static byte[] ReadXot(NetworkStream stream)
        {
            byte[] header = ReadExactly(stream, 4);
            Assert.That(header[0] == 0 && header[1] == 0, Is.True, "RFC 1613 section 4: XOT version 0");
            return ReadExactly(stream, (header[2] << 8) | header[3]);
        }

        /// <summary>
        /// Reads packets until one of the wanted kind arrives; Receive Ready packets (flow control only) are skipped.
        /// </summary>
        private static byte[] ReadUntil(NetworkStream stream, Func<byte, bool> wanted)
        {
            for (int i = 0; i < 20; i++)
            {
                byte[] packet = ReadXot(stream);
                if (wanted(packet[2]))
                {
                    return packet;
                }
            }

            throw new AssertionException("the wanted packet never came");
        }

        private static bool IsData(byte type) => (type & 0x01) == 0;

        /// <summary>
        /// Waits for line B to dial the far listener, and reads the Call Request it sends.
        /// </summary>
        private byte[] AcceptFarCall()
        {
            _farListener!.Server.ReceiveTimeout = 5000;
            IAsyncResult pending = _farListener.BeginAcceptTcpClient(null, null);
            Assert.That(pending.AsyncWaitHandle.WaitOne(5000), Is.True, "line B must dial the far end for the joined call");
            _far = _farListener.EndAcceptTcpClient(pending);
            _far.ReceiveTimeout = 5000;
            byte[] call = ReadUntil(_far.GetStream(), t => t == 0x0B);
            return call;
        }

        // ------------------------------------------------------------------ tests

        [Test]
        public void ACallRoutedToAnotherLine_IsPlacedThere_AndConnectsWhenTheFarEndAccepts()
        {
            Start();
            ManualResetEventSlim connected = new ManualResetEventSlim(false);
            _client!.SetOnConnected((c, vc) => connected.Set());

            Assert.That(_client.Call(FarAddress), Is.Not.Null);
            byte[] call = AcceptFarCall();

            // X.25 5.2.1: after the type octet, the address lengths (called in bits 4 to 1), then the BCD digits.
            int calledLength = call[3] & 0x0F;
            StringBuilder digits = new StringBuilder();
            for (int i = 0; i < calledLength; i++)
            {
                byte octet = call[4 + (i / 2)];
                digits.Append((char)('0' + ((i % 2) == 0 ? octet >> 4 : octet & 0x0F)));
            }

            Assert.That(digits.ToString(), Is.EqualTo(FarAddress), "the joined call goes out with the called address");
            Assert.That(connected.IsSet, Is.False, "the caller is not connected before the far end answers");

            // Call Connected on the channel of the Call Request.
            WriteXot(_far!.GetStream(), new byte[] { call[0], call[1], 0x0F });
            Assert.That(connected.Wait(5000), Is.True, "the far end's Call Connected connects the caller");
        }

        [Test]
        public void DataCrossesTheJoinBothWays_AndAClearFromTheFarEndClearsTheCaller()
        {
            Start();
            ManualResetEventSlim connected = new ManualResetEventSlim(false);
            ManualResetEventSlim gotData = new ManualResetEventSlim(false);
            ManualResetEventSlim cleared = new ManualResetEventSlim(false);
            List<string> received = new List<string>();
            int clearCause = -1;
            X25Vc? callerVc = null;
            _client!.SetOnConnected((c, vc) => { callerVc = vc; connected.Set(); });
            _client.SetOnData((c, vc, data, length, q, m, d) =>
            {
                lock (received)
                {
                    received.Add(Encoding.ASCII.GetString(data, 0, length));
                }

                gotData.Set();
            });
            _client.SetOnClear((c, vc, cause, diag) => { clearCause = cause; cleared.Set(); });

            Assert.That(_client.Call(FarAddress), Is.Not.Null);
            byte[] call = AcceptFarCall();
            NetworkStream far = _far!.GetStream();
            WriteXot(far, new byte[] { call[0], call[1], 0x0F });
            Assert.That(connected.Wait(5000), Is.True);

            // Caller to far end.
            Assert.That(_client.SendText(callerVc!, "HELLO"), Is.True);
            byte[] data = ReadUntil(far, IsData);
            Assert.That(Encoding.ASCII.GetString(data, 3, data.Length - 3), Is.EqualTo("HELLO"));

            // Far end to caller: P(S) 0, P(R) 1 (it has the caller's packet 0), M 0.
            byte[] world = Encoding.ASCII.GetBytes("WORLD");
            byte[] packet = new byte[3 + world.Length];
            packet[0] = call[0];
            packet[1] = call[1];
            packet[2] = 0x20;
            Array.Copy(world, 0, packet, 3, world.Length);
            WriteXot(far, packet);
            Assert.That(gotData.Wait(5000), Is.True, "the far end's data reaches the caller");
            lock (received)
            {
                Assert.That(string.Concat(received), Is.EqualTo("WORLD"));
            }

            // The far end clears, DTE originated (cause 0); the caller is cleared, and line B confirms.
            WriteXot(far, new byte[] { call[0], call[1], 0x13, 0x00, 0x00 });
            Assert.That(cleared.Wait(5000), Is.True, "a clear from the far end clears the caller");
            Assert.That(ReadUntil(far, t => t == 0x17)[2], Is.EqualTo(0x17), "line B confirms the far end's clear");
        }

        [Test]
        public void ACallTheFarEndRefuses_IsClearedToTheCallerWithTheFarEndsCause()
        {
            Start();
            ManualResetEventSlim cleared = new ManualResetEventSlim(false);
            bool connected = false;
            int clearCause = -1;
            int clearDiagnostic = -1;
            _client!.SetOnConnected((c, vc) => connected = true);
            _client.SetOnClear((c, vc, cause, diag) => { clearCause = cause; clearDiagnostic = diag; cleared.Set(); });

            Assert.That(_client.Call(FarAddress), Is.Not.Null);
            byte[] call = AcceptFarCall();

            // The far end refuses: Clear Request, DTE originated cause 0, diagnostic 0x41 (65, facility code not
            // allowed - any diagnostic will do; it must arrive unchanged).
            WriteXot(_far!.GetStream(), new byte[] { call[0], call[1], 0x13, 0x00, 0x41 });
            Assert.That(cleared.Wait(5000), Is.True, "the caller is told the call is refused");
            Assert.That(connected, Is.False);
            Assert.That(clearCause, Is.EqualTo(0x00), "X.25 Table 5-7: a clear the called DTE started reaches the caller as DTE originated");
            Assert.That(clearDiagnostic, Is.EqualTo(0x41), "the far end's diagnostic goes through unchanged");
        }

        [Test]
        public void TheCallerClearing_ClearsTheFarEnd()
        {
            Start();
            ManualResetEventSlim connected = new ManualResetEventSlim(false);
            X25Vc? callerVc = null;
            _client!.SetOnConnected((c, vc) => { callerVc = vc; connected.Set(); });

            Assert.That(_client.Call(FarAddress), Is.Not.Null);
            byte[] call = AcceptFarCall();
            NetworkStream far = _far!.GetStream();
            WriteXot(far, new byte[] { call[0], call[1], 0x0F });
            Assert.That(connected.Wait(5000), Is.True);

            _client.Clear(callerVc!, 0x00, 0x00);
            byte[] clear = ReadUntil(far, t => t == 0x13);
            Assert.That(clear[2], Is.EqualTo(0x13), "line B clears the far end when the caller clears");
        }

        /// <summary>
        /// Connects a joined call with the client on line A and returns the far end's Call Request.
        /// </summary>
        private byte[] ConnectJoinedCall(out X25Vc callerVc)
        {
            ManualResetEventSlim connected = new ManualResetEventSlim(false);
            X25Vc? vc = null;
            _client!.SetOnConnected((c, v) => { vc = v; connected.Set(); });
            Assert.That(_client.Call(FarAddress), Is.Not.Null);
            byte[] call = AcceptFarCall();
            WriteXot(_far!.GetStream(), new byte[] { call[0], call[1], 0x0F });
            Assert.That(connected.Wait(5000), Is.True);
            callerVc = vc!;
            return call;
        }

        [Test]
        public void AResetFromTheFarEnd_ResetsTheCaller_AndLineBConfirmsIt()
        {
            Start();
            ManualResetEventSlim reset = new ManualResetEventSlim(false);
            int resetDiagnostic = -1;
            _client!.SetOnReset((c, vc, cause, diag) => { resetDiagnostic = diag; reset.Set(); });
            byte[] call = ConnectJoinedCall(out _);
            NetworkStream far = _far!.GetStream();

            // Reset Indication (type 0x1B), cause 0 (DTE originated), diagnostic 0x24 - it must arrive unchanged.
            WriteXot(far, new byte[] { call[0], call[1], 0x1B, 0x00, 0x24 });
            Assert.That(ReadUntil(far, t => t == 0x1F)[2], Is.EqualTo(0x1F), "line B confirms the reset");
            Assert.That(reset.Wait(5000), Is.True, "X.25 4.4.3: the reset reaches the caller");
            Assert.That(resetDiagnostic, Is.EqualTo(0x24));
        }

        [Test]
        public void ALongSequence_FromTheFarEnd_ReachesTheCallerAsOneSequence()
        {
            Start();
            List<byte> received = new List<byte>();
            List<bool> moreBits = new List<bool>();
            ManualResetEventSlim last = new ManualResetEventSlim(false);
            _client!.SetOnData((c, vc, data, length, q, m, d) =>
            {
                lock (received)
                {
                    for (int i = 0; i < length; i++)
                    {
                        received.Add(data[i]);
                    }

                    moreBits.Add(m);
                }

                if (!m)
                {
                    last.Set();
                }
            });
            byte[] call = ConnectJoinedCall(out _);
            NetworkStream far = _far!.GetStream();

            // X.25 4.3.5: two full packets (128, the default size) with M = 1, then 10 octets with M = 0.
            byte[] message = new byte[266];
            for (int i = 0; i < message.Length; i++)
            {
                message[i] = (byte)i;
            }

            WriteXot(far, DataPacket(call, ps: 0, more: true, message, 0, 128));
            WriteXot(far, DataPacket(call, ps: 1, more: true, message, 128, 128));

            // Window 2: the third packet waits for line B's Receive Ready with P(R) = 2 (0x41).
            ReadUntil(far, t => t == 0x41);
            WriteXot(far, DataPacket(call, ps: 2, more: false, message, 256, 10));

            Assert.That(last.Wait(5000), Is.True, "the end of the sequence reaches the caller");
            lock (received)
            {
                Assert.That(received.ToArray(), Is.EqualTo(message), "every octet, in order");
                Assert.That(moreBits, Is.EqualTo(new List<bool> { true, true, false }), "one sequence: M = 1, 1, then 0");
            }
        }

        private static byte[] DataPacket(byte[] call, int ps, bool more, byte[] source, int offset, int length)
        {
            byte[] packet = new byte[3 + length];
            packet[0] = call[0];
            packet[1] = call[1];
            packet[2] = (byte)((more ? 0x10 : 0x00) | (ps << 1));
            Array.Copy(source, offset, packet, 3, length);
            return packet;
        }

        [Test]
        public void AnInterruptFromTheCaller_ReachesTheFarEnd()
        {
            int portA = Start();
            _client!.Dispose();
            _client = null;

            // A caller written out by hand on line A, so it can send an interrupt (X25Client has none). Call Request
            // (5.2.1): GFI and channel 1, type 0x0B, address lengths (calling 0, called 5), the called digits in BCD,
            // facility length 0.
            using TcpClient caller = new TcpClient();
            caller.Connect(IPAddress.Loopback, portA);
            caller.ReceiveTimeout = 5000;
            NetworkStream a = caller.GetStream();
            WriteXot(a, new byte[] { 0x10, 0x01, 0x0B, 0x05, 0x55, 0x51, 0x20, 0x00 });

            byte[] call = AcceptFarCall();
            NetworkStream far = _far!.GetStream();
            WriteXot(far, new byte[] { call[0], call[1], 0x0F });
            Assert.That(ReadUntil(a, t => t == 0x0F)[2], Is.EqualTo(0x0F), "the hand-written caller is connected");

            // Interrupt (4.4.2, type 0x23) with one octet of user data.
            WriteXot(a, new byte[] { 0x10, 0x01, 0x23, 0x7F });
            Assert.That(ReadUntil(a, t => t == 0x27)[2], Is.EqualTo(0x27), "line A confirms the caller's interrupt");
            byte[] interrupt = ReadUntil(far, t => t == 0x23);
            Assert.That(interrupt[3], Is.EqualTo(0x7F), "the interrupt reaches the far end with its data");
        }

        [Test]
        public void AFarLineThatCannotBeDialled_ClearsTheCallerOutOfOrder()
        {
            Start();
            int farPort = ((IPEndPoint)_farListener!.LocalEndpoint).Port;
            _farListener.Stop();
            ManualResetEventSlim cleared = new ManualResetEventSlim(false);
            int clearCause = -1;
            _client!.SetOnClear((c, vc, cause, diag) => { clearCause = cause; cleared.Set(); });

            Assert.That(_client.Call(FarAddress), Is.Not.Null);
            Assert.That(cleared.Wait(15000), Is.True, "the caller is told when the far line cannot be dialled (port " + farPort + " closed)");
            Assert.That(clearCause, Is.EqualTo(0x09), "X.25 Table 5-7: out of order");
        }
    }
}
