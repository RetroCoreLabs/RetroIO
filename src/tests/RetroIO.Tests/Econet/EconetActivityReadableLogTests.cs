//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
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
public sealed class EconetActivityReadableLogTests
{
    [Test]
    public void UnsupportedOpcode_RecordsReadableCommandAndResult()
    {
        EconetActivityLog log = new EconetActivityLog();
        FileServerCommandHandler handler = CreateHandler(log);

        Login(handler);
        handler.Handle(CreateRequest(0x65, ReadOnlySpan<byte>.Empty));

        IReadOnlyList<EconetCommandEntry> commands = log.GetRecentCommands(1);
        Assert.That(commands.Count, Is.EqualTo(1));
        Assert.That(commands[0].Command, Is.EqualTo("Unknown 0x65"));
        Assert.That(commands[0].Result, Does.Contain("Unsupported"));
        Assert.That(commands[0].Detail, Does.Contain("reply=&50"));
        Assert.That(commands[0].Detail, Does.Contain("fsop=&65"));
        Assert.That(commands[0].Detail, Does.Contain("raw 50 65"));
    }

    [Test]
    public void EconetPortAsOpcode_RecordsBadFsopLabel()
    {
        EconetActivityLog log = new EconetActivityLog();
        FileServerCommandHandler handler = CreateHandler(log);

        Login(handler);
        handler.Handle(CreateRequest(0x99, ReadOnlySpan<byte>.Empty));

        IReadOnlyList<EconetCommandEntry> commands = log.GetRecentCommands(1);
        Assert.That(commands.Count, Is.EqualTo(1));
        Assert.That(commands[0].Command, Is.EqualTo("Bad fsop 0x99"));
        Assert.That(commands[0].Result, Does.Contain("Unsupported"));
        Assert.That(commands[0].Detail, Does.Contain("fsop=&99"));
    }

    [Test]
    public void MalformedPayload_RecordsRawBytes()
    {
        EconetActivityLog log = new EconetActivityLog();
        FileServerCommandHandler handler = CreateHandler(log);

        EconetMessage request = new EconetMessage
        {
            PacketType = AunPacketType.Unicast,
            EconetPort = EconetPorts.FileServer,
            Control = 0x80,
            Sequence = 1,
            Payload = [0x50],
            ClientAddress = IPAddress.Loopback,
            ClientPort = 32768,
            ClientNet = 1,
            ClientStation = 10,
        };

        handler.Handle(request);

        IReadOnlyList<EconetCommandEntry> commands = log.GetRecentCommands(1);
        Assert.That(commands.Count, Is.EqualTo(1));
        Assert.That(commands[0].Command, Is.EqualTo("Malformed FS request"));
        Assert.That(commands[0].Detail, Does.Contain("raw 50"));
    }

    private static FileServerCommandHandler CreateHandler(EconetActivityLog log)
    {
        MockFileSystemBackend fileSystem = new MockFileSystemBackend();
        EconetSampleCatalogFixture.Seed(fileSystem);

        EconetUserDirectory users = EconetTestUsers.CreateDirectory(out _);
        DiscCatalog discs = new DiscCatalog(fileSystem, "/econet-root", "RetroFS");
        FileServerSessionManager sessions = new FileServerSessionManager(fileSystem, users, discs, "/econet-root");

        FileServerContext context = new FileServerContext(
            fileSystem,
            users,
            discs,
            sessions,
            new FileServerHandleManager(),
            new PathResolver(fileSystem, discs, "/econet-root"),
            new BulkTransferManager(),
            new FileServerConfiguration(),
            new FileServerClock(),
            "/econet-root",
            "RetroFS",
            FileServerCommandHandler.DefaultVersionString,
            log);

        return new FileServerCommandHandler(context, NullLogger<FileServerCommandHandler>.Instance);
    }

    private static void Login(FileServerCommandHandler handler)
    {
        handler.Handle(CreateRequest(FileServerOpcodes.OsCli, Encoding.ASCII.GetBytes("I AM SYST \r")));
    }

    private static EconetMessage CreateRequest(byte opcode, ReadOnlySpan<byte> parameters)
    {
        byte[] payload = new byte[5 + parameters.Length];
        payload[0] = 0x50;
        payload[1] = opcode;
        payload[2] = 1;
        payload[3] = 2;
        payload[4] = 3;
        parameters.CopyTo(payload.AsSpan(5));

        return new EconetMessage
        {
            PacketType = AunPacketType.Unicast,
            EconetPort = EconetPorts.FileServer,
            Control = 0x80,
            Sequence = 1,
            Payload = payload,
            ClientAddress = IPAddress.Loopback,
            ClientPort = 32768,
            ClientNet = 1,
            ClientStation = 10,
        };
    }
}
