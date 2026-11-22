//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

using RetroIO.Network.Router;

namespace RetroIO.Network.Nat
{
    // ICMP through the NAT (RFC 792).
    //
    // A caller's echo request is sent on with the host's own ping (System.Net.NetworkInformation.Ping),
    // which needs no administrator rights. The host picks its own identifier, so the answer is
    // rebuilt for the caller with the caller's identifier and sequence number (the ICMP query
    // translation of RFC 5508 section 3.1) and the data that came back. What the host's ping
    // reports becomes the matching message for the caller:
    //  - success: echo reply, type 0.
    //  - TTL expired on the way: time exceeded, type 11 code 0, from the router that said so.
    //  - network, host, protocol or port unreachable: destination unreachable, type 3, codes 0 to 3.
    //  - timed out: nothing, as on a real network.
    // Anything else (the host ping failed, or gave a status with no ICMP meaning) is counted in
    // PingsUnanswered and the caller hears nothing.
    //
    // A UDP "port unreachable" the host reports (Windows does, as a reset on the next receive) is
    // passed on as destination unreachable, code 3, quoting the caller's last datagram on that
    // mapping.
    public sealed partial class IpNat
    {
        /// <summary>
        /// The most echo requests waiting for a reply at once.
        /// </summary>
        public const int MaxPings = 64;

        /// <summary>
        /// How long the host's ping waits for a reply, in milliseconds.
        /// </summary>
        public const int PingTimeoutMilliseconds = 4000;

        private const byte ProtocolIcmp = 1;
        private const int IcmpHeaderBytes = 8;

        private readonly List<PendingPing> _pings = new List<PendingPing>();
        private readonly byte[] _address4 = new byte[4];
        private int _icmpLength;

        /// <summary>
        /// Gets how many echo requests are waiting for a reply.
        /// </summary>
        public int PingsWaiting => _pings.Count;

        /// <summary>
        /// Gets how many echo requests got no answer for a reason other than a timeout: the host's
        /// ping failed, or reported a status with no ICMP message for it.
        /// </summary>
        public long PingsUnanswered { get; private set; }

        private void FromCallerIcmp(ReadOnlySpan<byte> icmp, uint source, uint destination, byte ttl)
        {
            if (icmp.Length < IcmpHeaderBytes || Checksum(icmp, 0) != 0)
            {
                Drop(NatDropReason.BadIcmp);
                return;
            }

            if (icmp[0] != 8 || icmp[1] != 0)
            {
                // Only echo requests are carried; ICMP errors from a caller to the outside are not.
                Drop(NatDropReason.UnsupportedProtocol);
                return;
            }

            if (_pings.Count >= MaxPings)
            {
                Drop(NatDropReason.TooManyPings);
                return;
            }

            PendingPing pending = new PendingPing(source, destination, BinaryPrimitives.ReadUInt16BigEndian(icmp.Slice(4, 2)), BinaryPrimitives.ReadUInt16BigEndian(icmp.Slice(6, 2)));
            pending.Quote = BuildQuote(source, destination, ttl, icmp.Slice(0, IcmpHeaderBytes));
            pending.Pinger = new Ping();
            byte[] data = icmp.Slice(IcmpHeaderBytes).ToArray();
            IPAddress target = new IPAddress(BinaryPrimitives.ReverseEndianness(destination));
            pending.Reply = pending.Pinger.SendPingAsync(target, PingTimeoutMilliseconds, data, new PingOptions(Math.Max(1, (int)ttl), false));
            _pings.Add(pending);
        }

