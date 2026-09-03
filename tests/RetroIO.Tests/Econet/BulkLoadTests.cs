//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Metadata;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Validates the LOAD (fsop 0x02) metadata reply against the PiFS reference (utilities/fs.c fs_load):
/// 16-byte reply [00 00, load(4 LE), exec(4 LE), length(3), access, day, monthyear] carrying the file's
/// real attributes so the BBC can place the image and (for CHAIN/*RUN) obtain the execution address.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class BulkLoadTests
{
    private const string ProgUnix = EconetSampleCatalogFixture.UserDirectory + "/PROG";
    private const uint LoadAddress = 0xFFFF1900u;
    private const uint ExecAddress = 0xFFFF8023u;
    private const byte Access = 0x33;

    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new EconetFsTestHarness();
        byte[] content = Encoding.ASCII.GetBytes("machine code image");
        _harness.FileSystem.AddFile(ProgUnix, content);
        AcornInfStore.WriteAttributes(_harness.FileSystem, ProgUnix, LoadAddress, ExecAddress, content.Length, Access);
    }

    [Test]
    public void Load_ExistingFile_ReturnsMetadataWithRealAttributes()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] metadata = LoadRequest("PROG");

        Assert.That(metadata, Has.Length.EqualTo(16));
        Assert.That(metadata[0], Is.EqualTo(0));
        Assert.That(metadata[1], Is.EqualTo(0));
        Assert.That(ReadUInt32Le(metadata, 2), Is.EqualTo(LoadAddress), "load address");
        Assert.That(ReadUInt32Le(metadata, 6), Is.EqualTo(ExecAddress), "exec address");

        int length = metadata[10] | (metadata[11] << 8) | (metadata[12] << 16);
        Assert.That(length, Is.EqualTo("machine code image".Length));
        Assert.That(metadata[13], Is.EqualTo(Access), "access byte");
    }

    [Test]
    public void Load_NonExistentFile_ReturnsNotFound()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] reply = LoadRequest("NOSUCH");

        Assert.That(reply[1], Is.EqualTo(FileServerErrors.NotFound));
    }

    [Test]
    public void Load_WithoutLogin_ReturnsWhoAreYou()
    {
        byte[] reply = LoadRequest("PROG");

        Assert.That(reply[1], Is.EqualTo(FileServerErrors.WhoAreYou));
    }

    // Build a LOAD request (fsop 0x02): [replyPort, opcode, dataPort, relativeTo, 0, name\r].
    private byte[] LoadRequest(string filename)
    {
        byte[] name = Encoding.ASCII.GetBytes(filename + "\r");
        byte[] payload = new byte[FileServerBulkPacketLayout.LoadFilenameOffset + name.Length];
        payload[0] = 0x80;
        payload[1] = FileServerOpcodes.Load;
        payload[2] = 0x40; // data port
        payload[3] = AcornFileHandles.WireCwd; // relative to CWD
        payload[4] = 0;
        name.CopyTo(payload, FileServerBulkPacketLayout.LoadFilenameOffset);

        EconetMessage request = EconetFsTestHarness.CreateMessage(payload);
        return _harness.Handler.Handle(request)!.Payload;
    }

    private static uint ReadUInt32Le(byte[] data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
}
