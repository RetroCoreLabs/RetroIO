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

using RetroIO.Network.Router;

using Xunit;

namespace RetroIO.Network.Nat.Tests
{
    public sealed class IpNatTests : IDisposable
    {
        private static readonly uint Loopback = Ipv4.Parse("127.0.0.1");

        private readonly IpRouter _router = new IpRouter(new RouterSettings());
        private readonly IpNat _nat;
        private readonly List<byte[]> _toCaller = new List<byte[]>();
        private readonly List<NatDropReason> _natDrops = new List<NatDropReason>();
        private readonly RouterLink _link;
        private readonly Socket _echo = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly ushort _echoPort;

        public IpNatTests()
        {
            _nat = new IpNat(_router, 0);
            _nat.OnDrop += (reason, count) => _natDrops.Add(reason);
            _link = _router.AddLink(p => _toCaller.Add(p.ToArray()));
            _echo.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _echo.ReceiveTimeout = 5000;
            _echoPort = (ushort)((IPEndPoint)_echo.LocalEndPoint!).Port;
        }

        public void Dispose()
        {
            _nat.Dispose();
            _echo.Close();
        }

        // RFC 1071 sum of 16-bit words, odd byte padded, folded.
        private static uint Sum(ReadOnlySpan<byte> data, uint sum)
        {
            int i = 0;
            for (; i + 1 < data.Length; i += 2)
            {
                sum += (uint)((data[i] << 8) | data[i + 1]);
            }

            if (i < data.Length)
            {
                sum += (uint)(data[i] << 8);
            }

            while ((sum >> 16) != 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }

            return sum;
        }

        private static uint Pseudo(uint source, uint destination, int length)
        {
            return (source >> 16) + (source & 0xFFFF) + (destination >> 16) + (destination & 0xFFFF) + 17 + (uint)length;
        }

