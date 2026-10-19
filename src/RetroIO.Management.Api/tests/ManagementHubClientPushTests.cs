//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroIO.Management.Api.Hubs;
using RetroIO.Management.Client;
using RetroIO.Management.Contracts;

namespace RetroIO.Management.Api.Tests;

/// <summary>
/// Tests that <see cref="ManagementHubClient"/> receives the pushes of the real server hub. The hub runs
/// in this process on a loopback port chosen by the system, without the login rule of the full server,
/// and the client connects to it over a real SignalR connection.
/// </summary>
[TestFixture]
public class ManagementHubClientPushTests
{
    private WebApplication _app = null!;
    private string _baseUrl = string.Empty;
    private ManagementHubClient _client = null!;

    [SetUp]
    public async Task SetUp()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        _app = builder.Build();
        _app.MapHub<ServerHub>(ServerHubMethods.HubPath);
        await _app.StartAsync();

        List<string> urls = new List<string>(_app.Urls);
        Assert.That(urls, Is.Not.Empty);
        _baseUrl = urls[0];

        _client = new ManagementHubClient(_baseUrl);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>
    /// A push of Econet counters from the server reaches the Econet event of the client with the same values.
    /// </summary>
    [Test]
    public async Task EconetStatsPush_RaisesEconetStatsUpdated()
    {
        TaskCompletionSource<EconetStatsUpdate> received = new TaskCompletionSource<EconetStatsUpdate>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _client.EconetStatsUpdated += update => received.TrySetResult(update);
        await _client.ConnectAsync();

        IHubContext<ServerHub, IServerHubClient> hub =
            _app.Services.GetRequiredService<IHubContext<ServerHub, IServerHubClient>>();
        await hub.Clients.All.EconetStats(new EconetStatsUpdate
        {
            ActiveSessions = 3,
            TotalRequests = 1234567890123L,
            TotalBytesTransferred = 9876543210L,
        });

        Task finished = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.That(finished, Is.SameAs(received.Task), "the client never raised EconetStatsUpdated");
        EconetStatsUpdate update2 = await received.Task;
        Assert.That(update2.ActiveSessions, Is.EqualTo(3));
        Assert.That(update2.TotalRequests, Is.EqualTo(1234567890123L));
        Assert.That(update2.TotalBytesTransferred, Is.EqualTo(9876543210L));
    }

    /// <summary>
    /// A push of TNFS counters does not raise the Econet event: the two pushes have separate wire names.
    /// </summary>
    [Test]
    public async Task TnfsStatsPush_DoesNotRaiseTheEconetEvent()
    {
        int econetEvents = 0;
        TaskCompletionSource<TnfsStatsUpdate> tnfs = new TaskCompletionSource<TnfsStatsUpdate>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _client.EconetStatsUpdated += _ => Interlocked.Increment(ref econetEvents);
        _client.TnfsStatsUpdated += update => tnfs.TrySetResult(update);
        await _client.ConnectAsync();

        IHubContext<ServerHub, IServerHubClient> hub =
            _app.Services.GetRequiredService<IHubContext<ServerHub, IServerHubClient>>();
        await hub.Clients.All.TnfsStats(new TnfsStatsUpdate { ActiveConnections = 1, TotalRequests = 2, TotalBytesTransferred = 3 });

        Task finished = await Task.WhenAny(tnfs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.That(finished, Is.SameAs(tnfs.Task), "the client never raised TnfsStatsUpdated");
        Assert.That(Volatile.Read(ref econetEvents), Is.EqualTo(0));
    }
}
