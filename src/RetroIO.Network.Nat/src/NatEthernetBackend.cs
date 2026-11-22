//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using RetroIO.Network.Ethernet;

namespace RetroIO.Network.Nat
{
    /// <summary>
    /// Hands one bare IP packet (no Ethernet header) from the NAT to a guest on a serial line.
    /// </summary>
    /// <param name="packet">
    /// The buffer holding the packet.
    /// </param>
    /// <param name="offset">
    /// Where the packet starts.
    /// </param>
    /// <param name="length">
    /// The packet length: its IPv4 total length.
    /// </param>
    public delegate void NatPacketHandler(byte[] packet, int offset, int length);

    /// <summary>
    /// The built-in NAT as an Ethernet backend (<c>net=nat</c>): the guest is on a small network
    /// of its own with one router, which hands out its address, answers ping and relays its TCP
    /// and UDP to ordinary host sockets. No admin rights and no change to the host's network
    /// settings (RetroCore's host networking design (host-networking-design.md in the RetroCore repository),
    /// section 7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Threading (design 7.5): <see cref="Send"/> runs on the emulation thread and only copies the
    /// guest's frame into a bounded queue. One NAT thread per backend takes frames from that
    /// queue, runs the <see cref="NatRouter"/> and does all socket work. Frames toward the guest
    /// go into a second bounded queue that <see cref="Poll"/> drains on the emulation thread, at
    /// most <see cref="MaxFramesPerPoll"/> per call. A frame that finds a queue full is dropped
    /// and counted, as on real Ethernet when a receive ring is full.
    /// </para>
    /// <para>
    /// A guest on a serial line (SLIP, later PPP: item 18 of the TODO.md in the RetroCore repository) has no Ethernet. Its
    /// bare IP packets come in through <see cref="SendPacket"/> and go out through
    /// <see cref="OnPacketReceived"/>. Inside, each packet is carried in an Ethernet frame whose
    /// guest address is <see cref="SerialGuestMac"/> (all zeros: no network card has it, since it
    /// is not a station address), so the router treats it like any other guest; <see cref="Poll"/>
    /// takes the header off again for frames to that address.
    /// </para>
    /// <para>
    /// Emulated time: every <see cref="Poll"/> counts one poll, and the router's guest-side TCP
    /// timers count polls, so a paused machine is not sent retransmissions it cannot take.
    /// </para>
    /// </remarks>
    public sealed class NatEthernetBackend : IEthernetBackend, IEthernetBackendStatus, IEthernetPortForwarding
    {
        // ── Constants ──

        /// <summary>
        /// The backend spec that selects this backend: <c>nat</c>.
        /// </summary>
        public const string Spec = "nat";

        /// <summary>
        /// The spec option that turns off the gateway's way to the host's 127.0.0.1.
        /// </summary>
        public const string HostLoopbackOff = "hostloopback=off";

        /// <summary>
        /// The most frames handed to the guest per <see cref="Poll"/> (design 7.5).
        /// </summary>
        public const int MaxFramesPerPoll = 64;

        private const int QueueSlots = 256;

        // The guest address that marks a guest on a serial line inside the router.
        private static readonly byte[] SerialMac = new byte[6];

        private const int MaxFrameBytes = 1514;

        // ── Fields ──

        private readonly NatAddressPlan _plan;

        private readonly bool _hostLoopback;

        private readonly IPEndPoint? _nameServer;

        private readonly bool _findHostNameServer;

        private readonly EthernetFrameRing _fromGuest = new EthernetFrameRing(QueueSlots, MaxFrameBytes);

        private readonly EthernetFrameRing _toGuest = new EthernetFrameRing(QueueSlots, MaxFrameBytes);

        private readonly AutoResetEvent _wake = new AutoResetEvent(false);

        private readonly object _gate = new object();

        private NatRouter? _router;

        private Thread? _thread;

        private volatile bool _running;

