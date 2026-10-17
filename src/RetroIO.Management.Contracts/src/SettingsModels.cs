//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Management.Contracts;

/// <summary>
/// The TNFS server settings as the management API returns them.
/// </summary>
public class TnfsSettingsDto
{
    /// <summary>
    /// Gets or sets the UDP port.
    /// </summary>
    public int UdpPort { get; set; }
    /// <summary>
    /// Gets or sets the TCP port.
    /// </summary>
    public int TcpPort { get; set; }
    /// <summary>
    /// Gets or sets whether the UDP transport is enabled.
    /// </summary>
    public bool UdpEnabled { get; set; }
    /// <summary>
    /// Gets or sets whether the TCP transport is enabled.
    /// </summary>
    public bool TcpEnabled { get; set; }
    /// <summary>
    /// Gets or sets the maximum number of connections.
    /// </summary>
    public int MaxConnections { get; set; }
    /// <summary>
    /// Gets or sets the session timeout in seconds.
    /// </summary>
    public int SessionTimeoutSeconds { get; set; }
    /// <summary>
    /// Gets or sets the root folder that is served.
    /// </summary>
    public string RootPath { get; set; } = string.Empty;
}

/// <summary>
/// A request to change TNFS server settings. A property left null is not changed.
/// </summary>
public class UpdateTnfsSettingsRequest
{
    /// <summary>
    /// Gets or sets the new UDP port, or null to keep the current one.
    /// </summary>
    public int? UdpPort { get; set; }
    /// <summary>
    /// Gets or sets the new TCP port, or null to keep the current one.
    /// </summary>
    public int? TcpPort { get; set; }
    /// <summary>
    /// Gets or sets whether the UDP transport is enabled, or null to keep the current value.
    /// </summary>
    public bool? UdpEnabled { get; set; }
    /// <summary>
    /// Gets or sets whether the TCP transport is enabled, or null to keep the current value.
    /// </summary>
    public bool? TcpEnabled { get; set; }
    /// <summary>
    /// Gets or sets the new maximum number of connections, or null to keep the current one.
    /// </summary>
    public int? MaxConnections { get; set; }
    /// <summary>
    /// Gets or sets the new session timeout in seconds, or null to keep the current one.
    /// </summary>
    public int? SessionTimeoutSeconds { get; set; }
    /// <summary>
    /// Gets or sets the new root folder, or null to keep the current one.
    /// </summary>
    public string? RootPath { get; set; }
}

/// <summary>
/// The general server settings as the management API returns them.
/// </summary>
public class ServerSettingsDto
{
    /// <summary>
    /// Gets or sets whether the server starts automatically.
    /// </summary>
    public bool AutoStart { get; set; }
    /// <summary>
    /// Gets or sets the log level as text. The default is <c>Information</c>.
    /// </summary>
    public string LogLevel { get; set; } = "Information";
    /// <summary>
    /// Gets or sets the maximum number of log entries kept.
    /// </summary>
    public int MaxLogEntries { get; set; }
}

/// <summary>
/// A request to change general server settings. A property left null is not changed.
/// </summary>
public class UpdateServerSettingsRequest
{
    /// <summary>
    /// Gets or sets whether the server starts automatically, or null to keep the current value.
    /// </summary>
    public bool? AutoStart { get; set; }
    /// <summary>
    /// Gets or sets the new log level, or null to keep the current one.
    /// </summary>
    public string? LogLevel { get; set; }
    /// <summary>
    /// Gets or sets the new maximum number of log entries, or null to keep the current one.
    /// </summary>
    public int? MaxLogEntries { get; set; }
}
