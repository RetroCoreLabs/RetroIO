//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
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

/// <summary>
/// Tests the counters of the Econet file server: the counter class on its own, and that
/// the running service increments them for each request it really handles.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class EconetServerStatisticsTests
{
    [Test]
    public void NewStatistics_AreAllZero()
    {
        EconetServerStatistics stats = new EconetServerStatistics();

        Assert.That(stats.TotalRequests, Is.EqualTo(0));
        Assert.That(stats.BytesReceived, Is.EqualTo(0));
        Assert.That(stats.BytesSent, Is.EqualTo(0));
        Assert.That(stats.BytesTransferred, Is.EqualTo(0));
        Assert.That(stats.FileOperations, Is.EqualTo(0));
        Assert.That(stats.Errors, Is.EqualTo(0));
    }

    [Test]
    public void RecordRequest_AddsRequestsAndBytes()
    {
        EconetServerStatistics stats = new EconetServerStatistics();

        stats.RecordRequest(10, 100, false);
        stats.RecordRequest(20, 200, true);
        stats.RecordError();

        Assert.That(stats.TotalRequests, Is.EqualTo(2));
        Assert.That(stats.BytesReceived, Is.EqualTo(30));
        Assert.That(stats.BytesSent, Is.EqualTo(300));
        Assert.That(stats.BytesTransferred, Is.EqualTo(330));
        Assert.That(stats.FileOperations, Is.EqualTo(1));
        Assert.That(stats.Errors, Is.EqualTo(1));
    }

    [Test]
    public void RecordRequest_FromManyThreads_LosesNoCount()
    {
        EconetServerStatistics stats = new EconetServerStatistics();
        const int threads = 8;
        const int perThread = 5000;

        Task[] tasks = new Task[threads];
        for (int t = 0; t < threads; t++)
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                    stats.RecordRequest(1, 2, false);
            });
        Task.WaitAll(tasks);

        Assert.That(stats.TotalRequests, Is.EqualTo(threads * perThread));
        Assert.That(stats.BytesReceived, Is.EqualTo(threads * perThread));
        Assert.That(stats.BytesSent, Is.EqualTo(2L * threads * perThread));
    }

    [Test]
    public void Uptime_StartsAtCreation()
    {
        EconetServerStatistics stats = new EconetServerStatistics();

        Assert.That(stats.Uptime, Is.GreaterThanOrEqualTo(TimeSpan.Zero));
        Assert.That(stats.Uptime, Is.LessThan(TimeSpan.FromMinutes(1)));
    }

    [Test]
    public async Task Service_CountsEachHandledRequestAndItsReply()
    {
        InMemoryAunTransport transport = new InMemoryAunTransport();
        EconetServerStatistics stats = new EconetServerStatistics();
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

        IHost host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IFileSystemBackend>(fileSystem);
                services.AddSingleton(context);
                services.AddSingleton(sessions);
                services.AddSingleton<FileServerCommandHandler>();
                services.AddSingleton<BulkTransferHandler>();
                services.AddSingleton<ITransport>(transport);
                services.AddHostedService(sp =>
                    new EconetFileServerService(
                        new ITransport[] { transport },
                        sp.GetRequiredService<FileServerCommandHandler>(),
                        sp.GetRequiredService<BulkTransferHandler>(),
                        sp.GetRequiredService<FileServerSessionManager>(),
                        NullLogger<EconetFileServerService>.Instance,
                        statistics: stats));
            })
            .Build();

        try
        {
            await host.StartAsync();

            byte[] payload = [0x80, FileServerOpcodes.ReadVersion, 0, 0, 0];
            await transport.SendTestMessageAsync(new EconetMessage
            {
                PacketType = AunPacketType.Unicast,
                EconetPort = EconetPorts.FileServer,
                Control = 0x80,
                Sequence = 42,
                Payload = payload,
                ClientAddress = IPAddress.Loopback,
                ClientPort = 50000,
            });

            using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            EconetMessage reply = await transport.ReceiveResponseAsync(timeout.Token);

            // The counters are updated after the reply is sent: poll until they show it.
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (stats.TotalRequests == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            Assert.That(stats.TotalRequests, Is.EqualTo(1));
            Assert.That(stats.BytesReceived, Is.EqualTo(payload.Length));
            Assert.That(stats.BytesSent, Is.EqualTo(reply.Payload.Length));
            Assert.That(stats.Errors, Is.EqualTo(0));
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
            transport.Dispose();
        }
    }
}
