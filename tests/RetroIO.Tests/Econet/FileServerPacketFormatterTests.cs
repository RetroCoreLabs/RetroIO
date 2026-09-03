//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

[TestFixture]
[Category("Unit")]
public sealed class FileServerPacketFormatterTests
{
    [Test]
    public void FormatRequestDump_IncludesHeaderParamsAndRawHex()
    {
        byte[] payload = [0x50, 0x99, 0x01, 0x02, 0x03, 0x41, 0x42];
        FileServerRequest.TryParse(payload, out FileServerRequest request);

        string dump = FileServerPacketFormatter.FormatRequestDump(request);

        Assert.That(dump, Does.Contain("reply=&50"));
        Assert.That(dump, Does.Contain("fsop=&99"));
        Assert.That(dump, Does.Contain("URD=&01"));
        Assert.That(dump, Does.Contain("params 41 42"));
        Assert.That(dump, Does.Contain("raw 50 99 01 02 03 41 42"));
    }
}
