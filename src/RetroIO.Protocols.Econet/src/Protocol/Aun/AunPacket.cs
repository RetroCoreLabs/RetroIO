//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.Protocol.Aun;

/// <summary>
/// AUN (Acorn Universal Networking) wire format: 8-byte header followed by payload.
/// Layout: type, port, control, pad, sequence (uint32 LE), data[].
/// </summary>
public static class AunPacket
{
    /// <summary>
    /// The size of the AUN header in bytes.
    /// </summary>
    public const int HeaderSize = 8;

    /// <summary>
    /// Writes an AUN header and payload into a buffer.
    /// </summary>
    /// <param name="destination">
    /// The buffer to write to.
    /// </param>
    /// <param name="type">
    /// The packet type.
    /// </param>
    /// <param name="port">
    /// The Econet port.
    /// </param>
    /// <param name="control">
    /// The control byte. Only the low 7 bits are written.
    /// </param>
    /// <param name="sequence">
    /// The sequence number, written as 4 bytes with the lowest byte first.
    /// </param>
    /// <param name="payload">
    /// The payload that follows the header.
    /// </param>
    /// <returns>
    /// The number of bytes written.
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// Thrown when <paramref name="destination"/> is too small for the header and payload.
    /// </exception>
    public static int Encode(
        Span<byte> destination,
        AunPacketType type,
        byte port,
        byte control,
        uint sequence,
        ReadOnlySpan<byte> payload)
    {
        int total = HeaderSize + payload.Length;
        if (destination.Length < total)
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));

        destination[0] = (byte)type;
        destination[1] = port;
        destination[2] = (byte)(control & 0x7F);
        destination[3] = 0;
        destination[4] = (byte)sequence;
        destination[5] = (byte)(sequence >> 8);
        destination[6] = (byte)(sequence >> 16);
        destination[7] = (byte)(sequence >> 24);
        payload.CopyTo(destination.Slice(HeaderSize));
        return total;
    }

    /// <summary>
    /// Reads the AUN header of a datagram.
    /// </summary>
    /// <param name="datagram">
    /// The received datagram.
    /// </param>
    /// <param name="type">
    /// Receives the packet type.
    /// </param>
    /// <param name="port">
    /// Receives the Econet port.
    /// </param>
    /// <param name="control">
    /// Receives the control byte with bit 7 set.
    /// </param>
    /// <param name="sequence">
    /// Receives the sequence number.
    /// </param>
    /// <param name="payloadOffset">
    /// Receives the offset of the payload, which is the header size.
    /// </param>
    /// <returns>
    /// True when the datagram holds at least a full header, otherwise false.
    /// </returns>
    public static bool TryDecode(
        ReadOnlySpan<byte> datagram,
        out AunPacketType type,
        out byte port,
        out byte control,
        out uint sequence,
        out int payloadOffset)
    {
        if (datagram.Length < HeaderSize)
        {
            type = default;
            port = 0;
            control = 0;
            sequence = 0;
            payloadOffset = 0;
            return false;
        }

        type = (AunPacketType)datagram[0];
        port = datagram[1];
        control = (byte)(datagram[2] | 0x80);
        sequence = (uint)(datagram[4] | (datagram[5] << 8) | (datagram[6] << 16) | (datagram[7] << 24));
        payloadOffset = HeaderSize;
        return true;
    }
}
