using System;

namespace RetroIO.Protocols.Xmsg.Ethernet
{
    /// <summary>
    /// Builds an <see cref="IEthernetBackend"/> from a spec string, so how a node reaches the
    /// segment is one configuration value rather than a code path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The spec strings deliberately match RetroCore's
    /// <c>Emulated.HW.Common.Network.EthernetBackendFactory</c>, so the same value configures an
    /// emulated ND machine and this library and the two are guaranteed to meet on the same
    /// segment.
    /// </para>
    /// <para><b>Supported specs</b></para>
    /// Forms and what they do:
    ///  - <c>udp</c>, <c>udp:port</c>, <c>udp:group:port</c> - a multicast
    ///    segment, default <c>239.3.9.4:3094</c>. Any number of nodes. NOT capturable when peers
    ///    share a host.
    ///  - <c>listen[:port]</c>, <c>tcp-listen[:port]</c> - wait for ONE peer.
    ///  - <c>tcp:host[:port]</c>, <c>host:port</c> - dial out to a
    ///    peer, or to a central relay that joins many nodes.
    ///  - <c>null</c>, <c>none</c> - a backend that carries nothing, for tests and for a node
    ///    configured without a segment.
    /// <para>
    /// <c>pcap:interface</c> - a real host adapter - is NOT implemented here. It needs the
    /// SharpPcap/npcap dependency, and is only useful for talking to physical hardware or to
    /// emulators bridged onto a real NIC. Use the UDP or TCP forms for emulated segments.
    /// </para>
    /// </remarks>
    public static class EthernetBackendFactory
    {
        /// <summary>
        /// Creates a backend for a spec.
        /// </summary>
        /// <param name="spec">
        /// The transport spec; see the remarks on <see cref="EthernetBackendFactory"/>.
        /// </param>
        /// <returns>
        /// The backend, or <c>null</c> when the spec is empty or unrecognised.
        /// </returns>
        public static IEthernetBackend? FromSpec(string? spec)
        {
            if (string.IsNullOrWhiteSpace(spec))
            {
                return null;
            }

            string s = spec.Trim();

            if (s.Equals("null", StringComparison.OrdinalIgnoreCase)
                || s.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return new NullEthernetBackend();
            }

            if (s.Equals("udp", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("udp:", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("udp-mcast:", StringComparison.OrdinalIgnoreCase))
            {
                return UdpEthernetBackend.FromSpec(s);
            }

            if (s.StartsWith("pcap:", StringComparison.OrdinalIgnoreCase))
            {
                // Deliberately unsupported rather than silently wrong: a caller asking for a real
                // adapter must not be quietly given a different transport.
                return null;
            }

            return TcpEthernetBackend.FromSpec(s);
        }
    }
}