        // The IP header of the caller's echo request as it was sent, then its first 8 bytes: what an
        // ICMP error quotes (RFC 792). Rebuilt with a 20-byte header.
        private static byte[] BuildQuote(uint source, uint destination, byte ttl, ReadOnlySpan<byte> first8)
        {
            byte[] q = new byte[IpHeaderBytes + IcmpHeaderBytes];
            q[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(q.AsSpan(2), (ushort)q.Length);
            q[8] = ttl;
            q[9] = ProtocolIcmp;
            BinaryPrimitives.WriteUInt32BigEndian(q.AsSpan(12), source);
            BinaryPrimitives.WriteUInt32BigEndian(q.AsSpan(16), destination);
            BinaryPrimitives.WriteUInt16BigEndian(q.AsSpan(10), (ushort)~Ipv4.Sum(q.AsSpan(0, IpHeaderBytes)));
            first8.CopyTo(q.AsSpan(IpHeaderBytes));
            return q;
        }

        // Waits on the oldest ping when there is no socket to wait on, so an owner that paces itself
        // by Service still sleeps.
        private void WaitForPing(int waitMicroseconds)
        {
            if (_pings.Count == 0 || waitMicroseconds <= 0)
            {
                return;
            }

            ((IAsyncResult)_pings[0].Reply!).AsyncWaitHandle.WaitOne(Math.Max(1, waitMicroseconds / 1000));
        }

        private void TakePingReplies()
        {
            for (int i = 0; i < _pings.Count; i++)
            {
                PendingPing p = _pings[i];
                if (!p.Reply!.IsCompleted)
                {
                    continue;
                }

                _pings.RemoveAt(i);
                i--;
                p.Pinger!.Dispose();
                if (!p.Reply.IsCompletedSuccessfully)
                {
                    PingsUnanswered++;
                    continue;
                }

                Answer(p, p.Reply.Result);
            }
        }

        private void Answer(PendingPing p, PingReply reply)
        {
            uint from = p.Destination;
            if (reply.Address != null && reply.Address.TryWriteBytes(_address4, out int written) && written == 4)
            {
                uint reported = BinaryPrimitives.ReadUInt32BigEndian(_address4);
                if (reported != 0)
                {
                    from = reported;
                }
            }

            switch (reply.Status)
            {
                case IPStatus.Success:
                    SendEchoReply(p, reply.Buffer);
                    return;
                case IPStatus.TimedOut:
                    return;
                case IPStatus.TtlExpired:
                case IPStatus.TimeExceeded:
                    SendIcmpError(from, p.Caller, 11, 0, p.Quote!);
                    return;
                case IPStatus.DestinationNetworkUnreachable:
                    SendIcmpError(from, p.Caller, 3, 0, p.Quote!);
                    return;
                case IPStatus.DestinationHostUnreachable:
                    SendIcmpError(from, p.Caller, 3, 1, p.Quote!);
                    return;
                case IPStatus.DestinationProtocolUnreachable:
                    SendIcmpError(from, p.Caller, 3, 2, p.Quote!);
                    return;
                case IPStatus.DestinationPortUnreachable:
                    SendIcmpError(from, p.Caller, 3, 3, p.Quote!);
                    return;
                default:
                    PingsUnanswered++;
                    return;
            }
        }

        private void SendEchoReply(PendingPing p, byte[] data)
        {
            Span<byte> icmp = StartIcmp(p.Destination, p.Caller, IcmpHeaderBytes + data.Length);
            icmp[0] = 0;
            icmp[1] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(icmp.Slice(4, 2), p.Identifier);
            BinaryPrimitives.WriteUInt16BigEndian(icmp.Slice(6, 2), p.Sequence);
            data.CopyTo(icmp.Slice(IcmpHeaderBytes));
            FinishIcmp(icmp);
        }

        private void SendPortUnreachable(UdpMapping m)
        {
            if (m.LastQuoteLength == 0)
            {
                return;
            }

            SendIcmpError(m.LastDestination, m.CallerAddress, 3, 3, m.LastQuote.AsSpan(0, m.LastQuoteLength));
        }

        // An ICMP error: type, code, 4 unused bytes, then the quoted header and 8 bytes (RFC 792).
        private void SendIcmpError(uint from, uint caller, byte type, byte code, ReadOnlySpan<byte> quote)
        {
            Span<byte> icmp = StartIcmp(from, caller, IcmpHeaderBytes + quote.Length);
            icmp[0] = type;
            icmp[1] = code;
            quote.CopyTo(icmp.Slice(IcmpHeaderBytes));
            FinishIcmp(icmp);
        }

        // Writes the IP header of an ICMP packet into the packet buffer and returns the ICMP part,
        // cleared, of the given length.
        private Span<byte> StartIcmp(uint source, uint destination, int icmpLength)
        {
            int total = IpHeaderBytes + icmpLength;
            if (_packet.Length < total)
            {
                _packet = new byte[total];
            }

            Span<byte> p = _packet.AsSpan(0, total);
            p.Clear();
            p[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(p.Slice(2, 2), (ushort)total);
            BinaryPrimitives.WriteUInt16BigEndian(p.Slice(4, 2), ++_identification);
            p[8] = DefaultTtl;
            p[9] = ProtocolIcmp;
            BinaryPrimitives.WriteUInt32BigEndian(p.Slice(12, 4), source);
            BinaryPrimitives.WriteUInt32BigEndian(p.Slice(16, 4), destination);
            BinaryPrimitives.WriteUInt16BigEndian(p.Slice(10, 2), (ushort)~Ipv4.Sum(p.Slice(0, IpHeaderBytes)));
            _icmpLength = icmpLength;
            return p.Slice(IpHeaderBytes, icmpLength);
        }

        private void FinishIcmp(Span<byte> icmp)
        {
            BinaryPrimitives.WriteUInt16BigEndian(icmp.Slice(2, 2), Checksum(icmp, 0));
            _router.SendFromUpstream(_packet.AsSpan(0, IpHeaderBytes + _icmpLength));
        }

        private void DropAllPings()
        {
            for (int i = 0; i < _pings.Count; i++)
            {
                _pings[i].Pinger!.Dispose();
            }

            _pings.Clear();
        }

        // One echo request sent on with the host's ping.
        private sealed class PendingPing
        {
            public PendingPing(uint caller, uint destination, ushort identifier, ushort sequence)
            {
                Caller = caller;
                Destination = destination;
                Identifier = identifier;
                Sequence = sequence;
            }

            public uint Caller { get; }

            public uint Destination { get; }

            public ushort Identifier { get; }

            public ushort Sequence { get; }

            public byte[]? Quote { get; set; }

            public Ping? Pinger { get; set; }

            public Task<PingReply>? Reply { get; set; }
        }
    }
}
