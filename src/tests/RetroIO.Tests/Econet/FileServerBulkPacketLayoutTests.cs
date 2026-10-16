//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

[TestFixture]
[Category("Unit")]
public sealed class FileServerBulkPacketLayoutTests
{
    [Test]
    public void TryParseSave_ReadsAckPortLoadExecLengthAndFilename()
    {
        byte[] raw =
        [
            0x50, 0x01, 0x2A, 0x00, 0x00,
            0x00, 0x28, 0x00, 0x00,
            0x00, 0x28, 0x00, 0x00,
            0x05, 0x00, 0x00,
            (byte)'M', (byte)'Y', (byte)'T', (byte)'E', (byte)'S', (byte)'T', 0x0D,
        ];

        Assert.That(
            FileServerBulkPacketLayout.TryParseSave(raw, out byte ackPort, out uint load, out uint exec, out int length, out string filename),
            Is.True);
        Assert.That(ackPort, Is.EqualTo(0x2A));
        Assert.That(load, Is.EqualTo(0x2800));
        Assert.That(exec, Is.EqualTo(0x2800));
        Assert.That(length, Is.EqualTo(5));
        Assert.That(filename, Is.EqualTo("MYTEST"));
    }

    [Test]
    public void TryParseLoad_ReadsDataPortRelativeToAndFilename()
    {
        byte[] raw =
        [
            0x50, 0x02, 0x2B, 0x03, 0x00,
            (byte)'H', (byte)'E', (byte)'L', (byte)'L', (byte)'O', 0x0D,
        ];

        Assert.That(
            FileServerBulkPacketLayout.TryParseLoad(raw, out byte dataPort, out byte relativeTo, out string filename),
            Is.True);
        Assert.That(dataPort, Is.EqualTo(0x2B));
        Assert.That(relativeTo, Is.EqualTo(0x03));
        Assert.That(filename, Is.EqualTo("HELLO"));
    }
}
