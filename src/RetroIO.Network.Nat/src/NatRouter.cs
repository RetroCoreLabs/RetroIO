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
using System.Net.Sockets;

namespace RetroIO.Network.Nat
{
    /// <summary>
    /// Hands one frame from the NAT to the guest.
    /// </summary>
    /// <param name="frame">
    /// The frame (no FCS), at least 60 bytes.
    /// </param>
    public delegate void NatFrameOutput(ReadOnlySpan<byte> frame);

    /// <summary>
    /// The router of the built-in NAT: it answers ARP for the gateway and name server, hands
    /// out addresses with DHCP and BOOTP, answers ping to the gateway, and relays the guest's
    /// UDP and TCP to ordinary host sockets (RetroCore's host networking design (host-networking-design.md in the RetroCore repository),
    /// section 7, phase 1), and forwards host ports to
    /// the guest (section 7.7, phase 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every method runs on ONE thread, the NAT thread (design section 7.5). Nothing here ever
    /// calls into a chip: frames toward the guest go through <see cref="NatFrameOutput"/>, which
    /// the backend points at a queue the emulation thread drains.
    /// </para>
    /// <para>
    /// Guest-side TCP timers count emulated time in polls: the poll count the caller passes is
    /// the number of <c>IEthernetBackend.Poll</c> calls so far, one per host video frame
    /// (design section 2), so a paused or slowed machine is not flooded with retransmissions.
    /// Host-side timeouts (the UDP mapping lifetime) count host time.
    /// </para>
    /// <para>
    /// NOT BUILT in this phase, each dropped and counted, never answered with something made up:
    /// IP fragments from the guest and UDP replies bigger than one frame (phase 3), ping to
    /// outside addresses (phase 3), traffic between guests (phase 4).
    /// </para>
    /// </remarks>
    public sealed class NatRouter
    {
        // ── Constants ──

        /// <summary>
        /// Polls of emulated time per second: one poll per host video frame at 60 Hz.
        /// </summary>
        public const int PollsPerSecond = 60;

        /// <summary>
        /// The most TCP connections at once; a SYN beyond it gets RST (design 7.4, limits).
        /// </summary>
        public const int MaxTcpConnections = 256;

        /// <summary>
        /// The most UDP mappings at once; a datagram beyond it is dropped and counted.
        /// </summary>
        public const int MaxUdpMappings = 256;

        /// <summary>
        /// The largest TCP data in one frame to the guest: 1514 - 14 - 20 - 20.
        /// </summary>
        public const int MaxSegmentData = 1460;

        /// <summary>
        /// The MSS the guest is assumed to take when it sends none (RFC 9293 section 3.7.1, RFC 1122).
        /// </summary>
        public const int DefaultGuestMss = 536;

        /// <summary>
        /// Lease time handed out by DHCP, in seconds (one day).
        /// </summary>
        public const uint LeaseSeconds = 86400;

        /// <summary>
        /// How long an idle UDP mapping lives, host milliseconds (RFC 4787 REQ-5: at least two minutes).
        /// </summary>
        public const long UdpIdleMilliseconds = 120000;

        /// <summary>
        /// Retransmits of one segment before the connection is reset.
        /// </summary>
        public const int MaxRetries = 12;

        /// <summary>
        /// SYNs sent to the guest for one forwarded connection before the host client is let go.
        /// </summary>
        public const int MaxSynTries = 6;

        /// <summary>
        /// ARP requests for a guest that a forwarded connection waits for before the host client is let go.
        /// </summary>
        public const int MaxArpTries = 5;

        /// <summary>
        /// The first gateway port a forwarded connection or UDP sender gets toward the guest
        /// (the start of the dynamic ports, RFC 6335 section 6).
        /// </summary>
        public const int FirstGatewayPort = 49152;

        private const int FrameBytes = 1514;

        private const int MaxUdpData = FrameBytes - NatPacket.EthernetHeaderBytes - NatPacket.Ipv4HeaderBytes - NatPacket.UdpHeaderBytes;

        // ── Fields ──

        private readonly NatAddressPlan _plan;

        private readonly NatFrameOutput _output;

        private readonly IPEndPoint? _nameServer;

        private readonly NatNameResolver? _resolver;

        private readonly List<NatTcpConnection> _tcp = new List<NatTcpConnection>();

        private readonly List<NatUdpMapping> _udp = new List<NatUdpMapping>();

        private readonly List<byte[]> _leaseMacs = new List<byte[]>();

        private readonly List<uint> _leaseAddresses = new List<uint>();

        private readonly List<Socket> _readList = new List<Socket>();

        private readonly List<Socket> _writeList = new List<Socket>();

        private readonly List<Socket> _errorList = new List<Socket>();

        private readonly byte[] _frame = new byte[FrameBytes];

        private readonly byte[] _receive = new byte[65536];

        private readonly byte[] _broadcastMac = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        private readonly Random _random = new Random();

        private readonly List<NatForwardBinding> _forwards = new List<NatForwardBinding>();

        private readonly List<NatForwardBinding> _forwardsToAdd = new List<NatForwardBinding>();

        private readonly List<NatForwardBinding> _forwardsToRemove = new List<NatForwardBinding>();

        private readonly object _forwardGate = new object();

        private readonly List<NatUdpForwardSession> _udpForwards = new List<NatUdpForwardSession>();

        // Guest addresses seen in the guest's own frames, with the MAC each came from.
        private readonly List<uint> _seenAddresses = new List<uint>();

        private readonly List<byte[]> _seenMacs = new List<byte[]>();

        private int _nextGatewayPort = FirstGatewayPort;

        private ushort _ipIdentification;

        private long _polls;

        // ── Constructors ──

        /// <summary>
        /// Initializes a router.
        /// </summary>
        /// <param name="plan">
        /// The address plan.
        /// </param>
        /// <param name="output">
        /// Where frames toward the guest go.
        /// </param>
        /// <param name="hostLoopback">
        /// <c>true</c> to let connections to the gateway address reach the host's own 127.0.0.1
        /// (design 7.9; the owner's decision of 2026-11-12: on, with a setting to turn it off).
        /// </param>
        /// <param name="nameServer">
        /// The host name server queries to the name server address go to; <c>null</c> to take the
        /// first IPv4 name server of the host's network interfaces when
        /// <paramref name="findHostNameServer"/> is set.
        /// </param>
        /// <param name="findHostNameServer">
        /// <c>true</c> to look for the host's name server when <paramref name="nameServer"/> is
        /// <c>null</c>; <c>false</c> to use the name lookup fallback at once (as on a host with no
        /// name server address).
        /// </param>
        public NatRouter(NatAddressPlan plan, NatFrameOutput output, bool hostLoopback, IPEndPoint? nameServer, bool findHostNameServer)
        {
            _plan = plan ?? throw new ArgumentNullException(nameof(plan));
            _output = output ?? throw new ArgumentNullException(nameof(output));
            HostLoopback = hostLoopback;
            _nameServer = nameServer ?? (findHostNameServer ? FindHostNameServer() : null);
            if (_nameServer == null)
            {
                // No name server address on this host: answer name-to-address lookups from the
                // host's own resolver (design 7.3, DNS row, the fallback).
                _resolver = new NatNameResolver();
            }
        }

        // ── Properties ──

        /// <summary>
        /// Gets a value indicating whether the gateway address reaches the host's 127.0.0.1.
        /// </summary>
        public bool HostLoopback { get; }

        /// <summary>
        /// Gets the host name server in use, or <c>null</c> when the host has none.
        /// </summary>
        public IPEndPoint? NameServer => _nameServer;

        /// <summary>
        /// Gets the name lookup fallback, or <c>null</c> when a host name server is in use.
        /// </summary>
        public NatNameResolver? Resolver => _resolver;

        /// <summary>
        /// Gets the number of open TCP connections.
        /// </summary>
        public int TcpConnections => _tcp.Count;

        /// <summary>
        /// Gets the number of UDP mappings.
        /// </summary>
        public int UdpMappings => _udp.Count;

        /// <summary>
        /// Gets frames from the guest that were not ARP or IPv4, or too short.
        /// </summary>
        public long DroppedUnsupported { get; private set; }

        /// <summary>
        /// Gets IPv4 packets with a bad header or checksum.
        /// </summary>
        public long DroppedBadHeader { get; private set; }

        /// <summary>
        /// Gets IPv4 fragments from the guest (reassembly is phase 3).
        /// </summary>
        public long DroppedFragments { get; private set; }

        /// <summary>
        /// Gets pings to addresses other than the gateway and name server (phase 3).
        /// </summary>
        public long DroppedOutsidePing { get; private set; }

        /// <summary>
        /// Gets packets to other addresses inside the guest network (phase 4), or to the gateway with host loopback off.
        /// </summary>
        public long DroppedNoRoute { get; private set; }

        /// <summary>
        /// Gets name lookups dropped because the host has no name server address.
        /// </summary>
        public long DroppedNoNameServer { get; private set; }

        /// <summary>
        /// Gets UDP replies too big for one frame (fragmenting toward the guest is phase 3).
        /// </summary>
        public long DroppedUdpTooBig { get; private set; }

        /// <summary>
        /// Gets TCP SYNs refused because the connection limit was reached.
        /// </summary>
        public long RefusedOverLimit { get; private set; }

