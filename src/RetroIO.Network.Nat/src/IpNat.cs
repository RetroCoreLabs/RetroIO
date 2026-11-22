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
using System.Net.Sockets;

using RetroIO.Network.Router;

namespace RetroIO.Network.Nat
{
    /// <summary>
    /// Address sharing (NAT) for an <see cref="IpRouter"/>: the callers' TCP and UDP go to the
    /// internet through ordinary host sockets, and the replies come back to them.
    /// </summary>
    /// <remarks>
    /// <para><b>How it works</b></para>
    /// The NAT takes every packet the router sends upstream. For TCP it ends the caller's connection
    /// itself and opens a matching one from the host. For each caller address and UDP port it opens
    /// one host socket (a mapping), sends the datagram on with it, and later turns what comes back to
    /// that socket into a packet for the caller, given to <see cref="IpRouter.SendFromUpstream"/>.
    /// This is traditional NAT with port translation (RFC 3022 section 2.2), where the host's own
    /// network stack picks the outside port.
    /// <para><b>UDP behaviour (RFC 4787)</b></para>
    /// What the NAT does, by the RFC's requirements:
    ///  - one mapping per caller address and port, used for every destination (REQ-1, endpoint
    ///    independent mapping).
    ///  - any outside host may answer on a mapping (endpoint independent filtering, REQ-8 allows it).
    ///  - a mapping lives two minutes after its last datagram in either direction (REQ-5).
    /// <para><b>Threads and time</b></para>
    /// No thread and no timer of its own. The owner calls <see cref="Service"/> from the same thread
    /// that drives the router, and passes the time; mappings age by that time only.
    /// <para><b>Not built</b></para>
    /// Each of these is dropped and counted under its <see cref="NatDropReason"/>, never answered
    /// with something made up:
    ///  - ICMP other than echo request (<see cref="NatDropReason.UnsupportedProtocol"/>).
    /// </remarks>
    public sealed partial class IpNat : IDisposable
    {
        /// <summary>
        /// How long a mapping lives after its last datagram, in milliseconds (RFC 4787 REQ-5: at least
        /// two minutes).
        /// </summary>
        public const long UdpIdleMilliseconds = 120000;

        /// <summary>
        /// The most mappings at once; a datagram that would need one more is dropped as
        /// <see cref="NatDropReason.TooManyMappings"/>.
        /// </summary>
        public const int MaxUdpMappings = 256;

        private const int IpHeaderBytes = 20;
        private const int UdpHeaderBytes = 8;
        private const byte ProtocolUdp = 17;
        private const byte ProtocolTcp = 6;
        private const byte DefaultTtl = 64;

        private readonly IpRouter _router;
        private readonly List<UdpMapping> _udp = new List<UdpMapping>();
        private readonly List<Socket> _readList = new List<Socket>();
        private readonly List<Socket> _writeList = new List<Socket>();
        private readonly List<Socket> _errorList = new List<Socket>();
        private readonly long[] _drops = new long[(int)NatDropReason.TooManyPings + 1];
        private readonly byte[] _receive = new byte[65536];
        private readonly IpRouter.PacketOutput _upstream;
        private byte[] _packet = new byte[2048];
        private ushort _identification;
        private long _now;
        private bool _disposed;

        /// <summary>
        /// Called when the NAT drops a packet.
        /// </summary>
        /// <param name="reason">
        /// Why it was dropped.
        /// </param>
        /// <param name="count">
        /// How many packets have been dropped for this reason so far, this one included.
        /// </param>
        public delegate void PacketDropped(NatDropReason reason, long count);

        /// <summary>
        /// Raised for each packet dropped.
        /// </summary>
        public event PacketDropped? OnDrop;

        /// <summary>
        /// Creates a NAT and attaches it to the router's upstream side.
        /// </summary>
        /// <param name="router">
        /// The router whose packets for outside addresses this NAT takes.
        /// </param>
        /// <param name="nowMilliseconds">
        /// The owner's clock now, the same clock later passed to <see cref="Service"/>.
        /// </param>
        public IpNat(IpRouter router, long nowMilliseconds)
        {
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _now = nowMilliseconds;
            _upstream = FromRouter;
            _router.OnUpstream += _upstream;
        }

        /// <summary>
        /// Gets how many UDP mappings are open.
        /// </summary>
        public int UdpMappings => _udp.Count;

        /// <summary>
        /// Gets how many times the host reported that an outside port was unreachable.
        /// </summary>
        /// <remarks>
        /// Windows reports the ICMP "port unreachable" for an earlier send as a reset on the next
        /// receive; each one is passed on to the caller as an ICMP port unreachable. Other systems
        /// do not report it to an unconnected socket, so there the caller hears nothing.
        /// </remarks>
        public long HostPortUnreachable { get; private set; }

