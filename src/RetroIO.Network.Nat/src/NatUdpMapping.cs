//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Net.Sockets;

namespace RetroIO.Network.Nat
{
    /// <summary>
    /// One UDP mapping of the NAT: a host UDP socket for one (guest address, guest port), as
    /// the host networking design asks (section 7.3, UDP row).
    /// </summary>
    /// <remarks>
    /// Only the NAT thread touches this object. An idle mapping lives at least two minutes of
    /// host time (RFC 4787 section 4.3, REQ-5: "A NAT UDP mapping timer MUST NOT expire in less
    /// than two minutes").
    /// </remarks>
    public sealed class NatUdpMapping
    {
        // ── Constructors ──

        /// <summary>
        /// Initializes a mapping.
        /// </summary>
        /// <param name="guestMac">
        /// The guest's MAC.
        /// </param>
        /// <param name="guestAddress">
        /// The guest's address.
        /// </param>
        /// <param name="guestPort">
        /// The guest's port.
        /// </param>
        /// <param name="host">
        /// The host socket, bound and non-blocking.
        /// </param>
        /// <param name="nowMilliseconds">
        /// Host time now, from <see cref="Environment.TickCount64"/>.
        /// </param>
        public NatUdpMapping(ReadOnlySpan<byte> guestMac, uint guestAddress, ushort guestPort, Socket host, long nowMilliseconds)
        {
            GuestMac = guestMac.Slice(0, 6).ToArray();
            GuestAddress = guestAddress;
            GuestPort = guestPort;
            Host = host;
            LastUsedMilliseconds = nowMilliseconds;
        }

        // ── Properties ──

        /// <summary>
        /// Gets the guest's MAC.
        /// </summary>
        public byte[] GuestMac { get; }

        /// <summary>
        /// Gets the guest's address.
        /// </summary>
        public uint GuestAddress { get; }

        /// <summary>
        /// Gets the guest's port.
        /// </summary>
        public ushort GuestPort { get; }

        /// <summary>
        /// Gets the host socket.
        /// </summary>
        public Socket Host { get; }

        /// <summary>
        /// Gets or sets the host time of the last datagram either way.
        /// </summary>
        public long LastUsedMilliseconds { get; set; }

        /// <summary>
        /// Gets or sets the last remote address the guest sent to (as the guest sees it).
        /// </summary>
        public uint LastRemoteAddress { get; set; }

        /// <summary>
        /// Gets or sets the last remote port the guest sent to.
        /// </summary>
        public ushort LastRemotePort { get; set; }
    }
}
