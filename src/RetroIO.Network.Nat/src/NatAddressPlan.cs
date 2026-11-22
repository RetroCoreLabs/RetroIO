//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Network.Nat
{
    /// <summary>
    /// The addresses of the built-in NAT's small network: the guest network, the gateway, the
    /// name server, the first address DHCP hands out, and the gateway's own MAC.
    /// </summary>
    /// <remarks>
    /// The defaults are the address plan of the host networking design
    /// (RetroCore's host networking design (host-networking-design.md in the RetroCore repository), section 7.2, decided by the
    /// owner 2026-11-12): 10.0.2.0/24, gateway 10.0.2.2, name server 10.0.2.3, first guest
    /// 10.0.2.15. The gateway MAC 02:52:43:00:02:02 is our own locally administered address
    /// (bit 1 of the first byte set), not copied from any other program.
    /// </remarks>
    public sealed class NatAddressPlan
    {
        // ── Fields ──

        private readonly byte[] _gatewayMac;

        // ── Constructors ──

        /// <summary>
        /// Initializes the default plan of the design, section 7.2.
        /// </summary>
        public NatAddressPlan()
            : this(0x0A000200, 0xFFFFFF00, 0x0A000202, 0x0A000203, 0x0A00020F, new byte[] { 0x02, 0x52, 0x43, 0x00, 0x02, 0x02 })
        {
        }

        /// <summary>
        /// Initializes a plan with chosen addresses.
        /// </summary>
        /// <param name="network">
        /// The network address, for example 0x0A000200 for 10.0.2.0.
        /// </param>
        /// <param name="netmask">
        /// The netmask, for example 0xFFFFFF00 for /24.
        /// </param>
        /// <param name="gateway">
        /// The gateway (router) address.
        /// </param>
        /// <param name="nameServer">
        /// The name server address.
        /// </param>
        /// <param name="firstGuest">
        /// The first address DHCP hands out.
        /// </param>
        /// <param name="gatewayMac">
        /// The gateway's MAC, six bytes.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when the MAC is not six bytes, or an address is outside the network.
        /// </exception>
        public NatAddressPlan(uint network, uint netmask, uint gateway, uint nameServer, uint firstGuest, byte[] gatewayMac)
        {
            if (gatewayMac == null || gatewayMac.Length != 6)
            {
                throw new ArgumentException("The gateway MAC must be six bytes.", nameof(gatewayMac));
            }
            if ((gateway & netmask) != network || (nameServer & netmask) != network || (firstGuest & netmask) != network)
            {
                throw new ArgumentException("The gateway, the name server and the first guest address must be inside the network.");
            }

            Network = network;
            Netmask = netmask;
            Gateway = gateway;
            NameServer = nameServer;
            FirstGuest = firstGuest;
            _gatewayMac = (byte[])gatewayMac.Clone();
        }

        // ── Properties ──

        /// <summary>
        /// Gets the network address.
        /// </summary>
        public uint Network { get; }

        /// <summary>
        /// Gets the netmask.
        /// </summary>
        public uint Netmask { get; }

        /// <summary>
        /// Gets the gateway (router) address; connections to it reach the host's 127.0.0.1.
        /// </summary>
        public uint Gateway { get; }

        /// <summary>
        /// Gets the name server address; queries to its port 53 go to the host's name server.
        /// </summary>
        public uint NameServer { get; }

        /// <summary>
        /// Gets the first address DHCP hands out.
        /// </summary>
        public uint FirstGuest { get; }

        /// <summary>
        /// Gets the subnet's broadcast address.
        /// </summary>
        public uint Broadcast => Network | ~Netmask;

        /// <summary>
        /// Gets the gateway's MAC (a copy is not made; do not change it).
        /// </summary>
        public ReadOnlySpan<byte> GatewayMac => _gatewayMac;

        // ── Public methods ──

        /// <summary>
        /// True when <paramref name="address"/> is inside the guest network.
        /// </summary>
        /// <param name="address">
        /// The address.
        /// </param>
        /// <returns>
        /// <c>true</c> when inside.
        /// </returns>
        public bool IsInside(uint address)
        {
            return (address & Netmask) == Network;
        }
    }
}