        /// <summary>
        /// Gets how many packets have been dropped for one reason.
        /// </summary>
        /// <param name="reason">
        /// The reason.
        /// </param>
        /// <returns>
        /// The count.
        /// </returns>
        public long DropCount(NatDropReason reason)
        {
            return _drops[(int)reason];
        }

        /// <summary>
        /// Waits for the host sockets, turns what arrived into packets for the callers, and closes
        /// mappings that have been idle too long.
        /// </summary>
        /// <param name="nowMilliseconds">
        /// The owner's clock now.
        /// </param>
        /// <param name="waitMicroseconds">
        /// The longest wait for a socket, in microseconds; 0 only looks.
        /// </param>
        /// <returns>
        /// True when there was any socket or ping reply to wait on.
        /// </returns>
        /// <exception cref="ObjectDisposedException">
        /// Thrown after <see cref="Dispose"/>.
        /// </exception>
        public bool Service(long nowMilliseconds, int waitMicroseconds)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _now = nowMilliseconds;
            for (int i = _udp.Count - 1; i >= 0; i--)
            {
                if (_now - _udp[i].LastUsed >= UdpIdleMilliseconds)
                {
                    _udp[i].Host.Close();
                    _udp.RemoveAt(i);
                }
            }

            _readList.Clear();
            _writeList.Clear();
            _errorList.Clear();
            for (int i = 0; i < _udp.Count; i++)
            {
                _readList.Add(_udp[i].Host);
            }

            AddTcpSockets();
            TakePingReplies();
            bool waited = _readList.Count + _writeList.Count + _errorList.Count > 0;
            if (waited)
            {
                Socket.Select(_readList.Count > 0 ? _readList : null, _writeList.Count > 0 ? _writeList : null, _errorList.Count > 0 ? _errorList : null, waitMicroseconds);
                ServiceTcpConnects();
                for (int i = 0; i < _readList.Count; i++)
                {
                    UdpMapping? m = FindMapping(_readList[i]);
                    if (m != null)
                    {
                        ReadUdp(m);
                    }
                }

                ServiceTcpSockets();
            }

            if (!waited)
            {
                WaitForPing(waitMicroseconds);
            }

            TakePingReplies();
            RunTcpTimers();
            ExpireReassemblies();
            return waited || _pings.Count > 0;
        }

        /// <summary>
        /// Leaves the router and closes every host socket.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _router.OnUpstream -= _upstream;
            for (int i = 0; i < _udp.Count; i++)
            {
                _udp[i].Host.Close();
            }

