//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.SignalR;
using RetroIO.Management.Contracts;

namespace RetroIO.Management.Api.Hubs;

/// <summary>
/// SignalR hub for real-time server updates.
/// </summary>
/// <remarks>
/// Typed on <see cref="IServerHubClient"/>, so every push this hub (and every
/// IHubContext{ServerHub, IServerHubClient} pointed at it) makes is checked by the compiler
/// against the contract the clients deserialise with. An untyped Hub would accept any method
/// name and any payload shape, which is how the push payloads previously drifted from what the
/// UI expected without anything failing to build.
///
/// The hub exposes no callable methods: traffic here is one way, server to client.
/// </remarks>
public class ServerHub : Hub<IServerHubClient>
{
    private readonly ILogger<ServerHub> _logger;

    /// <summary>
    /// Creates the hub.
    /// </summary>
    /// <param name="logger">
    /// The logger for connect and disconnect messages.
    /// </param>
    public ServerHub(ILogger<ServerHub> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Logs the connection id of a client that connected, then runs the base behavior.
    /// </summary>
    /// <returns>
    /// A task that completes when the base method completes.
    /// </returns>
    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Logs the connection id of a client that disconnected, then runs the base behavior.
    /// </summary>
    /// <param name="exception">
    /// The exception that ended the connection, or null.
    /// </param>
    /// <returns>
    /// A task that completes when the base method completes.
    /// </returns>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
