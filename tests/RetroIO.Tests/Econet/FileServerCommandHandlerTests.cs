//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.Econet.FileServer;
using RetroIO.Protocols.Econet.FileServer.Bulk;
using RetroIO.Protocols.Econet.FileServer.Configuration;
using RetroIO.Protocols.Econet.FileServer.Discs;
using RetroIO.Protocols.Econet.FileServer.Handles;
using RetroIO.Protocols.Econet.FileServer.Paths;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Protocols.Econet.Protocol;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Tests.TNFS;

namespace RetroIO.Tests.Econet;

[TestFixture]
[Category("Unit")]
public sealed class FileServerCommandHandlerTests
{
    private MockFileSystemBackend _fileSystem = null!;
    private FileServerCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _fileSystem = new MockFileSystemBackend();
        EconetSampleCatalogFixture.Seed(_fileSystem);

        EconetUserDirectory users = EconetTestUsers.CreateDirectory(out _);
        DiscCatalog discs = new DiscCatalog(_fileSystem, "/econet-root", "RetroFS");
        FileServerSessionManager sessions = new FileServerSessionManager(_fileSystem, users, discs, "/econet-root");
        FileServerHandleManager handles = new FileServerHandleManager();
        PathResolver paths = new PathResolver(_fileSystem, discs, "/econet-root");
        BulkTransferManager bulk = new BulkTransferManager();

        FileServerContext context = new FileServerContext(
            _fileSystem,
            users,
            discs,
            sessions,
            handles,
            paths,
            bulk,
            new FileServerConfiguration(),
            new FileServerClock(),
            "/econet-root",
            "RetroFS",
            FileServerCommandHandler.DefaultVersionString);

        _handler = new FileServerCommandHandler(context, NullLogger<FileServerCommandHandler>.Instance);
    }

    [Test]
    public void Login_WithDefaultSyst_Succeeds()
    {
        EconetMessage request = CreateRequest(FileServerOpcodes.OsCli, Encoding.ASCII.GetBytes("I AM SYST \r"));
        EconetMessage? response = _handler.Handle(request);

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Payload[0], Is.EqualTo(FileServerOpcodes.LoginSuccessReturnCode));
        Assert.That(response.Payload[1], Is.EqualTo(0));
    }

    [Test]
    public void Login_SecondIam_ReplacesStaleSessionAndSucceeds()
    {
        Login();

        EconetMessage request = CreateRequest(FileServerOpcodes.OsCli, Encoding.ASCII.GetBytes("I AM SYST \r"));
        EconetMessage? response = _handler.Handle(request);

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Payload[0], Is.EqualTo(FileServerOpcodes.LoginSuccessReturnCode));
        Assert.That(response.Payload[1], Is.EqualTo(0));
        Assert.That(response.Payload[1], Is.Not.EqualTo(FileServerErrors.BadCommand));
    }

    [Test]
    public void ReadVersion_ReturnsConfiguredVersionString()
    {
        EconetMessage request = CreateRequest(FileServerOpcodes.ReadVersion, []);
        EconetMessage? response = _handler.Handle(request);

        Assert.That(response, Is.Not.Null);
        Assert.That(
            Encoding.ASCII.GetString(response!.Payload, 2, response.Payload.Length - 3),
            Is.EqualTo(FileServerCommandHandler.DefaultVersionString));
    }

    [Test]
    public void ProtectedOpcode_WithoutLogin_ReturnsWhoAreYou()
    {
        EconetMessage request = CreateRequest(FileServerOpcodes.Examine, [0, 0, 0xFF]);
        EconetMessage? response = _handler.Handle(request);

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Payload[1], Is.EqualTo(FileServerErrors.WhoAreYou));
    }

    [Test]
    public void Examine_AfterLogin_ReturnsCatalogEntries()
    {
        Login();

        byte[] examineParams = new byte[4];
        examineParams[0] = 0;
        examineParams[1] = 0;
        examineParams[2] = 0xFF;
        examineParams[3] = (byte)'.';

        EconetMessage request = CreateLoggedInRequest(FileServerOpcodes.Examine, examineParams);
        EconetMessage? response = _handler.Handle(request);

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Payload[2], Is.GreaterThan(0));
        Assert.That(response.Payload[^1], Is.EqualTo(0x80));
    }

    [Test]
    public void Sdisc_AfterLogin_ChangesCurrentDisc()
    {
        Login();

        EconetMessage request = CreateRequest(FileServerOpcodes.OsCli, Encoding.ASCII.GetBytes("SDISC STORAGE\r"));
        EconetMessage? response = _handler.Handle(request);

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Payload[0], Is.EqualTo(0));
        Assert.That(response.Payload[1], Is.EqualTo(0));
    }

    [Test]
    public void GetObjectInfo_Command1_ReturnsShortReplyForFile()
    {
        Login();

        byte[] parameters = new byte[1 + 6];
        parameters[0] = 1;
        Encoding.ASCII.GetBytes("HELLO\r").CopyTo(parameters, 1);
        EconetMessage request = CreateLoggedInRequest(FileServerOpcodes.GetObjectInfo, parameters);
        EconetMessage? response = _handler.Handle(request);

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Payload, Has.Length.EqualTo(5));
        Assert.That(response.Payload[0], Is.EqualTo(0));
        Assert.That(response.Payload[1], Is.EqualTo(0));
        Assert.That(response.Payload[2], Is.EqualTo(0x01));
    }

    [Test]
    public void GetObjectInfo_Command6_ReturnsDirectoryHeader()
    {
        Login();

        byte[] parameters = new byte[] { 6, (byte)'.', FileServerReply.CarriageReturn };
        EconetMessage request = CreateLoggedInRequest(FileServerOpcodes.GetObjectInfo, parameters);
        EconetMessage? response = _handler.Handle(request);

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Payload, Has.Length.EqualTo(17));
        Assert.That(response.Payload[2], Is.EqualTo(0x02));
        // PiFS fs.c:10388: the 10-byte dir name is "$" only at the disc root; the logged-in home is
        // $.USER and reports "USER". Re-baselined 2026-07-17 (PiFS naming parity).
        Assert.That(Encoding.ASCII.GetString(response.Payload, 5, 10), Is.EqualTo("USER      "));
    }

    private void Login()
    {
        EconetMessage request = CreateRequest(FileServerOpcodes.OsCli, Encoding.ASCII.GetBytes("I AM SYST \r"));
        _handler.Handle(request);
    }

    private static EconetMessage CreateRequest(byte opcode, byte[] parameters)
    {
        byte[] payload = new byte[5 + parameters.Length];
        payload[0] = 0x80;
        payload[1] = opcode;
        for (int i = 0; i < parameters.Length; i++)
            payload[5 + i] = parameters[i];

        return CreateRequest(payload);
    }

    private static EconetMessage CreateLoggedInRequest(byte opcode, byte[] parameters)
    {
        byte[] payload = new byte[5 + parameters.Length];
        payload[0] = 0x80;
        payload[1] = opcode;
        payload[2] = 1;
        payload[3] = 2;
        payload[4] = 4;
        for (int i = 0; i < parameters.Length; i++)
            payload[5 + i] = parameters[i];

        return CreateRequest(payload);
    }

    private static EconetMessage CreateRequest(byte[] payload)
    {
        return new EconetMessage
        {
            PacketType = AunPacketType.Unicast,
            EconetPort = EconetPorts.FileServer,
            Control = 0x80,
            Sequence = 1,
            Payload = payload,
            ClientAddress = IPAddress.Loopback,
            ClientPort = 50000,
            ClientNet = 1,
            ClientStation = 1,
        };
    }
}
