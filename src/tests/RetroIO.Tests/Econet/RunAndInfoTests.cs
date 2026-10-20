//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Metadata;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Validates *RUN library search (fsop 0x05) and GetObjectInfo (fsop 0x12) command variants 2/4/5
/// against the PiFS reference (utilities/fs.c fs_load loadas + fs_get_object_info field ordering).
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class RunAndInfoTests
{
    private const string GamesDir = EconetSampleCatalogFixture.UserDirectory + "/GAMES";
    private const string InvadersUnix = GamesDir + "/INVADERS";
    private const string ProgUnix = EconetSampleCatalogFixture.UserDirectory + "/PROG";
    private const uint LoadAddress = 0xFFFF1900u;
    private const uint ExecAddress = 0xFFFF8023u;
    private const byte Access = 0x33;
    private static readonly byte[] ProgContent = Encoding.ASCII.GetBytes("object-info sample!!");

    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new EconetFsTestHarness();
        _harness.FileSystem.AddDirectory(GamesDir);
        _harness.FileSystem.AddFile(InvadersUnix, Encoding.ASCII.GetBytes("invaders code"));

        _harness.FileSystem.AddFile(ProgUnix, ProgContent);
        AcornAttributeStore.WriteAttributes(_harness.FileSystem, ProgUnix, LoadAddress, ExecAddress, ProgContent.Length, Access);
    }

    [Test]
    public void RunAs_FileInCurrentDirectory_ReturnsLoadMetadata()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] metadata = RunAsRequest("PROG");

        Assert.That(metadata, Has.Length.EqualTo(16));
        Assert.That(metadata[1], Is.EqualTo(0));
    }

    [Test]
    public void RunAs_FileOnlyInLibrary_IsFoundViaLibrarySearch()
    {
        _harness.HandleOsCli("I AM SYST \r");
        _harness.HandleOsCli("LIB GAMES\r");

        // INVADERS is not in the current directory (USER) but is in the library (GAMES).
        byte[] metadata = RunAsRequest("INVADERS");

        Assert.That(metadata, Has.Length.EqualTo(16), "found via library search");
        Assert.That(metadata[1], Is.EqualTo(0));
    }

    [Test]
    public void RunAs_FileNowhere_ReturnsNotFound()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] reply = RunAsRequest("GHOST");

        Assert.That(reply[1], Is.EqualTo(FileServerErrors.NotFound));
    }

    [Test]
    public void RunAs_TruncatesCommandLineAtFirstSpace()
    {
        _harness.HandleOsCli("I AM SYST \r");

        // "PROG ARG1 ARG2" must resolve to PROG (PiFS truncates the command line at the first space).
        byte[] metadata = RunAsRequest("PROG ARG1 ARG2");

        Assert.That(metadata, Has.Length.EqualTo(16));
        Assert.That(metadata[1], Is.EqualTo(0));
    }

    [Test]
    public void GetObjectInfo_Cmd5_ReturnsFullAttributeBlock()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] reply = _harness.HandleLoggedIn(
            FileServerOpcodes.GetObjectInfo,
            BuildRequest(5, "PROG")).Payload;

        // 2 (status) + 1 (ftype) + 8 (load/exec) + 3 (length) + 1 (access) + 2 (date) + 1 (owner) = 18
        Assert.That(reply, Has.Length.EqualTo(18));
        Assert.That(reply[2], Is.EqualTo(0x01), "file type");
        Assert.That(ReadUInt32Le(reply, 3), Is.EqualTo(LoadAddress));
        Assert.That(ReadUInt32Le(reply, 7), Is.EqualTo(ExecAddress));
        int length = reply[11] | (reply[12] << 8) | (reply[13] << 16);
        Assert.That(length, Is.EqualTo(ProgContent.Length));
        Assert.That(reply[14], Is.EqualTo(Access), "access byte");
        Assert.That(reply[17], Is.EqualTo(0x00), "owner byte (session owns the object)");
    }

    [Test]
    public void GetObjectInfo_Cmd2_ReturnsLoadAndExecOnly()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] reply = _harness.HandleLoggedIn(
            FileServerOpcodes.GetObjectInfo,
            BuildRequest(2, "PROG")).Payload;

        // 2 + 1 (ftype) + 8 (load/exec) = 11
        Assert.That(reply, Has.Length.EqualTo(11));
        Assert.That(reply[2], Is.EqualTo(0x01));
        Assert.That(ReadUInt32Le(reply, 3), Is.EqualTo(LoadAddress));
        Assert.That(ReadUInt32Le(reply, 7), Is.EqualTo(ExecAddress));
    }

    [Test]
    public void GetObjectInfo_Cmd4_ReturnsAccessAndOwner()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] reply = _harness.HandleLoggedIn(
            FileServerOpcodes.GetObjectInfo,
            BuildRequest(4, "PROG")).Payload;

        // 2 + 1 (ftype) + 1 (access) + 1 (owner) = 5
        Assert.That(reply, Has.Length.EqualTo(5));
        Assert.That(reply[2], Is.EqualTo(0x01));
        Assert.That(reply[3], Is.EqualTo(Access));
        Assert.That(reply[4], Is.EqualTo(0x00));
    }

    private byte[] RunAsRequest(string commandLine)
    {
        byte[] name = Encoding.ASCII.GetBytes(commandLine + "\r");
        byte[] payload = new byte[FileServerBulkPacketLayout.LoadFilenameOffset + name.Length];
        payload[0] = 0x80;
        payload[1] = FileServerOpcodes.LoadAs;
        payload[2] = 0x40; // data port
        payload[3] = AcornFileHandles.WireCwd; // relative to CWD
        payload[4] = 0;
        name.CopyTo(payload, FileServerBulkPacketLayout.LoadFilenameOffset);

        EconetMessage request = EconetFsTestHarness.CreateMessage(payload);
        return _harness.Handler.Handle(request)!.Payload;
    }

    private static byte[] BuildRequest(byte command, string path)
    {
        byte[] pathBytes = Encoding.ASCII.GetBytes(path + "\r");
        byte[] request = new byte[1 + pathBytes.Length];
        request[0] = command;
        pathBytes.CopyTo(request, 1);
        return request;
    }

    private static uint ReadUInt32Le(byte[] data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
}
