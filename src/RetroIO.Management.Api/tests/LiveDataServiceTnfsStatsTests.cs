//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Net;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetroIO.Management.Api.Hubs;
using RetroIO.Management.Api.Services;
using RetroIO.Management.Contracts;
using RetroIO.Protocols.Tnfs.Server;

namespace RetroIO.Management.Api.Tests;

/// <summary>
/// Tests that <see cref="LiveDataService"/> pushes the real TNFS counters and not fixed zeros.
/// </summary>
[TestFixture]
public class LiveDataServiceTnfsStatsTests
{
    private static LiveDataService Build(List<TnfsStatsUpdate> pushed, TnfsServerStatistics? stats, SessionManager? sessions)
    {
        Mock<IServerHubClient> client = new Mock<IServerHubClient>();
        client.Setup(c => c.NewLogs(It.IsAny<LogEntryDto[]>())).Returns(Task.CompletedTask);
        client.Setup(c => c.ServerStatus(It.IsAny<ServerStatusUpdate>())).Returns(Task.CompletedTask);
        client.Setup(c => c.TnfsStats(It.IsAny<TnfsStatsUpdate>()))
            .Returns<TnfsStatsUpdate>(u =>
            {
                lock (pushed)
                    pushed.Add(u);
                return Task.CompletedTask;
            });

        Mock<IHubClients<IServerHubClient>> clients = new Mock<IHubClients<IServerHubClient>>();
        clients.SetupGet(c => c.All).Returns(client.Object);
        Mock<IHubContext<ServerHub, IServerHubClient>> hub = new Mock<IHubContext<ServerHub, IServerHubClient>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        Mock<IServerManager> manager = new Mock<IServerManager>();
        manager.Setup(m => m.GetStatus()).Returns(new ServerStatusResponse { IsRunning = true, Uptime = "0:00:01" });

        InMemoryLogProvider logs = new InMemoryLogProvider(maxEntries: 3);
        return new LiveDataService(hub.Object, logs, manager.Object, NullLogger<LiveDataService>.Instance, sessions, stats);
    }

    private static async Task<TnfsStatsUpdate> FirstPush(LiveDataService service, List<TnfsStatsUpdate> pushed)
    {
        await service.StartAsync(CancellationToken.None);
        DateTime deadline = DateTime.UtcNow.AddSeconds(6);
        while (true)
        {
            lock (pushed)
            {
                if (pushed.Count > 0)
                    return pushed[0];
            }
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out waiting for the TNFS statistics push");
            await Task.Delay(50);
        }
    }

    [Test]
    public async Task PushesTheRealCountersOfTheTnfsServer()
    {
        TnfsServerStatistics stats = new TnfsServerStatistics();
        stats.RecordRequest(1024);
        stats.RecordRequest(2048);

        ILoggerFactory loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        SessionManager sessions = new SessionManager(loggerFactory.CreateLogger<SessionManager>(), TimeSpan.FromMinutes(10), maxSessionsPerIP: 8);
        sessions.CreateSession(IPAddress.Parse("10.0.0.1"), "/root");

        List<TnfsStatsUpdate> pushed = new List<TnfsStatsUpdate>();
        LiveDataService service = Build(pushed, stats, sessions);
        try
        {
            TnfsStatsUpdate update = await FirstPush(service, pushed);

            Assert.That(update.TotalRequests, Is.EqualTo(2));
            Assert.That(update.TotalBytesTransferred, Is.EqualTo(3072));
            Assert.That(update.ActiveConnections, Is.EqualTo(1));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
            sessions.Dispose();
        }
    }

    [Test]
    public async Task WithNoTnfsServerInTheProcess_PushesZeros()
    {
        List<TnfsStatsUpdate> pushed = new List<TnfsStatsUpdate>();
        LiveDataService service = Build(pushed, null, null);
        try
        {
            TnfsStatsUpdate update = await FirstPush(service, pushed);

            Assert.That(update.TotalRequests, Is.EqualTo(0));
            Assert.That(update.TotalBytesTransferred, Is.EqualTo(0));
            Assert.That(update.ActiveConnections, Is.EqualTo(0));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }
}
