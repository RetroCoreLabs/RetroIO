//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Hdlc
{
    /// <summary>
    /// Identifies a single TCP flow direction by its 4-tuple (source and destination
    /// IPv4 address and port).
    /// </summary>
    /// <remarks>
    /// The key is directional: <c>A->B</c> and <c>B->A</c> are two distinct
    /// streams, mirroring how the reference validator reassembles each half of the
    /// TCP conversation independently.
    /// </remarks>
    public readonly struct StreamKey : IEquatable<StreamKey>
    {
        /// <summary>
        /// The source IPv4 address as a 32-bit big-endian value.
        /// </summary>
        public readonly uint SourceAddress;

        /// <summary>
        /// The source TCP port.
        /// </summary>
        public readonly ushort SourcePort;

        /// <summary>
        /// The destination IPv4 address as a 32-bit big-endian value.
        /// </summary>
        public readonly uint DestinationAddress;

        /// <summary>
        /// The destination TCP port.
        /// </summary>
        public readonly ushort DestinationPort;

        /// <summary>
        /// Initialises a new stream key from a TCP/IPv4 4-tuple.
        /// </summary>
        /// <param name="sourceAddress">
        /// The source IPv4 address as a 32-bit value.
        /// </param>
        /// <param name="sourcePort">
        /// The source TCP port.
        /// </param>
        /// <param name="destinationAddress">
        /// The destination IPv4 address as a 32-bit value.
        /// </param>
        /// <param name="destinationPort">
        /// The destination TCP port.
        /// </param>
        public StreamKey(uint sourceAddress, ushort sourcePort, uint destinationAddress, ushort destinationPort)
        {
            SourceAddress = sourceAddress;
            SourcePort = sourcePort;
            DestinationAddress = destinationAddress;
            DestinationPort = destinationPort;
        }

        /// <summary>
        /// Determines whether this key equals another key.
        /// </summary>
        /// <param name="other">
        /// The key to compare with.
        /// </param>
        /// <returns>
        /// <c>true</c> when all four tuple components match.
        /// </returns>
        public bool Equals(StreamKey other)
        {
            return SourceAddress == other.SourceAddress
                && SourcePort == other.SourcePort
                && DestinationAddress == other.DestinationAddress
                && DestinationPort == other.DestinationPort;
        }

        /// <summary>
        /// Determines whether this key equals another object.
        /// </summary>
        /// <param name="obj">
        /// The object to compare with.
        /// </param>
        /// <returns>
        /// <c>true</c> when <paramref name="obj"/> is a <see cref="StreamKey"/> with the
        /// same tuple.
        /// </returns>
        public override bool Equals(object? obj)
        {
            return obj is StreamKey other && Equals(other);
        }

        /// <summary>
        /// Computes a hash code over the 4-tuple.
        /// </summary>
        /// <returns>
        /// A hash code suitable for use as a dictionary key.
        /// </returns>
        public override int GetHashCode()
        {
            return HashCode.Combine(SourceAddress, SourcePort, DestinationAddress, DestinationPort);
        }

        /// <summary>
        /// Formats the key as <c>src:port -> dst:port</c> in dotted-quad notation.
        /// </summary>
        /// <returns>
        /// A human-readable representation of the flow.
        /// </returns>
        public override string ToString()
        {
            return FormatAddress(SourceAddress) + ":" + SourcePort
                + " -> " + FormatAddress(DestinationAddress) + ":" + DestinationPort;
        }

        /// <summary>
        /// Formats a 32-bit IPv4 address as dotted-quad text.
        /// </summary>
        /// <param name="address">
        /// The IPv4 address, most significant octet first.
        /// </param>
        /// <returns>
        /// The dotted-quad string, for example <c>10.0.0.1</c>.
        /// </returns>
        private static string FormatAddress(uint address)
        {
            int a = (int)((address >> 24) & 0xFF);
            int b = (int)((address >> 16) & 0xFF);
            int c = (int)((address >> 8) & 0xFF);
            int d = (int)(address & 0xFF);
            return a + "." + b + "." + c + "." + d;
        }
    }
}
