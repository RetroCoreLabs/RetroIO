//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using Microsoft.AspNetCore.Mvc;
using RetroIO.Management.Contracts;
using RetroIO.Protocols.Tnfs.Server;

namespace RetroIO.Management.Api.Controllers;

/// <summary>
/// Reports statistics/sessions for a live TNFS server.
/// </summary>
/// <remarks>
/// <b>Known architectural limitation (2026-07-04):</b> <see cref="SessionManager"/> and
/// <see cref="TnfsServerStatistics"/> are only ever registered by
/// <c>TnfsServerExtensions.AddTnfsServer</c>, which is called solely by the standalone
/// <c>RetroServer</c> process — never by <c>RetroIO.Management.Api</c>'s own <c>Program.cs</c>,
/// and the two are launched as separate processes (see <c>run-ui-with-api.bat</c>, which starts
/// only the Management API + RetroServeUI, never RetroServer). That means in every documented
/// launch path today, both dependencies below are null and this controller reports "no live
/// server" honestly (0 sessions, 0 traffic) rather than fabricated data — it does NOT mean the
/// underlying counters are fake; <see cref="TnfsServerStatistics"/> is real, live instrumentation
/// wired into <c>TnfsServerService.HandleMessageAsync</c>, ready to report real numbers the moment
/// this API is hosted in the same process as an active TNFS server.
/// </remarks>
[ApiController]
[Route("api/v1/[controller]")]
public class TnfsController : ControllerBase
{
    private readonly SessionManager? _sessionManager;
    private readonly TnfsServerStatistics? _statistics;
    private readonly ILogger<TnfsController> _logger;

    public TnfsController(
        ILogger<TnfsController> logger,
        SessionManager? sessionManager = null,
        TnfsServerStatistics? statistics = null)
    {
        _logger = logger;
        _sessionManager = sessionManager;
        _statistics = statistics;
    }

    /// <summary>
    /// Get TNFS server statistics
    /// </summary>
    [HttpGet("statistics")]
    public ActionResult<ApiResponse<TnfsStatisticsResponse>> GetStatistics()
    {
        try
        {
            var sessionStats = _sessionManager?.GetStatistics();

            var response = new TnfsStatisticsResponse
            {
                ActiveConnections = sessionStats?.TotalSessions ?? 0,
                TotalRequests = _statistics?.TotalRequests ?? 0,
                BytesTransferred = _statistics?.BytesTransferred ?? 0,
                FilesTransferred = _statistics?.FilesTransferred ?? 0,
                Uptime = _statistics != null ? FormatUptime(_statistics.Uptime) : "0s",
                RequestsPerSecond = _statistics?.RequestsPerSecond ?? 0,
                MbPerSecond = _statistics?.MbPerSecond ?? 0
            };

            return Ok(ApiResponse<TnfsStatisticsResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting TNFS statistics");
            return StatusCode(500, ApiResponse<TnfsStatisticsResponse>.Fail("Internal server error"));
        }
    }

    /// <summary>
    /// Get active TNFS sessions
    /// </summary>
    [HttpGet("sessions")]
    public ActionResult<ApiResponse<TnfsSessionsResponse>> GetSessions()
    {
        try
        {
            var sessions = new List<TnfsSessionInfo>();

            if (_sessionManager != null)
            {
                var allSessions = _sessionManager.GetAllSessions();
                for (int i = 0; i < allSessions.Count; i++)
                {
                    var session = allSessions[i];
                    int openFiles = 0;
                    for (int f = 0; f < session.FileDescriptors.Length; f++)
                    {
                        if (session.FileDescriptors[f] != null)
                            openFiles++;
                    }

                    int openDirs = 0;
                    for (int d = 0; d < session.DirectoryHandles.Length; d++)
                    {
                        if (session.DirectoryHandles[d].IsOpen)
                            openDirs++;
                    }

                    sessions.Add(new TnfsSessionInfo
                    {
                        SessionId = $"0x{session.SessionId:X4}",
                        ClientIP = session.ClientIP.ToString(),
                        Username = session.Username,
                        Transport = session.TcpConnectionId == 0 ? "UDP" : "TCP",
                        ConnectedAt = session.CreatedAt,
                        LastActivity = session.LastContact,
                        OpenFiles = openFiles,
                        OpenDirectories = openDirs
                    });
                }
            }

            var response = new TnfsSessionsResponse
            {
                Sessions = sessions,
                Total = sessions.Count
            };

            return Ok(ApiResponse<TnfsSessionsResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting TNFS sessions");
            return StatusCode(500, ApiResponse<TnfsSessionsResponse>.Fail("Internal server error"));
        }
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
            return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
        if (uptime.TotalHours >= 1)
            return $"{(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s";
        if (uptime.TotalMinutes >= 1)
            return $"{(int)uptime.TotalMinutes}m {uptime.Seconds}s";
        return $"{uptime.Seconds}s";
    }
}
