//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards
//

namespace RetroIO.Server;

/// <summary>
/// What a host wants from <c>AddRetroServer</c>.
/// </summary>
/// <remarks>
/// Everything here has a working default, so a host that wants the whole server can pass nothing
/// at all. The flags exist for the cases that genuinely differ: a test harness that wants the API
/// without opening real sockets, or a headless deployment that does not want Swagger exposed.
/// </remarks>
public sealed class RetroServerOptions
{
    /// <summary>Serve TNFS. Default true.</summary>
    public bool EnableTnfs { get; set; } = true;

    /// <summary>Serve the Econet AUN file server. Default true.</summary>
    public bool EnableEconet { get; set; } = true;

    /// <summary>Serve the management API and its SignalR hub. Default true.</summary>
    public bool EnableManagementApi { get; set; } = true;

    /// <summary>Register the Swagger/OpenAPI generator. Default true.</summary>
    public bool EnableSwagger { get; set; } = true;

    /// <summary>Print the configuration banners to the console. Default true.</summary>
    /// <remarks>Turned off by hosts with no console, such as a Windows service.</remarks>
    public bool PrintBanners { get; set; } = true;

    /// <summary>How many log entries the management API's in-memory buffer keeps.</summary>
    public int MaxLogEntries { get; set; } = 1000;
}
