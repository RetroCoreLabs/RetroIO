//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet.Fsop;

/// <summary>
/// <c>GetRandomAccessInfo</c> (0x0C) function values. PiFS (fs.c, fs_get_random_access_info) answers
/// function 0 with the cursor, 1 and 2 with the file length, and sends the three data bytes unset for
/// any other value.
/// </summary>
[TestFixture]
[Category("Unit")]
internal sealed class RandomAccessInfoFunctionTests : FsopProtocolTestBase
{
    private byte OpenSeededFile()
    {
        Harness.FileSystem.AddFile(EconetSampleCatalogFixture.UserDirectory + "/DATA", new byte[100]);
        byte[] open = [1, 0, (byte)'D', (byte)'A', (byte)'T', (byte)'A', 0x0D];
        EconetMessage reply = Harness.Send(FileServerOpcodes.Open, open);
        Assert.That(StatusOf(reply), Is.EqualTo(0x00), "open failed");
        return reply.Payload[2];
    }

    [TestCase((byte)1)]
    [TestCase((byte)2)]
    public void Function1And2_ReturnFileLength(byte function)
    {
        LoginAdmin();
        byte handle = OpenSeededFile();

        EconetMessage reply = Harness.Send(FileServerOpcodes.GetRandomAccessInfo, [handle, function]);

        Assert.That(reply.Payload[2], Is.EqualTo(100));
    }

    [TestCase((byte)3)]
    [TestCase((byte)0xFF)]
    public void OtherFunctions_DoNotReturnFileLength(byte function)
    {
        LoginAdmin();
        byte handle = OpenSeededFile();

        EconetMessage reply = Harness.Send(FileServerOpcodes.GetRandomAccessInfo, [handle, function]);

        Assert.That(StatusOf(reply), Is.EqualTo(0x00));
        Assert.That(reply.Payload[2], Is.EqualTo(0));
        Assert.That(reply.Payload[3], Is.EqualTo(0));
        Assert.That(reply.Payload[4], Is.EqualTo(0));
    }
}
