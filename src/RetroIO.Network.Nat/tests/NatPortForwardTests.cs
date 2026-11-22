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
    /// Port forwards of the built-in NAT (host networking design, section 7.7, phase 2): a host
    /// client on 127.0.0.1 reaches a hand-built guest through a rule, over TCP and UDP. Every
    /// rule here listens on 127.0.0.1 only, so no firewall prompt comes up.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class NatPortForwardTests
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
        public void TryParse_ReadsEveryWrittenForm_AndWritesItBackInFull()
        {
            string[] text =
            {
                "tcp:2323:23", "tcp:127.0.0.1:2323:23", "tcp:8080:10.0.2.16:80", "tcp:127.0.0.1:8080:10.0.2.16:80",
                "udp:5353:53", "tcp:6000-6009:6000", "tcp 2323 23", " TCP  2323   23 ",
            };
            string[] full =
            {
                "tcp:0.0.0.0:2323:23", "tcp:127.0.0.1:2323:23", "tcp:0.0.0.0:8080:10.0.2.16:80", "tcp:127.0.0.1:8080:10.0.2.16:80",
                "udp:0.0.0.0:5353:53", "tcp:0.0.0.0:6000-6009:6000", "tcp:0.0.0.0:2323:23", "tcp:0.0.0.0:2323:23",
            };
            for (int i = 0; i < text.Length; i++)
            {
                Assert.That(NatPortForward.TryParse(text[i], out NatPortForward? rule, out string error), Is.True, text[i] + ": " + error);
                Assert.That(rule!.ToString(), Is.EqualTo(full[i]), text[i]);
                Assert.That(NatPortForward.TryParse(rule.ToString(), out NatPortForward? again, out _), Is.True);
                Assert.That(again!.SameAs(rule), Is.True, "the full form reads back to the same rule");
            }
            Assert.That(NatPortForward.TryParse("tcp:6000-6009:6000", out NatPortForward? range, out _), Is.True);
            Assert.That(range!.PortCount, Is.EqualTo(10));
            Assert.That(NatPortForward.DefaultListenAddress, Is.EqualTo(IPAddress.Any), "the owner's decision of 2026-11-12: all addresses");

            string[] bad = { "", "tcp", "tcp:23", "icmp:1:2", "tcp:0:23", "tcp:70000:23", "tcp:10-5:23", "tcp:65535-65536:1", "tcp:65530-65535:65534", "tcp:a:b:c", "tcp:1-300:1" };
            for (int i = 0; i < bad.Length; i++)
            {
                Assert.That(NatPortForward.TryParse(bad[i], out _, out string error), Is.False, bad[i]);
                Assert.That(error, Is.Not.Empty, bad[i]);
            }
        }

        [Test]
        public void Tcp_AHostClientThroughAForward_ReachesTheGuestFromTheGatewayAndBack()
        {
            int port = FreePort(SocketType.Stream);
            Assert.That(_nat.AddForward("tcp:127.0.0.1:" + port + ":23", out string added), Is.True, added);
            GuestSpeaks();

            using TcpClient client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            byte[] syn = WaitFor(f => IsTcp(f, NatPacket.TcpSyn));
            Assert.That(syn.AsSpan(0, 6).SequenceEqual(GuestMac), Is.True, "to the guest's MAC");
            Assert.That(NatPacket.ReadAddress(syn.AsSpan(26)), Is.EqualTo(Gateway), "from the gateway address, so the guest needs no route");
            Assert.That(NatPacket.ReadAddress(syn.AsSpan(30)), Is.EqualTo(Guest));
            Assert.That(DestinationPort(syn), Is.EqualTo(23));
            Assert.That(TcpHeaderBytes(syn), Is.EqualTo(24), "our SYN offers the MSS option");
            ushort gatewayPort = SourcePort(syn);
            Assert.That(gatewayPort, Is.GreaterThanOrEqualTo(NatRouter.FirstGatewayPort));
            uint ours = TcpSeq(syn);

            uint guestSeq = 7000;
            SendTcp(23, gatewayPort, guestSeq, ours + 1, NatPacket.TcpSyn | NatPacket.TcpAck, 4096, Array.Empty<byte>(), 1024);
            WaitFor(f => IsTcp(f, NatPacket.TcpAck) && TcpAck(f) == guestSeq + 1 && TcpSeq(f) == ours + 1);

            NetworkStream stream = client.GetStream();
            stream.ReadTimeout = WaitMilliseconds;
            byte[] hello = Encoding.ASCII.GetBytes("hello from the host");
            stream.Write(hello, 0, hello.Length);
            byte[] data = WaitFor(f => IsTcp(f, NatPacket.TcpAck | NatPacket.TcpPsh) && TcpData(f).Length > 0);
            Assert.That(Encoding.ASCII.GetString(TcpData(data)), Is.EqualTo("hello from the host"));
            Assert.That(TcpSeq(data), Is.EqualTo(ours + 1));
            SendTcp(23, gatewayPort, guestSeq + 1, ours + 1 + (uint)hello.Length, NatPacket.TcpAck, 4096, Array.Empty<byte>(), 0);

            byte[] answer = Encoding.ASCII.GetBytes("login: ");
            SendTcp(23, gatewayPort, guestSeq + 1, ours + 1 + (uint)hello.Length, NatPacket.TcpAck | NatPacket.TcpPsh, 4096, answer, 0);
            WaitFor(f => IsTcp(f, NatPacket.TcpAck) && TcpAck(f) == guestSeq + 1 + (uint)answer.Length);
            Assert.That(ReadExactly(stream, answer.Length), Is.EqualTo("login: "));
            Assert.That(_nat.Router!.ForwardConnections, Is.EqualTo(1));
        }

        [Test]
        public void Tcp_ARuleNamingAGuestWhoseMacIsNotKnown_AsksByArpFirst()
        {
            const uint other = 0x0A000210;
            int port = FreePort(SocketType.Stream);
            Assert.That(_nat.AddForward("tcp:127.0.0.1:" + port + ":10.0.2.16:80", out string added), Is.True, added);

            using TcpClient client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            byte[] arp = WaitFor(f => Type(f) == 0x0806 && BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(20)) == 1);
            Assert.That(arp.AsSpan(0, 6).SequenceEqual(Broadcast), Is.True);
            Assert.That(NatPacket.ReadAddress(arp.AsSpan(28)), Is.EqualTo(Gateway), "asked by the gateway");
            Assert.That(NatPacket.ReadAddress(arp.AsSpan(38)), Is.EqualTo(other), "for the rule's guest");
            Assert.That(_received.Count == 0 || !IsTcp(_received[0], NatPacket.TcpSyn), Is.True, "no SYN before the MAC is known");

            byte[] reply = new byte[60];
            Ether(reply, GatewayMac, 0x0806);
            Span<byte> body = reply.AsSpan(14);
            BinaryPrimitives.WriteUInt16BigEndian(body, 1);
            BinaryPrimitives.WriteUInt16BigEndian(body.Slice(2), 0x0800);
            body[4] = 6;
            body[5] = 4;
            BinaryPrimitives.WriteUInt16BigEndian(body.Slice(6), 2);
            GuestMac.CopyTo(body.Slice(8));
            NatPacket.WriteAddress(body.Slice(14), other);
            GatewayMac.CopyTo(body.Slice(18));
            NatPacket.WriteAddress(body.Slice(24), Gateway);
            _nat.Send(reply, 0, reply.Length);

            byte[] syn = WaitFor(f => IsTcp(f, NatPacket.TcpSyn));
            Assert.That(NatPacket.ReadAddress(syn.AsSpan(30)), Is.EqualTo(other));
            Assert.That(DestinationPort(syn), Is.EqualTo(80));
            Assert.That(syn.AsSpan(0, 6).SequenceEqual(GuestMac), Is.True, "to the MAC from the ARP reply");
        }

        [Test]
        public void Tcp_TheGuestRefuses_TheHostClientIsReset()
        {
            int port = FreePort(SocketType.Stream);
            Assert.That(_nat.AddForward("tcp:127.0.0.1:" + port + ":23", out string added), Is.True, added);
            GuestSpeaks();

            using TcpClient client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            byte[] syn = WaitFor(f => IsTcp(f, NatPacket.TcpSyn));
            SendTcp(23, SourcePort(syn), 0, TcpSeq(syn) + 1, NatPacket.TcpRst | NatPacket.TcpAck, 0, Array.Empty<byte>(), 0);

            NetworkStream stream = client.GetStream();
            stream.ReadTimeout = WaitMilliseconds;
            Stopwatch clock = Stopwatch.StartNew();
            bool ended = false;
            while (!ended && clock.ElapsedMilliseconds < WaitMilliseconds)
            {
                _nat.Poll();
                try
                {
                    if (client.Client.Poll(10000, SelectMode.SelectRead))
                    {
                        ended = stream.Read(new byte[1], 0, 1) == 0;
                    }
                }
                catch (System.IO.IOException)
                {
                    ended = true;
                }
            }
            Assert.That(ended, Is.True, "the host client's connection ends");
            Assert.That(_nat.Router!.ForwardGuestRefused, Is.EqualTo(1));
        }

        [Test]
        public void Udp_AHostSenderThroughAForward_ReachesTheGuestAndTheAnswerComesBack()
        {
            int port = FreePort(SocketType.Dgram);
            Assert.That(_nat.AddForward("udp:127.0.0.1:" + port + ":53", out string added), Is.True, added);
            GuestSpeaks();

            using Socket sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sender.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            sender.ReceiveTimeout = WaitMilliseconds;
            sender.SendTo(Encoding.ASCII.GetBytes("question"), new IPEndPoint(IPAddress.Loopback, port));

            byte[] q = WaitFor(f => Type(f) == 0x0800 && f[23] == NatPacket.ProtocolUdp && DestinationPort(f) == 53);
            Assert.That(NatPacket.ReadAddress(q.AsSpan(26)), Is.EqualTo(Gateway), "from the gateway address");
            ushort gatewayPort = SourcePort(q);
            int length = BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(38)) - 8;
            Assert.That(Encoding.ASCII.GetString(q, 42, length), Is.EqualTo("question"));
            Assert.That(NatPacket.Fold(NatPacket.Sum(q.AsSpan(34, 8 + length), NatPacket.PseudoHeaderSum(Gateway, Guest, NatPacket.ProtocolUdp, 8 + length))), Is.EqualTo(0), "UDP checksum right");

            byte[] answer = Encoding.ASCII.GetBytes("answer");
            byte[] udp = new byte[8 + answer.Length];
            answer.CopyTo(udp, 8);
            NatPacket.WriteUdp(udp, Guest, Gateway, 53, gatewayPort);
            SendIp(NatPacket.ProtocolUdp, Guest, Gateway, udp);

            byte[] buffer = new byte[100];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n = ReceiveWhilePolling(sender, buffer, ref from);
            Assert.That(Encoding.ASCII.GetString(buffer, 0, n), Is.EqualTo("answer"));
            Assert.That(((IPEndPoint)from).Port, Is.EqualTo(port), "the answer comes from the forward's own port");
            // The NAT thread counts the answer just after sending it, so the count may come a moment later.
            Stopwatch clock = Stopwatch.StartNew();
            while (_nat.Router!.ForwardDatagramsToHost == 0 && clock.ElapsedMilliseconds < WaitMilliseconds)
            {
                Thread.Sleep(2);
            }
            Assert.That(_nat.Router.ForwardDatagramsToHost, Is.EqualTo(1));
        }

        [Test]
        public void Rules_AreAddedAndRemovedWhileRunning_AndConflictsAreNamed()
        {
            int port = FreePort(SocketType.Stream);
            string rule = "tcp:127.0.0.1:" + port + ":23";
            Assert.That(_nat.AddForward(rule, out _), Is.True);
            Assert.That(_nat.AddForward(rule, out string again), Is.False);
            Assert.That(again, Does.Contain("already there"));
            Assert.That(_nat.AddForward("tcp:127.0.0.1:" + port + ":24", out string overlap), Is.False);
            Assert.That(overlap, Does.Contain("already has"));
            Assert.That(_nat.AddForward("bogus", out string bad), Is.False);
            Assert.That(bad, Does.Contain("not a forward rule"));

            // A host port another program holds is refused by name; the NAT keeps running.
            using Socket holder = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            holder.ExclusiveAddressUse = OperatingSystem.IsWindows();
            holder.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            holder.Listen(1);
            int held = ((IPEndPoint)holder.LocalEndPoint!).Port;
            Assert.That(_nat.AddForward("tcp:127.0.0.1:" + held + ":23", out string inUse), Is.False);
            Assert.That(inUse, Does.Contain("already in use"));

            string[] lines = _nat.DescribeForwards();
            Assert.That(lines.Length, Is.EqualTo(1));
            Assert.That(lines[0], Does.Contain("listening"));

            Assert.That(_nat.RemoveForward("tcp 127.0.0.1 " + port + " 23", out string removed), Is.True, removed);
            Assert.That(_nat.GetForwards().Length, Is.EqualTo(0));
            Assert.That(_nat.RemoveForward(rule, out string none), Is.False);
            Assert.That(none, Does.Contain("no rule"));

            // The port is free again once the NAT thread has closed it.
            Stopwatch clock = Stopwatch.StartNew();
            bool free = false;
            while (!free && clock.ElapsedMilliseconds < WaitMilliseconds)
            {
                _nat.Poll();
                using Socket probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                probe.ExclusiveAddressUse = OperatingSystem.IsWindows();
                try
                {
                    probe.Bind(new IPEndPoint(IPAddress.Loopback, port));
                    free = true;
                }
                catch (SocketException)
                {
                    Thread.Sleep(5);
                }
            }
            Assert.That(free, Is.True, "a removed rule's port is closed");
        }

        [Test]
        public void TwentyRulesAtOnce_AllListen_AndARuleAddedWhileStoppedListensOnStart()
        {
            _nat.Stop();
            int early = FreePort(SocketType.Stream);
            Assert.That(_nat.AddForward("tcp:127.0.0.1:" + early + ":23", out string e), Is.True, e);
            Assert.That(_nat.DescribeForwards()[0], Does.Contain("NAT is stopped"));
            _nat.Start();
            Assert.That(_nat.DescribeForwards()[0], Does.EndWith("listening"));

            for (int i = 0; i < 19; i++)
            {
                SocketType type = i % 2 == 0 ? SocketType.Stream : SocketType.Dgram;
                int port = FreePort(type);
                string rule = (type == SocketType.Stream ? "tcp" : "udp") + ":127.0.0.1:" + port + ":" + (100 + i);
                Assert.That(_nat.AddForward(rule, out string message), Is.True, message);
            }
            string[] lines = _nat.DescribeForwards();
            Assert.That(lines.Length, Is.EqualTo(20));
            for (int i = 0; i < lines.Length; i++)
            {
                Assert.That(lines[i], Does.EndWith("listening"), lines[i]);
            }
        }

        // ── Private methods ──

        // A port nobody uses on 127.0.0.1 right now.
        private static int FreePort(SocketType type)
        {
            using Socket s = new Socket(AddressFamily.InterNetwork, type, type == SocketType.Stream ? ProtocolType.Tcp : ProtocolType.Udp);
            s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)s.LocalEndPoint!).Port;
        }

        // The guest sends one frame (an ARP request for the gateway), so the NAT knows its
        // address and MAC, as after any real guest's first packet.
        private void GuestSpeaks()
        {
            byte[] frame = new byte[60];
            Ether(frame, Broadcast, 0x0806);
            Span<byte> body = frame.AsSpan(14);
            BinaryPrimitives.WriteUInt16BigEndian(body, 1);
            BinaryPrimitives.WriteUInt16BigEndian(body.Slice(2), 0x0800);
            body[4] = 6;
            body[5] = 4;
            BinaryPrimitives.WriteUInt16BigEndian(body.Slice(6), 1);
            GuestMac.CopyTo(body.Slice(8));
            NatPacket.WriteAddress(body.Slice(14), Guest);
            NatPacket.WriteAddress(body.Slice(24), Gateway);
            _nat.Send(frame, 0, frame.Length);
            WaitFor(f => Type(f) == 0x0806 && BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(20)) == 2);
        }

        private string ReadExactly(NetworkStream stream, int count)
        {
            byte[] got = new byte[count];
            int read = 0;
            Stopwatch clock = Stopwatch.StartNew();
            while (read < count && clock.ElapsedMilliseconds < WaitMilliseconds)
            {
                _nat.Poll();
                if (stream.DataAvailable)
                {
                    read += stream.Read(got, read, count - read);
                }
                else
                {
                    Thread.Sleep(2);
                }
            }
            return Encoding.ASCII.GetString(got, 0, read);
        }

        private int ReceiveWhilePolling(Socket s, byte[] buffer, ref EndPoint from)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < WaitMilliseconds)
            {
                _nat.Poll();
                if (s.Poll(2000, SelectMode.SelectRead))
                {
                    return s.ReceiveFrom(buffer, ref from);
                }
            }
            Assert.Fail("no datagram back within " + WaitMilliseconds + " ms");
            return 0;
        }

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

        // A TCP segment from the guest to the gateway.
        private void SendTcp(ushort sourcePort, ushort destinationPort, uint seq, uint ack, int flags, ushort window, byte[] data, ushort mss)
        {
            int header = mss != 0 ? 24 : 20;
            byte[] tcp = new byte[header + data.Length];
            data.CopyTo(tcp, header);
            NatPacket.WriteTcp(tcp, header, Guest, Gateway, sourcePort, destinationPort, seq, ack, (byte)flags, window, mss);
            SendIp(NatPacket.ProtocolTcp, Guest, Gateway, tcp);
        }

        private static ushort Type(byte[] f)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(12));
        }

        private static ushort SourcePort(byte[] f)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(34));
        }

        private static ushort DestinationPort(byte[] f)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(36));
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
