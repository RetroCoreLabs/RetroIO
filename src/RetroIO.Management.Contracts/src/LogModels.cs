//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Management.Contracts;

/// <summary>
/// A list of log entries.
/// </summary>
public class LogListResponse
{
    /// <summary>
    /// Gets or sets the log entries.
    /// </summary>
    public List<LogEntryDto> Entries { get; set; } = new();
    /// <summary>
    /// Gets or sets the number of entries.
    /// </summary>
    public int Total { get; set; }
}

/// <summary>
/// One log entry.
/// </summary>
public class LogEntryDto
{
    /// <summary>
    /// Gets or sets when the entry was written.
    /// </summary>
    public DateTime Timestamp { get; set; }
    /// <summary>
    /// Gets or sets the log level as text.
    /// </summary>
    public string Level { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the log category.
    /// </summary>
    public string Category { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the log message.
    /// </summary>
    public string Message { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the exception text, or null when there is none.
    /// </summary>
    public string? Exception { get; set; }
}

/// <summary>
/// A request that selects log entries.
/// </summary>
public class LogFilterRequest
{
    /// <summary>
    /// Gets or sets the level to match, or null for any.
    /// </summary>
    public string? Level { get; set; }
    /// <summary>
    /// Gets or sets the category to match, or null for any.
    /// </summary>
    public string? Category { get; set; }
    /// <summary>
    /// Gets or sets the earliest time to include, or null for no limit.
    /// </summary>
    public DateTime? StartTime { get; set; }
    /// <summary>
    /// Gets or sets the latest time to include, or null for no limit.
    /// </summary>
    public DateTime? EndTime { get; set; }
    /// <summary>
    /// Gets or sets the number of entries to return. The default is 100.
    /// </summary>
    public int Limit { get; set; } = 100;
}
