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
    /// The built-in NAT (host networking design, section 9.1): a test wire sends hand-built
    /// guest frames into <see cref="NatEthernetBackend"/> and reads the frames that come back;
    /// the far end is real host sockets on 127.0.0.1. No admin rights and no network needed.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class NatEthernetBackendTests
    {
        // ── Constants ──

        private const uint Guest = 0x0A00020F;

        private const uint Gateway = 0x0A000202;

        private const int WaitMilliseconds = 5000;

        // ── Fields ──

        private static readonly byte[] GuestMac = { 0x02, 0x00, 0x00, 0x11, 0x22, 0x33 };

        private static readonly byte[] GatewayMac = { 0x02, 0x52, 0x43, 0x00, 0x02, 0x02 };

        private static readonly byte[] Broadcast = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        private NatEthernetBackend _nat = null!;

        private List<byte[]> _received = null!;

        // ── Public methods ──

        [SetUp]
        public void SetUp()
        {
            _received = new List<byte[]>();
            _nat = new NatEthernetBackend();
            _nat.OnFrameReceived += (frame, offset, length) => _received.Add(new ReadOnlySpan<byte>(frame, offset, length).ToArray());
            _nat.Start();
        }

        [TearDown]
        public void TearDown()
        {
            _nat.Stop();
        }

        [Test]
        public void Arp_RequestForTheGateway_IsAnsweredWithTheGatewayMac()
        {
            byte[] frame = new byte[42];
            Ether(frame, Broadcast, 0x0806);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(14), 1);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), 0x0800);
            frame[18] = 6;
            frame[19] = 4;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(20), 1);
            GuestMac.CopyTo(frame, 22);
            NatPacket.WriteAddress(frame.AsSpan(28), Guest);
            NatPacket.WriteAddress(frame.AsSpan(38), Gateway);
            _nat.Send(frame, 0, frame.Length);

            byte[] reply = WaitFor(f => Type(f) == 0x0806);
            Assert.That(reply.Length, Is.EqualTo(60), "padded to the shortest Ethernet frame");
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(20)), Is.EqualTo(2), "ARP reply");
            Assert.That(reply.AsSpan(22, 6).ToArray(), Is.EqualTo(GatewayMac), "sender MAC = the gateway MAC");
            Assert.That(NatPacket.ReadAddress(reply.AsSpan(28)), Is.EqualTo(Gateway));
            Assert.That(reply.AsSpan(0, 6).ToArray(), Is.EqualTo(GuestMac), "sent to the guest");
        }

        [Test]
        public void Ping_ToTheGateway_GetsAnEchoReplyWithTheSameData()
        {
            byte[] icmp = new byte[8 + 32];
            icmp[0] = 8;
            BinaryPrimitives.WriteUInt16BigEndian(icmp.AsSpan(4), 0x1234);
            BinaryPrimitives.WriteUInt16BigEndian(icmp.AsSpan(6), 7);
            for (int i = 8; i < icmp.Length; i++)
            {
                icmp[i] = (byte)i;
            }
            BinaryPrimitives.WriteUInt16BigEndian(icmp.AsSpan(2), NatPacket.Fold(NatPacket.Sum(icmp, 0)));
            SendIp(NatPacket.ProtocolIcmp, Guest, Gateway, icmp);

            byte[] reply = WaitFor(f => Type(f) == 0x0800 && f[23] == NatPacket.ProtocolIcmp);
            Span<byte> body = reply.AsSpan(34, icmp.Length);
            Assert.That(body[0], Is.EqualTo(0), "echo reply");
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(body.Slice(4)), Is.EqualTo(0x1234), "identifier");
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(body.Slice(6)), Is.EqualTo(7), "sequence");
            Assert.That(body.Slice(8).ToArray(), Is.EqualTo(icmp.AsSpan(8).ToArray()), "data");
            Assert.That(NatPacket.Fold(NatPacket.Sum(body, 0)), Is.EqualTo(0), "ICMP checksum right");
            Assert.That(NatPacket.Ipv4HeaderChecksumOk(reply.AsSpan(14, 20)), Is.True, "IP header checksum right");
            Assert.That(NatPacket.ReadAddress(reply.AsSpan(26)), Is.EqualTo(Gateway), "from the gateway");
        }

        [Test]
        public void Dhcp_DiscoverThenRequest_GivesTheFirstGuestAddressWithRouterAndNameServer()
        {
            SendDhcp(1, 0);
            byte[] offer = WaitFor(f => IsDhcpReply(f, 2));
            Assert.That(NatPacket.ReadAddress(offer.AsSpan(42 + 16)), Is.EqualTo(Guest), "yiaddr of the offer");

            SendDhcp(3, Guest);
            byte[] ack = WaitFor(f => IsDhcpReply(f, 5));
            Assert.That(NatPacket.ReadAddress(ack.AsSpan(42 + 16)), Is.EqualTo(Guest), "yiaddr of the ACK");
            Assert.That(DhcpOption(ack, 3), Is.EqualTo(Gateway), "router option");
            Assert.That(DhcpOption(ack, 6), Is.EqualTo(0x0A000203u), "name server option");
            Assert.That(DhcpOption(ack, 1), Is.EqualTo(0xFFFFFF00u), "subnet mask option");
        }

        [Test]
        public void Bootp_RequestWithoutDhcpOptions_GetsAPlainBootpReply()
        {
            SendDhcp(0, 0);
            byte[] reply = WaitFor(f => Type(f) == 0x0800 && f[23] == NatPacket.ProtocolUdp && BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(36)) == 68);
            Assert.That(reply[42], Is.EqualTo(2), "BOOTREPLY");
            Assert.That(NatPacket.ReadAddress(reply.AsSpan(42 + 16)), Is.EqualTo(Guest), "yiaddr");
            Assert.That(DhcpOption(reply, 53), Is.EqualTo(uint.MaxValue), "no DHCP message type in a BOOTP reply");
            Assert.That(DhcpOption(reply, 3), Is.EqualTo(Gateway), "router in the vendor area");
        }

        /// <summary>
        /// A guest TCP connection to the gateway reaches a listener on the host's 127.0.0.1, the
        /// guest's bytes arrive there, the listener's answer comes back to the guest, and the
        /// close goes both ways (design 7.4 and 7.9).
        /// </summary>
        [Test]
        public void Tcp_GuestConnectsToTheGateway_ReachesAHostListenerAndBack()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                ushort port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
                uint iss = 1000;
                SendTcp(Gateway, 40000, port, iss, 0, NatPacket.TcpSyn, 4096, Array.Empty<byte>(), 1024);
                byte[] synAck = WaitFor(f => IsTcp(f, NatPacket.TcpSyn | NatPacket.TcpAck));
                Assert.That(TcpAck(synAck), Is.EqualTo(iss + 1), "SYN-ACK acknowledges the guest's SYN");
                Assert.That(TcpHeaderBytes(synAck), Is.EqualTo(24), "only the MSS option is offered");
                uint server = TcpSeq(synAck) + 1;

                using TcpClient accepted = AcceptWithin(listener);
                NetworkStream stream = accepted.GetStream();
                SendTcp(Gateway, 40000, port, iss + 1, server, NatPacket.TcpAck, 4096, Array.Empty<byte>(), 0);
                byte[] hello = Encoding.ASCII.GetBytes("hello from the guest");
                SendTcp(Gateway, 40000, port, iss + 1, server, NatPacket.TcpAck | NatPacket.TcpPsh, 4096, hello, 0);
                WaitFor(f => IsTcp(f, NatPacket.TcpAck) && TcpAck(f) == iss + 1 + (uint)hello.Length);

                byte[] got = new byte[hello.Length];
                stream.ReadTimeout = WaitMilliseconds;
                int read = 0;
                while (read < got.Length)
                {
                    int n = stream.Read(got, read, got.Length - read);
                    Assert.That(n, Is.GreaterThan(0), "the host listener got the guest's bytes");
                    read += n;
                }
                Assert.That(Encoding.ASCII.GetString(got), Is.EqualTo("hello from the guest"));

                byte[] answer = Encoding.ASCII.GetBytes("hello from the host");
                stream.Write(answer, 0, answer.Length);
                byte[] data = WaitFor(f => IsTcp(f, NatPacket.TcpAck | NatPacket.TcpPsh) && TcpData(f).Length > 0);
                Assert.That(Encoding.ASCII.GetString(TcpData(data)), Is.EqualTo("hello from the host"));
                Assert.That(TcpSeq(data), Is.EqualTo(server), "first data byte after the SYN-ACK");
                Assert.That(NatPacket.ReadAddress(data.AsSpan(26)), Is.EqualTo(Gateway), "it comes from the gateway address");
                uint serverNext = server + (uint)answer.Length;
                SendTcp(Gateway, 40000, port, iss + 1 + (uint)hello.Length, serverNext, NatPacket.TcpAck, 4096, Array.Empty<byte>(), 0);

                // Host closes: the guest gets FIN; the guest answers with ACK and its own FIN.
                accepted.Client.Shutdown(SocketShutdown.Send);
                byte[] fin = WaitFor(f => IsTcp(f, NatPacket.TcpFin | NatPacket.TcpAck));
                Assert.That(TcpSeq(fin), Is.EqualTo(serverNext));
                uint guestNext = iss + 1 + (uint)hello.Length;
                SendTcp(Gateway, 40000, port, guestNext, serverNext + 1, NatPacket.TcpAck | NatPacket.TcpFin, 4096, Array.Empty<byte>(), 0);
                WaitFor(f => IsTcp(f, NatPacket.TcpAck) && TcpAck(f) == guestNext + 1);
                Assert.That(stream.Read(got, 0, got.Length), Is.EqualTo(0), "the guest's FIN ends the host side's stream");
            }
            finally
            {
                listener.Stop();
            }
        }

        [Test]
        public void Tcp_ToAHostPortNobodyListensOn_GetsReset()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            ushort port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            SendTcp(Gateway, 40001, port, 5000, 0, NatPacket.TcpSyn, 4096, Array.Empty<byte>(), 1024);
            byte[] reset = WaitFor(f => IsTcp(f, NatPacket.TcpRst | NatPacket.TcpAck));
            Assert.That(TcpAck(reset), Is.EqualTo(5001u), "the reset acknowledges the SYN");
            Assert.That(_received.Exists(f => IsTcp(f, NatPacket.TcpSyn | NatPacket.TcpAck)), Is.False, "no SYN-ACK before the host connect worked");
        }

        [Test]
        public void Tcp_HostLoopbackOff_AConnectToTheGatewayGetsReset()
        {
            _nat.Stop();
            _received.Clear();
            _nat = (NatEthernetBackend)NatEthernetBackend.CreateFromSpec("nat:hostloopback=off")!;
            _nat.OnFrameReceived += (frame, offset, length) => _received.Add(new ReadOnlySpan<byte>(frame, offset, length).ToArray());
            _nat.Start();
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                ushort port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
                SendTcp(Gateway, 40002, port, 7000, 0, NatPacket.TcpSyn, 4096, Array.Empty<byte>(), 1024);
                WaitFor(f => IsTcp(f, NatPacket.TcpRst | NatPacket.TcpAck));
                Assert.That(listener.Pending(), Is.False, "nothing reached the host listener");
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// A segment the guest does not acknowledge is resent only as emulated time (polls) goes
        /// by, not as host time goes by (design 7.4, retransmitting).
        /// </summary>
        [Test]
        public void Tcp_UnacknowledgedData_IsResentAfterOneSecondOfPollsNotOfHostTime()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                ushort port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
                SendTcp(Gateway, 40003, port, 9000, 0, NatPacket.TcpSyn, 4096, Array.Empty<byte>(), 1024);
                byte[] synAck = WaitFor(f => IsTcp(f, NatPacket.TcpSyn | NatPacket.TcpAck));
                uint server = TcpSeq(synAck) + 1;
                using TcpClient accepted = AcceptWithin(listener);
                SendTcp(Gateway, 40003, port, 9001, server, NatPacket.TcpAck, 4096, Array.Empty<byte>(), 0);
                accepted.GetStream().Write(new byte[] { 1, 2, 3 }, 0, 3);
                WaitFor(f => IsTcp(f, NatPacket.TcpAck | NatPacket.TcpPsh) && TcpData(f).Length == 3);

                // Host time goes by, emulated time does not: nothing is resent.
                long before = _nat.Router!.Retransmits;
                Stopwatch quiet = Stopwatch.StartNew();
                while (quiet.ElapsedMilliseconds < 1500)
                {
                    Thread.Yield();
                }
                Assert.That(_nat.Router!.Retransmits, Is.EqualTo(before), "no resend while no poll happened");

                // One second of polls: the segment comes again.
                _received.Clear();
                byte[] again = WaitFor(f => IsTcp(f, NatPacket.TcpAck | NatPacket.TcpPsh) && TcpData(f).Length == 3);
                Assert.That(TcpSeq(again), Is.EqualTo(server), "the same bytes again");
                Assert.That(_nat.Router!.Retransmits, Is.GreaterThan(before));
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// A guest datagram to the gateway reaches a UDP socket on the host's 127.0.0.1, and the
        /// host's answer comes back to the guest from the gateway address.
        /// </summary>
        [Test]
        public void Udp_ToTheGateway_ReachesAHostSocketAndTheAnswerComesBack()
        {
            using Socket host = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            host.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            host.ReceiveTimeout = WaitMilliseconds;
            ushort port = (ushort)((IPEndPoint)host.LocalEndPoint!).Port;

            byte[] question = Encoding.ASCII.GetBytes("question");
            byte[] udp = new byte[8 + question.Length];
            question.CopyTo(udp, 8);
            NatPacket.WriteUdp(udp, Guest, Gateway, 5555, port);
            SendIp(NatPacket.ProtocolUdp, Guest, Gateway, udp);

            byte[] buffer = new byte[100];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n = host.ReceiveFrom(buffer, ref from);
            Assert.That(Encoding.ASCII.GetString(buffer, 0, n), Is.EqualTo("question"));
            host.SendTo(Encoding.ASCII.GetBytes("answer"), from);

            byte[] reply = WaitFor(f => Type(f) == 0x0800 && f[23] == NatPacket.ProtocolUdp && BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(36)) == 5555);
            Assert.That(NatPacket.ReadAddress(reply.AsSpan(26)), Is.EqualTo(Gateway), "from the gateway address");
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(34)), Is.EqualTo(port), "from the host socket's port");
            int length = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(38)) - 8;
            Assert.That(Encoding.ASCII.GetString(reply, 42, length), Is.EqualTo("answer"));
            Assert.That(NatPacket.Fold(NatPacket.Sum(reply.AsSpan(34, 8 + length), NatPacket.PseudoHeaderSum(Gateway, Guest, NatPacket.ProtocolUdp, 8 + length))), Is.EqualTo(0), "UDP checksum right");
        }

        [Test]
        public void CreateFromSpec_TakesOnlyNatSpecs()
        {
            Assert.That(NatEthernetBackend.CreateFromSpec("pcap"), Is.Null);
            Assert.That(NatEthernetBackend.CreateFromSpec("nat"), Is.InstanceOf<NatEthernetBackend>());
            Assert.That(((NatEthernetBackend)NatEthernetBackend.CreateFromSpec("nat:hostloopback=off")!).HostLoopback, Is.False);
            Assert.Throws<ArgumentException>(() => NatEthernetBackend.CreateFromSpec("nat:bogus"));
        }

        // ── Private methods ──

        // Polls (one emulated host frame each) until a frame matching the test comes, or fails.
        private byte[] WaitFor(Predicate<byte[]> match)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < WaitMilliseconds)
            {
                _nat.Poll();
                for (int i = 0; i < _received.Count; i++)
                {
                    if (match(_received[i]))
                    {
                        byte[] f = _received[i];
                        _received.RemoveAt(i);
                        return f;
                    }
                }
                Thread.Sleep(2);
            }
            Assert.Fail("no matching frame from the NAT within " + WaitMilliseconds + " ms; got " + _received.Count + " other frames");
            return Array.Empty<byte>();
        }

        private TcpClient AcceptWithin(TcpListener listener)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!listener.Pending())
            {
                Assert.That(clock.ElapsedMilliseconds, Is.LessThan(WaitMilliseconds), "the host listener got a connection");
                _nat.Poll();
                Thread.Sleep(2);
            }
            return listener.AcceptTcpClient();
        }

        private static void Ether(byte[] frame, byte[] destination, ushort type)
        {
            destination.CopyTo(frame, 0);
            GuestMac.CopyTo(frame, 6);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), type);
        }

        private void SendIp(byte protocol, uint source, uint destination, byte[] payload)
        {
            byte[] frame = new byte[14 + 20 + payload.Length];
            Ether(frame, GatewayMac, 0x0800);
            payload.CopyTo(frame, 34);
            NatPacket.WriteIpv4(frame.AsSpan(14), 20 + payload.Length, 1, protocol, source, destination);
            _nat.Send(frame, 0, frame.Length);
        }

        private void SendTcp(uint destination, ushort sourcePort, ushort destinationPort, uint seq, uint ack, int flags, ushort window, byte[] data, ushort mss)
        {
            int header = mss != 0 ? 24 : 20;
            byte[] tcp = new byte[header + data.Length];
            data.CopyTo(tcp, header);
            NatPacket.WriteTcp(tcp, header, Guest, destination, sourcePort, destinationPort, seq, ack, (byte)flags, window, mss);
            SendIp(NatPacket.ProtocolTcp, Guest, destination, tcp);
        }

        private void SendDhcp(int messageType, uint requested)
        {
            byte[] bootp = new byte[300];
            bootp[0] = 1;
            bootp[1] = 1;
            bootp[2] = 6;
            BinaryPrimitives.WriteUInt32BigEndian(bootp.AsSpan(4), 0xCAFE0001);
            GuestMac.CopyTo(bootp, 28);
            int o = 236;
            if (messageType != 0)
            {
                bootp[o++] = 0x63;
                bootp[o++] = 0x82;
                bootp[o++] = 0x53;
                bootp[o++] = 0x63;
                bootp[o++] = 53;
                bootp[o++] = 1;
                bootp[o++] = (byte)messageType;
                if (requested != 0)
                {
                    bootp[o++] = 50;
                    bootp[o++] = 4;
                    NatPacket.WriteAddress(bootp.AsSpan(o), requested);
                    o += 4;
                }
                bootp[o] = 255;
            }
            byte[] udp = new byte[8 + bootp.Length];
            bootp.CopyTo(udp, 8);
            NatPacket.WriteUdp(udp, 0, 0xFFFFFFFF, 68, 67);
            byte[] frame = new byte[14 + 20 + udp.Length];
            Ether(frame, Broadcast, 0x0800);
            udp.CopyTo(frame, 34);
            NatPacket.WriteIpv4(frame.AsSpan(14), 20 + udp.Length, 2, NatPacket.ProtocolUdp, 0, 0xFFFFFFFF);
            _nat.Send(frame, 0, frame.Length);
        }

        private static bool IsDhcpReply(byte[] f, int type)
        {
            return Type(f) == 0x0800 && f[23] == NatPacket.ProtocolUdp && BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(36)) == 68
                && DhcpOption(f, 53) == (uint)type;
        }

        // The value of a DHCP option in a reply frame (one byte, or four as an address);
        // uint.MaxValue when the option is not there.
        private static uint DhcpOption(byte[] f, int code)
        {
            int i = 42 + 240;
            while (i + 1 < f.Length && f[i] != 255)
            {
                if (f[i] == 0)
                {
                    i++;
                    continue;
                }
                int len = f[i + 1];
                if (f[i] == code)
                {
                    return len == 4 ? NatPacket.ReadAddress(f.AsSpan(i + 2)) : f[i + 2];
                }
                i += 2 + len;
            }
            return uint.MaxValue;
        }

        private static ushort Type(byte[] f)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(12));
        }

        private static bool IsTcp(byte[] f, int flags)
        {
            return Type(f) == 0x0800 && f[23] == NatPacket.ProtocolTcp && (f[34 + 13] & 0x1F) == flags;
        }

        private static uint TcpSeq(byte[] f)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(34 + 4));
        }

        private static uint TcpAck(byte[] f)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(34 + 8));
        }

        private static int TcpHeaderBytes(byte[] f)
        {
            return (f[34 + 12] >> 4) * 4;
        }

        private static byte[] TcpData(byte[] f)
        {
            int ipLength = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(16));
            int header = TcpHeaderBytes(f);
            return f.AsSpan(34 + header, ipLength - 20 - header).ToArray();
        }
    }
}
