//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Xunit;

namespace RetroIO.Network.Router.Tests
{
    public class IpRouterTests
    {
        private static readonly uint RouterAddress = Ipv4.Parse("10.42.0.1");

        private readonly IpRouter _router = new IpRouter(new RouterSettings());
        private readonly List<byte[]> _local = new List<byte[]>();
        private readonly List<byte[]> _upstream = new List<byte[]>();
        private readonly List<DropReason> _drops = new List<DropReason>();

        public IpRouterTests()
        {
            _router.OnLocal += p => _local.Add(p.ToArray());
            _router.OnDrop += (reason, count) => _drops.Add(reason);
        }

        // A UDP packet with a correct header checksum: 20 bytes of header, 8 of UDP, then the data.
        private static byte[] Packet(uint source, uint destination, byte ttl = 64, int dataLength = 4)
        {
            byte[] p = new byte[28 + dataLength];
            p[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)p.Length);
            p[8] = ttl;
            p[9] = 17;
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(12), source);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), destination);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(10), (ushort)~Ipv4.Sum(p.AsSpan(0, 20)));
            for (int i = 28; i < p.Length; i++)
            {
                p[i] = (byte)i;
            }

            return p;
        }

        private RouterLink Link(List<byte[]> into, uint address = 0, int mtu = 1500)
        {
            return _router.AddLink(p => into.Add(p.ToArray()), address, mtu);
        }

        [Fact]
        public void Ipv4_ParseAndFormat()
        {
            Assert.Equal(0x0A2A0001u, Ipv4.Parse("10.42.0.1"));
            Assert.Equal("192.168.255.7", Ipv4.Format(Ipv4.Parse("192.168.255.7")));
            Assert.Throws<FormatException>(() => Ipv4.Parse("10.42.0"));
            Assert.Throws<FormatException>(() => Ipv4.Parse("10.42.0.256"));
            Assert.Throws<FormatException>(() => Ipv4.Parse("10.42..1"));
        }

        [Fact]
        public void Ipv4_Checksum_MatchesAWorkedExample()
        {
            // The worked example on Wikipedia's "Internet checksum" / IPv4 header pages: 45 00 00 73
            // 00 00 40 00 40 11 [b8 61] c0 a8 00 01 c0 a8 00 c7, checksum B861h. Worked by hand too:
            // the words sum to 2479Ch, folded 479Eh, complement B861h.
            byte[] header =
            {
                0x45, 0x00, 0x00, 0x73, 0x00, 0x00, 0x40, 0x00, 0x40, 0x11,
                0x00, 0x00, 0xC0, 0xA8, 0x00, 0x01, 0xC0, 0xA8, 0x00, 0xC7,
            };
            Assert.Equal(0xB861, (ushort)~Ipv4.Sum(header));
        }

        [Fact]
        public void Links_GetPoolAddressesInOrder_SkippingTheRouter_AndAFreedOneComesBack()
        {
            RouterLink a = Link(new List<byte[]>());
            RouterLink b = Link(new List<byte[]>());
            Assert.Equal("10.42.0.2", Ipv4.Format(a.Address));
            Assert.Equal("10.42.0.3", Ipv4.Format(b.Address));

            Assert.True(_router.RemoveLink(a));
            Assert.False(a.IsAttached);
            Assert.Equal("10.42.0.2", Ipv4.Format(Link(new List<byte[]>()).Address));
        }

        [Fact]
        public void FixedAddress_AlreadyTaken_NamesTheAddress()
        {
            uint fixedAddress = Ipv4.Parse("10.42.0.50");
            Link(new List<byte[]>(), fixedAddress);

            ArgumentException e = Assert.Throws<ArgumentException>(() => Link(new List<byte[]>(), fixedAddress));
            Assert.Contains("10.42.0.50", e.Message);
            Assert.Contains("already given", e.Message);
        }

        [Fact]
        public void FixedAddress_OutsideTheSubnet_OrTheRouters_IsRefused_NamingIt()
        {
            ArgumentException outside = Assert.Throws<ArgumentException>(() => Link(new List<byte[]>(), Ipv4.Parse("10.43.0.5")));
            Assert.Contains("10.43.0.5", outside.Message);
            Assert.Throws<ArgumentException>(() => Link(new List<byte[]>(), RouterAddress));
            Assert.Throws<ArgumentException>(() => Link(new List<byte[]>(), Ipv4.Parse("10.42.0.255")));
        }

        [Fact]
        public void APacketBetweenTwoLinks_ArrivesWithTtlLoweredAndAGoodChecksum_AndIsCounted()
        {
            List<byte[]> toA = new List<byte[]>();
            List<byte[]> toB = new List<byte[]>();
            RouterLink a = Link(toA);
            RouterLink b = Link(toB);
            byte[] sent = Packet(a.Address, b.Address, ttl: 64);

            a.Receive(sent);

            Assert.Single(toB);
            Assert.Empty(toA);
            Assert.Equal(63, toB[0][8]);
            Assert.True(Ipv4.TryCheckHeader(toB[0], out _), "the checksum after the TTL change");
            Assert.Equal(sent.AsSpan(28).ToArray(), toB[0].AsSpan(28).ToArray());
            Assert.Equal(64, sent[8]);

            Assert.Equal(1, a.PacketsIn);
            Assert.Equal(sent.Length, a.BytesIn);
            Assert.Equal(1, b.PacketsOut);
            Assert.Equal(sent.Length, b.BytesOut);
            Assert.Equal(0, a.PacketsOut);
            Assert.Equal(0, b.PacketsIn);
        }

        [Fact]
        public void APacketForTheRouter_GoesToTheLocalServices_WithItsTtlUnchanged()
        {
            RouterLink a = Link(new List<byte[]>());

            a.Receive(Packet(a.Address, RouterAddress, ttl: 1));

            Assert.Single(_local);
            Assert.Equal(1, _local[0][8]);
            Assert.Empty(_drops);
        }

        [Fact]
        public void Broadcasts_GoToTheLocalServicesOnly_NeverToTheOtherLinks()
        {
            List<byte[]> toB = new List<byte[]>();
            RouterLink a = Link(new List<byte[]>());
            Link(toB);

            a.Receive(Packet(a.Address, _router.BroadcastAddress));
            a.Receive(Packet(a.Address, Ipv4.LimitedBroadcast));

            Assert.Equal(2, _local.Count);
            Assert.Empty(toB);
        }

        [Fact]
        public void TheLocalServices_ReachALink()
        {
            List<byte[]> toA = new List<byte[]>();
            RouterLink a = Link(toA);

            _router.SendFromLocal(Packet(RouterAddress, a.Address, ttl: 64));

            Assert.Single(toA);
            Assert.Equal(64, toA[0][8]);
            Assert.Equal(1, a.PacketsOut);
        }

        [Fact]
        public void ASpoofedSource_IsDropped()
        {
            List<byte[]> toB = new List<byte[]>();
            RouterLink a = Link(new List<byte[]>());
            RouterLink b = Link(toB);

            a.Receive(Packet(Ipv4.Parse("10.42.0.99"), b.Address));
            _router.SendFromLocal(Packet(a.Address, b.Address));

            Assert.Empty(toB);
            Assert.Equal(new[] { DropReason.Spoofed, DropReason.Spoofed }, _drops.ToArray());
            Assert.Equal(2, _router.DropCount(DropReason.Spoofed));
        }

        [Fact]
        public void ABadChecksum_OrVersion_OrLength_IsDropped()
        {
            RouterLink a = Link(new List<byte[]>());
            byte[] badSum = Packet(a.Address, RouterAddress);
            badSum[10] ^= 1;
            byte[] badVersion = Packet(a.Address, RouterAddress);
            badVersion[0] = 0x65;
            byte[] tooShort = Packet(a.Address, RouterAddress);

            a.Receive(badSum);
            a.Receive(badVersion);
            a.Receive(tooShort.AsSpan(0, tooShort.Length - 1));
            a.Receive(new byte[10]);

            Assert.Empty(_local);
            Assert.Equal(4, _router.DropCount(DropReason.BadHeader));
            Assert.Equal(0, a.PacketsIn);
        }

        [Fact]
        public void BytesPastTheTotalLength_AreCutOff()
        {
            RouterLink a = Link(new List<byte[]>());
            byte[] p = Packet(a.Address, RouterAddress);
            byte[] padded = new byte[p.Length + 3];
            p.CopyTo(padded, 0);

            a.Receive(padded);

            Assert.Equal(p, _local[0]);
        }

        [Fact]
        public void TtlOne_IsNotForwarded()
        {
            List<byte[]> toB = new List<byte[]>();
            RouterLink a = Link(new List<byte[]>());
            RouterLink b = Link(toB);

            a.Receive(Packet(a.Address, b.Address, ttl: 1));

            Assert.Empty(toB);
            Assert.Equal(1, _router.DropCount(DropReason.TtlExpired));
        }

        [Fact]
        public void AnUnusedSubnetAddress_AndOutsideWithNothingUpstream_HaveNoRoute()
        {
            RouterLink a = Link(new List<byte[]>());

            a.Receive(Packet(a.Address, Ipv4.Parse("10.42.0.200")));
            a.Receive(Packet(a.Address, Ipv4.Parse("8.8.8.8")));

            Assert.Equal(2, _router.DropCount(DropReason.NoRoute));
        }

        [Fact]
        public void OutsideTheSubnet_GoesUpstream_WhenSomethingIsAttached()
        {
            _router.OnUpstream += p => _upstream.Add(p.ToArray());
            RouterLink a = Link(new List<byte[]>());

            a.Receive(Packet(a.Address, Ipv4.Parse("8.8.8.8"), ttl: 10));

            Assert.Single(_upstream);
            Assert.Equal(9, _upstream[0][8]);
        }

        [Fact]
        public void APacketLargerThanTheLinksMtu_IsDropped()
        {
            List<byte[]> toB = new List<byte[]>();
            RouterLink a = Link(new List<byte[]>());
            RouterLink b = Link(toB, mtu: 100);

            a.Receive(Packet(a.Address, b.Address, dataLength: 100));

            Assert.Empty(toB);
            Assert.Equal(1, _router.DropCount(DropReason.TooBig));
        }

        [Fact]
        public void ALargerSubnet_IsASetting()
        {
            IpRouter big = new IpRouter(new RouterSettings { Network = Ipv4.Parse("10.42.0.0"), PrefixLength = 16, RouterAddress = Ipv4.Parse("10.42.0.1"), PoolFirst = Ipv4.Parse("10.42.1.0") });
            RouterLink first = big.AddLink(p => { });
            RouterLink far = big.AddLink(p => { }, Ipv4.Parse("10.42.200.9"));

            Assert.Equal("10.42.1.0", Ipv4.Format(first.Address));
            Assert.Equal("10.42.255.255", Ipv4.Format(big.BroadcastAddress));
            Assert.Equal("10.42.200.9", Ipv4.Format(far.Address));
        }

        [Fact]
        public void BadSettings_AreRefused()
        {
            Assert.Throws<ArgumentException>(() => new IpRouter(new RouterSettings { PrefixLength = 31 }));
            Assert.Throws<ArgumentException>(() => new IpRouter(new RouterSettings { Network = Ipv4.Parse("10.42.0.5") }));
            Assert.Throws<ArgumentException>(() => new IpRouter(new RouterSettings { RouterAddress = Ipv4.Parse("10.43.0.1") }));
        }

        [Fact]
        public void ARemovedLink_CannotSend_AndIsNoLongerARoute()
        {
            RouterLink a = Link(new List<byte[]>());
            RouterLink b = Link(new List<byte[]>());
            _router.RemoveLink(b);

            a.Receive(Packet(a.Address, b.Address));

            Assert.Equal(1, _router.DropCount(DropReason.NoRoute));
            Assert.Throws<InvalidOperationException>(() => b.Receive(Packet(b.Address, RouterAddress)));
        }
    }
}
