//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace RetroIO.Network.Nat
{
    /// <summary>
    /// The protocol a port forward carries.
    /// </summary>
    public enum NatForwardProtocol
    {
        /// <summary>
        /// TCP: each host connection becomes one connection to the guest.
        /// </summary>
        Tcp,

        /// <summary>
        /// UDP: each host sender gets its own port on the gateway toward the guest.
        /// </summary>
        Udp,
    }

    /// <summary>
    /// One port forward rule of the built-in NAT (host networking design, section 7.7): the
    /// host listens on a port (or a range of ports) and every connection or datagram that
    /// arrives there goes on to a port of the guest.
    /// </summary>
    /// <remarks>
    /// <para>The ways to write a rule, the protocol first, the parts split by <c>:</c>:</para>
    ///  - <c>tcp:2323:23</c>: host port 2323 on every host address to guest port 23.
    ///  - <c>tcp:127.0.0.1:2323:23</c>: the same, this computer only.
    ///  - <c>tcp:8080:10.0.2.16:80</c>: to a named guest address.
    ///  - <c>tcp:127.0.0.1:8080:10.0.2.16:80</c>: both named.
    ///  - <c>udp:5353:53</c>: UDP.
    ///  - <c>tcp:6000-6009:6000</c>: a range, host 6000..6009 to guest 6000..6009.
    /// <para>The listen address left out means every host address (the owner's decision of
    /// 2026-11-12). The guest address left out means the guest the NAT knows: the address it
    /// gave by DHCP or saw in the guest's own frames.</para>
    /// </remarks>
    public sealed class NatPortForward
    {
        // ── Constants ──

        /// <summary>
        /// The most ports one range rule may name.
        /// </summary>
        public const int MaxRangePorts = 256;

        // ── Fields ──

        /// <summary>
        /// The listen address used when a rule names none: every host address.
        /// </summary>
        public static readonly IPAddress DefaultListenAddress = IPAddress.Any;

        // ── Constructors ──

        /// <summary>
        /// Initializes a rule.
        /// </summary>
        /// <param name="protocol">
        /// TCP or UDP.
        /// </param>
        /// <param name="listenAddress">
        /// The host address to listen on (IPv4).
        /// </param>
        /// <param name="hostPortFirst">
        /// The first host port.
        /// </param>
        /// <param name="hostPortLast">
        /// The last host port; the same as the first for one port.
        /// </param>
        /// <param name="guestAddress">
        /// The guest address, or 0 for the guest the NAT knows.
        /// </param>
        /// <param name="guestPortFirst">
        /// The guest port that the first host port goes to.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown for ports out of range or a listen address that is not IPv4.
        /// </exception>
        public NatPortForward(NatForwardProtocol protocol, IPAddress listenAddress, int hostPortFirst, int hostPortLast, uint guestAddress, int guestPortFirst)
        {
            if (listenAddress == null || listenAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ArgumentException("The listen address must be an IPv4 address.", nameof(listenAddress));
            }
            if (hostPortFirst < 1 || hostPortFirst > 65535 || hostPortLast < hostPortFirst || hostPortLast > 65535)
            {
                throw new ArgumentException("Host ports are 1 to 65535, the first not after the last.", nameof(hostPortFirst));
            }
            if (hostPortLast - hostPortFirst + 1 > MaxRangePorts)
            {
                throw new ArgumentException("A range names at most " + MaxRangePorts + " ports.", nameof(hostPortLast));
            }
            if (guestPortFirst < 1 || guestPortFirst + (hostPortLast - hostPortFirst) > 65535)
            {
                throw new ArgumentException("Guest ports are 1 to 65535.", nameof(guestPortFirst));
            }
            Protocol = protocol;
            ListenAddress = listenAddress;
            HostPortFirst = hostPortFirst;
            HostPortLast = hostPortLast;
            GuestAddress = guestAddress;
            GuestPortFirst = guestPortFirst;
        }

        // ── Properties ──

        /// <summary>
        /// Gets the protocol.
        /// </summary>
        public NatForwardProtocol Protocol { get; }

        /// <summary>
        /// Gets the host address the rule listens on.
        /// </summary>
        public IPAddress ListenAddress { get; }

        /// <summary>
        /// Gets the first host port.
        /// </summary>
        public int HostPortFirst { get; }

        /// <summary>
        /// Gets the last host port.
        /// </summary>
        public int HostPortLast { get; }

        /// <summary>
        /// Gets the number of ports the rule covers.
        /// </summary>
        public int PortCount => HostPortLast - HostPortFirst + 1;

        /// <summary>
        /// Gets the guest address, or 0 for the guest the NAT knows.
        /// </summary>
        public uint GuestAddress { get; }

        /// <summary>
        /// Gets the guest port the first host port goes to.
        /// </summary>
        public int GuestPortFirst { get; }

        // ── Public methods ──

        /// <summary>
        /// Reads a rule in one of the ways listed on the class. Spaces may stand for the
        /// <c>:</c> separators, so <c>tcp 2323 23</c> reads as <c>tcp:2323:23</c>.
        /// </summary>
        /// <param name="text">
        /// The rule.
        /// </param>
        /// <param name="rule">
        /// The rule read, or <c>null</c>.
        /// </param>
        /// <param name="error">
        /// Why it could not be read, or empty.
        /// </param>
        /// <returns>
        /// <c>true</c> when the rule was read.
        /// </returns>
        public static bool TryParse(string? text, out NatPortForward? rule, out string error)
        {
            rule = null;
            error = string.Empty;
            string s = (text ?? string.Empty).Trim();
            while (s.Contains("  ", StringComparison.Ordinal))
            {
                s = s.Replace("  ", " ", StringComparison.Ordinal);
            }
            s = s.Replace(' ', ':');
            string[] parts = s.Split(':');
            if (parts.Length < 3 || parts.Length > 5)
            {
                error = "'" + text + "' is not a forward rule. Use PROTOCOL:[LISTEN:]HOSTPORT[-LAST]:[GUEST:]GUESTPORT, for example tcp:2323:23.";
                return false;
            }
            NatForwardProtocol protocol;
            string p = parts[0].ToLowerInvariant();
            if (p == "tcp")
            {
                protocol = NatForwardProtocol.Tcp;
            }
            else if (p == "udp")
            {
                protocol = NatForwardProtocol.Udp;
            }
            else
            {
                error = "Unknown protocol '" + parts[0] + "'. Use tcp or udp.";
                return false;
            }

            IPAddress listen = DefaultListenAddress;
            string hostPorts;
            uint guest = 0;
            string guestPort;
            if (parts.Length == 3)
            {
                hostPorts = parts[1];
                guestPort = parts[2];
            }
            else if (parts.Length == 4)
            {
                if (TryReadAddress(parts[1], out IPAddress? a))
                {
                    listen = a!;
                    hostPorts = parts[2];
                    guestPort = parts[3];
                }
                else if (TryReadAddress(parts[2], out IPAddress? g))
                {
                    hostPorts = parts[1];
                    guest = NatPacket.ReadAddress(g!.GetAddressBytes());
                    guestPort = parts[3];
                }
                else
                {
                    error = "'" + text + "': with four parts, the second (listen) or the third (guest) must be an IPv4 address.";
                    return false;
                }
            }
            else
            {
                if (!TryReadAddress(parts[1], out IPAddress? a) || !TryReadAddress(parts[3], out IPAddress? g))
                {
                    error = "'" + text + "': with five parts, the second (listen) and the fourth (guest) must be IPv4 addresses.";
                    return false;
                }
                listen = a!;
                hostPorts = parts[2];
                guest = NatPacket.ReadAddress(g!.GetAddressBytes());
                guestPort = parts[4];
            }

            int first;
            int last;
            int dash = hostPorts.IndexOf('-');
            if (dash < 0)
            {
                if (!TryReadPort(hostPorts, out first))
                {
                    error = "Host port '" + hostPorts + "' is not 1 to 65535.";
                    return false;
                }
                last = first;
            }
            else if (!TryReadPort(hostPorts.Substring(0, dash), out first) || !TryReadPort(hostPorts.Substring(dash + 1), out last) || last < first)
            {
                error = "Host port range '" + hostPorts + "' must be FIRST-LAST, both 1 to 65535.";
                return false;
            }
            if (!TryReadPort(guestPort, out int guestFirst))
            {
                error = "Guest port '" + guestPort + "' is not 1 to 65535.";
                return false;
            }
            try
            {
                rule = new NatPortForward(protocol, listen, first, last, guest, guestFirst);
            }
            catch (ArgumentException e)
            {
                error = "'" + text + "': " + e.Message;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Tells whether another rule is the same rule (same protocol, addresses and ports).
        /// </summary>
        /// <param name="other">
        /// The other rule.
        /// </param>
        /// <returns>
        /// <c>true</c> when they are the same.
        /// </returns>
        public bool SameAs(NatPortForward? other)
        {
            return other != null && other.Protocol == Protocol && other.ListenAddress.Equals(ListenAddress)
                && other.HostPortFirst == HostPortFirst && other.HostPortLast == HostPortLast
                && other.GuestAddress == GuestAddress && other.GuestPortFirst == GuestPortFirst;
        }

        /// <summary>
        /// Tells whether two rules want the same host port on overlapping listen addresses.
        /// </summary>
        /// <param name="other">
        /// The other rule.
        /// </param>
        /// <returns>
        /// <c>true</c> when they cannot both listen.
        /// </returns>
        public bool Overlaps(NatPortForward other)
        {
            if (other == null || other.Protocol != Protocol)
            {
                return false;
            }
            bool addresses = other.ListenAddress.Equals(ListenAddress) || IPAddress.Any.Equals(ListenAddress) || IPAddress.Any.Equals(other.ListenAddress);
            return addresses && other.HostPortFirst <= HostPortLast && HostPortFirst <= other.HostPortLast;
        }

        /// <summary>
        /// Writes the rule in its full form, which <see cref="TryParse"/> reads back to the same rule:
        /// protocol, listen address, host port or range, guest address when one is named, guest port.
        /// </summary>
        /// <returns>
        /// The rule as text, for example <c>tcp:0.0.0.0:2323:23</c>.
        /// </returns>
        public override string ToString()
        {
            string ports = HostPortFirst == HostPortLast
                ? HostPortFirst.ToString(CultureInfo.InvariantCulture)
                : HostPortFirst.ToString(CultureInfo.InvariantCulture) + "-" + HostPortLast.ToString(CultureInfo.InvariantCulture);
            string guest = GuestAddress == 0 ? string.Empty : NatPacket.Dotted(GuestAddress) + ":";
            return (Protocol == NatForwardProtocol.Tcp ? "tcp" : "udp") + ":" + ListenAddress + ":" + ports + ":" + guest
                + GuestPortFirst.ToString(CultureInfo.InvariantCulture);
        }

        // ── Private methods ──

        private static bool TryReadAddress(string text, out IPAddress? address)
        {
            address = null;
            if (text.IndexOf('.') < 0 || !IPAddress.TryParse(text, out IPAddress? a) || a.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }
            address = a;
            return true;
        }

        private static bool TryReadPort(string text, out int port)
        {
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port >= 1 && port <= 65535;
        }
    }
}
