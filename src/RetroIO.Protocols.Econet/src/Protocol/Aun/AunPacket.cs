//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Protocols.Econet.Protocol.Aun;

/// <summary>
/// AUN (Acorn Universal Networking) wire format: 8-byte header followed by payload.
/// Layout: type, port, control, pad, sequence (uint32 LE), data[].
/// </summary>
public static class AunPacket
{
    public const int HeaderSize = 8;

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