        private static byte[] Udp(uint source, ushort sourcePort, uint destination, ushort destinationPort, byte[] data, bool checksum = true, ushort fragment = 0, byte protocol = 17)
        {
            byte[] p = new byte[28 + data.Length];
            p[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)p.Length);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(6), fragment);
            p[8] = 64;
            p[9] = protocol;
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(12), source);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), destination);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(10), (ushort)~Ipv4.Sum(p.AsSpan(0, 20)));
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(20), sourcePort);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(22), destinationPort);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(24), (ushort)(8 + data.Length));
            data.CopyTo(p.AsSpan(28));
            if (checksum)
            {
                ushort c = (ushort)~Sum(p.AsSpan(20), Pseudo(source, destination, 8 + data.Length));
                BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(26), c == 0 ? (ushort)0xFFFF : c);
            }

            return p;
        }

        // Runs the NAT until a packet reaches the caller, or five seconds pass.
        private void ServiceUntilReply(int count)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (_toCaller.Count < count && clock.ElapsedMilliseconds < 5000)
            {
                _nat.Service(1000, 100000);
            }
        }

        private (byte[] Data, IPEndPoint From) EchoReceive()
        {
            byte[] buffer = new byte[2048];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n = _echo.ReceiveFrom(buffer, ref from);
            return (buffer.AsSpan(0, n).ToArray(), (IPEndPoint)from);
        }

        [Fact]
        public void ADatagramGoesOutAndTheReplyComesBackToTheCaller()
        {
            byte[] hello = { 0x48, 0x45, 0x4C, 0x4C, 0x4F };
            _link.Receive(Udp(_link.Address, 4000, Loopback, _echoPort, hello));

            (byte[] data, IPEndPoint from) = EchoReceive();
            Assert.Equal(hello, data);
            Assert.Equal(1, _nat.UdpMappings);

            byte[] answer = { 1, 2, 3 };
            _echo.SendTo(answer, from);
            ServiceUntilReply(1);

            byte[] p = Assert.Single(_toCaller);
            Assert.True(Ipv4.TryCheckHeader(p, out int total));
            Assert.Equal(p.Length, total);
            Assert.Equal(17, p[9]);
            Assert.Equal(63, p[8]);
            Assert.Equal(Loopback, Ipv4.Source(p));
            Assert.Equal(_link.Address, Ipv4.Destination(p));
            Assert.Equal(_echoPort, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(20)));
            Assert.Equal(4000, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(22)));
            Assert.Equal(11, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(24)));
            Assert.Equal(answer, p.AsSpan(28).ToArray());
            Assert.Equal(0xFFFFu, Sum(p.AsSpan(20), Pseudo(Loopback, _link.Address, 11)));
        }

        [Fact]
        public void OneCallerPortKeepsOneMappingForEveryDestination()
        {
            using Socket second = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            second.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            second.ReceiveTimeout = 5000;
            ushort secondPort = (ushort)((IPEndPoint)second.LocalEndPoint!).Port;

            _link.Receive(Udp(_link.Address, 4000, Loopback, _echoPort, new byte[] { 1 }));
            _link.Receive(Udp(_link.Address, 4000, Loopback, secondPort, new byte[] { 2 }));

            (_, IPEndPoint fromFirst) = EchoReceive();
            byte[] buffer = new byte[16];
            EndPoint fromSecond = new IPEndPoint(IPAddress.Any, 0);
            second.ReceiveFrom(buffer, ref fromSecond);

            // RFC 4787 REQ-1: the same outside port for both destinations.
            Assert.Equal(fromFirst.Port, ((IPEndPoint)fromSecond).Port);
            Assert.Equal(1, _nat.UdpMappings);

            _link.Receive(Udp(_link.Address, 4001, Loopback, _echoPort, new byte[] { 3 }));
            Assert.Equal(2, _nat.UdpMappings);
        }

        [Fact]
        public void AnIdleMappingIsClosedAfterTwoMinutes()
        {
            _link.Receive(Udp(_link.Address, 4000, Loopback, _echoPort, new byte[] { 1 }));
            Assert.Equal(1, _nat.UdpMappings);

            _nat.Service(IpNat.UdpIdleMilliseconds - 1, 0);
            Assert.Equal(1, _nat.UdpMappings);

            _nat.Service(IpNat.UdpIdleMilliseconds, 0);
            Assert.Equal(0, _nat.UdpMappings);
        }

        [Fact]
        public void ANoChecksumDatagramIsSentAndABadChecksumIsDropped()
        {
            _link.Receive(Udp(_link.Address, 4000, Loopback, _echoPort, new byte[] { 7 }, checksum: false));
            Assert.Equal(new byte[] { 7 }, EchoReceive().Data);

            byte[] bad = Udp(_link.Address, 4001, Loopback, _echoPort, new byte[] { 8 });
            bad[27] ^= 0x01;
            _link.Receive(bad);

            Assert.Equal(new[] { NatDropReason.BadUdp }, _natDrops);
            Assert.Equal(1, _nat.UdpMappings);
        }

        [Fact]
        public void GreFragmentsAndMulticastAreDroppedAndCounted()
        {
            _link.Receive(Udp(_link.Address, 4000, Loopback, _echoPort, new byte[] { 1 }, protocol: 47));
            _link.Receive(Udp(_link.Address, 4000, Loopback, _echoPort, new byte[] { 1 }, fragment: 0x2000));
            _link.Receive(Udp(_link.Address, 4000, Ipv4.Parse("224.0.0.9"), 520, new byte[] { 1 }));

            Assert.Equal(new[] { NatDropReason.UnsupportedProtocol, NatDropReason.Fragment, NatDropReason.NotUnicast }, _natDrops);
            Assert.Equal(0, _nat.UdpMappings);
        }

        [Fact]
        public void TheRouterTakesUpstreamPacketsOnlyFromOutsideAndOnlyForInside()
        {
            List<DropReason> drops = new List<DropReason>();
            _router.OnDrop += (reason, count) => drops.Add(reason);

            _router.SendFromUpstream(Udp(Loopback, 53, _link.Address, 4000, new byte[] { 1 }));
            Assert.Single(_toCaller);

            _router.SendFromUpstream(Udp(Ipv4.Parse("10.42.0.9"), 53, _link.Address, 4000, new byte[] { 1 }));
            _router.SendFromUpstream(Udp(Loopback, 53, Ipv4.Parse("8.8.8.8"), 53, new byte[] { 1 }));
            _router.SendFromUpstream(Udp(Loopback, 53, _router.BroadcastAddress, 53, new byte[] { 1 }));

            Assert.Equal(new[] { DropReason.Spoofed, DropReason.NoRoute, DropReason.NoRoute }, drops);
            Assert.Single(_toCaller);
        }

        [Fact]
        public void AfterDisposeTheRouterHasNoUpstream()
        {
            List<DropReason> drops = new List<DropReason>();
            _router.OnDrop += (reason, count) => drops.Add(reason);
            _nat.Dispose();

            _link.Receive(Udp(_link.Address, 4000, Loopback, _echoPort, new byte[] { 1 }));

            Assert.Equal(new[] { DropReason.NoRoute }, drops);
        }
    }
}
