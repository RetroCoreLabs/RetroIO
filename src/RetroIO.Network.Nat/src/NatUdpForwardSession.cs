//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Net;
using System.Net.Sockets;

namespace RetroIO.Network.Nat
{
    /// <summary>
    /// One host sender on a UDP port forward: its datagrams reach the guest from the gateway
    /// address and a gateway port of its own, so the guest's answers to that port go back to
    /// that sender through the forward's host socket (design 7.7).
    /// </summary>
    /// <remarks>
    /// Only the NAT thread touches this object.
    /// </remarks>
    public sealed class NatUdpForwardSession
    {
        // ── Constructors ──

        /// <summary>
        /// Initializes a session.
        /// </summary>
        /// <param name="listener">
        /// The forward's host socket the sender wrote to.
        /// </param>
        /// <param name="remote">
        /// The host sender.
        /// </param>
        /// <param name="gatewayPort">
        /// The gateway port the guest sees the datagrams come from.
        /// </param>
        /// <param name="guestAddress">
        /// The guest address.
        /// </param>
        /// <param name="guestPort">
        /// The guest port.
        /// </param>
        /// <param name="nowMilliseconds">
        /// Host time now.
        /// </param>
        public NatUdpForwardSession(Socket listener, IPEndPoint remote, ushort gatewayPort, uint guestAddress, ushort guestPort, long nowMilliseconds)
        {
            Listener = listener;
            Remote = remote;
            GatewayPort = gatewayPort;
            GuestAddress = guestAddress;
            GuestPort = guestPort;
            LastUsedMilliseconds = nowMilliseconds;
        }

        // ── Properties ──

        /// <summary>
        /// Gets the forward's host socket.
        /// </summary>
        public Socket Listener { get; }

        /// <summary>
        /// Gets the host sender.
        /// </summary>
        public IPEndPoint Remote { get; }

        /// <summary>
        /// Gets the gateway port toward the guest.
        /// </summary>
        public ushort GatewayPort { get; }

        /// <summary>
        /// Gets the guest address.
        /// </summary>
        public uint GuestAddress { get; }

        /// <summary>
        /// Gets the guest port.
        /// </summary>
        public ushort GuestPort { get; }

        /// <summary>
        /// Gets or sets when the session was last used, host milliseconds.
        /// </summary>
        public long LastUsedMilliseconds { get; set; }
    }
}
