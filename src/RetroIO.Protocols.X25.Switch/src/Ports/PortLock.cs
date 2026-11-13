//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Switch.Ports
{
    /// <summary>
    /// The one lock every real switch port takes while it works on a packet.
    /// </summary>
    /// <remarks>
    /// A port decodes a packet under this lock and, still inside the call, hands it to the switch, which
    /// sends it on by calling the OTHER port, which takes the lock again. With a lock for each port, two
    /// ports that each receive a packet at the same moment would each hold their own lock and wait for the
    /// other's. With one lock shared by every port (XOT and HDLC alike) the thread that holds it takes it
    /// again on the way into the other port, and nobody waits in a circle. The full story is in the comment
    /// on <see cref="XotSwitchPort"/>.
    /// </remarks>
    internal static class PortLock
    {
        /// <summary>
        /// The shared lock object.
        /// </summary>
        internal static readonly object Shared = new object();
    }
}
