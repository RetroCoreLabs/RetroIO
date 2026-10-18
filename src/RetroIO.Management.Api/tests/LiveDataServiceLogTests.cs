//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetroIO.Management.Api.Hubs;
using RetroIO.Management.Api.Services;
using RetroIO.Management.Contracts;

namespace RetroIO.Management.Api.Tests;

/// <summary>
/// Tests that <see cref="LiveDataService"/> keeps pushing new log lines to the browser after the
/// in-memory log buffer has filled up and started to drop its oldest entries.
/// </summary>
[TestFixture]
public class LiveDataServiceLogTests
{
    private readonly List<string[]> _pushed = new();
    private InMemoryLogProvider _logs = null!;
    private LiveDataService _service = null!;
    private ILogger _writer = null!;

    [SetUp]
    public void SetUp()
    {
        // NUnit makes one instance of this class for all its tests, so start each test with nothing pushed.
        lock (_pushed)
            _pushed.Clear();

        // A buffer of three, so it is full after three lines and every further line pushes one out.
        _logs = new InMemoryLogProvider(maxEntries: 3);
        _writer = _logs.CreateLogger("test");

        Mock<IServerHubClient> client = new Mock<IServerHubClient>();
        client.Setup(c => c.NewLogs(It.IsAny<LogEntryDto[]>()))
            .Returns<LogEntryDto[]>(entries =>
            {
                lock (_pushed)
                {
                    string[] messages = new string[entries.Length];
                    for (int i = 0; i < entries.Length; i++)
                        messages[i] = entries[i].Message;
                    _pushed.Add(messages);
                }
                return Task.CompletedTask;
            });
        client.Setup(c => c.ServerStatus(It.IsAny<ServerStatusUpdate>())).Returns(Task.CompletedTask);
        client.Setup(c => c.TnfsStats(It.IsAny<TnfsStatsUpdate>())).Returns(Task.CompletedTask);

        Mock<IHubClients<IServerHubClient>> clients = new Mock<IHubClients<IServerHubClient>>();
        clients.SetupGet(c => c.All).Returns(client.Object);
        Mock<IHubContext<ServerHub, IServerHubClient>> hub = new Mock<IHubContext<ServerHub, IServerHubClient>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        Mock<IServerManager> manager = new Mock<IServerManager>();
        manager.Setup(m => m.GetStatus()).Returns(new ServerStatusResponse { IsRunning = true, Uptime = "0:00:01" });

        _service = new LiveDataService(hub.Object, _logs, manager.Object, NullLogger<LiveDataService>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
        _logs.Dispose();
    }

    private string[][] Pushed()
    {
        lock (_pushed)
            return _pushed.ToArray();
    }

    // The service ticks once a second; wait up to 6 s for the condition.
    private static async Task WaitFor(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(6);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out waiting for " + what);
            await Task.Delay(50);
        }
    }

    [Test]
    public async Task AfterTheBufferHasFilled_NewLinesAreStillPushed()
    {
        // Regression: the service remembered how many lines it had seen as a position in the buffer.
        // Once the buffer held its maximum, the count stopped growing, "no more than last time" was
        // always true, and no new line was ever pushed again.
        await _service.StartAsync(CancellationToken.None);

        _writer.LogInformation("a1");
        _writer.LogInformation("a2");
        _writer.LogInformation("a3");
        await WaitFor(() => Pushed().Length >= 1, "the first three lines");
        Assert.That(Pushed()[0], Is.EqualTo(new[] { "a1", "a2", "a3" }));

        _writer.LogInformation("b1");
        _writer.LogInformation("b2");
        await WaitFor(() => Pushed().Length >= 2, "the two lines written after the buffer was full");

        Assert.That(Pushed()[1], Is.EqualTo(new[] { "b1", "b2" }));
    }

    [Test]
    public async Task MoreNewLinesThanTheBufferHolds_PushesTheOnesThatAreStillThere()
    {
        await _service.StartAsync(CancellationToken.None);

        _writer.LogInformation("a1");
        await WaitFor(() => Pushed().Length >= 1, "the first line");

        // Five lines between two ticks: only the last three are still in a buffer of three.
        for (int i = 1; i <= 5; i++)
            _writer.LogInformation("c" + i);
        await WaitFor(() => Pushed().Length >= 2, "the burst");

        Assert.That(Pushed()[1], Is.EqualTo(new[] { "c3", "c4", "c5" }));
    }
}
