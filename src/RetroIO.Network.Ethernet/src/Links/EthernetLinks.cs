//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//


namespace RetroIO.Network.Ethernet
{
    /// <summary>
    /// The one call a host makes to offer every link between emulators: <c>tcp:HOST:PORT</c>,
    /// <c>listen:PORT</c>, <c>hub:PORT</c> and <c>udp:GROUP:PORT</c>.
    /// </summary>
    public static class EthernetLinks
    {
        // ── Public methods ──

        /// <summary>
        /// Adds the TCP link, TCP hub and UDP multicast spec readers to <see cref="EthernetBackends"/>.
        /// </summary>
        public static void RegisterFactories()
        {
            TcpLinkBackend.RegisterFactory();
            TcpHubBackend.RegisterFactory();
            UdpMulticastBackend.RegisterFactory();
            TapEthernetBackend.RegisterFactory();
        }
    }
}
