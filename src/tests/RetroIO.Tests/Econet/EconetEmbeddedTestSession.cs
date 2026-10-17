//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading;
using System.Threading.Tasks;
using RetroIO.Identity;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Embedded RetroFS Econet file server (UDP) plus AUN client and activity monitor.
/// </summary>
internal sealed class EconetEmbeddedTestSession : IAsyncDisposable
{
    private readonly EconetUdpTestServer _server;

    public EconetAunTestClient Client { get; }
    public EconetTestMonitor Monitor { get; }
    public IRetroIDP Identity => _server.IdentityProvider;
    public int UdpPort => _server.UdpPort;

    private EconetEmbeddedTestSession(EconetUdpTestServer server, EconetAunTestClient client)
    {
        _server = server;
        Client = client;
        Monitor = new EconetTestMonitor(server.ActivityLog);
    }

    public static async Task<EconetEmbeddedTestSession> StartAsync(
        int serverPort = EconetTestPorts.UnitTestServerUdp,
        int clientPort = 0,
        CancellationToken cancellationToken = default)
    {
        if (clientPort == 0)
            clientPort = EconetTestPorts.UnitTestClientUdp + Random.Shared.Next(1, 1000);

        EconetUdpTestServer server = await EconetUdpTestServer.StartAsync(serverPort, cancellationToken);
        EconetAunTestClient client = new EconetAunTestClient(serverPort: server.UdpPort, clientPort: clientPort);
        return new EconetEmbeddedTestSession(server, client);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _server.DisposeAsync();
    }
}
