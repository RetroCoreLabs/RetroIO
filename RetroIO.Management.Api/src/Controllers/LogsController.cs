//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using Microsoft.AspNetCore.Mvc;
using RetroIO.Management.Contracts;
using RetroIO.Management.Api.Services;

namespace RetroIO.Management.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class LogsController : ControllerBase
{
    private readonly InMemoryLogProvider _logProvider;
    private readonly ILogger<LogsController> _logger;

    public LogsController(InMemoryLogProvider logProvider, ILogger<LogsController> logger)
    {
        _logProvider = logProvider;
        _logger = logger;
    }

    /// <summary>
    /// Get recent log entries
    /// </summary>
    [HttpGet]
    public ActionResult<ApiResponse<LogListResponse>> GetLogs([FromQuery] int limit = 100)
    {
        try
        {
            var entries = _logProvider.GetLogEntries();
            var limitedEntries = entries.Skip(Math.Max(0, entries.Count - limit)).ToArray();

            var response = new LogListResponse
            {
                Entries = new List<LogEntryDto>(limitedEntries),
                Total = limitedEntries.Length
            };

            return Ok(ApiResponse<LogListResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving logs");
            return StatusCode(500, ApiResponse<LogListResponse>.Fail("Failed to retrieve logs"));
        }
    }

    /// <summary>
    /// Get filtered log entries
    /// </summary>
    [HttpPost("filter")]
    public ActionResult<ApiResponse<LogListResponse>> FilterLogs([FromBody] LogFilterRequest request)
    {
        try
        {
            var entries = _logProvider.GetLogEntries();
            var filteredEntries = new List<LogEntryDto>();

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                bool match = true;

                if (!string.IsNullOrEmpty(request.Level) && entry.Level != request.Level)
                {
                    match = false;
                }

                if (!string.IsNullOrEmpty(request.Category) && !entry.Category.Contains(request.Category, StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                }

                if (request.StartTime.HasValue && entry.Timestamp < request.StartTime.Value)
                {
                    match = false;
                }

                if (request.EndTime.HasValue && entry.Timestamp > request.EndTime.Value)
                {
                    match = false;
                }

                if (match)
                {
                    filteredEntries.Add(entry);
                }
            }

            // Apply limit
            var limitedEntries = filteredEntries.Skip(Math.Max(0, filteredEntries.Count - request.Limit)).ToArray();

            var logDtos = new List<LogEntryDto>();
            for (int i = 0; i < limitedEntries.Length; i++)
            {
                var entry = limitedEntries[i];
                logDtos.Add(new LogEntryDto
                {
                    Timestamp = entry.Timestamp,
                    Level = entry.Level,
                    Category = entry.Category,
                    Message = entry.Message,
                    Exception = entry.Exception
                });
            }

            var response = new LogListResponse
            {
                Entries = logDtos,
                Total = logDtos.Count
            };

            return Ok(ApiResponse<LogListResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error filtering logs");
            return StatusCode(500, ApiResponse<LogListResponse>.Fail("Failed to filter logs"));
        }
    }

    /// <summary>
    /// Clear all log entries
    /// </summary>
    [HttpDelete]
    public ActionResult<ApiResponse<object>> ClearLogs()
    {
        try
        {
            _logProvider.Clear();
            return Ok(ApiResponse<object>.Ok(null, "Logs cleared successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing logs");
            return StatusCode(500, ApiResponse<object>.Fail("Failed to clear logs"));
        }
    }
}