        private long _polls;

        private long _framesFromGuestDropped;

        // The port forward rules in the order they were added, each with its open sockets
        // (null while the NAT is stopped or when a port could not be opened) and the reason a
        // port could not be opened. Guarded by _gate.
        private readonly List<NatPortForward> _forwardRules = new List<NatPortForward>();

        private readonly List<NatForwardBinding?> _forwardBindings = new List<NatForwardBinding?>();

        private readonly List<string> _forwardProblems = new List<string>();

        // ── Constructors ──

        /// <summary>
        /// Initializes a NAT backend with the default address plan and host loopback on.
        /// </summary>
        public NatEthernetBackend()
            : this(new NatAddressPlan(), true, null, true)
        {
        }

        /// <summary>
        /// Initializes a NAT backend.
        /// </summary>
        /// <param name="plan">
        /// The address plan.
        /// </param>
        /// <param name="hostLoopback">
        /// <c>true</c> to let connections to the gateway reach the host's own 127.0.0.1 (design 7.9).
        /// </param>
        /// <param name="nameServer">
        /// The host name server for the guest's lookups; <c>null</c> for the host's own first one.
        /// </param>
        /// <param name="findHostNameServer">
        /// <c>false</c> to skip looking for the host's name server and answer name-to-address
        /// lookups from the host's resolver, as on a host with no name server address.
        /// </param>
        public NatEthernetBackend(NatAddressPlan plan, bool hostLoopback, IPEndPoint? nameServer, bool findHostNameServer)
        {
            _plan = plan ?? throw new ArgumentNullException(nameof(plan));
            _hostLoopback = hostLoopback;
            _nameServer = nameServer;
            _findHostNameServer = findHostNameServer;
        }

        // ── Events ──

        /// <inheritdoc />
        public event EthernetFrameHandler? OnFrameReceived;

        /// <summary>
        /// Raised in <see cref="Poll"/> for each bare IP packet to the guest on a serial line.
        /// </summary>
        public event NatPacketHandler? OnPacketReceived;

        // ── Properties ──

        /// <inheritdoc />
        public bool IsRunning => _running;

        /// <summary>
        /// Gets the address plan.
        /// </summary>
        public NatAddressPlan Plan => _plan;

        /// <summary>
        /// Gets a value indicating whether the gateway reaches the host's 127.0.0.1.
        /// </summary>
        public bool HostLoopback => _hostLoopback;

        /// <summary>
        /// Gets the router while running (for status and tests); <c>null</c> when stopped.
        /// </summary>
        public NatRouter? Router => _router;

        /// <summary>
        /// Gets the guest address that marks a guest on a serial line (six zero bytes).
        /// </summary>
        public static ReadOnlySpan<byte> SerialGuestMac => SerialMac;

        /// <summary>
        /// Gets the polls so far: the emulated time the guest-side timers count.
        /// </summary>
        public long Polls => Interlocked.Read(ref _polls);

        // ── Public methods ──

        /// <summary>
        /// Registers the factory that turns the spec <c>nat</c> (or <c>nat:hostloopback=off</c>)
        /// into a NAT backend, so a machine's <c>net=nat</c> option and the <c>net.nat</c> command
        /// find it through <see cref="EthernetBackends"/>.
        /// </summary>
        public static void RegisterFactory()
        {
            EthernetBackends.Register(CreateFromSpec);
        }

