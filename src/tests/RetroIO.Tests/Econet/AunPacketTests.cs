//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.Aun;

namespace RetroIO.Tests.Econet;

[TestFixture]
[Category("Unit")]
public sealed class AunPacketTests
{
    [Test]
    public void EncodeDecode_RoundTripsPayload()
    {
        byte[] payload = Encoding.ASCII.GetBytes("FS");
        byte[] buffer = new byte[AunPacket.HeaderSize + payload.Length];

        int written = AunPacket.Encode(
            buffer,
            AunPacketType.Unicast,
            0x99,
            0x80,
            0x12345678,
            payload);

        Assert.That(written, Is.EqualTo(buffer.Length));

        bool decoded = AunPacket.TryDecode(
            buffer,
            out AunPacketType type,
            out byte port,
            out byte control,
            out uint sequence,
            out int payloadOffset);

        Assert.That(decoded, Is.True);
        Assert.That(type, Is.EqualTo(AunPacketType.Unicast));
        Assert.That(port, Is.EqualTo(0x99));
        Assert.That(control, Is.EqualTo(0x80));
        Assert.That(sequence, Is.EqualTo(0x12345678u));
        Assert.That(payloadOffset, Is.EqualTo(AunPacket.HeaderSize));
        Assert.That(buffer.AsSpan(payloadOffset).ToArray(), Is.EqualTo(payload));
    }

    [Test]
    public void TryDecode_RejectsShortBuffer()
    {
        bool decoded = AunPacket.TryDecode(
            [0x02, 0x99],
            out AunPacketType type,
            out byte port,
            out byte control,
            out uint sequence,
            out int payloadOffset);

        Assert.That(decoded, Is.False);
        Assert.That(payloadOffset, Is.EqualTo(0));
    }
}
