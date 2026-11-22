//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using RetroIO.Network.Nat;
using NUnit.Framework;

namespace RetroIO.Network.Nat.Tests
{
    /// <summary>
    /// The NAT's entry for a guest on a serial line (SLIP, later PPP): bare IP packets in through
    /// <see cref="NatEthernetBackend.SendPacket"/>, bare IP packets out through
    /// <see cref="NatEthernetBackend.OnPacketReceived"/>, never an Ethernet header either way.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class NatPacketEntryTests
    {
        private const uint Guest = 0x0A00020F;

        private const uint Gateway = 0x0A000202;

        private NatEthernetBackend _nat = null!;

        private List<byte[]> _packets = null!;

        private int _frames;

        [SetUp]
        public void SetUp()
        {
            _packets = new List<byte[]>();
            _frames = 0;
            _nat = new NatEthernetBackend();
            _nat.OnPacketReceived += (packet, offset, length) => _packets.Add(new ReadOnlySpan<byte>(packet, offset, length).ToArray());
            _nat.OnFrameReceived += (frame, offset, length) => _frames++;
            _nat.Start();
        }

        [TearDown]
        public void TearDown()
        {
            _nat.Stop();
        }

        [Test]
        public void Ping_AsABarePacket_GetsABareEchoReplyOfExactlyItsIpLength()
        {
            byte[] icmp = new byte[8 + 4];
            icmp[0] = 8;
            BinaryPrimitives.WriteUInt16BigEndian(icmp.AsSpan(6), 1);
            BinaryPrimitives.WriteUInt16BigEndian(icmp.AsSpan(2), NatPacket.Fold(NatPacket.Sum(icmp, 0)));
            SendPacket(NatPacket.ProtocolIcmp, Gateway, icmp);

            byte[] reply = WaitFor(p => p[9] == NatPacket.ProtocolIcmp);
            Assert.That(reply.Length, Is.EqualTo(20 + icmp.Length), "no Ethernet header and no padding");
            Assert.That(reply[0], Is.EqualTo(0x45), "starts with the IPv4 header");
            Assert.That(reply[20], Is.EqualTo(0), "echo reply");
            Assert.That(NatPacket.ReadAddress(reply.AsSpan(12)), Is.EqualTo(Gateway));
            Assert.That(NatPacket.ReadAddress(reply.AsSpan(16)), Is.EqualTo(Guest));
            Assert.That(_frames, Is.EqualTo(0), "nothing went out as an Ethernet frame");
        }

        [Test]
        public void Tcp_AsBarePackets_ReachesAHostListenerAndBack()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                ushort port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
                SendTcp(port, 100, 0, NatPacket.TcpSyn, Array.Empty<byte>(), 536);
                byte[] synAck = WaitFor(p => p[9] == NatPacket.ProtocolTcp && (p[33] & 0x1F) == (NatPacket.TcpSyn | NatPacket.TcpAck));
                uint server = BinaryPrimitives.ReadUInt32BigEndian(synAck.AsSpan(24)) + 1;
                SendTcp(port, 101, server, NatPacket.TcpAck, Encoding.ASCII.GetBytes("ping"), 0);

                Stopwatch clock = Stopwatch.StartNew();
                while (!listener.Pending())
                {
                    Assert.That(clock.ElapsedMilliseconds, Is.LessThan(5000), "the host listener got the connection");
                    _nat.Poll();
                    Thread.Sleep(2);
                }
                using TcpClient accepted = listener.AcceptTcpClient();
                NetworkStream stream = accepted.GetStream();
                stream.ReadTimeout = 5000;
                byte[] got = new byte[4];
                int read = 0;
                while (read < 4)
                {
                    int n = stream.Read(got, read, 4 - read);
                    Assert.That(n, Is.GreaterThan(0));
                    read += n;
                }
                Assert.That(Encoding.ASCII.GetString(got), Is.EqualTo("ping"));
                stream.Write(Encoding.ASCII.GetBytes("pong"), 0, 4);
                byte[] data = WaitFor(p => p[9] == NatPacket.ProtocolTcp && BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2)) == 20 + 20 + 4);
                Assert.That(Encoding.ASCII.GetString(data, 40, 4), Is.EqualTo("pong"));
                Assert.That(_frames, Is.EqualTo(0), "nothing went out as an Ethernet frame");
            }
            finally
            {
                listener.Stop();
            }
        }

        private void SendPacket(byte protocol, uint destination, byte[] payload)
        {
            byte[] packet = new byte[20 + payload.Length];
            payload.CopyTo(packet, 20);
            NatPacket.WriteIpv4(packet, packet.Length, 1, protocol, Guest, destination);
            _nat.SendPacket(packet, 0, packet.Length);
        }

        private void SendTcp(ushort port, uint seq, uint ack, int flags, byte[] data, ushort mss)
        {
            int header = mss != 0 ? 24 : 20;
            byte[] tcp = new byte[header + data.Length];
            data.CopyTo(tcp, header);
            NatPacket.WriteTcp(tcp, header, Guest, Gateway, 41000, port, seq, ack, (byte)flags, 4096, mss);
            SendPacket(NatPacket.ProtocolTcp, Gateway, tcp);
        }

        private byte[] WaitFor(Predicate<byte[]> match)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 5000)
            {
                _nat.Poll();
                for (int i = 0; i < _packets.Count; i++)
                {
                    if (match(_packets[i]))
                    {
                        byte[] p = _packets[i];
                        _packets.RemoveAt(i);
                        return p;
                    }
                }
                Thread.Sleep(2);
            }
            Assert.Fail("no matching packet from the NAT within 5 s; got " + _packets.Count + " others and " + _frames + " Ethernet frames");
            return Array.Empty<byte>();
        }
    }
}
