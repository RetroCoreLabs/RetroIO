//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Server.Hosting.Configuration;

/// <summary>
/// The traffic limits and the security audit log settings, stored in security-config.json and read at start.
/// </summary>
/// <remarks>
/// Until 2026-11-10 every one of these numbers was written into <c>AddRetroSecurity</c> and could only be changed by
/// a rebuild. The defaults are those same numbers, so a server without the file behaves exactly as before.
/// A limit of null means no limit.
/// </remarks>
public sealed class SecurityServerConfiguration
{
    /// <summary>
    /// Gets or sets the most requests one user may make per second (default 200; null = no limit).
    /// </summary>
    public int? PerUserRequestsPerSecond { get; set; } = 200;

    /// <summary>
    /// Gets or sets the most megabytes one user may move per hour (default 100; null = no limit).
    /// </summary>
    public int? PerUserMegabytesPerHour { get; set; } = 100;

    /// <summary>
    /// Gets or sets whether administrators are exempt from the per-user limits (default true).
    /// </summary>
    public bool SkipLimitsForAdmins { get; set; } = true;

    /// <summary>
    /// Gets or sets the most requests one IP address may make per second (default 200; null = no limit).
    /// </summary>
    public int? PerIpRequestsPerSecond { get; set; } = 200;

    /// <summary>
    /// Gets or sets the most megabytes one IP address may move per hour (default 50; null = no limit).
    /// </summary>
    public int? PerIpMegabytesPerHour { get; set; } = 50;

    /// <summary>
    /// Gets or sets the most requests the whole server takes per second (default 1000; null = no limit).
    /// </summary>
    public int? ServerRequestsPerSecond { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the most megabytes the whole server moves per hour (default 10000; null = no limit).
    /// </summary>
    public int? ServerMegabytesPerHour { get; set; } = 10000;

    /// <summary>
    /// Gets or sets how often the limit counters are saved to ratelimit-state.json, in seconds (default 60).
    /// </summary>
    public int RateLimitSaveIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets whether the saved counters are read back at start, so a restart does not reset them (default true).
    /// </summary>
    public bool RestoreRateLimitStateOnStartup { get; set; } = true;

    /// <summary>
    /// Gets or sets how many audit entries are held before they are written to the audit-logs folder (default 1000).
    /// </summary>
    public int AuditBatchSize { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the longest time audit entries are held before they are written, in seconds (default 5).
    /// </summary>
    public int AuditFlushIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Creates the defaults (the numbers that were built in before the file existed).
    /// </summary>
    /// <returns>
    /// A new configuration.
    /// </returns>
    public static SecurityServerConfiguration GetDefault()
    {
        return new SecurityServerConfiguration();
    }
}
