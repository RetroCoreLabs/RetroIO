//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Network.Router
{
    /// <summary>
    /// The subnet, router address and address pool of an <see cref="IpRouter"/>.
    /// </summary>
    /// <remarks>
    /// Addresses are 32-bit numbers in host order (<see cref="Ipv4.Parse"/> reads dotted text). The
    /// defaults are the dial-up subnet Ronny chose on 2026-11-22: 10.42.0.0/24, with the router at
    /// 10.42.0.1, inside the private range 10.0.0.0/8 (RFC 1918).
    /// </remarks>
    public sealed class RouterSettings
    {
        /// <summary>
        /// Gets the first address of the subnet. Default 10.42.0.0.
        /// </summary>
        public uint Network { get; init; } = 0x0A2A0000;

        /// <summary>
        /// Gets the number of leading bits that name the subnet, from 8 to 30. Default 24.
        /// </summary>
        public int PrefixLength { get; init; } = 24;

        /// <summary>
        /// Gets the router's own address, where the local services are. Default 10.42.0.1.
        /// </summary>
        public uint RouterAddress { get; init; } = 0x0A2A0001;

        /// <summary>
        /// Gets the first address given to links, or 0 for the first host address of the subnet.
        /// </summary>
        public uint PoolFirst { get; init; }

        /// <summary>
        /// Gets the last address given to links, or 0 for the last host address of the subnet.
        /// </summary>
        public uint PoolLast { get; init; }

        /// <summary>
        /// Gets addresses inside the subnet that belong to the upstream side, such as the NAT's
        /// gateway and name server. Empty by default.
        /// </summary>
        /// <remarks>
        /// RetroIO.Network.Nat reaches the host's own 127.0.0.1 through its gateway address and the
        /// host's name server through its name server address, and both are inside the NAT's
        /// subnet. The router sends packets for these addresses upstream, takes packets from
        /// upstream that come from them, and never gives them to a link. Each must be a host
        /// address of the subnet other than the router's own.
        /// </remarks>
        public uint[] UpstreamAddresses { get; init; } = System.Array.Empty<uint>();
    }
}
