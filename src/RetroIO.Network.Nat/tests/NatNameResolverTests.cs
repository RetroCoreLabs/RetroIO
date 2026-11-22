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
    /// The NAT's name lookup fallback for a host with no name server address (host networking
    /// design, section 7.3, DNS row): A queries are answered from the host's resolver, by the
    /// rules of RFC 1035.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class NatNameResolverTests
    {
        // ── Constants ──

        private const uint Guest = 0x0A00020F;

        private const uint NameServer = 0x0A000203;

        // ── Fields ──

        private static readonly byte[] GuestMac = { 0x02, 0x00, 0x00, 0x11, 0x22, 0x33 };

        private static readonly byte[] GatewayMac = { 0x02, 0x52, 0x43, 0x00, 0x02, 0x02 };

        // ── Public methods ──

        [Test]
        public void Answer_AQuery_GivesOneARecordPerIpv4Address()
        {
            byte[] query = Query(0x4242, "host.example", 1);
            byte[] answer = NatNameResolver.Answer(query, name => name == "host.example"
                ? new[] { IPAddress.Parse("10.1.2.3"), IPAddress.IPv6Loopback, IPAddress.Parse("10.1.2.4") }
                : throw new SocketException((int)SocketError.HostNotFound))!;

            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(answer), Is.EqualTo(0x4242), "ID copied");
            Assert.That(answer[2] & 0x80, Is.EqualTo(0x80), "QR: a response");
            Assert.That(answer[2] & 0x01, Is.EqualTo(0x01), "RD copied");
            Assert.That(answer[3] & 0x0F, Is.EqualTo(0), "RCODE 0");
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(6)), Is.EqualTo(2), "two A records, the IPv6 address left out");
            int first = query.Length;
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(first)), Is.EqualTo(0xC00C), "name points at the question");
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(first + 2)), Is.EqualTo(1), "type A");
            Assert.That(answer.AsSpan(first + 12, 4).ToArray(), Is.EqualTo(new byte[] { 10, 1, 2, 3 }));
            Assert.That(answer.AsSpan(first + 28, 4).ToArray(), Is.EqualTo(new byte[] { 10, 1, 2, 4 }));
        }

        [Test]
        public void Answer_AnUnknownName_IsNameError()
        {
            byte[] answer = NatNameResolver.Answer(Query(1, "nothing.example", 1), name => throw new SocketException((int)SocketError.HostNotFound))!;
            Assert.That(answer[3] & 0x0F, Is.EqualTo(3), "RCODE 3, Name Error");
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(6)), Is.EqualTo(0));
        }

        [Test]
        public void Answer_AnAaaaQuery_IsNotImplemented()
        {
            byte[] answer = NatNameResolver.Answer(Query(1, "host.example", 28), name => new[] { IPAddress.Parse("10.1.2.3") })!;
            Assert.That(answer[3] & 0x0F, Is.EqualTo(4), "RCODE 4, Not Implemented: only name-to-address (A) lookups work");
        }

        /// <summary>
        /// With no host name server, a guest's query to 10.0.2.3 port 53 is answered from the
        /// host's resolver, from 10.0.2.3 port 53.
        /// </summary>
        [Test]
        public void Nat_WithNoHostNameServer_AnswersALookupOfLocalhost()
        {
            NatEthernetBackend nat = new NatEthernetBackend(new NatAddressPlan(), true, null, false);
            List<byte[]> received = new List<byte[]>();
            nat.OnFrameReceived += (frame, offset, length) => received.Add(new ReadOnlySpan<byte>(frame, offset, length).ToArray());
            nat.Start();
            try
            {
                Assert.That(nat.Router!.NameServer, Is.Null);
                Assert.That(nat.Router.Resolver, Is.Not.Null, "the fallback is on");
                byte[] query = Query(0x0777, "localhost", 1);
                byte[] udp = new byte[8 + query.Length];
                query.CopyTo(udp, 8);
                NatPacket.WriteUdp(udp, Guest, NameServer, 3333, 53);
                byte[] frame = new byte[34 + udp.Length];
                GatewayMac.CopyTo(frame, 0);
                GuestMac.CopyTo(frame, 6);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0800);
                udp.CopyTo(frame, 34);
                NatPacket.WriteIpv4(frame.AsSpan(14), 20 + udp.Length, 3, NatPacket.ProtocolUdp, Guest, NameServer);
                nat.Send(frame, 0, frame.Length);

                byte[]? reply = null;
                Stopwatch clock = Stopwatch.StartNew();
                while (reply == null && clock.ElapsedMilliseconds < 10000)
                {
                    nat.Poll();
                    for (int i = 0; i < received.Count; i++)
                    {
                        if (received[i][23] == NatPacket.ProtocolUdp && BinaryPrimitives.ReadUInt16BigEndian(received[i].AsSpan(36)) == 3333)
                        {
                            reply = received[i];
                        }
                    }
                    Thread.Sleep(2);
                }
                Assert.That(reply, Is.Not.Null, "an answer came back");
                Assert.That(NatPacket.ReadAddress(reply!.AsSpan(26)), Is.EqualTo(NameServer), "from 10.0.2.3");
                Assert.That(BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(34)), Is.EqualTo(53), "from port 53");
                byte[] dns = reply.AsSpan(42, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(38)) - 8).ToArray();
                Assert.That(BinaryPrimitives.ReadUInt16BigEndian(dns), Is.EqualTo(0x0777), "the guest's ID");
                Assert.That(dns[3] & 0x0F, Is.EqualTo(0), "RCODE 0");
                Assert.That(BinaryPrimitives.ReadUInt16BigEndian(dns.AsSpan(6)), Is.GreaterThanOrEqualTo(1), "at least one A record");
                Assert.That(dns.AsSpan(query.Length + 12, 4).ToArray(), Is.EqualTo(new byte[] { 127, 0, 0, 1 }), "localhost is 127.0.0.1");
            }
            finally
            {
                nat.Stop();
            }
        }

        // ── Private methods ──

        // A standard query with RD set and one question.
        private static byte[] Query(ushort id, string name, ushort type)
        {
            string[] labels = name.Split('.');
            int length = 12 + 1 + 4;
            for (int i = 0; i < labels.Length; i++)
            {
                length += 1 + labels[i].Length;
            }
            byte[] q = new byte[length];
            BinaryPrimitives.WriteUInt16BigEndian(q, id);
            q[2] = 0x01;
            BinaryPrimitives.WriteUInt16BigEndian(q.AsSpan(4), 1);
            int o = 12;
            for (int i = 0; i < labels.Length; i++)
            {
                q[o++] = (byte)labels[i].Length;
                Encoding.ASCII.GetBytes(labels[i]).CopyTo(q, o);
                o += labels[i].Length;
            }
            q[o++] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(q.AsSpan(o), type);
            BinaryPrimitives.WriteUInt16BigEndian(q.AsSpan(o + 2), 1);
            return q;
        }
    }
}
