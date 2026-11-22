//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Network.Router
{
    /// <summary>
    /// One point-to-point link on an <see cref="IpRouter"/>, such as one dial-up call.
    /// </summary>
    /// <remarks>
    /// The counters count packets the router took from the link and gave to it; a packet the
    /// router dropped is counted by the router (<see cref="IpRouter.DropCount"/>), not here.
    /// </remarks>
    public sealed class RouterLink
    {
        private readonly PacketOutput _output;
        private IpRouter? _router;

        /// <summary>
        /// Called with a packet for the far end of the link.
        /// </summary>
        /// <param name="packet">
        /// The packet, valid only during the call.
        /// </param>
        public delegate void PacketOutput(ReadOnlySpan<byte> packet);

        internal RouterLink(IpRouter router, uint address, int mtu, PacketOutput output)
        {
            _router = router;
            Address = address;
            Mtu = mtu;
            _output = output;
        }

        /// <summary>
        /// Gets the link's address.
        /// </summary>
        public uint Address { get; }

        /// <summary>
        /// Gets the largest packet the link carries.
        /// </summary>
        public int Mtu { get; }

        /// <summary>
        /// Gets whether the link is still on its router.
        /// </summary>
        public bool IsAttached => _router != null;

        /// <summary>
        /// Gets how many packets the router has taken from the link.
        /// </summary>
        public long PacketsIn { get; private set; }

        /// <summary>
        /// Gets how many bytes the router has taken from the link.
        /// </summary>
        public long BytesIn { get; private set; }

        /// <summary>
        /// Gets how many packets the router has given to the link.
        /// </summary>
        public long PacketsOut { get; private set; }

        /// <summary>
        /// Gets how many bytes the router has given to the link.
        /// </summary>
        public long BytesOut { get; private set; }

        /// <summary>
        /// Takes a packet from the far end of the link, for example from <c>SlipLink.OnPacket</c>.
        /// </summary>
        /// <param name="packet">
        /// The IPv4 packet.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the link has been removed from its router.
        /// </exception>
        public void Receive(ReadOnlySpan<byte> packet)
        {
            IpRouter router = _router ?? throw new InvalidOperationException("the link " + Ipv4.Format(Address) + " was removed from its router");
            router.FromLink(this, packet);
        }

        internal void CountIn(int length)
        {
            PacketsIn++;
            BytesIn += length;
        }

        internal void Deliver(ReadOnlySpan<byte> packet)
        {
            PacketsOut++;
            BytesOut += packet.Length;
            _output(packet);
        }

        internal void Detach()
        {
            _router = null;
        }
    }
}