        /// <summary>
        /// Gets host connects that were refused (the guest got RST).
        /// </summary>
        public long HostConnectsRefused { get; private set; }

        /// <summary>
        /// Gets host connects that failed otherwise (the guest got nothing and gives up itself).
        /// </summary>
        public long HostConnectsFailed { get; private set; }

        /// <summary>
        /// Gets TCP segments resent to the guest.
        /// </summary>
        public long Retransmits { get; private set; }

        /// <summary>
        /// Gets DHCP and BOOTP replies sent.
        /// </summary>
        public long DhcpReplies { get; private set; }

        /// <summary>
        /// Gets host connections that came in through a TCP port forward.
        /// </summary>
        public long ForwardConnections { get; private set; }

        /// <summary>
        /// Gets forwarded connections the guest refused (it answered our SYN with RST).
        /// </summary>
        public long ForwardGuestRefused { get; private set; }

        /// <summary>
        /// Gets forwarded connections the guest never answered (no SYN-ACK, or no ARP reply).
        /// </summary>
        public long ForwardGuestNoAnswer { get; private set; }

        /// <summary>
        /// Gets forwarded connections and datagrams dropped because no guest address was known yet.
        /// </summary>
        public long ForwardNoGuest { get; private set; }

        /// <summary>
        /// Gets datagrams that came in through a UDP port forward and went to the guest.
        /// </summary>
        public long ForwardDatagramsToGuest { get; private set; }

        /// <summary>
        /// Gets the guest's answers that went back to a host sender of a UDP port forward.
        /// </summary>
        public long ForwardDatagramsToHost { get; private set; }

        // ── Public methods ──

        /// <summary>
        /// Hands the open sockets of a port forward rule to the router; it starts accepting on
        /// its next <see cref="Service"/>. Any thread.
        /// </summary>
        /// <param name="binding">
        /// The rule's sockets.
        /// </param>
        public void AttachForward(NatForwardBinding binding)
        {
            lock (_forwardGate)
            {
                _forwardsToAdd.Add(binding);
            }
        }

        /// <summary>
        /// Takes a port forward rule away; the router closes its sockets on its next
        /// <see cref="Service"/>. Connections already open stay open. Any thread.
        /// </summary>
        /// <param name="binding">
        /// The rule's sockets.
        /// </param>
        public void DetachForward(NatForwardBinding binding)
        {
            lock (_forwardGate)
            {
                _forwardsToRemove.Add(binding);
            }
        }

