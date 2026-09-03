//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Common;
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
using RetroIO.Protocols.Econet.Server.Transport;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Tests.TNFS;

namespace RetroIO.Tests.Econet;

[TestFixture]
[Category("Unit")]
public sealed class EconetInMemoryTransportTests
{
    private InMemoryAunTransport _transport = null!;
    private IHost _host = null!;

    [SetUp]
    public async Task SetUp()
    {
        _transport = new InMemoryAunTransport();

        MockFileSystemBackend fileSystem = new MockFileSystemBackend();
        EconetSampleCatalogFixture.Seed(fileSystem);

        EconetUserDirectory users = EconetTestUsers.CreateDirectory(out _);
        DiscCatalog discs = new DiscCatalog(fileSystem, "/econet-root", "RetroFS");
        FileServerSessionManager sessions = new FileServerSessionManager(fileSystem, users, discs, "/econet-root");
        FileServerHandleManager handles = new FileServerHandleManager();
        PathResolver paths = new PathResolver(fileSystem, discs, "/econet-root");
        BulkTransferManager bulk = new BulkTransferManager();
        FileServerContext context = new FileServerContext(
            fileSystem, users, discs, sessions, handles, paths, bulk,
            new FileServerConfiguration(), new FileServerClock(),
            "/econet-root", "RetroFS", FileServerCommandHandler.DefaultVersionString);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IFileSystemBackend>(fileSystem);
                services.AddSingleton(context);
                services.AddSingleton(sessions);
                services.AddSingleton<FileServerCommandHandler>();
                services.AddSingleton<BulkTransferHandler>();
                services.AddSingleton<ITransport>(_transport);
                services.AddHostedService(sp =>
                    new EconetFileServerService(
                        new ITransport[] { _transport },
                        sp.GetRequiredService<FileServerCommandHandler>(),
                        sp.GetRequiredService<BulkTransferHandler>(),
                        sp.GetRequiredService<FileServerSessionManager>(),
                        NullLogger<EconetFileServerService>.Instance));
            })
            .Build();

        await _host.StartAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _transport.Dispose();
    }

    [Test]
    public async Task ReadVersion_OverInMemoryTransport_ReturnsVersionString()
    {
        byte[] fsPayload =
        [
            0x80,
            FileServerOpcodes.ReadVersion,
            0, 0, 0,
        ];

        await _transport.SendTestMessageAsync(CreateRequest(fsPayload));

        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        EconetMessage response = await _transport.ReceiveResponseAsync(timeout.Token);

        Assert.That(
            Encoding.ASCII.GetString(response.Payload, 2, response.Payload.Length - 3),
            Is.EqualTo(FileServerCommandHandler.DefaultVersionString));
    }

    [Test]
    public async Task Login_OverInMemoryTransport_ReturnsSuccess()
    {
        byte[] command = Encoding.ASCII.GetBytes("I AM SYST \r");
        byte[] fsPayload = new byte[5 + command.Length];
        fsPayload[0] = 0x80;
        fsPayload[1] = FileServerOpcodes.OsCli;
        command.CopyTo(fsPayload, 5);

        await _transport.SendTestMessageAsync(CreateRequest(fsPayload));

        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        EconetMessage response = await _transport.ReceiveResponseAsync(timeout.Token);

        Assert.That(response.Payload, Is.EqualTo(new byte[]
        {
            FileServerOpcodes.LoginSuccessReturnCode,
            0,
            AcornFileHandles.WireUrd,
            AcornFileHandles.WireCwd,
            AcornFileHandles.WireLib,
            0,
        }));
    }

    [Test]
    public async Task Cat_OverInMemoryTransport_AfterLogin_RedirectsToExamine()
    {
        await LoginOverTransportAsync();

        byte[] command = Encoding.ASCII.GetBytes("CAT\r");
        byte[] fsPayload = new byte[5 + command.Length];
        fsPayload[0] = 0x90;
        fsPayload[1] = FileServerOpcodes.OsCli;
        command.CopyTo(fsPayload, 5);

        await _transport.SendTestMessageAsync(CreateRequest(fsPayload));

        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        EconetMessage response = await _transport.ReceiveResponseAsync(timeout.Token);

        Assert.That(response.Payload[0], Is.EqualTo(FileServerOpcodes.Examine));
        Assert.That(response.Payload[1], Is.EqualTo(0));
        Assert.That(response.EconetPort, Is.EqualTo(0x90));
        Assert.That(AcornPath.ReadCrTerminatedString(response.Payload.AsSpan(2)), Is.EqualTo("."));
    }

    private async Task LoginOverTransportAsync()
    {
        byte[] command = Encoding.ASCII.GetBytes("I AM SYST \r");
        byte[] fsPayload = new byte[5 + command.Length];
        fsPayload[0] = 0x80;
        fsPayload[1] = FileServerOpcodes.OsCli;
        command.CopyTo(fsPayload, 5);

        await _transport.SendTestMessageAsync(CreateRequest(fsPayload));

        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _transport.ReceiveResponseAsync(timeout.Token);
    }

    private static EconetMessage CreateRequest(byte[] payload)
    {
        return new EconetMessage
        {
            PacketType = AunPacketType.Unicast,
            EconetPort = EconetPorts.FileServer,
            Control = 0x80,
            Sequence = 42,
            Payload = payload,
            ClientAddress = IPAddress.Loopback,
            ClientPort = 50000,
        };
    }
}
