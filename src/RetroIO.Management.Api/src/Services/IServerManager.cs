//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Management.Contracts;

namespace RetroIO.Management.Api.Services;

public interface IServerManager
{
    ServerStatusResponse GetStatus();
    Task<ServerActionResponse> StartAsync();
    Task<ServerActionResponse> StopAsync();
    Task<ServerActionResponse> RestartAsync();
}