        /// <summary>
        /// Makes a NAT backend from a spec, or gives <c>null</c> when the spec is not for the NAT.
        /// </summary>
        /// <param name="spec">
        /// <c>nat</c> or <c>nat:hostloopback=off</c> (or <c>=on</c>).
        /// </param>
        /// <returns>
        /// The backend, or <c>null</c>.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown for a <c>nat:</c> spec with an unknown option.
        /// </exception>
        public static IEthernetBackend? CreateFromSpec(string spec)
        {
            if (spec == null)
            {
                return null;
            }
            string s = spec.Trim().ToLowerInvariant();
            if (s == Spec)
            {
                return new NatEthernetBackend();
            }
            if (!s.StartsWith(Spec + ":", StringComparison.Ordinal))
            {
                return null;
            }
            string option = s.Substring(Spec.Length + 1);
            if (option == HostLoopbackOff)
            {
                return new NatEthernetBackend(new NatAddressPlan(), false, null, true);
            }
            if (option == "hostloopback=on")
            {
                return new NatEthernetBackend();
            }
            throw new ArgumentException("Unknown NAT option '" + option + "'. Use hostloopback=on or hostloopback=off.", nameof(spec));
        }

        /// <inheritdoc />
        public void Send(byte[] frame, int offset, int length)
        {
            if (!_running || frame == null || length <= 0)
            {
                return;
            }
            if (_fromGuest.TryReserve(length, out Span<byte> slot))
            {
                new ReadOnlySpan<byte>(frame, offset, length).CopyTo(slot);
                _fromGuest.Commit();
                _wake.Set();
            }
            else
            {
                Interlocked.Increment(ref _framesFromGuestDropped);
            }
        }

        /// <summary>
        /// Takes one bare IP packet from a guest on a serial line (no Ethernet header); called
        /// on the emulation thread, as <see cref="Send"/> is.
        /// </summary>
        /// <param name="packet">
        /// The buffer holding the packet.
        /// </param>
        /// <param name="offset">
        /// Where the packet starts.
        /// </param>
        /// <param name="length">
        /// The packet length.
        /// </param>
        public void SendPacket(byte[] packet, int offset, int length)
        {
            if (!_running || packet == null || length <= 0)
            {
                return;
            }
            if (_fromGuest.TryReserve(NatPacket.EthernetHeaderBytes + length, out Span<byte> slot))
            {
                NatPacket.WriteEthernet(slot, _plan.GatewayMac, SerialMac, NatPacket.EtherTypeIpv4);
                new ReadOnlySpan<byte>(packet, offset, length).CopyTo(slot.Slice(NatPacket.EthernetHeaderBytes));
                _fromGuest.Commit();
                _wake.Set();
            }
            else
            {
                Interlocked.Increment(ref _framesFromGuestDropped);
            }
        }

        /// <inheritdoc />
        public void Poll()
        {
            Interlocked.Increment(ref _polls);
            EthernetFrameHandler? handler = OnFrameReceived;
            NatPacketHandler? packets = OnPacketReceived;
            for (int i = 0; i < MaxFramesPerPoll; i++)
            {
                if (!_toGuest.TryPeek(out byte[]? buffer, out int length))
                {
                    break;
                }
                if (new ReadOnlySpan<byte>(buffer, 0, 6).SequenceEqual(SerialMac))
                {
                    // A frame for the serial-line guest: hand on the IP packet alone, by its own
                    // total length (the frame may carry padding to 60 bytes).
                    int ipLength = length >= NatPacket.EthernetHeaderBytes + NatPacket.Ipv4HeaderBytes
                        ? (buffer![NatPacket.EthernetHeaderBytes + 2] << 8) | buffer[NatPacket.EthernetHeaderBytes + 3]
                        : 0;
                    if (ipLength > 0 && ipLength <= length - NatPacket.EthernetHeaderBytes)
                    {
                        packets?.Invoke(buffer!, NatPacket.EthernetHeaderBytes, ipLength);
                    }
                }
                else
                {
                    handler?.Invoke(buffer!, 0, length);
                }
                _toGuest.Release();
            }
        }

