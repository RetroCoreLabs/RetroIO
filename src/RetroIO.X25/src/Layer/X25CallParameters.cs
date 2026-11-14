//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.PacketLayer
{
    /// <summary>
    /// The flow control values one virtual call runs with, in the terms of the end that holds the circuit: "out" is the
    /// direction this end sends in, "in" the direction it receives in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// X.25 6.9 and 6.12 negotiate the window and packet size of a call end to end. A switch relays a call between two
    /// circuits, one on each side, so it has to carry these values from the circuit the call came in on to the circuit it
    /// goes out on (and the answer back), swapping the directions: what the caller sends is what the callee receives.
    /// <see cref="Swapped"/> does that.
    /// </para>
    /// </remarks>
    public readonly struct X25CallParameters
    {
        /// <summary>
        /// Creates a set of values.
        /// </summary>
        /// <param name="windowOut">
        /// The window for packets this end sends.
        /// </param>
        /// <param name="windowIn">
        /// The window for packets this end receives.
        /// </param>
        /// <param name="packetSizeOut">
        /// The largest user data field, in octets, of a packet this end sends.
        /// </param>
        /// <param name="packetSizeIn">
        /// The largest user data field, in octets, of a packet this end receives.
        /// </param>
        public X25CallParameters(byte windowOut, byte windowIn, int packetSizeOut, int packetSizeIn)
        {
            WindowSizeOut = windowOut;
            WindowSizeIn = windowIn;
            PacketSizeOut = packetSizeOut;
            PacketSizeIn = packetSizeIn;
        }

        /// <summary>
        /// Gets the window for packets this end sends.
        /// </summary>
        public byte WindowSizeOut { get; }

        /// <summary>
        /// Gets the window for packets this end receives.
        /// </summary>
        public byte WindowSizeIn { get; }

        /// <summary>
        /// Gets the largest user data field, in octets, of a packet this end sends.
        /// </summary>
        public int PacketSizeOut { get; }

        /// <summary>
        /// Gets the largest user data field, in octets, of a packet this end receives.
        /// </summary>
        public int PacketSizeIn { get; }

        /// <summary>
        /// Returns the same values as seen from the other end of the call: what this end sends is what the other receives.
        /// </summary>
        /// <returns>
        /// The values with the two directions exchanged.
        /// </returns>
        public X25CallParameters Swapped()
        {
            return new X25CallParameters(WindowSizeIn, WindowSizeOut, PacketSizeIn, PacketSizeOut);
        }

        /// <summary>
        /// Returns the values with each packet size limited to the given largest size.
        /// </summary>
        /// <param name="largestPacketSize">
        /// The largest packet size the line can carry.
        /// </param>
        /// <returns>
        /// The values with neither packet size above <paramref name="largestPacketSize"/>.
        /// </returns>
        public X25CallParameters WithPacketSizeAtMost(int largestPacketSize)
        {
            int packetOut = PacketSizeOut < largestPacketSize ? PacketSizeOut : largestPacketSize;
            int packetIn = PacketSizeIn < largestPacketSize ? PacketSizeIn : largestPacketSize;
            return new X25CallParameters(WindowSizeOut, WindowSizeIn, packetOut, packetIn);
        }
    }
}
