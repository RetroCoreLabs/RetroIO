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

using NUnit.Framework;

using RetroIO.Network.Router;

namespace RetroIO.Network.Nat.Tests
{
    /// <summary>
    /// A dial-up caller on <see cref="IpRouter"/> reaches the host through the NAT: the router's
    /// upstream side feeds <see cref="NatEthernetBackend.SendPacket"/>, and the NAT's bare packets
    /// go back through <see cref="IpRouter.SendFromUpstream"/>.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class RouterNatTests
    {
        private static readonly uint Gateway = Ipv4.Parse("10.0.2.2");

        private IpRouter _router = null!;
        private NatEthernetBackend _nat = null!;
        private RouterLink _link = null!;
        private List<byte[]> _toCaller = null!;
        private Socket _echo = null!;

        [SetUp]
        public void SetUp()
        {
            // The router's subnet is the NAT's (10.0.2.0/24); the NAT's gateway and name server
            // are its upstream addresses.
            _router = new IpRouter(new RouterSettings
            {
                Network = Ipv4.Parse("10.0.2.0"),
                RouterAddress = Ipv4.Parse("10.0.2.1"),
                PoolFirst = Ipv4.Parse("10.0.2.15"),
                UpstreamAddresses = new[] { Gateway, Ipv4.Parse("10.0.2.3") },
            });
            _nat = new NatEthernetBackend();
            _toCaller = new List<byte[]>();
            _router.OnUpstream += p => _nat.SendPacket(p.ToArray(), 0, p.Length);
            _nat.OnPacketReceived += (packet, offset, length) => _router.SendFromUpstream(packet.AsSpan(offset, length));
            _link = _router.AddLink(p => _toCaller.Add(p.ToArray()));
            _nat.Start();
            _echo = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _echo.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _echo.ReceiveTimeout = 5000;
        }

        [TearDown]
        public void TearDown()
        {
            _nat.Stop();
            _echo.Close();
        }

        [Test]
        public void ADatagramToTheGatewayReachesTheHostAndTheReplyReachesTheCaller()
        {
            ushort port = (ushort)((IPEndPoint)_echo.LocalEndPoint!).Port;
            Assert.That(_link.Address, Is.EqualTo(Ipv4.Parse("10.0.2.15")));

            _link.Receive(Udp(_link.Address, 4000, Gateway, port, new byte[] { 0x48, 0x49 }));

            byte[] buffer = new byte[64];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n = _echo.ReceiveFrom(buffer, ref from);
            Assert.That(buffer.AsSpan(0, n).ToArray(), Is.EqualTo(new byte[] { 0x48, 0x49 }));

            _echo.SendTo(new byte[] { 1, 2, 3 }, from);
            Stopwatch clock = Stopwatch.StartNew();
            while (_toCaller.Count == 0 && clock.ElapsedMilliseconds < 5000)
            {
                _nat.Poll();
            }

            Assert.That(_toCaller, Has.Count.EqualTo(1));
            byte[] reply = _toCaller[0];
            Assert.That(Ipv4.TryCheckHeader(reply, out _), Is.True);
            Assert.That(Ipv4.Source(reply), Is.EqualTo(Gateway), "the host is seen at the gateway address");
            Assert.That(Ipv4.Destination(reply), Is.EqualTo(_link.Address));
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(22)), Is.EqualTo(4000));
            Assert.That(reply.AsSpan(28).ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        }

        [Test]
        public void TheRouterNeverGivesAnUpstreamAddressToALink()
        {
            Assert.Throws<ArgumentException>(() => _router.AddLink(p => { }, Gateway));
        }

        // An IPv4 UDP packet with correct checksums.
        private static byte[] Udp(uint source, ushort sourcePort, uint destination, ushort destinationPort, byte[] data)
        {
            byte[] p = new byte[28 + data.Length];
            p[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)p.Length);
            p[8] = 64;
            p[9] = 17;
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(12), source);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), destination);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(10), (ushort)~Ipv4.Sum(p.AsSpan(0, 20)));
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(20), sourcePort);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(22), destinationPort);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(24), (ushort)(8 + data.Length));
            data.CopyTo(p.AsSpan(28));
            uint sum = (source >> 16) + (source & 0xFFFF) + (destination >> 16) + (destination & 0xFFFF) + 17 + (uint)(8 + data.Length);
            for (int i = 20; i + 1 < p.Length; i += 2)
            {
                sum += (uint)((p[i] << 8) | p[i + 1]);
            }

            if ((p.Length & 1) != 0)
            {
                sum += (uint)(p[p.Length - 1] << 8);
            }

            while ((sum >> 16) != 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }

            ushort c = (ushort)~sum;
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(26), c == 0 ? (ushort)0xFFFF : c);
            return p;
        }
    }
}
