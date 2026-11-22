//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Network.Router
{
    /// <summary>
    /// A small IPv4 router: point-to-point links on one subnet, the owner's own services, and a place
    /// for NAT to attach.
    /// </summary>
    /// <remarks>
    /// Where a packet goes, by its destination address:
    ///  - the router address (for example 10.42.0.1): to the local services (<see cref="OnLocal"/>).
    ///  - the subnet broadcast or 255.255.255.255: to the local services only, never to the other
    ///    links. RFC 1812 section 5.3.5.2 lets a router forward a directed broadcast but says it must
    ///    be possible to turn that off, and off is the default; this router never forwards one.
    ///  - the address of a link: to that link.
    ///  - any other address in the subnet: dropped as <see cref="DropReason.NoRoute"/>.
    ///  - one of <see cref="RouterSettings.UpstreamAddresses"/> (the NAT's gateway and name server):
    ///    to <see cref="OnUpstream"/>, as for an address outside the subnet.
    ///  - an address outside the subnet: to <see cref="OnUpstream"/> (RetroIO.Network.Nat), or dropped as
    ///    <see cref="DropReason.NoRoute"/> when nothing is attached.
    /// <para>
    /// A packet from a link must carry the link's own address as its source, or it is dropped as
    /// <see cref="DropReason.Spoofed"/>. A packet passed from one link to another, or upstream, has its
    /// TTL lowered by one and its checksum corrected (RFC 1812 section 5.3.1); one whose TTL would
    /// reach 0 is dropped as <see cref="DropReason.TtlExpired"/>. A packet for the router itself is
    /// delivered, not forwarded, so its TTL is not changed. No ICMP messages are sent yet.
    /// </para>
    /// <para>
    /// No threads, no timers. Every event is raised from inside the call that caused it, and the span
    /// it gives is valid only during the event.
    /// </para>
    /// </remarks>
    public sealed class IpRouter
    {
        private readonly Dictionary<uint, RouterLink> _links = new Dictionary<uint, RouterLink>();
        private readonly long[] _drops = new long[(int)DropReason.TooBig + 1];
        private readonly uint _mask;
        private readonly uint _broadcast;
        private readonly uint _poolFirst;
        private readonly uint _poolLast;
        private readonly uint[] _upstreamAddresses;
        private byte[] _scratch = new byte[2048];

        /// <summary>
        /// Called with a packet for the local services or for the upstream.
        /// </summary>
        /// <param name="packet">
        /// The packet, valid only during the call.
        /// </param>
        public delegate void PacketOutput(ReadOnlySpan<byte> packet);

        /// <summary>
        /// Called when a packet is dropped.
        /// </summary>
        /// <param name="reason">
        /// Why it was dropped.
        /// </param>
        /// <param name="count">
        /// How many packets have been dropped for this reason so far, this one included.
        /// </param>
        public delegate void PacketDropped(DropReason reason, long count);

        /// <summary>
        /// Raised with each packet for the local services: for the router address, or a broadcast.
        /// </summary>
        public event PacketOutput? OnLocal;

        /// <summary>
        /// Raised with each packet for an address outside the subnet. With no handler, such packets
        /// are dropped as <see cref="DropReason.NoRoute"/>.
        /// </summary>
        public event PacketOutput? OnUpstream;

        /// <summary>
        /// Raised for each packet dropped.
        /// </summary>
        public event PacketDropped? OnDrop;

        /// <summary>
        /// Creates a router.
        /// </summary>
        /// <param name="settings">
        /// The subnet, the router's address and the address pool.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when the settings do not describe a usable subnet.
        /// </exception>
        public IpRouter(RouterSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (settings.PrefixLength < 8 || settings.PrefixLength > 30)
            {
                throw new ArgumentException("the prefix length is 8 to 30, not " + settings.PrefixLength, nameof(settings));
            }

            Settings = settings;
            _mask = 0xFFFFFFFFu << (32 - settings.PrefixLength);
            if ((settings.Network & ~_mask) != 0)
            {
                throw new ArgumentException(Ipv4.Format(settings.Network) + " is not the start of a /" + settings.PrefixLength + " subnet", nameof(settings));
            }

            _broadcast = settings.Network | ~_mask;
            if (!IsHostAddress(settings.RouterAddress))
            {
                throw new ArgumentException("the router address " + Ipv4.Format(settings.RouterAddress) + " is not a host address in the subnet", nameof(settings));
            }

            uint[] upstream = settings.UpstreamAddresses ?? Array.Empty<uint>();
            for (int i = 0; i < upstream.Length; i++)
            {
                if (!IsHostAddress(upstream[i]) || upstream[i] == settings.RouterAddress)
                {
                    throw new ArgumentException("the upstream address " + Ipv4.Format(upstream[i]) + " is not a host address in the subnet other than the router's", nameof(settings));
                }
            }

            _upstreamAddresses = upstream;
            _poolFirst = settings.PoolFirst != 0 ? settings.PoolFirst : settings.Network + 1;
            _poolLast = settings.PoolLast != 0 ? settings.PoolLast : _broadcast - 1;
            if (!IsHostAddress(_poolFirst) || !IsHostAddress(_poolLast) || _poolFirst > _poolLast)
            {
                throw new ArgumentException("the address pool " + Ipv4.Format(_poolFirst) + " to " + Ipv4.Format(_poolLast) + " is not inside the subnet", nameof(settings));
            }
        }

        /// <summary>
        /// Gets the settings the router was made with.
        /// </summary>
        public RouterSettings Settings { get; }

        /// <summary>
        /// Gets the subnet broadcast address.
        /// </summary>
        public uint BroadcastAddress => _broadcast;

        /// <summary>
        /// Gets how many links there are.
        /// </summary>
        public int LinkCount => _links.Count;

        /// <summary>
        /// Gets how many packets have been dropped for one reason.
        /// </summary>
        /// <param name="reason">
        /// The reason.
        /// </param>
        /// <returns>
        /// The count.
        /// </returns>
        public long DropCount(DropReason reason)
        {
            return _drops[(int)reason];
        }

        /// <summary>
        /// Adds a point-to-point link, such as one dial-up call.
        /// </summary>
        /// <param name="output">
        /// Where packets for the link go.
        /// </param>
        /// <param name="address">
        /// The link's fixed address, or 0 for the next free one from the pool.
        /// </param>
        /// <param name="mtu">
        /// The largest packet the link carries; a larger one for it is dropped as
        /// <see cref="DropReason.TooBig"/>. 1006 is SLIP's usual size (RFC 1055).
        /// </param>
        /// <returns>
        /// The link.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when a fixed address is not a host address in the subnet, is the router's own, or is
        /// already given to another link; the message names the address.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the pool has no free address left.
        /// </exception>
        public RouterLink AddLink(RouterLink.PacketOutput output, uint address = 0, int mtu = 1500)
        {
            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            if (mtu < Ipv4.MinHeader)
            {
                throw new ArgumentOutOfRangeException(nameof(mtu), mtu, "a link carries at least an IPv4 header");
            }

            if (address == 0)
            {
                address = FreePoolAddress();
            }
            else if (!IsHostAddress(address) || address == Settings.RouterAddress || IsUpstreamAddress(address))
            {
                throw new ArgumentException(Ipv4.Format(address) + " is not a free host address in " + Ipv4.Format(Settings.Network) + "/" + Settings.PrefixLength, nameof(address));
            }
            else if (_links.ContainsKey(address))
            {
                throw new ArgumentException(Ipv4.Format(address) + " is already given to another link", nameof(address));
            }

            RouterLink link = new RouterLink(this, address, mtu, output);
            _links.Add(address, link);
            return link;
        }

        /// <summary>
        /// Removes a link; its address goes back to the pool.
        /// </summary>
        /// <param name="link">
        /// The link.
        /// </param>
        /// <returns>
        /// <c>true</c> when the link was on this router.
        /// </returns>
        public bool RemoveLink(RouterLink link)
        {
            if (link == null || !_links.TryGetValue(link.Address, out RouterLink? held) || held != link)
            {
                return false;
            }

            _links.Remove(link.Address);
            link.Detach();
            return true;
        }

        /// <summary>
        /// Sends a packet from the local services, such as a TNFS reply from RetroServer.
        /// </summary>
        /// <param name="packet">
        /// The packet; its source must be the router address.
        /// </param>
        public void SendFromLocal(ReadOnlySpan<byte> packet)
        {
            if (!Ipv4.TryCheckHeader(packet, out int total))
            {
                Drop(DropReason.BadHeader);
                return;
            }

            packet = packet.Slice(0, total);
            if (Ipv4.Source(packet) != Settings.RouterAddress)
            {
                Drop(DropReason.Spoofed);
                return;
            }

            // The router is the sender here, not a router on the way, so the TTL is left as it is.
            Route(packet, PacketSource.Local);
        }

        /// <summary>
        /// Takes a packet from the upstream side, such as a reply the NAT received from the internet.
        /// </summary>
        /// <remarks>
        /// The source must be outside the subnet, or the packet is dropped as
        /// <see cref="DropReason.Spoofed"/>. The packet is forwarded, so its TTL is lowered by one. A
        /// packet for an address outside the subnet is dropped as <see cref="DropReason.NoRoute"/>:
        /// the router never sends a packet back the way it came. A broadcast from upstream is dropped
        /// the same way (RFC 1812 section 5.3.5.2: directed broadcasts are not forwarded).
        /// </remarks>
        /// <param name="packet">
        /// The packet.
        /// </param>
        public void SendFromUpstream(ReadOnlySpan<byte> packet)
        {
            if (!Ipv4.TryCheckHeader(packet, out int total))
            {
                Drop(DropReason.BadHeader);
                return;
            }

            packet = packet.Slice(0, total);
            uint source = Ipv4.Source(packet);
            if ((source & _mask) == Settings.Network && !IsUpstreamAddress(source))
            {
                Drop(DropReason.Spoofed);
                return;
            }

            uint destination = Ipv4.Destination(packet);
            if ((destination & _mask) != Settings.Network || destination == _broadcast || IsUpstreamAddress(destination))
            {
                Drop(DropReason.NoRoute);
                return;
            }

            Route(packet, PacketSource.Upstream);
        }

        // A packet from one of the links.
        internal void FromLink(RouterLink link, ReadOnlySpan<byte> packet)
        {
            if (!Ipv4.TryCheckHeader(packet, out int total))
            {
                Drop(DropReason.BadHeader);
                return;
            }

            packet = packet.Slice(0, total);
            if (Ipv4.Source(packet) != link.Address)
            {
                Drop(DropReason.Spoofed);
                return;
            }

            link.CountIn(packet.Length);
            Route(packet, PacketSource.Link);
        }

        private void Route(ReadOnlySpan<byte> packet, PacketSource source)
        {
            bool forwarding = source != PacketSource.Local;
            uint destination = Ipv4.Destination(packet);
            if (destination == Settings.RouterAddress || destination == _broadcast || destination == Ipv4.LimitedBroadcast)
            {
                // Delivered to the router itself, not forwarded: the TTL stays.
                if (source == PacketSource.Local)
                {
                    // The local services talking to themselves: there is no route back out.
                    Drop(DropReason.NoRoute);
                    return;
                }

                OnLocal?.Invoke(packet);
                return;
            }

            bool inSubnet = (destination & _mask) == Settings.Network && !IsUpstreamAddress(destination);
            RouterLink? to = null;
            if (inSubnet && !_links.TryGetValue(destination, out to))
            {
                Drop(DropReason.NoRoute);
                return;
            }

            if (!inSubnet && (OnUpstream == null || source == PacketSource.Upstream))
            {
                Drop(DropReason.NoRoute);
                return;
            }

            if (to != null && packet.Length > to.Mtu)
            {
                Drop(DropReason.TooBig);
                return;
            }

            ReadOnlySpan<byte> outgoing = packet;
            if (forwarding)
            {
                if (packet[8] <= 1)
                {
                    Drop(DropReason.TtlExpired);
                    return;
                }

                if (_scratch.Length < packet.Length)
                {
                    _scratch = new byte[packet.Length];
                }

                Span<byte> copy = _scratch.AsSpan(0, packet.Length);
                packet.CopyTo(copy);
                Ipv4.DecrementTtl(copy);
                outgoing = copy;
            }

            if (to != null)
            {
                to.Deliver(outgoing);
            }
            else
            {
                OnUpstream!.Invoke(outgoing);
            }
        }

        private uint FreePoolAddress()
        {
            for (uint a = _poolFirst; a <= _poolLast; a++)
            {
                if (a != Settings.RouterAddress && !IsUpstreamAddress(a) && !_links.ContainsKey(a))
                {
                    return a;
                }
            }

            throw new InvalidOperationException("no free address left in the pool " + Ipv4.Format(_poolFirst) + " to " + Ipv4.Format(_poolLast));
        }

        private bool IsUpstreamAddress(uint address)
        {
            for (int i = 0; i < _upstreamAddresses.Length; i++)
            {
                if (_upstreamAddresses[i] == address)
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsHostAddress(uint address)
        {
            return (address & _mask) == Settings.Network && address != Settings.Network && address != _broadcast;
        }

        private void Drop(DropReason reason)
        {
            long count = ++_drops[(int)reason];
            OnDrop?.Invoke(reason, count);
        }
    }

    // Where a packet came into the router from.
    internal enum PacketSource
    {
        Local,
        Link,
        Upstream,
    }

    /// <summary>
    /// Why the router dropped a packet.
    /// </summary>
    public enum DropReason
    {
        /// <summary>
        /// Not an IPv4 packet, or its header length, total length or checksum is wrong (RFC 1812
        /// section 5.2.2).
        /// </summary>
        BadHeader,

        /// <summary>
        /// The source address is not the sender's own: a link's address for a link, the router
        /// address for the local services, an address outside the subnet for the upstream side.
        /// </summary>
        Spoofed,

        /// <summary>
        /// The TTL would reach 0 here (RFC 1812 section 5.3.1).
        /// </summary>
        TtlExpired,

        /// <summary>
        /// No link has the destination address, or it is outside the subnet and nothing is upstream.
        /// </summary>
        NoRoute,

        /// <summary>
        /// The packet is larger than the link it is for carries; this router does not fragment.
        /// </summary>
        TooBig,
    }
}