        /// <inheritdoc />
        public void Start()
        {
            lock (_gate)
            {
                if (_running)
                {
                    return;
                }
                _router = new NatRouter(_plan, ToGuest, _hostLoopback, _nameServer, _findHostNameServer);
                // Rules added while stopped start listening now. A port that cannot be opened
                // is kept as a rule and its reason shown; the other rules still start (design 7.7).
                for (int i = 0; i < _forwardRules.Count; i++)
                {
                    if (NatForwardBinding.TryOpen(_forwardRules[i], out NatForwardBinding? binding, out string error))
                    {
                        _forwardBindings[i] = binding;
                        _forwardProblems[i] = string.Empty;
                        _router.AttachForward(binding!);
                    }
                    else
                    {
                        _forwardBindings[i] = null;
                        _forwardProblems[i] = error;
                    }
                }
                _running = true;
                _thread = new Thread(Run) { IsBackground = true, Name = "RetroIO NAT" };
                _thread.Start();
            }
        }

        /// <inheritdoc />
        public void Stop()
        {
            Thread? thread;
            lock (_gate)
            {
                if (!_running)
                {
                    return;
                }
                _running = false;
                thread = _thread;
                _thread = null;
            }
            _wake.Set();
            thread?.Join();
            lock (_gate)
            {
                for (int i = 0; i < _forwardBindings.Count; i++)
                {
                    _forwardBindings[i]?.Close();
                    _forwardBindings[i] = null;
                }
            }
        }

        /// <inheritdoc />
        public bool AddForward(string rule, out string message)
        {
            if (!NatPortForward.TryParse(rule, out NatPortForward? r, out message))
            {
                return false;
            }
            lock (_gate)
            {
                for (int i = 0; i < _forwardRules.Count; i++)
                {
                    if (_forwardRules[i].SameAs(r))
                    {
                        message = r + " is already there";
                        return false;
                    }
                    if (_forwardRules[i].Overlaps(r!))
                    {
                        message = r + " wants a host port that " + _forwardRules[i] + " already has";
                        return false;
                    }
                }
                NatForwardBinding? binding = null;
                if (_running)
                {
                    if (!NatForwardBinding.TryOpen(r!, out binding, out string error))
                    {
                        message = r + ": " + error;
                        return false;
                    }
                    _router!.AttachForward(binding!);
                }
                _forwardRules.Add(r!);
                _forwardBindings.Add(binding);
                _forwardProblems.Add(string.Empty);
            }
            message = r!.ToString();
            return true;
        }

        /// <inheritdoc />
        public bool RemoveForward(string rule, out string message)
        {
            if (!NatPortForward.TryParse(rule, out NatPortForward? r, out message))
            {
                return false;
            }
            lock (_gate)
            {
                for (int i = 0; i < _forwardRules.Count; i++)
                {
                    if (!_forwardRules[i].SameAs(r))
                    {
                        continue;
                    }
                    NatForwardBinding? binding = _forwardBindings[i];
                    _forwardRules.RemoveAt(i);
                    _forwardBindings.RemoveAt(i);
                    _forwardProblems.RemoveAt(i);
                    if (binding != null)
                    {
                        if (_running)
                        {
                            // The NAT thread may be waiting on these sockets: it closes them.
                            _router!.DetachForward(binding);
                            _wake.Set();
                        }
                        else
                        {
                            binding.Close();
                        }
                    }
                    message = r!.ToString();
                    return true;
                }
            }
            message = "no rule " + r;
            return false;
        }

        /// <inheritdoc />
        public string[] GetForwards()
        {
            lock (_gate)
            {
                string[] rules = new string[_forwardRules.Count];
                for (int i = 0; i < rules.Length; i++)
                {
                    rules[i] = _forwardRules[i].ToString();
                }
                return rules;
            }
        }

