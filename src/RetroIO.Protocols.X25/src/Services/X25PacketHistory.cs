//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Services
{
    /// <summary>
    /// The runtime's packet history bus: anything that has just received or transmitted an X.25
    /// packet announces it here, and the history and display code listens. It carries no protocol
    /// state and sends nothing.
    /// </summary>
    /// <remarks>
    /// Until 2026-09-06 this role was played by the old <c>X25Layer</c> wrapper's
    /// <c>FirePacketReceived</c> / <c>FirePacketTransmitted</c>, which meant that services, the
    /// serial transport and the display all took a dependency on the old packet-layer engine just
    /// to log a packet. Splitting the bus out is Phase C2 stage 1 of the one-stack plan; the old
    /// engine's own events keep flowing into the same two handlers until it is gone.
    /// </remarks>
    public sealed class X25PacketHistory
    {
        /// <summary>
        /// Raised for every packet received, by whoever received it.
        /// </summary>
        public event EventHandler<X25PacketEventArgs>? PacketReceived;

        /// <summary>
        /// Raised for every packet transmitted, by whoever transmitted it.
        /// </summary>
        public event EventHandler<X25PacketEventArgs>? PacketTransmitted;

        /// <summary>
        /// Announces a received packet.
        /// </summary>
        public void FirePacketReceived(X25PacketEventArgs eventArgs)
        {
            PacketReceived?.Invoke(this, eventArgs);
        }

        /// <summary>
        /// Announces a transmitted packet.
        /// </summary>
        public void FirePacketTransmitted(X25PacketEventArgs eventArgs)
        {
            PacketTransmitted?.Invoke(this, eventArgs);
        }
    }
}
