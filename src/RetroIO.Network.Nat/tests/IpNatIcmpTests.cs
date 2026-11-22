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
    public sealed class IpNatIcmpTests : IDisposable
    {
        private static readonly uint Loopback = Ipv4.Parse("127.0.0.1");

        private readonly IpRouter _router = new IpRouter(new RouterSettings());
        private readonly IpNat _nat;
        private readonly List<byte[]> _toCaller = new List<byte[]>();
        private readonly RouterLink _link;

        public IpNatIcmpTests()
        {
            _nat = new IpNat(_router, 0);
            _link = _router.AddLink(p => _toCaller.Add(p.ToArray()));
        }

        public void Dispose()
        {
            _nat.Dispose();
        }

        private static ushort Checksum(ReadOnlySpan<byte> data)
        {
            uint sum = 0;
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

            return (ushort)~sum;
        }

        private byte[] Icmp(byte type, ushort id, ushort seq, byte[] data, bool goodChecksum = true)
        {
            byte[] p = new byte[28 + data.Length];
            p[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)p.Length);
            p[8] = 64;
            p[9] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(12), _link.Address);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), Loopback);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(10), (ushort)~Ipv4.Sum(p.AsSpan(0, 20)));
            p[20] = type;
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(24), id);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(26), seq);
            data.CopyTo(p.AsSpan(28));
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(22), Checksum(p.AsSpan(20)));
            if (!goodChecksum)
            {
                p[23] ^= 0x01;
            }

            return p;
        }

        private void ServiceUntil(int count)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (_toCaller.Count < count && clock.ElapsedMilliseconds < 8000)
            {
                _nat.Service(0, 50000);
            }
        }

        [Fact]
        public void AnEchoRequestGetsTheCallersIdentifierSequenceAndDataBack()
        {
            byte[] data = { 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68 };
            _link.Receive(Icmp(8, 0x1234, 7, data));
            Assert.Equal(1, _nat.PingsWaiting);

            ServiceUntil(1);

            byte[] p = Assert.Single(_toCaller);
            Assert.True(Ipv4.TryCheckHeader(p, out _));
            Assert.Equal(1, p[9]);
            Assert.Equal(Loopback, Ipv4.Source(p));
            Assert.Equal(_link.Address, Ipv4.Destination(p));
            Assert.Equal(0, p[20]);
            Assert.Equal(0, p[21]);
            Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(24)));
            Assert.Equal(7, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(26)));
            Assert.Equal(data, p.AsSpan(28).ToArray());
            Assert.Equal(0, Checksum(p.AsSpan(20)));
            Assert.Equal(0, _nat.PingsWaiting);
        }

        [Fact]
        public void ABadChecksumAndOtherIcmpTypesAreDropped()
        {
            _link.Receive(Icmp(8, 1, 1, new byte[4], goodChecksum: false));
            _link.Receive(Icmp(13, 1, 1, new byte[12]));

            Assert.Equal(1, _nat.DropCount(NatDropReason.BadIcmp));
            Assert.Equal(1, _nat.DropCount(NatDropReason.UnsupportedProtocol));
            Assert.Equal(0, _nat.PingsWaiting);
        }

        [Fact]
        public void AHostPortUnreachableBecomesAnIcmpErrorQuotingTheDatagram()
        {
            Socket closed = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            closed.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            ushort port = (ushort)((IPEndPoint)closed.LocalEndPoint!).Port;
            closed.Close();

            byte[] udp = new byte[30];
            udp[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(2), 30);
            udp[8] = 64;
            udp[9] = 17;
            BinaryPrimitives.WriteUInt32BigEndian(udp.AsSpan(12), _link.Address);
            BinaryPrimitives.WriteUInt32BigEndian(udp.AsSpan(16), Loopback);
            BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(10), (ushort)~Ipv4.Sum(udp.AsSpan(0, 20)));
            BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(20), 6000);
            BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(22), port);
            BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(24), 10);
            _link.Receive(udp);
            ServiceUntil(1);

            if (!OperatingSystem.IsWindows())
            {
                // Only Windows reports it to an unconnected socket (see HostPortUnreachable).
                Assert.Equal(0, _nat.HostPortUnreachable);
                return;
            }

            byte[] p = Assert.Single(_toCaller);
            Assert.Equal(1, _nat.HostPortUnreachable);
            Assert.Equal(1, p[9]);
            Assert.Equal(3, p[20]);
            Assert.Equal(3, p[21]);
            Assert.Equal(Loopback, Ipv4.Source(p));
            Assert.Equal(0, Checksum(p.AsSpan(20)));

            // The quote is the caller's header as it reached the NAT, then the 8 UDP header bytes.
            Assert.Equal(6000, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(48)));
            Assert.Equal(port, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(50)));
            Assert.Equal(_link.Address, BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(40)));
        }
    }
}