        /// <inheritdoc />
        public string[] DescribeForwards()
        {
            lock (_gate)
            {
                string[] lines = new string[_forwardRules.Count];
                for (int i = 0; i < lines.Length; i++)
                {
                    NatPortForward r = _forwardRules[i];
                    string guest = r.GuestAddress == 0 ? "the guest" : NatPacket.Dotted(r.GuestAddress);
                    string ports = r.PortCount == 1 ? "port " + r.HostPortFirst : "ports " + r.HostPortFirst + "-" + r.HostPortLast;
                    string state = _forwardBindings[i] != null
                        ? "listening"
                        : (_forwardProblems[i].Length > 0 ? "NOT listening: " + _forwardProblems[i] : "not listening (the NAT is stopped)");
                    lines[i] = r + " - host " + ports + " on " + r.ListenAddress + " to " + guest + " port " + r.GuestPortFirst
                        + (r.PortCount == 1 ? string.Empty : "-" + (r.GuestPortFirst + r.PortCount - 1)) + ", " + state;
                }
                return lines;
            }
        }

        /// <inheritdoc />
        public string[] DescribeStatus()
        {
            NatRouter? r = _router;
            if (r == null)
            {
                return new[] { "nat: stopped" };
            }
            return new[]
            {
                "nat: network " + NatPacket.Dotted(_plan.Network) + ", gateway " + NatPacket.Dotted(_plan.Gateway)
                    + (_hostLoopback ? " (reaches this computer's 127.0.0.1)" : " (host loopback off)")
                    + ", name server " + NatPacket.Dotted(_plan.NameServer) + " -> "
                    + (r.NameServer == null ? "none found on this computer: only name-to-address lookups work, answered by this computer's resolver" : r.NameServer.ToString()),
                "nat: tcp connections " + r.TcpConnections + ", udp mappings " + r.UdpMappings + ", dhcp replies " + r.DhcpReplies
                    + ", retransmits " + r.Retransmits,
                "nat: host connects refused " + r.HostConnectsRefused + ", failed " + r.HostConnectsFailed + ", refused over the limit " + r.RefusedOverLimit,
                "nat: dropped - queue from the guest full " + Interlocked.Read(ref _framesFromGuestDropped) + ", queue to the guest full " + _toGuest.FramesDroppedFull
                    + ", bad header " + r.DroppedBadHeader + ", not ARP/IPv4 " + r.DroppedUnsupported + ", fragments " + r.DroppedFragments
                    + ", no route " + r.DroppedNoRoute + ", ping outside " + r.DroppedOutsidePing + ", no name server " + r.DroppedNoNameServer
                    + ", udp too big " + r.DroppedUdpTooBig + (r.Resolver != null ? ", name lookups waiting too long " + r.Resolver.Dropped : string.Empty),
                "nat: port forwards " + GetForwards().Length + ", tcp connections in " + r.ForwardConnections + " (refused by the guest " + r.ForwardGuestRefused
                    + ", not answered " + r.ForwardGuestNoAnswer + "), udp to the guest " + r.ForwardDatagramsToGuest + ", answers back " + r.ForwardDatagramsToHost
                    + ", dropped with no guest known yet " + r.ForwardNoGuest,
            };
        }

        // ── Private methods ──

        // The NAT thread: guest frames in, socket work and timers, until Stop.
        private void Run()
        {
            NatRouter router = _router!;
            try
            {
                while (_running)
                {
                    long polls = Interlocked.Read(ref _polls);
                    while (_fromGuest.TryPeek(out byte[]? buffer, out int length))
                    {
                        router.HandleGuestFrame(new ReadOnlySpan<byte>(buffer, 0, length), polls);
                        _fromGuest.Release();
                    }
                    if (!router.Service(polls, 10000))
                    {
                        // No host socket to wait on: wait for a guest frame or 10 ms for timers.
                        _wake.WaitOne(10);
                    }
                }
            }
            finally
            {
                router.CloseAll();
            }
        }

        // Called by the router on the NAT thread: queue the frame for Poll.
        private void ToGuest(ReadOnlySpan<byte> frame)
        {
            if (_toGuest.TryReserve(frame.Length, out Span<byte> slot))
            {
                frame.CopyTo(slot);
                _toGuest.Commit();
            }
        }
    }
}
