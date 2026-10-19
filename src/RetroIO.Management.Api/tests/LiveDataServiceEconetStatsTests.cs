//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetroIO.Management.Api.Hubs;
using RetroIO.Management.Api.Services;
using RetroIO.Management.Contracts;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Management.Api.Tests;

/// <summary>
/// Tests that <see cref="LiveDataService"/> pushes the real Econet counters and not fixed zeros.
/// </summary>
[TestFixture]
public class LiveDataServiceEconetStatsTests
{
    private static LiveDataService Build(List<EconetStatsUpdate> pushed, EconetServerStatistics? stats)
    {
        Mock<IServerHubClient> client = new Mock<IServerHubClient>();
        client.Setup(c => c.NewLogs(It.IsAny<LogEntryDto[]>())).Returns(Task.CompletedTask);
        client.Setup(c => c.ServerStatus(It.IsAny<ServerStatusUpdate>())).Returns(Task.CompletedTask);
        client.Setup(c => c.TnfsStats(It.IsAny<TnfsStatsUpdate>())).Returns(Task.CompletedTask);
        client.Setup(c => c.EconetStats(It.IsAny<EconetStatsUpdate>()))
            .Returns<EconetStatsUpdate>(u =>
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
        return new LiveDataService(hub.Object, logs, manager.Object, NullLogger<LiveDataService>.Instance,
            null, null, null, stats);
    }

    private static async Task<EconetStatsUpdate> FirstPush(LiveDataService service, List<EconetStatsUpdate> pushed)
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
                Assert.Fail("Timed out waiting for the Econet statistics push");
            await Task.Delay(50);
        }
    }

    [Test]
    public async Task PushesTheRealCountersOfTheEconetServer()
    {
        EconetServerStatistics stats = new EconetServerStatistics();
        stats.RecordRequest(10, 100, false);
        stats.RecordRequest(20, 200, true);

        List<EconetStatsUpdate> pushed = new List<EconetStatsUpdate>();
        LiveDataService service = Build(pushed, stats);
        try
        {
            EconetStatsUpdate update = await FirstPush(service, pushed);

            Assert.That(update.TotalRequests, Is.EqualTo(2));
            Assert.That(update.TotalBytesTransferred, Is.EqualTo(330));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    [Test]
    public async Task WithNoEconetServerInTheProcess_PushesZeros()
    {
        List<EconetStatsUpdate> pushed = new List<EconetStatsUpdate>();
        LiveDataService service = Build(pushed, null);
        try
        {
            EconetStatsUpdate update = await FirstPush(service, pushed);

            Assert.That(update.TotalRequests, Is.EqualTo(0));
            Assert.That(update.TotalBytesTransferred, Is.EqualTo(0));
            Assert.That(update.ActiveSessions, Is.EqualTo(0));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }
}
