//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Management.Contracts;

/// <summary>
/// The status of the server.
/// </summary>
public class ServerStatusResponse
{
    /// <summary>
    /// Gets or sets whether the server is running.
    /// </summary>
    public bool IsRunning { get; set; }
    /// <summary>
    /// Gets or sets the uptime as text.
    /// </summary>
    public string Uptime { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the version text. The default is <c>1.0.0</c>.
    /// </summary>
    public string Version { get; set; } = "1.0.0";
    /// <summary>
    /// Gets or sets when the server started, or null when it has not.
    /// </summary>
    public DateTime? StartedAt { get; set; }
}

/// <summary>
/// The result of a server action such as start or stop.
/// </summary>
public class ServerActionResponse
{
    /// <summary>
    /// Gets or sets whether the action succeeded.
    /// </summary>
    public bool Success { get; set; }
    /// <summary>
    /// Gets or sets the result text.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}
