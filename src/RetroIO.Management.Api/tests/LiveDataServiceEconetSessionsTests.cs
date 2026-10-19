//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetroIO.Identity;
using RetroIO.Management.Api.Hubs;
using RetroIO.Management.Api.Services;
using RetroIO.Management.Contracts;
using RetroIO.Protocols.Econet.FileServer;
using RetroIO.Protocols.Econet.FileServer.Discs;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Management.Api.Tests;

/// <summary>
/// Tests that <see cref="LiveDataService"/> pushes the number of users logged in to the Econet
/// server, read from the real session manager.
/// </summary>
[TestFixture]
public class LiveDataServiceEconetSessionsTests
{
    private string _dir = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "retroio-mgmt-econet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // A temp folder left behind is harmless.
        }
    }

    // A session manager over a real folder with one user who may log in with an empty password.
    private FileServerSessionManager BuildSessionManager()
    {
        string root = Path.Combine(_dir, "econet-root");
        LocalFileSystemBackend backend = new LocalFileSystemBackend();
        FileServerBootstrap.EnsureLayout(backend, root, "RetroFS");

        JsonRetroIDP idp = new JsonRetroIDP(Path.Combine(_dir, "users.json"), Path.Combine(_dir, "groups.json"));
        RetroUser user = new RetroUser
        {
            UserId = Guid.NewGuid(),
            Username = "USER",
            DisplayName = "Normal User",
            IsActive = true,
            IsAdmin = false,
            AllowEconet = true,
            AllowTnfs = false,
        };
        user.Metadata[EconetMetadataKeys.AllowEmptyPassword] = "true";
        user.Metadata[EconetMetadataKeys.DiscMask] = "65535";
        idp.CreateUserAsync(user).AsTask().GetAwaiter().GetResult();
        idp.SaveAsync().AsTask().GetAwaiter().GetResult();

        EconetUserDirectory users = new EconetUserDirectory(idp);
        DiscCatalog discs = new DiscCatalog(backend, root, "RetroFS");
        return new FileServerSessionManager(backend, users, discs, root);
    }

    private static LiveDataService Build(List<EconetStatsUpdate> pushed, FileServerSessionManager sessions)
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

        return new LiveDataService(hub.Object, new InMemoryLogProvider(maxEntries: 3), manager.Object,
            NullLogger<LiveDataService>.Instance, null, null, sessions, null);
    }

    /// <summary>
    /// One user is logged in, so the push says one active session.
    /// </summary>
    [Test]
    public async Task PushesTheNumberOfLoggedInUsers()
    {
        FileServerSessionManager sessions = BuildSessionManager();
        bool loggedIn = sessions.TryLogin(1, 1, "USER", string.Empty, out _, out _, out string message);
        Assert.That(loggedIn, Is.True, message);
        Assert.That(sessions.GetActiveSessionCount(), Is.EqualTo(1));

        List<EconetStatsUpdate> pushed = new List<EconetStatsUpdate>();
        LiveDataService service = Build(pushed, sessions);
        try
        {
            await service.StartAsync(CancellationToken.None);
            DateTime deadline = DateTime.UtcNow.AddSeconds(6);
            while (true)
            {
                lock (pushed)
                {
                    if (pushed.Count > 0)
                    {
                        Assert.That(pushed[0].ActiveSessions, Is.EqualTo(1));
                        return;
                    }
                }

                if (DateTime.UtcNow > deadline)
                    Assert.Fail("Timed out waiting for the Econet statistics push");
                await Task.Delay(50);
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }
}
