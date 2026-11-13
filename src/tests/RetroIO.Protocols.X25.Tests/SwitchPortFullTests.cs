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
using RetroIO.Protocols.X25.Client;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;
using ClientVc = RetroIO.Protocols.X25.Client.X25Vc;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// A switch port that has run out of channels, seen from a caller that sends more calls than the
    /// port has channels.
    /// </summary>
    /// <remarks>
    /// Found with <c>x25traffic -C</c>: a port with 64 channels connected 64 of 200 calls, and the other
    /// 136 got no answer at all and hung. The port answered the 65th call with a Diagnostic on channel
    /// 0, which a caller does not take as the answer to a call. X.25 has the network clear such a call
    /// with cause 5, network congestion (Table 5-7).
    /// </remarks>
    [TestFixture]
    public class SwitchPortFullTests
    {
        private const int Channels = 3;
        private const int Calls = 5;

        private SwitchNode _node = null!;
        private XotSwitchPort _portA = null!;
        private XotSwitchPort _portB = null!;

        [SetUp]
        public void SetUp()
        {
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340" };
            cfg.Routing.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = "2342", PrefixLen = 4, DestPort = 1 });

            _node = new SwitchNode(cfg);
            _portA = new XotSwitchPort(new PortConfig { Id = 0, Mode = PortMode.Dce, Port = 0, LciBase = 1, LciCount = Channels });
            _portB = new XotSwitchPort(new PortConfig { Id = 1, Mode = PortMode.Dce, Port = 0, LciBase = 1, LciCount = 64 });
            Assert.That(_portA.Start(), Is.True);
            Assert.That(_portB.Start(), Is.True);
            _node.AddPort(_portA);
            _node.AddPort(_portB);
            new SwitchCore(_node);
        }

        [TearDown]
        public void TearDown()
        {
            _portA?.Dispose();
            _portB?.Dispose();
        }

        private static void WaitFor(Func<bool> condition, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (!condition() && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(10);
            }
        }

        private static X25Client Dial(ushort port, string localAddress)
        {
            X25Client client = new X25Client { ServiceName = "full-port-test" };
            client.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = port,
                LocalAddress = localAddress,
                Framing = X25ClientFraming.Xot,
            }, NullLogger.Instance);

            return client;
        }

        /// <summary>
        /// With three channels and five calls on one connection: three calls connect, the other two
        /// are cleared at once with cause 5, and the port writes no Diagnostic.
        /// </summary>
        [Test]
        public void MoreCallsThanChannels_TheExtraCallsAreClearedWithNetworkCongestion()
        {
            object gate = new object();
            int diagnostics = 0;
            _portA.PacketCaptured += (id, inbound, packet) =>
            {
                // Packet type 0xF1 is a Diagnostic (Table 5-2/5-3).
                if (!inbound && packet.Length >= 3 && packet[2] == 0xF1)
                {
                    Interlocked.Increment(ref diagnostics);
                }
            };

            using X25Client callee = Dial(_portB.BoundPort, "23420001");
            callee.SetOnIncoming((c, vc) => c.Accept(vc));

            // The caller is a bare TCP connection with XOT frames, because X25Client now opens one connection for
            // each call (RFC 1613 section 5) and this test is about many circuits on ONE connection: the port
            // has three channels and the connection asks for five.
            using System.Net.Sockets.TcpClient raw = new System.Net.Sockets.TcpClient();
            raw.Connect(System.Net.IPAddress.Loopback, _portA.BoundPort);
            System.Net.Sockets.NetworkStream stream = raw.GetStream();
            WaitFor(() => _portA.PeerConnected && _portB.PeerConnected, 3000);

            for (int i = 0; i < Calls; i++)
            {
                byte[] packet = RetroIO.Protocols.X25.Models.X25PacketBuilder.CreateCallRequest((ushort)(i + 1), "23420001", "23400001");
                byte[] frame = new byte[4 + packet.Length];
                frame[2] = (byte)(packet.Length >> 8);
                frame[3] = (byte)packet.Length;
                Array.Copy(packet, 0, frame, 4, packet.Length);
                stream.Write(frame, 0, frame.Length);
            }

            // Read what the port answers: Call Accepted (0x0F) for the calls it took, Clear Request (0x13) with
            // its cause for the others.
            List<byte> causes = new List<byte>();
            int connected = 0;
            long readDeadline = Environment.TickCount64 + 3000;
            stream.ReadTimeout = 200;
            byte[] header = new byte[4];
            while (Environment.TickCount64 < readDeadline && connected + causes.Count < Calls)
            {
                try
                {
                    if (stream.Read(header, 0, 4) < 4)
                    {
                        break;
                    }

                    int length = (header[2] << 8) | header[3];
                    byte[] body = new byte[length];
                    int got = 0;
                    while (got < length)
                    {
                        int n = stream.Read(body, got, length - got);
                        if (n <= 0)
                        {
                            break;
                        }

                        got += n;
                    }

                    if (body[2] == 0x0F)
                    {
                        connected++;
                    }
                    else if (body[2] == 0x13)
                    {
                        causes.Add(body[3]);
                        // Confirm the clear, as a caller does.
                        byte[] confirm = { 0, 0, 0, 3, (byte)(0x10 | (body[0] & 0x0F)), body[1], 0x17 };
                        stream.Write(confirm, 0, confirm.Length);
                    }
                }
                catch (System.IO.IOException)
                {
                    // read timeout: try again until the deadline
                }
            }

            Assert.That(connected, Is.EqualTo(Channels), "the port has three channels");
            Assert.That(causes.Count, Is.EqualTo(Calls - Channels), "each call over the limit must be cleared, not left hanging");
            for (int i = 0; i < causes.Count; i++)
            {
                Assert.That(causes[i], Is.EqualTo(0x05), "cause 5: network congestion");
            }

            // Let the caller's clear confirmations arrive, then check the port answered none of them
            // with a Diagnostic on an unassigned channel.
            Thread.Sleep(300);
            Assert.That(Volatile.Read(ref diagnostics), Is.EqualTo(0), "no Diagnostic: the refused calls were answered with clears");
        }
    }
}
