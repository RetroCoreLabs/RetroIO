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
using RetroIO.Identity;
using RetroIO.Protocols.Econet.FileServer;
using RetroIO.Protocols.Econet.FileServer.Bulk;
using RetroIO.Protocols.Econet.FileServer.Configuration;
using RetroIO.Protocols.Econet.FileServer.Discs;
using RetroIO.Protocols.Econet.FileServer.Handlers;
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
public sealed class EconetActivityRecordingTests
{
    private MockFileSystemBackend _fileSystem = null!;
    private EconetActivityLog _activityLog = null!;
    private FileServerCommandHandler _handler = null!;
    private BulkTransferHandler _bulkHandler = null!;
    private FileServerContext _context = null!;
    private IRetroIDP _identityProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _fileSystem = new MockFileSystemBackend();
        EconetSampleCatalogFixture.Seed(_fileSystem);

        _activityLog = new EconetActivityLog();
        EconetUserDirectory users = EconetTestUsers.CreateDirectory(out _identityProvider);
        DiscCatalog discs = new DiscCatalog(_fileSystem, "/econet-root", "RetroFS");
        FileServerSessionManager sessions = new FileServerSessionManager(_fileSystem, users, discs, "/econet-root");
        FileServerHandleManager handles = new FileServerHandleManager();
        PathResolver paths = new PathResolver(_fileSystem, discs, "/econet-root");
        BulkTransferManager bulk = new BulkTransferManager();

        _context = new FileServerContext(
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
            FileServerCommandHandler.DefaultVersionString,
            _activityLog);