            _udp.Clear();
            CloseAllTcp();
            DropAllPings();
        }

        // The Internet checksum (RFC 1071): the ones' complement of the ones' complement sum of the
        // 16-bit words, an odd last byte padded with a zero byte. The start is the pseudo-header sum.
        private static ushort Checksum(ReadOnlySpan<byte> data, uint start)
        {
            uint sum = start;
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

        // The UDP and TCP pseudo-header (RFC 768, RFC 9293 section 3.1): source, destination, zero
        // and protocol, then the length of the UDP or TCP part.
        private static uint PseudoHeaderSum(uint source, uint destination, int length, byte protocol = ProtocolUdp)
        {
            return (source >> 16) + (source & 0xFFFF) + (destination >> 16) + (destination & 0xFFFF) + protocol + (uint)length;
        }

        private static bool IsUnicast(uint address)
        {
            // 0.0.0.0/8 ("this network", RFC 1122 section 3.2.1.3) and 224.0.0.0 and up (multicast,
            // the reserved range and the limited broadcast) are not places a socket can send to.
            return (address >> 24) != 0 && address < 0xE0000000;
        }

        private void FromRouter(ReadOnlySpan<byte> packet)
        {
            if (_disposed)
            {
                return;
            }

            int headerBytes = (packet[0] & 0x0F) * 4;
            ushort fragment = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2));
            if ((fragment & 0x3FFF) != 0)
            {
                // More Fragments set, or a fragment offset: this packet is part of a larger one.
                Reassemble(packet, headerBytes, fragment);
                return;
            }

            FromRouterWhole(packet, headerBytes);
        }

        // A whole packet: never fragmented, or put back together.
        private void FromRouterWhole(ReadOnlySpan<byte> packet, int headerBytes)
        {
            uint source = Ipv4.Source(packet);
            uint destination = Ipv4.Destination(packet);
            if (packet[9] != ProtocolUdp && packet[9] != ProtocolTcp && packet[9] != ProtocolIcmp)
            {
                Drop(NatDropReason.UnsupportedProtocol);
                return;
            }

            if (!IsUnicast(destination))
            {
                Drop(NatDropReason.NotUnicast);
                return;
            }

            if (packet[9] == ProtocolIcmp)
            {
                FromCallerIcmp(packet.Slice(headerBytes), source, destination, packet[8]);
                return;
            }

            if (packet[9] == ProtocolTcp)
            {
                FromCallerTcp(packet.Slice(headerBytes), source, destination);
                return;
            }

            ReadOnlySpan<byte> udp = packet.Slice(headerBytes);
            if (udp.Length < UdpHeaderBytes)
            {
                Drop(NatDropReason.BadUdp);
                return;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(udp.Slice(4, 2));
            if (length < UdpHeaderBytes || length > udp.Length)
            {
                Drop(NatDropReason.BadUdp);
                return;
            }

            udp = udp.Slice(0, length);

            // A checksum of 0 means the sender computed none (RFC 768); any other value must check.
            if (BinaryPrimitives.ReadUInt16BigEndian(udp.Slice(6, 2)) != 0 && Checksum(udp, PseudoHeaderSum(source, destination, length)) != 0)
            {
                Drop(NatDropReason.BadUdp);
                return;
            }

            ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(udp);
            ushort destinationPort = BinaryPrimitives.ReadUInt16BigEndian(udp.Slice(2, 2));
            UdpMapping? mapping = null;
            for (int i = 0; i < _udp.Count; i++)
            {
                if (_udp[i].CallerAddress == source && _udp[i].CallerPort == sourcePort)
                {
                    mapping = _udp[i];
                    break;
                }
            }

            if (mapping == null)
            {
                if (_udp.Count >= MaxUdpMappings)
                {
                    Drop(NatDropReason.TooManyMappings);
                    return;
                }

                Socket host = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                host.Blocking = false;
                host.Bind(new IPEndPoint(IPAddress.Any, 0));
                mapping = new UdpMapping(source, sourcePort, host);
                _udp.Add(mapping);
            }

            mapping.LastUsed = _now;
            mapping.Remember(packet.Slice(0, headerBytes + UdpHeaderBytes), destination);
            IPEndPoint target = new IPEndPoint(new IPAddress(BinaryPrimitives.ReverseEndianness(destination)), destinationPort);
            try
            {
                mapping.Host.SendTo(udp.Slice(UdpHeaderBytes), SocketFlags.None, target);
            }
            catch (SocketException)
            {
                // The host would not send it (no route, buffer full). UDP promises nothing, so the
                // caller's own retry covers it; the drop is counted.
                Drop(NatDropReason.HostSendFailed);
            }
        }

        private void ReadUdp(UdpMapping m)
        {
            Span<byte> address = stackalloc byte[4];
            while (true)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try
                {
                    n = m.Host.ReceiveFrom(_receive, ref from);
                }
                catch (SocketException e)
                {
                    if (e.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        HostPortUnreachable++;
                        SendPortUnreachable(m);
                        continue;
                    }

                    if (e.SocketErrorCode == SocketError.WouldBlock)
                    {
                        return;
                    }

                    throw;
                }

                m.LastUsed = _now;
                IPEndPoint remote = (IPEndPoint)from;
                if (!remote.Address.TryWriteBytes(address, out int written) || written != 4)
                {
                    continue;
                }

                uint source = BinaryPrimitives.ReadUInt32BigEndian(address);
                int total = IpHeaderBytes + UdpHeaderBytes + n;
                if (total > 65535)
                {
                    Drop(NatDropReason.TooBigForIp);
                    continue;
                }

                if (_packet.Length < total)
                {
                    _packet = new byte[total];
                }

                Span<byte> p = _packet.AsSpan(0, total);
                p.Slice(0, IpHeaderBytes + UdpHeaderBytes).Clear();
                p[0] = 0x45;
                BinaryPrimitives.WriteUInt16BigEndian(p.Slice(2, 2), (ushort)total);
                BinaryPrimitives.WriteUInt16BigEndian(p.Slice(4, 2), ++_identification);
                p[8] = DefaultTtl;
                p[9] = ProtocolUdp;
                BinaryPrimitives.WriteUInt32BigEndian(p.Slice(12, 4), source);
                BinaryPrimitives.WriteUInt32BigEndian(p.Slice(16, 4), m.CallerAddress);
                BinaryPrimitives.WriteUInt16BigEndian(p.Slice(10, 2), (ushort)~Ipv4.Sum(p.Slice(0, IpHeaderBytes)));

                Span<byte> udp = p.Slice(IpHeaderBytes);
                int udpLength = UdpHeaderBytes + n;
                BinaryPrimitives.WriteUInt16BigEndian(udp, (ushort)remote.Port);
                BinaryPrimitives.WriteUInt16BigEndian(udp.Slice(2, 2), m.CallerPort);
                BinaryPrimitives.WriteUInt16BigEndian(udp.Slice(4, 2), (ushort)udpLength);
                _receive.AsSpan(0, n).CopyTo(udp.Slice(UdpHeaderBytes));
                ushort checksum = Checksum(udp, PseudoHeaderSum(source, m.CallerAddress, udpLength));

                // A computed 0 is sent as FFFFh, since 0 means "no checksum" (RFC 768).
                BinaryPrimitives.WriteUInt16BigEndian(udp.Slice(6, 2), checksum == 0 ? (ushort)0xFFFF : checksum);
                _router.SendFromUpstream(p);
            }
        }

        private UdpMapping? FindMapping(Socket s)
        {
            for (int i = 0; i < _udp.Count; i++)
            {
                if (_udp[i].Host == s)
                {
                    return _udp[i];
                }
            }

            return null;
        }

        private void Drop(NatDropReason reason)
        {
            long count = ++_drops[(int)reason];
            OnDrop?.Invoke(reason, count);
        }

        // One caller address and port, and the host socket that stands for it outside.
        private sealed class UdpMapping
        {
            public UdpMapping(uint callerAddress, ushort callerPort, Socket host)
            {
                CallerAddress = callerAddress;
                CallerPort = callerPort;
                Host = host;
            }

            public uint CallerAddress { get; }

            public ushort CallerPort { get; }

            public Socket Host { get; }

            public long LastUsed { get; set; }

            // The IP header and first 8 bytes of the caller's last datagram, which an ICMP error
            // quotes (RFC 792), and where that datagram went.
            public byte[] LastQuote { get; } = new byte[60 + UdpHeaderBytes];

            public int LastQuoteLength { get; private set; }

            public uint LastDestination { get; private set; }

            public void Remember(ReadOnlySpan<byte> quote, uint destination)
            {
                quote.CopyTo(LastQuote);
                LastQuoteLength = quote.Length;
                LastDestination = destination;
            }
        }
    }

    /// <summary>
    /// Why the NAT dropped a packet.
    /// </summary>
    public enum NatDropReason
    {
        /// <summary>
        /// Not UDP, TCP or an ICMP echo request.
        /// </summary>
        UnsupportedProtocol,

        /// <summary>
        /// A fragment that cannot be part of a good packet: it would end past 65535 bytes, or it is
        /// not the last fragment and its data is not a multiple of 8 bytes (RFC 791).
        /// </summary>
        Fragment,

        /// <summary>
        /// A fragment of a new packet while <see cref="IpNat.MaxReassemblies"/> packets are already
        /// being put back together.
        /// </summary>
        TooManyReassemblies,

        /// <summary>
        /// A packet whose fragments did not all arrive within
        /// <see cref="IpNat.ReassemblyTimeoutMilliseconds"/>; counted once per packet.
        /// </summary>
        ReassemblyTimeout,

        /// <summary>
        /// For a multicast, reserved or "this network" address, which a host socket cannot send to.
        /// </summary>
        NotUnicast,

        /// <summary>
        /// The UDP header is too short, its length does not fit the packet, or its checksum is wrong.
        /// </summary>
        BadUdp,

        /// <summary>
        /// A new mapping was needed and <see cref="IpNat.MaxUdpMappings"/> are already open.
        /// </summary>
        TooManyMappings,

        /// <summary>
        /// A reply from outside is too large to fit in one IPv4 packet.
        /// </summary>
        TooBigForIp,

        /// <summary>
        /// The host socket refused to send the datagram.
        /// </summary>
        HostSendFailed,

        /// <summary>
        /// The TCP header is too short, its data offset does not fit, or its checksum is wrong.
        /// </summary>
        BadTcp,

        /// <summary>
        /// A TCP connection was asked for and <see cref="IpNat.MaxTcpConnections"/> are already
        /// open; the caller gets a reset.
        /// </summary>
        TooManyConnections,

        /// <summary>
        /// The host could not connect for a reason other than a refusal (a timeout, no route); the
        /// caller gets no answer and gives up by itself.
        /// </summary>
        HostConnectFailed,

        /// <summary>
        /// An ICMP message whose checksum is wrong or that is too short.
        /// </summary>
        BadIcmp,

        /// <summary>
        /// An echo request while <see cref="IpNat.MaxPings"/> are already waiting for a reply.
        /// </summary>
        TooManyPings,
    }
}
