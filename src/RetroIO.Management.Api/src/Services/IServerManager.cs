//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Management.Contracts;

namespace RetroIO.Management.Api.Services;

/// <summary>
/// Starts, stops and reports on the RetroFS server for the management API.
/// </summary>
public interface IServerManager
{
    /// <summary>
    /// Gets the current server status.
    /// </summary>
    /// <returns>
    /// The server status.
    /// </returns>
    ServerStatusResponse GetStatus();
    /// <summary>
    /// Starts the server.
    /// </summary>
    /// <returns>
    /// The result of the start action.
    /// </returns>
    Task<ServerActionResponse> StartAsync();
    /// <summary>
    /// Stops the server.
    /// </summary>
    /// <returns>
    /// The result of the stop action.
    /// </returns>
    Task<ServerActionResponse> StopAsync();
    /// <summary>
    /// Restarts the server.
    /// </summary>
    /// <returns>
    /// The result of the restart action.
    /// </returns>
    Task<ServerActionResponse> RestartAsync();
}