        /// <summary>
        /// Takes one frame the guest sent.
        /// </summary>
        /// <param name="frame">
        /// The frame (no FCS).
        /// </param>
        /// <param name="polls">
        /// Polls so far (emulated time).
        /// </param>
        public void HandleGuestFrame(ReadOnlySpan<byte> frame, long polls)
        {
            _polls = polls;
            if (frame.Length < NatPacket.EthernetHeaderBytes)
            {
                DroppedUnsupported++;
                return;
            }
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12));
            if (type == NatPacket.EtherTypeArp)
            {
                HandleArp(frame);
            }
            else if (type == NatPacket.EtherTypeIpv4)
            {
                HandleIpv4(frame);
            }
            else
            {
                DroppedUnsupported++;
            }
        }

        /// <summary>
        /// Does the socket work and the timers: waits up to <paramref name="waitMicroseconds"/>
        /// for any host socket to be ready (one socket select call), moves data both ways, and runs the guest-side TCP
        /// timers.
        /// </summary>
        /// <param name="polls">
        /// Polls so far (emulated time).
        /// </param>
        /// <param name="waitMicroseconds">
        /// The longest wait for a socket, in microseconds.
        /// </param>
        /// <returns>
        /// <c>true</c> when any host socket was waited on; <c>false</c> when there was none.
        /// </returns>
        public bool Service(long polls, int waitMicroseconds)
        {
            _polls = polls;
            TakeForwardChanges();
            ExpireUdp();
            TakeNameAnswers();

            _readList.Clear();
            _writeList.Clear();
            _errorList.Clear();
            for (int i = 0; i < _udp.Count; i++)
            {
                _readList.Add(_udp[i].Host);
            }
            for (int i = 0; i < _forwards.Count; i++)
            {
                Socket[] sockets = _forwards[i].Sockets;
                for (int j = 0; j < sockets.Length; j++)
                {
                    _readList.Add(sockets[j]);
                }
            }
            for (int i = 0; i < _tcp.Count; i++)
            {
                NatTcpConnection c = _tcp[i];
                if (c.Host == null)
                {
                    continue;
                }
                if (c.State == NatTcpState.HostConnecting)
                {
                    _writeList.Add(c.Host);
                    _errorList.Add(c.Host);
                    continue;
                }
                // Back-pressure (design 7.4): stop reading the host once the buffer toward the
                // guest is full, so the host TCP stack slows the far end down.
                if (!c.HostEnded && c.ToGuestCount < NatTcpConnection.BufferBytes)
                {
                    _readList.Add(c.Host);
                }
                if (c.State == NatTcpState.Established && c.ToHostCount > 0)
                {
                    _writeList.Add(c.Host);
                }
            }

            bool waited = _readList.Count + _writeList.Count + _errorList.Count > 0;
            if (waited)
            {
                Socket.Select(_readList.Count > 0 ? _readList : null, _writeList.Count > 0 ? _writeList : null,
                    _errorList.Count > 0 ? _errorList : null, waitMicroseconds);
                for (int i = 0; i < _errorList.Count; i++)
                {
                    NatTcpConnection? c = FindTcp(_errorList[i]);
                    if (c != null && c.State == NatTcpState.HostConnecting)
                    {
                        HostConnectFailed(c);
                    }
                }
                for (int i = 0; i < _writeList.Count; i++)
                {
                    NatTcpConnection? c = FindTcp(_writeList[i]);
                    if (c == null)
                    {
                        continue;
                    }
                    if (c.State == NatTcpState.HostConnecting)
                    {
                        HostConnectDone(c);
                    }
                    else if (c.State == NatTcpState.Established)
                    {
                        WriteToHost(c);
                    }
                }
                for (int i = 0; i < _readList.Count; i++)
                {
                    Socket s = _readList[i];
                    if (ServeForward(s))
                    {
                        continue;
                    }
                    NatUdpMapping? m = FindUdp(s);
                    if (m != null)
                    {
                        ReadUdp(m);
                        continue;
                    }
                    NatTcpConnection? c = FindTcp(s);
                    if (c != null)
                    {
                        ReadTcp(c);
                    }
                }
            }

            for (int i = _tcp.Count - 1; i >= 0; i--)
            {
                NatTcpConnection c = _tcp[i];
                if (c.State == NatTcpState.Established)
                {
                    ShutHostSendWhenDrained(c);
                    PumpToGuest(c);
                    EnterTimeWaitWhenDone(c);
                }
                else if (c.State == NatTcpState.TimeWait && _polls >= c.TimeWaitEndsAt)
                {
                    c.Release();
                }
                else if ((c.State == NatTcpState.SynSent || c.State == NatTcpState.GuestResolving) && c.RetransmitAt != 0 && _polls >= c.RetransmitAt)
                {
                    RetryForward(c);
                }
                if (c.State == NatTcpState.Closed)
                {
                    _tcp.RemoveAt(i);
                }
            }
            return waited;
        }

        /// <summary>
        /// Closes every host socket and forgets every connection and mapping.
        /// </summary>
        public void CloseAll()
        {
            for (int i = 0; i < _tcp.Count; i++)
            {
                _tcp[i].Release();
            }
            _tcp.Clear();
            for (int i = 0; i < _udp.Count; i++)
            {
                _udp[i].Host.Close();
            }
            _udp.Clear();
            _udpForwards.Clear();
            lock (_forwardGate)
            {
                for (int i = 0; i < _forwardsToAdd.Count; i++)
                {
                    _forwards.Add(_forwardsToAdd[i]);
                }
                _forwardsToAdd.Clear();
                _forwardsToRemove.Clear();
            }
            for (int i = 0; i < _forwards.Count; i++)
            {
                _forwards[i].Close();
            }
            _forwards.Clear();
            _resolver?.Stop();
        }

        // ── Private methods: ARP, IPv4, ICMP ──

        // RFC 826: answer a request for the gateway or name server address with the gateway MAC.
        private void HandleArp(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < NatPacket.EthernetHeaderBytes + 28)
            {
                DroppedUnsupported++;
                return;
            }
            ReadOnlySpan<byte> arp = frame.Slice(NatPacket.EthernetHeaderBytes);
            ushort hardware = BinaryPrimitives.ReadUInt16BigEndian(arp);
            ushort protocol = BinaryPrimitives.ReadUInt16BigEndian(arp.Slice(2));
            ushort operation = BinaryPrimitives.ReadUInt16BigEndian(arp.Slice(6));
            if (hardware != 1 || protocol != NatPacket.EtherTypeIpv4 || arp[4] != 6 || arp[5] != 4)
            {
                return;
            }
            // Requests and replies both tell which MAC an address sits at; a port forward that
            // waits for the guest's MAC goes on from here (RFC 826, "merge" step).
            uint sender = NatPacket.ReadAddress(arp.Slice(14));
            if (IsGuestAddress(sender))
            {
                Learn(sender, arp.Slice(8, 6));
            }
            if (operation != 1)
            {
                return;
            }
            uint target = NatPacket.ReadAddress(arp.Slice(24));
            if (target != _plan.Gateway && target != _plan.NameServer)
            {
                return;
            }

            Span<byte> reply = _frame.AsSpan(0, NatPacket.MinFrameBytes);
            reply.Clear();
            NatPacket.WriteEthernet(reply, arp.Slice(8, 6), _plan.GatewayMac, NatPacket.EtherTypeArp);
            Span<byte> body = reply.Slice(NatPacket.EthernetHeaderBytes);
            BinaryPrimitives.WriteUInt16BigEndian(body, 1);
            BinaryPrimitives.WriteUInt16BigEndian(body.Slice(2), NatPacket.EtherTypeIpv4);
            body[4] = 6;
            body[5] = 4;
            BinaryPrimitives.WriteUInt16BigEndian(body.Slice(6), 2);
            _plan.GatewayMac.CopyTo(body.Slice(8));
            NatPacket.WriteAddress(body.Slice(14), target);
            arp.Slice(8, 6).CopyTo(body.Slice(18));
            arp.Slice(14, 4).CopyTo(body.Slice(24));
            _output(reply);
        }

        private void HandleIpv4(ReadOnlySpan<byte> frame)
        {
            ReadOnlySpan<byte> ip = frame.Slice(NatPacket.EthernetHeaderBytes);
            if (ip.Length < NatPacket.Ipv4HeaderBytes || (ip[0] >> 4) != 4)
            {
                DroppedBadHeader++;
                return;
            }
            int headerBytes = (ip[0] & 0x0F) * 4;
            int totalLength = BinaryPrimitives.ReadUInt16BigEndian(ip.Slice(2));
            if (headerBytes < NatPacket.Ipv4HeaderBytes || totalLength < headerBytes || totalLength > ip.Length
                || !NatPacket.Ipv4HeaderChecksumOk(ip.Slice(0, headerBytes)))
            {
                DroppedBadHeader++;
                return;
            }
            ushort fragment = BinaryPrimitives.ReadUInt16BigEndian(ip.Slice(6));
            if ((fragment & 0x2000) != 0 || (fragment & 0x1FFF) != 0)
            {
                DroppedFragments++;
                return;
            }

            ReadOnlySpan<byte> guestMac = frame.Slice(6, 6);
            uint source = NatPacket.ReadAddress(ip.Slice(12));
            uint destination = NatPacket.ReadAddress(ip.Slice(16));
            if (IsGuestAddress(source))
            {
                Learn(source, guestMac);
            }
            ReadOnlySpan<byte> payload = ip.Slice(headerBytes, totalLength - headerBytes);
            byte protocol = ip[9];
            if (protocol == NatPacket.ProtocolIcmp)
            {
                HandleIcmp(guestMac, source, destination, payload);
            }
            else if (protocol == NatPacket.ProtocolUdp)
            {
                HandleUdp(guestMac, source, destination, payload);
            }
            else if (protocol == NatPacket.ProtocolTcp)
            {
                HandleTcp(guestMac, source, destination, payload);
            }
            else
            {
                DroppedUnsupported++;
            }
        }

        // RFC 792: an echo request to the gateway or name server gets an echo reply with the same
        // identifier, sequence number and data.
        private void HandleIcmp(ReadOnlySpan<byte> guestMac, uint source, uint destination, ReadOnlySpan<byte> icmp)
        {
            if (icmp.Length < 8 || icmp[0] != 8)
            {
                return;
            }
            if (destination != _plan.Gateway && destination != _plan.NameServer)
            {
                DroppedOutsidePing++;
                return;
            }
            if (NatPacket.Fold(NatPacket.Sum(icmp, 0)) != 0 || icmp.Length > FrameBytes - NatPacket.EthernetHeaderBytes - NatPacket.Ipv4HeaderBytes)
            {
                DroppedBadHeader++;
                return;
            }

            int ipLength = NatPacket.Ipv4HeaderBytes + icmp.Length;
            Span<byte> reply = StartFrame(guestMac, ipLength);
            Span<byte> body = reply.Slice(NatPacket.EthernetHeaderBytes + NatPacket.Ipv4HeaderBytes, icmp.Length);
            icmp.CopyTo(body);
            body[0] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(body.Slice(2), 0);
            BinaryPrimitives.WriteUInt16BigEndian(body.Slice(2), NatPacket.Fold(NatPacket.Sum(body, 0)));
            NatPacket.WriteIpv4(reply.Slice(NatPacket.EthernetHeaderBytes), ipLength, ++_ipIdentification, NatPacket.ProtocolIcmp, destination, source);
            Emit(reply);
        }

        // ── Private methods: UDP, DHCP ──

        private void HandleUdp(ReadOnlySpan<byte> guestMac, uint source, uint destination, ReadOnlySpan<byte> udp)
        {
            if (udp.Length < NatPacket.UdpHeaderBytes)
            {
                DroppedBadHeader++;
                return;
            }
            int length = BinaryPrimitives.ReadUInt16BigEndian(udp.Slice(4));
            if (length < NatPacket.UdpHeaderBytes || length > udp.Length)
            {
                DroppedBadHeader++;
                return;
            }
            ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(udp);
            ushort destinationPort = BinaryPrimitives.ReadUInt16BigEndian(udp.Slice(2));
            ReadOnlySpan<byte> data = udp.Slice(NatPacket.UdpHeaderBytes, length - NatPacket.UdpHeaderBytes);

            if (destinationPort == 67)
            {
                if (destination == 0xFFFFFFFF || destination == _plan.Broadcast || destination == _plan.Gateway)
                {
                    HandleDhcp(data);
                }
                return;
            }
            if (destination == 0xFFFFFFFF || destination == _plan.Broadcast)
            {
                return;
            }

            if (destination == _plan.Gateway && AnswerUdpForward(source, sourcePort, destinationPort, data))
            {
                return;
            }
            if (destination == _plan.NameServer && destinationPort == 53 && _resolver != null)
            {
                _resolver.Enqueue(new NatNameQuery(guestMac.Slice(0, 6).ToArray(), source, sourcePort, data.ToArray()));
                return;
            }
            IPEndPoint? target = TranslateOut(destination, destinationPort);
            if (target == null)
            {
                return;
            }
            NatUdpMapping? mapping = null;
            for (int i = 0; i < _udp.Count; i++)
            {
                if (_udp[i].GuestAddress == source && _udp[i].GuestPort == sourcePort)
                {
                    mapping = _udp[i];
                    break;
                }
            }
            if (mapping == null)
            {
                if (_udp.Count >= MaxUdpMappings)
                {
                    RefusedOverLimit++;
                    return;
                }
                Socket host = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                host.Blocking = false;
                host.Bind(new IPEndPoint(IPAddress.Any, 0));
                mapping = new NatUdpMapping(guestMac, source, sourcePort, host, Environment.TickCount64);
                _udp.Add(mapping);
            }
            mapping.LastUsedMilliseconds = Environment.TickCount64;
            mapping.LastRemoteAddress = destination;
            mapping.LastRemotePort = destinationPort;
            try
            {
                mapping.Host.SendTo(data, SocketFlags.None, target);
            }
            catch (SocketException)
            {
                // The host refused to send (no route, buffer full): UDP gives no promise, the
                // guest's own retry covers it, and the drop is counted.
                DroppedNoRoute++;
            }
        }

        // RFC 2131 (DHCP) and RFC 951 (BOOTP). A fixed address per MAC, so a guest keeps its
        // address across restarts. Options sent: subnet mask (1), router (3), name server (6),
        // interface MTU (26), lease time (51), message type (53), server identifier (54).
        private void HandleDhcp(ReadOnlySpan<byte> request)
        {
            if (request.Length < 236 || request[0] != 1 || request[1] != 1 || request[2] != 6)
            {
                return;
            }
            ReadOnlySpan<byte> clientMac = request.Slice(28, 6);
            int messageType = 0;
            uint requested = 0;
            bool hasMagic = request.Length >= 240 && request[236] == 0x63 && request[237] == 0x82 && request[238] == 0x53 && request[239] == 0x63;
            if (hasMagic)
            {
                int i = 240;
                while (i < request.Length)
                {
                    byte code = request[i];
                    if (code == 255)
                    {
                        break;
                    }
                    if (code == 0)
                    {
                        i++;
                        continue;
                    }
                    if (i + 1 >= request.Length || i + 2 + request[i + 1] > request.Length)
                    {
                        break;
                    }
                    int len = request[i + 1];
                    if (code == 53 && len >= 1)
                    {
                        messageType = request[i + 2];
                    }
                    else if (code == 50 && len >= 4)
                    {
                        requested = NatPacket.ReadAddress(request.Slice(i + 2));
                    }
                    i += 2 + len;
                }
            }

            uint lease = LeaseFor(clientMac);
            uint clientAddress = NatPacket.ReadAddress(request.Slice(12));
            int replyType;
            if (messageType == 0)
            {
                replyType = 0;
            }
            else if (messageType == 1)
            {
                replyType = 2;
            }
            else if (messageType == 3)
            {
                uint asked = requested != 0 ? requested : clientAddress;
                replyType = asked == lease ? 5 : 6;
            }
            else if (messageType == 8)
            {
                replyType = 5;
            }
            else
            {
                return;
            }

            const int bootpBytes = 300;
            int ipLength = NatPacket.Ipv4HeaderBytes + NatPacket.UdpHeaderBytes + bootpBytes;
            bool broadcast = replyType == 6 || (BinaryPrimitives.ReadUInt16BigEndian(request.Slice(10)) & 0x8000) != 0;
            uint to;
            if (messageType == 8 || (replyType == 5 && clientAddress != 0))
            {
                to = clientAddress;
                broadcast = false;
            }
            else
            {
                to = broadcast ? 0xFFFFFFFF : lease;
            }

            Span<byte> frame = StartFrame(broadcast ? _broadcastMac : clientMac, ipLength);
            Span<byte> bootp = frame.Slice(NatPacket.EthernetHeaderBytes + NatPacket.Ipv4HeaderBytes + NatPacket.UdpHeaderBytes, bootpBytes);
            bootp.Clear();
            bootp[0] = 2;
            bootp[1] = 1;
            bootp[2] = 6;
            request.Slice(4, 4).CopyTo(bootp.Slice(4));
            request.Slice(10, 2).CopyTo(bootp.Slice(10));
            if (messageType == 8)
            {
                request.Slice(12, 4).CopyTo(bootp.Slice(12));
            }
            else if (replyType != 6)
            {
                NatPacket.WriteAddress(bootp.Slice(16), lease);
            }
            NatPacket.WriteAddress(bootp.Slice(20), _plan.Gateway);
            request.Slice(28, 16).CopyTo(bootp.Slice(28));
            bootp[236] = 0x63;
            bootp[237] = 0x82;
            bootp[238] = 0x53;
            bootp[239] = 0x63;
            int o = 240;
            if (replyType != 0)
            {
                bootp[o++] = 53;
                bootp[o++] = 1;
                bootp[o++] = (byte)replyType;
                bootp[o++] = 54;
                bootp[o++] = 4;
                NatPacket.WriteAddress(bootp.Slice(o), _plan.Gateway);
                o += 4;
            }
            if (replyType != 6)
            {
                bootp[o++] = 1;
                bootp[o++] = 4;
                NatPacket.WriteAddress(bootp.Slice(o), _plan.Netmask);
                o += 4;
                bootp[o++] = 3;
                bootp[o++] = 4;
                NatPacket.WriteAddress(bootp.Slice(o), _plan.Gateway);
                o += 4;
                bootp[o++] = 6;
                bootp[o++] = 4;
                NatPacket.WriteAddress(bootp.Slice(o), _plan.NameServer);
                o += 4;
                bootp[o++] = 26;
                bootp[o++] = 2;
                BinaryPrimitives.WriteUInt16BigEndian(bootp.Slice(o), 1500);
                o += 2;
                if (replyType == 2 || (replyType == 5 && messageType != 8))
                {
                    bootp[o++] = 51;
                    bootp[o++] = 4;
                    BinaryPrimitives.WriteUInt32BigEndian(bootp.Slice(o), LeaseSeconds);
                    o += 4;
                }
            }
            bootp[o] = 255;

            Span<byte> udp = frame.Slice(NatPacket.EthernetHeaderBytes + NatPacket.Ipv4HeaderBytes, NatPacket.UdpHeaderBytes + bootpBytes);
            NatPacket.WriteUdp(udp, _plan.Gateway, to, 67, 68);
            NatPacket.WriteIpv4(frame.Slice(NatPacket.EthernetHeaderBytes), ipLength, ++_ipIdentification, NatPacket.ProtocolUdp, _plan.Gateway, to);
            Emit(frame);
            DhcpReplies++;
        }

        private uint LeaseFor(ReadOnlySpan<byte> mac)
        {
            for (int i = 0; i < _leaseMacs.Count; i++)
            {
                if (mac.SequenceEqual(_leaseMacs[i]))
                {
                    return _leaseAddresses[i];
                }
            }
            uint address = _plan.FirstGuest + (uint)_leaseMacs.Count;
            _leaseMacs.Add(mac.Slice(0, 6).ToArray());
            _leaseAddresses.Add(address);
            return address;
        }

        private void ReadUdp(NatUdpMapping m)
        {
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
                        // Windows reports an ICMP "port unreachable" for an earlier send here:
                        // tell the guest the same (design 7.3, ICMP row).
                        PortUnreachable(m);
                        continue;
                    }
                    return;
                }
                m.LastUsedMilliseconds = Environment.TickCount64;
                if (n > MaxUdpData)
                {
                    DroppedUdpTooBig++;
                    continue;
                }
                IPEndPoint remote = (IPEndPoint)from;
                uint source = TranslateBack(remote, m);
                int ipLength = NatPacket.Ipv4HeaderBytes + NatPacket.UdpHeaderBytes + n;
                Span<byte> frame = StartFrame(m.GuestMac, ipLength);
                Span<byte> udp = frame.Slice(NatPacket.EthernetHeaderBytes + NatPacket.Ipv4HeaderBytes, NatPacket.UdpHeaderBytes + n);
                _receive.AsSpan(0, n).CopyTo(udp.Slice(NatPacket.UdpHeaderBytes));
                NatPacket.WriteUdp(udp, source, m.GuestAddress, (ushort)remote.Port, m.GuestPort);
                NatPacket.WriteIpv4(frame.Slice(NatPacket.EthernetHeaderBytes), ipLength, ++_ipIdentification, NatPacket.ProtocolUdp, source, m.GuestAddress);
                Emit(frame);
            }
        }

        // RFC 792: destination unreachable, code 3 (port unreachable), carrying the IP header and
        // the first 8 bytes of the datagram that failed. Rebuilt from what the mapping remembers.
        private void PortUnreachable(NatUdpMapping m)
        {
            const int quoted = NatPacket.Ipv4HeaderBytes + NatPacket.UdpHeaderBytes;
            int ipLength = NatPacket.Ipv4HeaderBytes + 8 + quoted;
            Span<byte> frame = StartFrame(m.GuestMac, ipLength);
            Span<byte> icmp = frame.Slice(NatPacket.EthernetHeaderBytes + NatPacket.Ipv4HeaderBytes, 8 + quoted);
            icmp.Clear();
            icmp[0] = 3;
            icmp[1] = 3;
            Span<byte> inner = icmp.Slice(8);
            NatPacket.WriteIpv4(inner, quoted, 0, NatPacket.ProtocolUdp, m.GuestAddress, m.LastRemoteAddress);
            BinaryPrimitives.WriteUInt16BigEndian(inner.Slice(20), m.GuestPort);
            BinaryPrimitives.WriteUInt16BigEndian(inner.Slice(22), m.LastRemotePort);
            BinaryPrimitives.WriteUInt16BigEndian(inner.Slice(24), NatPacket.UdpHeaderBytes);
            BinaryPrimitives.WriteUInt16BigEndian(icmp.Slice(2), NatPacket.Fold(NatPacket.Sum(icmp, 0)));
            NatPacket.WriteIpv4(frame.Slice(NatPacket.EthernetHeaderBytes), ipLength, ++_ipIdentification, NatPacket.ProtocolIcmp, m.LastRemoteAddress, m.GuestAddress);
            Emit(frame);
        }

        // Answers of the name lookup fallback go to the guest from the name server address.
        private void TakeNameAnswers()
        {
            if (_resolver == null)
            {
                return;
            }
            while (_resolver.TryTakeAnswer(out NatNameQuery? answer))
            {
                int n = answer!.Message.Length;
                if (n > MaxUdpData)
                {
                    DroppedUdpTooBig++;
                    continue;
                }
                int ipLength = NatPacket.Ipv4HeaderBytes + NatPacket.UdpHeaderBytes + n;
                Span<byte> frame = StartFrame(answer.GuestMac, ipLength);
                Span<byte> udp = frame.Slice(NatPacket.EthernetHeaderBytes + NatPacket.Ipv4HeaderBytes, NatPacket.UdpHeaderBytes + n);
                answer.Message.CopyTo(udp.Slice(NatPacket.UdpHeaderBytes));
                NatPacket.WriteUdp(udp, _plan.NameServer, answer.GuestAddress, 53, answer.GuestPort);
                NatPacket.WriteIpv4(frame.Slice(NatPacket.EthernetHeaderBytes), ipLength, ++_ipIdentification, NatPacket.ProtocolUdp, _plan.NameServer, answer.GuestAddress);
                Emit(frame);
            }
        }

        private void ExpireUdp()
        {
            long now = Environment.TickCount64;
            for (int i = _udp.Count - 1; i >= 0; i--)
            {
                if (now - _udp[i].LastUsedMilliseconds >= UdpIdleMilliseconds)
                {
                    _udp[i].Host.Close();
                    _udp.RemoveAt(i);
                }
            }
            for (int i = _udpForwards.Count - 1; i >= 0; i--)
            {
                if (now - _udpForwards[i].LastUsedMilliseconds >= UdpIdleMilliseconds)
                {
                    _udpForwards.RemoveAt(i);
                }
            }
        }

        // ── Private methods: TCP ──

        private void HandleTcp(ReadOnlySpan<byte> guestMac, uint source, uint destination, ReadOnlySpan<byte> tcp)
        {
            if (tcp.Length < NatPacket.TcpHeaderBytes
                || NatPacket.Fold(NatPacket.Sum(tcp, NatPacket.PseudoHeaderSum(source, destination, NatPacket.ProtocolTcp, tcp.Length))) != 0)
            {
                DroppedBadHeader++;
                return;
            }
            int headerBytes = (tcp[12] >> 4) * 4;
            if (headerBytes < NatPacket.TcpHeaderBytes || headerBytes > tcp.Length)
            {
                DroppedBadHeader++;
                return;
            }
            ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(tcp);
            ushort destinationPort = BinaryPrimitives.ReadUInt16BigEndian(tcp.Slice(2));
            uint sequence = BinaryPrimitives.ReadUInt32BigEndian(tcp.Slice(4));
            uint acknowledgment = BinaryPrimitives.ReadUInt32BigEndian(tcp.Slice(8));
            byte flags = tcp[13];
            int window = BinaryPrimitives.ReadUInt16BigEndian(tcp.Slice(14));
            ReadOnlySpan<byte> data = tcp.Slice(headerBytes);

            NatTcpConnection? c = null;
            for (int i = 0; i < _tcp.Count; i++)
            {
                if (_tcp[i].Matches(source, sourcePort, destination, destinationPort))
                {
                    c = _tcp[i];
                    break;
                }
            }

            if (c == null)
            {
                if ((flags & (NatPacket.TcpSyn | NatPacket.TcpAck | NatPacket.TcpRst)) == NatPacket.TcpSyn)
                {
                    OpenTcp(guestMac, source, sourcePort, destination, destinationPort, sequence, ReadMss(tcp.Slice(NatPacket.TcpHeaderBytes, headerBytes - NatPacket.TcpHeaderBytes)));
                }
                else if ((flags & NatPacket.TcpRst) == 0)
                {
                    // RFC 9293 section 3.10.7.1: a segment for no connection gets a reset.
                    int length = data.Length + ((flags & NatPacket.TcpSyn) != 0 ? 1 : 0) + ((flags & NatPacket.TcpFin) != 0 ? 1 : 0);
                    if ((flags & NatPacket.TcpAck) != 0)
                    {
                        SendBareTcp(guestMac, destination, source, destinationPort, sourcePort, acknowledgment, 0, NatPacket.TcpRst);
                    }
                    else
                    {
                        SendBareTcp(guestMac, destination, source, destinationPort, sourcePort, 0, sequence + (uint)length, (byte)(NatPacket.TcpRst | NatPacket.TcpAck));
                    }
                }
                return;
            }

            if ((flags & NatPacket.TcpRst) != 0)
            {
                if (c.State == NatTcpState.SynSent)
                {
                    // The guest refused a forwarded connection: refuse the host client too
                    // (design 7.7), with a reset rather than an orderly close.
                    ForwardGuestRefused++;
                    ResetHost(c);
                }
                // Guest reset: close the host socket at once (design 7.4, closing).
                c.Release();
                _tcp.Remove(c);
                return;
            }

            if (c.State == NatTcpState.SynSent)
            {
                // RFC 9293 section 3.5: the guest's SYN-ACK to our SYN of a forwarded connection.
                if ((flags & (NatPacket.TcpSyn | NatPacket.TcpAck)) == (NatPacket.TcpSyn | NatPacket.TcpAck) && acknowledgment == c.OurInitialSequence + 1)
                {
                    c.ReceiveNext = sequence + 1;
                    c.GuestMss = ReadMss(tcp.Slice(NatPacket.TcpHeaderBytes, headerBytes - NatPacket.TcpHeaderBytes));
                    c.State = NatTcpState.Established;
                    c.SendUnacked = acknowledgment;
                    c.SendNext = acknowledgment;
                    c.GuestWindow = window;
                    c.RetransmitAt = 0;
                    c.Retries = 0;
                    c.RetransmitPolls = PollsPerSecond;
                    SendSegment(c, c.SendNext, NatPacket.TcpAck, ReadOnlySpan<byte>.Empty, false);
                }
                return;
            }
            if (c.State == NatTcpState.GuestResolving)
            {
                return;
            }

            if (c.State == NatTcpState.HostConnecting)
            {
                // The guest resent its SYN while the host connect is still under way: it gets
                // its answer when the connect ends.
                return;
            }
            if (c.State == NatTcpState.SynAckSent)
            {
                if ((flags & NatPacket.TcpSyn) != 0)
                {
                    SendSynAck(c);
                    return;
                }
                if ((flags & NatPacket.TcpAck) == 0 || acknowledgment != c.OurInitialSequence + 1)
                {
                    return;
                }
                c.State = NatTcpState.Established;
                c.SendUnacked = acknowledgment;
                c.SendNext = acknowledgment;
                c.GuestWindow = window;
                c.RetransmitAt = 0;
                c.Retries = 0;
            }
            if (c.State == NatTcpState.TimeWait)
            {
                if ((flags & NatPacket.TcpFin) != 0)
                {
                    SendSegment(c, c.SendNext, NatPacket.TcpAck, ReadOnlySpan<byte>.Empty, false);
                }
                return;
            }
            if (c.State == NatTcpState.Established)
            {
                Established(c, sequence, acknowledgment, flags, window, data);
            }
        }

        private void Established(NatTcpConnection c, uint sequence, uint acknowledgment, byte flags, int window, ReadOnlySpan<byte> data)
        {
            if ((flags & NatPacket.TcpAck) != 0 && !NatPacket.SeqBefore(acknowledgment, c.SendUnacked) && !NatPacket.SeqBefore(c.SendNext, acknowledgment))
            {
                int advanced = (int)(acknowledgment - c.SendUnacked);
                if (advanced > 0)
                {
                    int dataAcked = advanced;
                    if (c.FinSent && !c.FinAcked && acknowledgment == c.FinSequence + 1)
                    {
                        c.FinAcked = true;
                        dataAcked--;
                    }
                    if (dataAcked > c.ToGuestCount)
                    {
                        dataAcked = c.ToGuestCount;
                    }
                    if (dataAcked > 0)
                    {
                        Buffer.BlockCopy(c.ToGuest, dataAcked, c.ToGuest, 0, c.ToGuestCount - dataAcked);
                        c.ToGuestCount -= dataAcked;
                    }
                    c.SendUnacked = acknowledgment;
                    c.Retries = 0;
                    c.RetransmitPolls = PollsPerSecond;
                    c.RetransmitAt = c.SendNext != c.SendUnacked ? _polls + c.RetransmitPolls : 0;
                }
                c.GuestWindow = window;
                if (window > 0)
                {
                    c.PersistAt = 0;
                }
            }

            bool fin = (flags & NatPacket.TcpFin) != 0;
            if (data.Length == 0 && !fin)
            {
                return;
            }
            if (sequence == c.ReceiveNext && !c.GuestFinished)
            {
                int take = data.Length < c.FreeToHost ? data.Length : c.FreeToHost;
                if (take > 0)
                {
                    data.Slice(0, take).CopyTo(c.ToHost.AsSpan(c.ToHostCount));
                    c.ToHostCount += take;
                    c.ReceiveNext += (uint)take;
                }
                if (fin && take == data.Length)
                {
                    c.ReceiveNext++;
                    c.GuestFinished = true;
                }
            }
            // Every segment is acknowledged at once (design 7.4: old stacks with small windows
            // stall on delayed ACKs); out of order data is dropped and the last in-order byte is
            // acknowledged again, so the guest resends.
            SendSegment(c, c.SendNext, NatPacket.TcpAck, ReadOnlySpan<byte>.Empty, false);
            ShutHostSendWhenDrained(c);
            EnterTimeWaitWhenDone(c);
        }

        private void OpenTcp(ReadOnlySpan<byte> guestMac, uint source, ushort sourcePort, uint destination, ushort destinationPort, uint sequence, int mss)
        {
            if (_tcp.Count >= MaxTcpConnections)
            {
                RefusedOverLimit++;
                SendBareTcp(guestMac, destination, source, destinationPort, sourcePort, 0, sequence + 1, (byte)(NatPacket.TcpRst | NatPacket.TcpAck));
                return;
            }
            IPEndPoint? target = TranslateOut(destination, destinationPort);
            if (target == null)
            {
                SendBareTcp(guestMac, destination, source, destinationPort, sourcePort, 0, sequence + 1, (byte)(NatPacket.TcpRst | NatPacket.TcpAck));
                return;
            }

            NatTcpConnection c = new NatTcpConnection(guestMac, source, sourcePort, destination, destinationPort, sequence, mss, (uint)_random.Next());
            c.RetransmitPolls = PollsPerSecond;
            Socket host = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            host.Blocking = false;
            host.NoDelay = true;
            c.Host = host;
            try
            {
                host.Connect(target);
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode != SocketError.WouldBlock && e.SocketErrorCode != SocketError.InProgress)
                {
                    _tcp.Add(c);
                    if (e.SocketErrorCode == SocketError.ConnectionRefused)
                    {
                        HostConnectRefused(c);
                    }
                    else
                    {
                        HostConnectFailed(c);
                    }
                    return;
                }
            }
            _tcp.Add(c);
        }

        // On the guest's SYN the host connect goes first; the guest gets SYN-ACK only when it
        // worked (design 7.4, opening).
        private void HostConnectDone(NatTcpConnection c)
        {
            int error = 0;
            try
            {
                error = (int)c.Host!.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error)!;
            }
            catch (SocketException e)
            {
                error = (int)e.SocketErrorCode;
            }
            if (error == (int)SocketError.ConnectionRefused)
            {
                HostConnectRefused(c);
                return;
            }
            if (error != 0)
            {
                HostConnectFailed(c);
                return;
            }
            c.State = NatTcpState.SynAckSent;
            SendSynAck(c);
        }

        private void HostConnectFailed(NatTcpConnection c)
        {
            int error = 0;
            try
            {
                error = (int)c.Host!.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error)!;
            }
            catch (SocketException e)
            {
                error = (int)e.SocketErrorCode;
            }
            catch (ObjectDisposedException)
            {
                error = 0;
            }
            if (error == (int)SocketError.ConnectionRefused)
            {
                HostConnectRefused(c);
                return;
            }
            // A connect that timed out or found no route: the guest gets nothing and gives up by
            // itself (design 7.4, opening).
            HostConnectsFailed++;
            c.Release();
        }

        private void HostConnectRefused(NatTcpConnection c)
        {
            HostConnectsRefused++;
            SendBareTcp(c.GuestMac, c.RemoteAddress, c.GuestAddress, c.RemotePort, c.GuestPort, 0, c.GuestInitialSequence + 1, (byte)(NatPacket.TcpRst | NatPacket.TcpAck));
            c.Release();
        }

        private void SendSynAck(NatTcpConnection c)
        {
            c.SendUnacked = c.OurInitialSequence;
            c.SendNext = c.OurInitialSequence + 1;
            SendSegment(c, c.OurInitialSequence, (byte)(NatPacket.TcpSyn | NatPacket.TcpAck), ReadOnlySpan<byte>.Empty, true);
        }

        private void ReadTcp(NatTcpConnection c)
        {
            if (c.Host == null)
            {
                return;
            }
            int room = NatTcpConnection.BufferBytes - c.ToGuestCount;
            if (room <= 0)
            {
                return;
            }
            int n;
            try
            {
                n = c.Host.Receive(c.ToGuest, c.ToGuestCount, room, SocketFlags.None);
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode == SocketError.WouldBlock)
                {
                    return;
                }
                // Host reset: reset to the guest (design 7.4, closing).
                SendSegment(c, c.SendNext, (byte)(NatPacket.TcpRst | NatPacket.TcpAck), ReadOnlySpan<byte>.Empty, false);
                c.Release();
                return;
            }
            if (n == 0)
            {
                c.HostEnded = true;
                return;
            }
            c.ToGuestCount += n;
        }

        private void WriteToHost(NatTcpConnection c)
        {
            if (c.Host == null || c.ToHostCount == 0)
            {
                return;
            }
            bool wasClosed = c.FreeToHost < c.GuestMss;
            int n;
            try
            {
                n = c.Host.Send(c.ToHost, 0, c.ToHostCount, SocketFlags.None);
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode == SocketError.WouldBlock)
                {
                    return;
                }
                SendSegment(c, c.SendNext, (byte)(NatPacket.TcpRst | NatPacket.TcpAck), ReadOnlySpan<byte>.Empty, false);
                c.Release();
                return;
            }
            Buffer.BlockCopy(c.ToHost, n, c.ToHost, 0, c.ToHostCount - n);
            c.ToHostCount -= n;
            if (wasClosed && c.FreeToHost >= c.GuestMss)
            {
                // The window we offer opened again: tell the guest, or it waits for ever.
                SendSegment(c, c.SendNext, NatPacket.TcpAck, ReadOnlySpan<byte>.Empty, false);
            }
            ShutHostSendWhenDrained(c);
        }

        // Guest FIN -> half-close of the host socket once every byte before it is written
        // (design 7.4, closing).
        private static void ShutHostSendWhenDrained(NatTcpConnection c)
        {
            if (c.GuestFinished && c.ToHostCount == 0 && !c.HostShutSend && c.Host != null)
            {
                c.HostShutSend = true;
                try
                {
                    c.Host.Shutdown(SocketShutdown.Send);
                }
                catch (SocketException)
                {
                    // The host side is already gone; the read side reports it.
                }
            }
        }

        private void EnterTimeWaitWhenDone(NatTcpConnection c)
        {
            if (c.State == NatTcpState.Established && c.GuestFinished && c.FinAcked && c.ToHostCount == 0)
            {
                Socket? host = c.Host;
                c.Host = null;
                host?.Close();
                c.State = NatTcpState.TimeWait;
                // A short TIME-WAIT, so a late resent FIN still gets its ACK (design 7.4).
                c.TimeWaitEndsAt = _polls + 2 * PollsPerSecond;
            }
        }

        // Sends what the guest's window allows, our FIN after the last byte, a one-byte probe
        // of a zero window, and the retransmit of the oldest unacknowledged segment.
        private void PumpToGuest(NatTcpConnection c)
        {
            int mss = c.GuestMss < MaxSegmentData ? c.GuestMss : MaxSegmentData;
            int unsent = c.ToGuestCount - c.InFlight;
            int usable = c.GuestWindow - c.InFlight;
            while (unsent > 0 && usable > 0 && !c.FinSent)
            {
                int n = unsent;
                if (n > usable)
                {
                    n = usable;
                }
                if (n > mss)
                {
                    n = mss;
                }
                SendSegment(c, c.SendNext, (byte)(NatPacket.TcpAck | NatPacket.TcpPsh), c.ToGuest.AsSpan(c.InFlight, n), false);
                c.SendNext += (uint)n;
                unsent -= n;
                usable -= n;
                if (c.RetransmitAt == 0)
                {
                    c.RetransmitAt = _polls + c.RetransmitPolls;
                }
            }

            if (unsent == 0 && c.HostEnded && !c.FinSent)
            {
                c.FinSent = true;
                c.FinSequence = c.SendNext;
                SendSegment(c, c.SendNext, (byte)(NatPacket.TcpFin | NatPacket.TcpAck), ReadOnlySpan<byte>.Empty, false);
                c.SendNext++;
                if (c.RetransmitAt == 0)
                {
                    c.RetransmitAt = _polls + c.RetransmitPolls;
                }
            }

            // RFC 9293 section 3.8.6.1: probe a zero window with one byte now and then.
            if (c.GuestWindow == 0 && unsent > 0 && c.SendNext == c.SendUnacked)
            {
                if (c.PersistAt == 0)
                {
                    c.PersistAt = _polls + c.RetransmitPolls;
                }
                else if (_polls >= c.PersistAt)
                {
                    SendSegment(c, c.SendNext, (byte)(NatPacket.TcpAck | NatPacket.TcpPsh), c.ToGuest.AsSpan(0, 1), false);
                    c.SendNext += 1;
                    c.PersistAt = 0;
                    c.RetransmitAt = _polls + c.RetransmitPolls;
                }
            }

            if (c.SendNext == c.SendUnacked)
            {
                c.RetransmitAt = 0;
                return;
            }
            if (c.RetransmitAt == 0 || _polls < c.RetransmitAt)
            {
                return;
            }

            // RFC 6298: resend the oldest segment and double the timeout; the timer counts
            // emulated time (design 7.4).
            c.Retries++;
            if (c.Retries > MaxRetries)
            {
                SendSegment(c, c.SendNext, (byte)(NatPacket.TcpRst | NatPacket.TcpAck), ReadOnlySpan<byte>.Empty, false);
                c.Release();
                return;
            }
            Retransmits++;
            int inFlight = c.InFlight;
            if (inFlight > 0)
            {
                int n = inFlight < mss ? inFlight : mss;
                if (c.GuestWindow > 0 && n > c.GuestWindow)
                {
                    n = c.GuestWindow;
                }
                SendSegment(c, c.SendUnacked, (byte)(NatPacket.TcpAck | NatPacket.TcpPsh), c.ToGuest.AsSpan(0, n), false);
            }
            else if (c.FinSent && !c.FinAcked)
            {
                SendSegment(c, c.FinSequence, (byte)(NatPacket.TcpFin | NatPacket.TcpAck), ReadOnlySpan<byte>.Empty, false);
            }
            long doubled = c.RetransmitPolls * 2;
            c.RetransmitPolls = doubled < 60L * PollsPerSecond ? doubled : 60L * PollsPerSecond;
            c.RetransmitAt = _polls + c.RetransmitPolls;
        }

        private void SendSegment(NatTcpConnection c, uint sequence, byte flags, ReadOnlySpan<byte> data, bool withMss)
        {
            int header = withMss ? 24 : NatPacket.TcpHeaderBytes;
            int ipLength = NatPacket.Ipv4HeaderBytes + header + data.Length;
            Span<byte> frame = StartFrame(c.GuestMac, ipLength);
            Span<byte> tcp = frame.Slice(NatPacket.EthernetHeaderBytes + NatPacket.Ipv4HeaderBytes, header + data.Length);
            data.CopyTo(tcp.Slice(header));
            int free = c.FreeToHost;
            ushort window = (ushort)(free > 65535 ? 65535 : (free < 0 ? 0 : free));
            uint ack = (flags & NatPacket.TcpAck) != 0 ? c.ReceiveNext : 0;
            NatPacket.WriteTcp(tcp, header, c.RemoteAddress, c.GuestAddress, c.RemotePort, c.GuestPort, sequence, ack, flags, window, MaxSegmentData);
            NatPacket.WriteIpv4(frame.Slice(NatPacket.EthernetHeaderBytes), ipLength, ++_ipIdentification, NatPacket.ProtocolTcp, c.RemoteAddress, c.GuestAddress);
            Emit(frame);
        }

        private void SendBareTcp(ReadOnlySpan<byte> guestMac, uint source, uint destination, ushort sourcePort, ushort destinationPort, uint sequence, uint acknowledgment, byte flags)
        {
            int ipLength = NatPacket.Ipv4HeaderBytes + NatPacket.TcpHeaderBytes;
            Span<byte> frame = StartFrame(guestMac, ipLength);
            NatPacket.WriteTcp(frame.Slice(NatPacket.EthernetHeaderBytes + NatPacket.Ipv4HeaderBytes, NatPacket.TcpHeaderBytes), NatPacket.TcpHeaderBytes,
                source, destination, sourcePort, destinationPort, sequence, acknowledgment, flags, 0, 0);
            NatPacket.WriteIpv4(frame.Slice(NatPacket.EthernetHeaderBytes), ipLength, ++_ipIdentification, NatPacket.ProtocolTcp, source, destination);
            Emit(frame);
        }

        // RFC 9293 section 3.2: the MSS option, kind 2 length 4; 536 when the guest sends none.
        private static int ReadMss(ReadOnlySpan<byte> options)
        {
            int i = 0;
            while (i < options.Length)
            {
                byte kind = options[i];
                if (kind == 0)
                {
                    break;
                }
                if (kind == 1)
                {
                    i++;
                    continue;
                }
                if (i + 1 >= options.Length || options[i + 1] < 2)
                {
                    break;
                }
                if (kind == 2 && options[i + 1] == 4 && i + 4 <= options.Length)
                {
                    int mss = BinaryPrimitives.ReadUInt16BigEndian(options.Slice(i + 2));
                    return mss > 0 ? mss : DefaultGuestMss;
                }
                i += options[i + 1];
            }
            return DefaultGuestMss;
        }

        // ── Private methods: port forwards ──

        // Moves rules added or removed on other threads into the router's own list.
        private void TakeForwardChanges()
        {
            lock (_forwardGate)
            {
                for (int i = 0; i < _forwardsToAdd.Count; i++)
                {
                    _forwards.Add(_forwardsToAdd[i]);
                }
                _forwardsToAdd.Clear();
                for (int i = 0; i < _forwardsToRemove.Count; i++)
                {
                    NatForwardBinding b = _forwardsToRemove[i];
                    _forwards.Remove(b);
                    for (int j = _udpForwards.Count - 1; j >= 0; j--)
                    {
                        if (b.IndexOf(_udpForwards[j].Listener) >= 0)
                        {
                            _udpForwards.RemoveAt(j);
                        }
                    }
                    b.Close();
                }
                _forwardsToRemove.Clear();
            }
        }

        // A readable socket of a port forward: accept the TCP clients or read the datagrams.
        // False when the socket is not a forward's.
        private bool ServeForward(Socket s)
        {
            for (int i = 0; i < _forwards.Count; i++)
            {
                int index = _forwards[i].IndexOf(s);
                if (index < 0)
                {
                    continue;
                }
                if (_forwards[i].Rule.Protocol == NatForwardProtocol.Tcp)
                {
                    AcceptForward(_forwards[i], index);
                }
                else
                {
                    ReadUdpForward(_forwards[i], index);
                }
                return true;
            }
            return false;
        }

        // A host client on a TCP forward: the NAT opens the connection to the guest itself, from
        // the gateway address, so the guest needs no route (design 7.7).
        private void AcceptForward(NatForwardBinding b, int index)
        {
            while (true)
            {
                Socket accepted;
                try
                {
                    accepted = b.Sockets[index].Accept();
                }
                catch (SocketException)
                {
                    return;
                }
                uint guest = b.Rule.GuestAddress != 0 ? b.Rule.GuestAddress : KnownGuest();
                if (guest == 0)
                {
                    ForwardNoGuest++;
                    accepted.Close();
                    continue;
                }
                if (_tcp.Count >= MaxTcpConnections)
                {
                    RefusedOverLimit++;
                    accepted.Close();
                    continue;
                }
                accepted.Blocking = false;
                accepted.NoDelay = true;
                byte[]? mac = MacFor(guest);
                NatTcpConnection c = new NatTcpConnection(mac ?? new byte[6], guest, b.GuestPorts[index], _plan.Gateway, NextGatewayPort(),
                    0, DefaultGuestMss, (uint)_random.Next());
                c.Host = accepted;
                c.RetransmitPolls = PollsPerSecond;
                _tcp.Add(c);
                ForwardConnections++;
                if (mac == null)
                {
                    c.State = NatTcpState.GuestResolving;
                    c.Retries = 0;
                    SendArpRequest(guest);
                    c.RetransmitAt = _polls + c.RetransmitPolls;
                }
                else
                {
                    SendSyn(c);
                }
            }
        }

        private void SendSyn(NatTcpConnection c)
        {
            c.State = NatTcpState.SynSent;
            c.SendUnacked = c.OurInitialSequence;
            c.SendNext = c.OurInitialSequence + 1;
            c.Retries = 0;
            c.RetransmitPolls = PollsPerSecond;
            SendSegment(c, c.OurInitialSequence, NatPacket.TcpSyn, ReadOnlySpan<byte>.Empty, true);
            c.RetransmitAt = _polls + c.RetransmitPolls;
        }

        // Our SYN or ARP request went unanswered for a while: send it again, or give up and let
        // the host client go.
        private void RetryForward(NatTcpConnection c)
        {
            c.Retries++;
            int limit = c.State == NatTcpState.SynSent ? MaxSynTries : MaxArpTries;
            if (c.Retries >= limit)
            {
                ForwardGuestNoAnswer++;
                c.Release();
                return;
            }
            if (c.State == NatTcpState.SynSent)
            {
                Retransmits++;
                SendSegment(c, c.OurInitialSequence, NatPacket.TcpSyn, ReadOnlySpan<byte>.Empty, true);
            }
            else
            {
                SendArpRequest(c.GuestAddress);
            }
            long doubled = c.RetransmitPolls * 2;
            c.RetransmitPolls = doubled < 8L * PollsPerSecond ? doubled : 8L * PollsPerSecond;
            c.RetransmitAt = _polls + c.RetransmitPolls;
        }

        // A reset, not an orderly close, toward the host client (SO_LINGER with time 0).
        private static void ResetHost(NatTcpConnection c)
        {
            try
            {
                if (c.Host != null)
                {
                    c.Host.LingerState = new LingerOption(true, 0);
                }
            }
            catch (SocketException)
            {
                // The host side is already gone.
            }
            catch (ObjectDisposedException)
            {
                // Already closed.
            }
        }

        // Datagrams from host senders on a UDP forward go to the guest from the gateway address
        // and a gateway port per sender, so the guest's answer finds its way back.
        private void ReadUdpForward(NatForwardBinding b, int index)
        {
            Socket s = b.Sockets[index];
            while (true)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try
                {
                    n = s.ReceiveFrom(_receive, ref from);
                }
                catch (SocketException e)
                {
                    if (e.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        // Windows reports an ICMP error for an earlier answer here; go on.
                        continue;
                    }
                    return;
                }
                if (n > MaxUdpData)
                {
                    DroppedUdpTooBig++;
                    continue;
                }
                uint guest = b.Rule.GuestAddress != 0 ? b.Rule.GuestAddress : KnownGuest();
                byte[]? mac = guest == 0 ? null : MacFor(guest);
                if (mac == null)
                {
                    // UDP gives no promise: drop it, count it, and ask for the MAC so the
                    // sender's next try gets through.
                    ForwardNoGuest++;
                    if (guest != 0)
                    {
                        SendArpRequest(guest);
                    }
                    continue;
                }
                IPEndPoint remote = (IPEndPoint)from;
                NatUdpForwardSession? session = null;
                for (int i = 0; i < _udpForwards.Count; i++)
                {
                    if (ReferenceEquals(_udpForwards[i].Listener, s) && _udpForwards[i].Remote.Equals(remote))
                    {
                        session = _udpForwards[i];
                        break;
                    }
                }
                long now = Environment.TickCount64;
                if (session == null)
                {
                    if (_udpForwards.Count >= MaxUdpMappings)
                    {
                        RefusedOverLimit++;
                        continue;
                    }
                    session = new NatUdpForwardSession(s, remote, NextGatewayPort(), guest, b.GuestPorts[index], now);
                    _udpForwards.Add(session);
                }
                session.LastUsedMilliseconds = now;
                int ipLength = NatPacket.Ipv4HeaderBytes + NatPacket.UdpHeaderBytes + n;
                Span<byte> frame = StartFrame(mac, ipLength);
                Span<byte> udp = frame.Slice(NatPacket.EthernetHeaderBytes + NatPacket.Ipv4HeaderBytes, NatPacket.UdpHeaderBytes + n);
                _receive.AsSpan(0, n).CopyTo(udp.Slice(NatPacket.UdpHeaderBytes));
                NatPacket.WriteUdp(udp, _plan.Gateway, guest, session.GatewayPort, session.GuestPort);
                NatPacket.WriteIpv4(frame.Slice(NatPacket.EthernetHeaderBytes), ipLength, ++_ipIdentification, NatPacket.ProtocolUdp, _plan.Gateway, guest);
                Emit(frame);
                ForwardDatagramsToGuest++;
            }
        }

        // The guest's answer to a UDP forward's gateway port: back to that host sender. False
        // when the datagram belongs to no forward.
        private bool AnswerUdpForward(uint source, ushort sourcePort, ushort destinationPort, ReadOnlySpan<byte> data)
        {
            for (int i = 0; i < _udpForwards.Count; i++)
            {
                NatUdpForwardSession f = _udpForwards[i];
                if (f.GuestAddress != source || f.GuestPort != sourcePort || f.GatewayPort != destinationPort)
                {
                    continue;
                }
                f.LastUsedMilliseconds = Environment.TickCount64;
                try
                {
                    f.Listener.SendTo(data, SocketFlags.None, f.Remote);
                    ForwardDatagramsToHost++;
                }
                catch (SocketException)
                {
                    DroppedNoRoute++;
                }
                return true;
            }
            return false;
        }

        // The next gateway port no forwarded connection or UDP sender is using.
        private ushort NextGatewayPort()
        {
            for (int tries = 0; tries < 65536 - FirstGatewayPort; tries++)
            {
                int port = _nextGatewayPort;
                _nextGatewayPort = port >= 65535 ? FirstGatewayPort : port + 1;
                bool used = false;
                for (int i = 0; i < _tcp.Count && !used; i++)
                {
                    used = _tcp[i].RemoteAddress == _plan.Gateway && _tcp[i].RemotePort == port;
                }
                for (int i = 0; i < _udpForwards.Count && !used; i++)
                {
                    used = _udpForwards[i].GatewayPort == port;
                }
                if (!used)
                {
                    return (ushort)port;
                }
            }
            // Every dynamic port is taken: the connection limits make this unreachable
            // (256 TCP connections and 256 UDP senders against 16384 ports).
            throw new InvalidOperationException("No free gateway port for a port forward.");
        }

        // A guest's own address: inside the guest network, not the gateway or name server.
        private bool IsGuestAddress(uint address)
        {
            return address != 0 && address != _plan.Gateway && address != _plan.NameServer && address != _plan.Broadcast && _plan.IsInside(address);
        }

        // Notes which MAC an address sits at; a forwarded connection waiting for it goes on.
        private void Learn(uint address, ReadOnlySpan<byte> mac)
        {
            for (int i = 0; i < _seenAddresses.Count; i++)
            {
                if (_seenAddresses[i] == address)
                {
                    if (mac.SequenceEqual(_seenMacs[i]))
                    {
                        return;
                    }
                    mac.Slice(0, 6).CopyTo(_seenMacs[i]);
                    ResumeResolving(address, mac);
                    return;
                }
            }
            _seenAddresses.Add(address);
            _seenMacs.Add(mac.Slice(0, 6).ToArray());
            ResumeResolving(address, mac);
        }

        private void ResumeResolving(uint address, ReadOnlySpan<byte> mac)
        {
            for (int i = 0; i < _tcp.Count; i++)
            {
                NatTcpConnection c = _tcp[i];
                if (c.State == NatTcpState.GuestResolving && c.GuestAddress == address)
                {
                    mac.Slice(0, 6).CopyTo(c.GuestMac);
                    SendSyn(c);
                }
            }
        }

        // The guest a rule without a guest address goes to: the first address seen in the
        // guest's frames, else the first address DHCP handed out, else none (0).
        private uint KnownGuest()
        {
            if (_seenAddresses.Count > 0)
            {
                return _seenAddresses[0];
            }
            return _leaseAddresses.Count > 0 ? _leaseAddresses[0] : 0;
        }

        private byte[]? MacFor(uint address)
        {
            for (int i = 0; i < _seenAddresses.Count; i++)
            {
                if (_seenAddresses[i] == address)
                {
                    return _seenMacs[i];
                }
            }
            for (int i = 0; i < _leaseAddresses.Count; i++)
            {
                if (_leaseAddresses[i] == address)
                {
                    return _leaseMacs[i];
                }
            }
            return null;
        }

        // RFC 826: who has this address? Asked from the gateway.
        private void SendArpRequest(uint target)
        {
            Span<byte> request = _frame.AsSpan(0, NatPacket.MinFrameBytes);
            request.Clear();
            NatPacket.WriteEthernet(request, _broadcastMac, _plan.GatewayMac, NatPacket.EtherTypeArp);
            Span<byte> body = request.Slice(NatPacket.EthernetHeaderBytes);
            BinaryPrimitives.WriteUInt16BigEndian(body, 1);
            BinaryPrimitives.WriteUInt16BigEndian(body.Slice(2), NatPacket.EtherTypeIpv4);
            body[4] = 6;
            body[5] = 4;
            BinaryPrimitives.WriteUInt16BigEndian(body.Slice(6), 1);
            _plan.GatewayMac.CopyTo(body.Slice(8));
            NatPacket.WriteAddress(body.Slice(14), _plan.Gateway);
            NatPacket.WriteAddress(body.Slice(24), target);
            _output(request);
        }

        // ── Private methods: addresses, frames ──

        // Where a guest packet to (address, port) goes on the host: the gateway reaches the
        // host's own 127.0.0.1 (design 7.9), the name server's port 53 the host's name server,
        // any other outside address itself. Null when there is no way (counted).
        private IPEndPoint? TranslateOut(uint address, ushort port)
        {
            if (address == _plan.Gateway)
            {
                if (!HostLoopback)
                {
                    DroppedNoRoute++;
                    return null;
                }
                return new IPEndPoint(IPAddress.Loopback, port);
            }
            if (address == _plan.NameServer)
            {
                if (port != 53)
                {
                    DroppedNoRoute++;
                    return null;
                }
                if (_nameServer == null)
                {
                    DroppedNoNameServer++;
                    return null;
                }
                return _nameServer;
            }
            if (_plan.IsInside(address) || address == 0 || (address >> 28) >= 14 || (address >> 24) == 127)
            {
                // Another guest (phase 4), multicast or a loopback address of the guest's own.
                DroppedNoRoute++;
                return null;
            }
            return new IPEndPoint(new IPAddress(new byte[] { (byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address }), port);
        }

        // The source address the guest sees for a datagram from the host.
        private uint TranslateBack(IPEndPoint remote, NatUdpMapping m)
        {
            if (_nameServer != null && m.LastRemoteAddress == _plan.NameServer && remote.Port == _nameServer.Port && remote.Address.Equals(_nameServer.Address))
            {
                return _plan.NameServer;
            }
            byte[] bytes = remote.Address.GetAddressBytes();
            uint address = NatPacket.ReadAddress(bytes);
            if ((address >> 24) == 127)
            {
                return _plan.Gateway;
            }
            return address;
        }

        private NatTcpConnection? FindTcp(Socket s)
        {
            for (int i = 0; i < _tcp.Count; i++)
            {
                if (ReferenceEquals(_tcp[i].Host, s))
                {
                    return _tcp[i];
                }
            }
            return null;
        }

        private NatUdpMapping? FindUdp(Socket s)
        {
            for (int i = 0; i < _udp.Count; i++)
            {
                if (ReferenceEquals(_udp[i].Host, s))
                {
                    return _udp[i];
                }
            }
            return null;
        }

        // The scratch frame with its Ethernet header to the guest; the caller writes the IP
        // packet of ipLength bytes after it. Short frames are padded to 60 bytes with zeros.
        private Span<byte> StartFrame(ReadOnlySpan<byte> destinationMac, int ipLength)
        {
            int length = NatPacket.EthernetHeaderBytes + ipLength;
            if (length < NatPacket.MinFrameBytes)
            {
                length = NatPacket.MinFrameBytes;
            }
            Span<byte> frame = _frame.AsSpan(0, length);
            frame.Clear();
            NatPacket.WriteEthernet(frame, destinationMac, _plan.GatewayMac, NatPacket.EtherTypeIpv4);
            return frame;
        }

        private void Emit(ReadOnlySpan<byte> frame)
        {
            _output(frame);
        }

        // The first IPv4 name server of a host network interface that is up. Reading it through
        // the interface properties is what the design assumed works on every system (7.3, DNS row).
        private static IPEndPoint? FindHostNameServer()
        {
            NetworkInterface[] interfaces;
            try
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (NetworkInformationException)
            {
                return null;
            }
            for (int i = 0; i < interfaces.Length; i++)
            {
                NetworkInterface nic = interfaces[i];
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }
                IPAddressCollection servers;
                try
                {
                    servers = nic.GetIPProperties().DnsAddresses;
                }
                catch (NetworkInformationException)
                {
                    continue;
                }
                for (int j = 0; j < servers.Count; j++)
                {
                    IPAddress server = servers[j];
                    if (server.AddressFamily == AddressFamily.InterNetwork && !server.Equals(IPAddress.Any))
                    {
                        return new IPEndPoint(server, 53);
                    }
                }
            }
            return null;
        }
    }
}
