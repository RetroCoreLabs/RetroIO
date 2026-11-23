//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroIO.Identity;
using RetroIO.Server.Hosting.Configuration;

namespace RetroIO.Server.DialUp;

/// <summary>
/// Runs the <see cref="DialUpServer"/> for as long as RetroServer runs.
/// </summary>
public sealed class DialUpHostedService : IHostedService, IDisposable
{
    private readonly DialUpServer _server;

    /// <summary>
    /// Initialises the service.
    /// </summary>
    /// <param name="config">
    /// The dial-up settings.
    /// </param>
    /// <param name="idp">
    /// RetroServer's users.
    /// </param>
    /// <param name="logger">
    /// Receives the server's log lines.
    /// </param>
    public DialUpHostedService(DialUpServerConfiguration config, IRetroIDP idp, ILogger<DialUpHostedService> logger)
    {
        _server = new DialUpServer(config, idp, logger);
    }

    /// <summary>
    /// Gets the running server, for its counters.
    /// </summary>
    public DialUpServer Server => _server;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _server.Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _server.Dispose();
    }
}
