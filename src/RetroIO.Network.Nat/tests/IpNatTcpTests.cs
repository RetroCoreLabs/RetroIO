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
    public sealed class IpNatTcpTests : IDisposable
    {
        private const byte Fin = 0x01;
        private const byte Syn = 0x02;
        private const byte Rst = 0x04;
        private const byte Psh = 0x08;
        private const byte Ack = 0x10;
        private const ushort CallerPort = 5000;

        private static readonly uint Loopback = Ipv4.Parse("127.0.0.1");

        private readonly IpRouter _router = new IpRouter(new RouterSettings());
        private readonly IpNat _nat;
        private readonly List<byte[]> _toCaller = new List<byte[]>();
        private readonly RouterLink _link;
        private readonly Socket _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private readonly ushort _port;
        private long _now = 1000;

        public IpNatTcpTests()
        {
            _nat = new IpNat(_router, _now);
            _link = _router.AddLink(p => _toCaller.Add(p.ToArray()));
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _listener.Listen(4);
            _port = (ushort)((IPEndPoint)_listener.LocalEndPoint!).Port;
        }

        public void Dispose()
        {
            _nat.Dispose();
            _listener.Close();
        }

        private readonly struct Segment
        {
            public Segment(byte[] packet)
            {
                SourcePort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(20));
                DestinationPort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(22));
                Seq = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(24));
                AckNumber = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(28));
                int offset = (packet[32] >> 4) * 4;
                Flags = packet[33];
                Window = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(34));
                Data = packet.AsSpan(20 + offset).ToArray();
                Options = packet.AsSpan(40, offset - 20).ToArray();
            }

            public ushort SourcePort { get; }

            public ushort DestinationPort { get; }

            public uint Seq { get; }

            public uint AckNumber { get; }

            public byte Flags { get; }

            public int Window { get; }

            public byte[] Data { get; }

            public byte[] Options { get; }
        }

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
            return (source >> 16) + (source & 0xFFFF) + (destination >> 16) + (destination & 0xFFFF) + 6 + (uint)length;
        }

        private byte[] Tcp(byte flags, uint seq, uint ack, byte[]? data = null, ushort window = 8192, ushort port = 0, bool mss = false)
        {
            data ??= Array.Empty<byte>();
            int header = mss ? 24 : 20;
            byte[] p = new byte[20 + header + data.Length];
            p[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)p.Length);
            p[8] = 64;
            p[9] = 6;
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(12), _link.Address);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), Loopback);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(10), (ushort)~Ipv4.Sum(p.AsSpan(0, 20)));
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(20), CallerPort);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(22), port == 0 ? _port : port);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(24), seq);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(28), ack);
            p[32] = (byte)((header / 4) << 4);
            p[33] = flags;
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(34), window);
            if (mss)
            {
                p[40] = 2;
                p[41] = 4;
                BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(42), 100);
            }

            data.CopyTo(p.AsSpan(20 + header));
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(36), (ushort)~Sum(p.AsSpan(20), Pseudo(_link.Address, Loopback, header + data.Length)));
            return p;
        }

        // Runs the NAT until the caller has had the given number of packets, or five seconds pass.
        private Segment Next(int count)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (_toCaller.Count < count && clock.ElapsedMilliseconds < 5000)
            {
                _nat.Service(_now, 50000);
            }

            Assert.True(_toCaller.Count >= count, "no packet reached the caller");
            byte[] p = _toCaller[count - 1];
            Assert.True(Ipv4.TryCheckHeader(p, out int total));
            Assert.Equal(p.Length, total);
            Assert.Equal(Loopback, Ipv4.Source(p));
            Assert.Equal(_link.Address, Ipv4.Destination(p));
            Assert.Equal(0xFFFFu, Sum(p.AsSpan(20), Pseudo(Loopback, _link.Address, p.Length - 20)));
            return new Segment(p);
        }

        private byte[] ReadAtLeast(Socket s, int n)
        {
            byte[] buffer = new byte[n];
            int got = 0;
            s.ReceiveTimeout = 5000;
            while (got < n)
            {
                int r = s.Receive(buffer, got, n - got, SocketFlags.None);
                Assert.True(r > 0, "the host connection ended early");
                got += r;
            }

            return buffer;
        }

        // Caller SYN, our SYN-ACK, caller ACK. Returns the accepted host socket and our first data
        // sequence number.
        private (Socket Server, uint OurSeq) Handshake(uint callerIss)
        {
            _link.Receive(Tcp(Syn, callerIss, 0, mss: true));
            Segment synAck = Next(1);
            Assert.Equal(Syn | Ack, synAck.Flags);
            Assert.Equal(callerIss + 1, synAck.AckNumber);
            Assert.Equal(_port, synAck.SourcePort);
            Assert.Equal(CallerPort, synAck.DestinationPort);
            Assert.Equal(new byte[] { 2, 4, 0x05, 0xB4 }, synAck.Options);

            Socket server = _listener.Accept();
            _link.Receive(Tcp(Ack, callerIss + 1, synAck.Seq + 1));
            return (server, synAck.Seq + 1);
        }

        [Fact]
        public void DataGoesBothWaysAndTheConnectionCloses()
        {
            (Socket server, uint ours) = Handshake(1000);
            using (server)
            {
                _link.Receive(Tcp(Ack | Psh, 1001, ours, new byte[] { 0x68, 0x69 }));
                Segment ack = Next(2);
                Assert.Equal(Ack, ack.Flags);
                Assert.Equal(1003u, ack.AckNumber);
                _nat.Service(_now, 0);
                Assert.Equal(new byte[] { 0x68, 0x69 }, ReadAtLeast(server, 2));

                server.Send(new byte[] { 1, 2, 3 });
                Segment data = Next(3);
                Assert.Equal(ours, data.Seq);
                Assert.Equal(new byte[] { 1, 2, 3 }, data.Data);
                _link.Receive(Tcp(Ack, 1003, ours + 3));

                // The host closes first: our FIN, then the caller's ACK and FIN.
                server.Shutdown(SocketShutdown.Send);
                Segment fin = Next(4);
                Assert.Equal(Fin | Ack, fin.Flags);
                Assert.Equal(ours + 3, fin.Seq);

                _link.Receive(Tcp(Ack | Fin, 1003, ours + 4));
                Segment last = Next(5);
                Assert.Equal(Ack, last.Flags);
                Assert.Equal(1004u, last.AckNumber);

                // This side closed first, so it waits in TIME-WAIT, and the host saw the caller's
                // FIN as the end of the stream.
                Assert.Equal(1, _nat.TcpConnections);
                Assert.Equal(0, server.Receive(new byte[4]));
                _now += IpNat.TcpTimeWaitMilliseconds;
                _nat.Service(_now, 0);
                Assert.Equal(0, _nat.TcpConnections);
            }
        }

        [Fact]
        public void TheCallersMssLimitsTheSegments()
        {
            (Socket server, uint ours) = Handshake(1000);
            using (server)
            {
                server.Send(new byte[250]);
                Segment first = Next(1 + 1);
                Assert.Equal(100, first.Data.Length);
                Segment second = Next(3);
                Assert.Equal(ours + 100, second.Seq);
            }
        }

        [Fact]
        public void AnUnacknowledgedSegmentIsSentAgainAfterTheTimeout()
        {
            (Socket server, uint ours) = Handshake(1000);
            using (server)
            {
                server.Send(new byte[] { 9 });
                Segment sent = Next(2);
                Assert.Equal(new byte[] { 9 }, sent.Data);

                _now += IpNat.TcpInitialRetransmitMilliseconds - 1;
                _nat.Service(_now, 0);
                Assert.Equal(2, _toCaller.Count);

                _now += 1;
                _nat.Service(_now, 0);
                Segment again = Next(3);
                Assert.Equal(ours, again.Seq);
                Assert.Equal(new byte[] { 9 }, again.Data);
                Assert.Equal(1, _nat.TcpRetransmits);
            }
        }

        [Fact]
        public void ARefusedHostConnectResetsTheCallersSyn()
        {
            Socket closed = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            closed.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            ushort port = (ushort)((IPEndPoint)closed.LocalEndPoint!).Port;
            closed.Close();

            _link.Receive(Tcp(Syn, 7000, 0, port: port));
            Segment rst = Next(1);

            Assert.Equal(Rst | Ack, rst.Flags);
            Assert.Equal(7001u, rst.AckNumber);
            Assert.Equal(1, _nat.HostConnectsRefused);
            Assert.Equal(0, _nat.TcpConnections);
        }

        [Fact]
        public void ASegmentForNoConnectionGetsAReset()
        {
            _link.Receive(Tcp(Ack, 5, 12345));
            Segment rst = Next(1);

            Assert.Equal(Rst, rst.Flags);
            Assert.Equal(12345u, rst.Seq);
        }

        [Fact]
        public void TheCallersResetClosesTheHostConnection()
        {
            (Socket server, _) = Handshake(1000);
            using (server)
            {
                _link.Receive(Tcp(Rst, 1001, 0));

                Assert.Equal(0, _nat.TcpConnections);
                server.ReceiveTimeout = 5000;
                Assert.Equal(0, server.Receive(new byte[4]));
            }
        }

        [Fact]
        public void ABadChecksumIsDropped()
        {
            byte[] p = Tcp(Syn, 1, 0);
            p[36] ^= 0x01;
            _link.Receive(p);

            Assert.Equal(1, _nat.DropCount(NatDropReason.BadTcp));
            Assert.Equal(0, _nat.TcpConnections);
        }
    }
}
