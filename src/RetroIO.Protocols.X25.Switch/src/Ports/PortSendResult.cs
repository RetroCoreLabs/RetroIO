//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Switch.Ports
{
    /// <summary>
    /// What happened to a packet handed to a port.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three cases need three different answers from the switch, and a boolean cannot tell two
    /// of them apart. A shut window is a pause: hold the packet and send it when the window opens.
    /// A dead circuit is not: holding the packet only delays the clear, and every packet after it
    /// piles up behind one that will never leave.
    /// </para>
    /// <para>
    /// Until Phase E1 this was a <c>bool</c> that no caller read. A refused packet was dropped in
    /// silence, counted as forwarded, and the far end saw a gap in P(S) and reset the circuit
    /// (X.25 4.4.1.3). The same shape lost packets in five places in the C switch.
    /// </para>
    /// </remarks>
    public enum PortSendResult
    {
        /// <summary>
        /// The packet was accepted and is on its way.
        /// </summary>
        Sent,

        /// <summary>
        /// Not now: the send window is closed or the peer has sent RNR (X.25 4.4.1, 4.4.2). The
        /// packet was not taken, and the port raises <see cref="PortReadyToSend"/> when it can
        /// take it. Nothing is wrong with the circuit.
        /// </summary>
        WindowFull,

        /// <summary>
        /// The packet cannot be sent at all: no such circuit, the circuit is not in data transfer,
        /// the data is longer than the negotiated packet size, or the port is down. Holding it
        /// would not help.
        /// </summary>
        Failed,
    }
}