        _handler = new FileServerCommandHandler(_context, NullLogger<FileServerCommandHandler>.Instance);
        _bulkHandler = new BulkTransferHandler(_context);
    }

    [Test]
    public void Login_RecordsOsCliCommand()
    {
        EconetMessage request = CreateRequest(FileServerOpcodes.OsCli, Encoding.ASCII.GetBytes("I AM SYST \r"));
        _handler.Handle(request);

        IReadOnlyList<EconetCommandEntry> commands = _activityLog.GetRecentCommands(10);
        Assert.That(commands.Count, Is.EqualTo(1));
        Assert.That(commands[0].Command, Is.EqualTo("*I AM"));
        Assert.That(commands[0].Result, Does.Contain("Login OK"));
    }

    [Test]
    public void WrongPassword_RecordsFailureNotLoginOk()
    {
        RetroUser? systUser = _identityProvider.GetUserByUsernameAsync("SYST").AsTask().GetAwaiter().GetResult();
        Assert.That(systUser, Is.Not.Null);
        _identityProvider.SetPasswordAsync(systUser!.UserId, "MYPASSWORD").AsTask().GetAwaiter().GetResult();

        EconetMessage request = CreateRequest(FileServerOpcodes.OsCli, Encoding.ASCII.GetBytes("I AM SYST WRONGPASSWORD\r"));
        _handler.Handle(request);

        IReadOnlyList<EconetCommandEntry> commands = _activityLog.GetRecentCommands(1);
        Assert.That(commands.Count, Is.EqualTo(1));
        Assert.That(commands[0].Command, Is.EqualTo("*I AM"));
        Assert.That(commands[0].Success, Is.False);
        Assert.That(commands[0].Result, Does.Not.Contain("Login OK"));
        Assert.That(commands[0].Result, Does.Contain("Wrong password").IgnoreCase);
    }

    [Test]
    public void ByeWithoutLogin_RecordsWhoAreYouFailure()
    {
        EconetMessage request = CreateRequest(FileServerOpcodes.OsCli, Encoding.ASCII.GetBytes("BYE\r"));
        _handler.Handle(request);

        IReadOnlyList<EconetCommandEntry> commands = _activityLog.GetRecentCommands(1);
        Assert.That(commands.Count, Is.EqualTo(1));
        Assert.That(commands[0].Command, Is.EqualTo("*BYE"));
        Assert.That(commands[0].Success, Is.False);
        Assert.That(commands[0].Result, Does.Contain("Not logged in").IgnoreCase);
    }

    [Test]
    public void GetObjectInfoNotFound_RecordsFailureNotOk()
    {
        Login();

        byte[] parameters = Encoding.ASCII.GetBytes("1NOFILE\r");
        byte[] payload = new byte[5 + parameters.Length];
        payload[0] = 0x50;
        payload[1] = FileServerOpcodes.GetObjectInfo;
        payload[2] = 1;
        payload[3] = 2;
        payload[4] = 4;
        parameters.CopyTo(payload, 5);

        EconetMessage request = new EconetMessage
        {
            PacketType = AunPacketType.Unicast,
            EconetPort = EconetPorts.FileServer,
            Control = 0x80,
            Sequence = 2,
            Payload = payload,
            ClientAddress = IPAddress.Loopback,
            ClientPort = 32768,
            ClientNet = 1,
            ClientStation = 10,
        };
        _handler.Handle(request);

        IReadOnlyList<EconetCommandEntry> commands = _activityLog.GetRecentCommands(1);
        Assert.That(commands[0].Success, Is.False);
        Assert.That(commands[0].Result, Is.Not.EqualTo("OK"));
    }

    [Test]
    public void GetObjectInfo_FilenameOnly_WireCwdZero_RecordsSoftNotFound()
    {
        // Handles (payload bytes 2-4) default to 0, i.e. wire CWD handle 0. PiFS ground truth
        // (utilities/fs.c:9951, 2341, 5641-5642) makes that a soft not-found [00 00 00] for
        // commands 1-5, not a resolved file block. The request detail still parses as cmd=1
        // (filename-only, command byte omitted); only the reply summary changes to not-found.
        Login();

        byte[] payload = new byte[5 + "HELLO\r".Length];
        payload[0] = 0x50;
        payload[1] = FileServerOpcodes.GetObjectInfo;
        Encoding.ASCII.GetBytes("HELLO\r").CopyTo(payload, 5);

        EconetMessage request = new EconetMessage
        {
            PacketType = AunPacketType.Unicast,
            EconetPort = EconetPorts.FileServer,
            Control = 0x80,
            Sequence = 2,
            Payload = payload,
            ClientAddress = IPAddress.Loopback,
            ClientPort = 32768,
            ClientNet = 1,
            ClientStation = 10,
        };
        _handler.Handle(request);

        IReadOnlyList<EconetCommandEntry> commands = _activityLog.GetRecentCommands(1);
        Assert.That(commands[0].Detail, Does.Contain("cmd=1"));
        Assert.That(commands[0].Detail, Does.Contain("HELLO"));
        Assert.That(commands[0].Result, Does.Contain("soft-not-found"));
    }

    [Test]
    public void Load_CompletesWithSendTransferRecord()
    {
        Login();

        byte[] payload = new byte[5 + "HELLO\r".Length];
        payload[0] = 0x50;
        payload[1] = FileServerOpcodes.Load;
        payload[2] = 42;
        payload[3] = 2;
        payload[4] = 0;
        Encoding.ASCII.GetBytes("HELLO\r").CopyTo(payload, 5);

        EconetMessage loadRequest = new EconetMessage
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
        _handler.Handle(loadRequest);

        EconetMessage ack = new EconetMessage
        {
            PacketType = AunPacketType.Ack,
            EconetPort = 0x99,
            Control = 0x80,
            Sequence = loadRequest.Sequence,
            Payload = Array.Empty<byte>(),
            ClientAddress = IPAddress.Loopback,
            ClientPort = 32768,
            ClientNet = 1,
            ClientStation = 10,
        };

        while (true)
        {
            EconetMessage? outbound = _bulkHandler.HandleAck(ack);
            if (outbound == null)
                break;
        }

        IReadOnlyList<EconetFileTransferEntry> transfers = _activityLog.GetRecentTransfers(10);
        Assert.That(transfers.Count, Is.EqualTo(1));
        Assert.That(transfers[0].Direction, Is.EqualTo(EconetFileTransferDirection.Send));
        Assert.That(transfers[0].FilePath, Does.Contain("HELLO"));
        Assert.That(transfers[0].Bytes, Is.EqualTo(EconetSampleCatalogFixture.ReadSampleFile(_fileSystem, "HELLO").Length));
        Assert.That(transfers[0].Status, Is.EqualTo("OK"));
    }

    [Test]
    public void Save_CompletesWithReceiveTransferRecord()
    {
        Login();

        EconetMessage saveRequest = CreateSaveRequest(load: 0x2800, exec: 0x2800, length: 5, "NEWFILE");
        EconetMessage? saveResponse = _handler.Handle(saveRequest);
        Assert.That(saveResponse, Is.Not.Null);
        Assert.That(saveResponse!.Payload[2], Is.GreaterThan(0));

        byte bulkPort = saveResponse.Payload[2];
        EconetMessage data = new EconetMessage
        {
            PacketType = AunPacketType.Unicast,
            EconetPort = bulkPort,
            Control = 0x80,
            Sequence = saveRequest.Sequence + 1,
            Payload = Encoding.ASCII.GetBytes("12345"),
            ClientAddress = IPAddress.Loopback,
            ClientPort = 32768,
            ClientNet = 1,
            ClientStation = 10,
        };

        EconetMessage? complete = _bulkHandler.HandleData(data);
        Assert.That(complete, Is.Not.Null);
        Assert.That(complete!.Payload[0], Is.EqualTo(3));

        IReadOnlyList<EconetFileTransferEntry> transfers = _activityLog.GetRecentTransfers(10);
        Assert.That(transfers.Count, Is.EqualTo(1));
        Assert.That(transfers[0].Direction, Is.EqualTo(EconetFileTransferDirection.Receive));
        Assert.That(transfers[0].Bytes, Is.EqualTo(5));
        Assert.That(transfers[0].Status, Is.EqualTo("OK"));
    }

    private void Login()
    {
        EconetMessage request = CreateRequest(FileServerOpcodes.OsCli, Encoding.ASCII.GetBytes("I AM SYST \r"));
        _handler.Handle(request);
        _activityLog.GetRecentCommands(100);
    }

    private EconetMessage CreateSaveRequest(uint load, uint exec, int length, string filename, byte ackPort = 0x32)
    {
        string crName = filename + "\r";
        byte[] payload = new byte[16 + crName.Length];
        payload[0] = 0x50;
        payload[1] = FileServerOpcodes.Save;
        payload[2] = ackPort;
        WriteUInt32Le(payload, 5, load);
        WriteUInt32Le(payload, 9, exec);
        payload[13] = (byte)length;
        payload[14] = (byte)(length >> 8);
        payload[15] = (byte)(length >> 16);
        Encoding.ASCII.GetBytes(crName).CopyTo(payload, 16);

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

    private static void WriteUInt32Le(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private EconetMessage CreateRequest(byte opcode, byte[] parameters)
    {
        byte[] payload = new byte[5 + parameters.Length];
        payload[0] = 0x50;
        payload[1] = opcode;
        payload[2] = 1;
        payload[3] = 2;
        payload[4] = 3;
        parameters.CopyTo(payload, 5);

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
