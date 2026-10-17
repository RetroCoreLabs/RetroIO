//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.Mvc;
using RetroIO.Management.Contracts;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Management.Api.Controllers;

/// <summary>
/// Management API controller that reports Econet file server statistics and sessions.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class EconetController : ControllerBase
{
    private readonly FileServerSessionManager? _sessionManager;

    /// <summary>
    /// Activity log, absent when this process does not run the Econet server.
    /// </summary>
    private readonly IEconetActivityLog? _activityLog;
    private readonly ILogger<EconetController> _logger;

    /// <summary>
    /// Creates the controller.
    /// </summary>
    /// <param name="logger">
    /// The logger for error messages.
    /// </param>
    /// <param name="sessionManager">
    /// The Econet file server session manager, or null when this process does not run the Econet server.
    /// </param>
    /// <param name="activityLog">
    /// The Econet activity log, or null when this process does not run the Econet server.
    /// </param>
    public EconetController(ILogger<EconetController> logger,
                            FileServerSessionManager? sessionManager = null,
                            IEconetActivityLog? activityLog = null)
    {
        _logger = logger;
        _sessionManager = sessionManager;
        _activityLog = activityLog;
    }

    /// <summary>
    /// Returns Econet statistics.
    /// </summary>
    /// <remarks>
    /// Only the active session count is read from the session manager, and it is 0 when there is no session manager. The request count, the byte count and the uptime are fixed at 0, 0 and the text 0s.
    /// </remarks>
    /// <returns>
    /// A response with the statistics, or status 500 with an error response when an exception occurs.
    /// </returns>
    [HttpGet("statistics")]
    public ActionResult<ApiResponse<EconetStatisticsResponse>> GetStatistics()
    {
        try
        {
            EconetStatisticsResponse response = new EconetStatisticsResponse
            {
                ActiveSessions = _sessionManager?.GetActiveSessionCount() ?? 0,
                TotalRequests = 0,
                BytesTransferred = 0,
                Uptime = "0s"
            };

            return Ok(ApiResponse<EconetStatisticsResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Econet statistics");
            return StatusCode(500, ApiResponse<EconetStatisticsResponse>.Fail("Internal server error"));
        }
    }

    /// <summary>
    /// Returns the active Econet file server sessions.
    /// </summary>
    /// <returns>
    /// A response with one entry per active session, or status 500 with an error response when an exception occurs.
    /// </returns>
    [HttpGet("sessions")]
    public ActionResult<ApiResponse<EconetSessionsResponse>> GetSessions()
    {
        try
        {
            List<EconetSessionInfo> sessions = new List<EconetSessionInfo>();
            if (_sessionManager != null)
            {
                FileServerSession[] active = _sessionManager.GetActiveSessions();
                for (int i = 0; i < active.Length; i++)
                {
                    FileServerSession session = active[i];
                    sessions.Add(new EconetSessionInfo
                    {
                        Station = $"{session.ClientNet}.{session.ClientStation}",
                        ClientNet = session.ClientNet.ToString(),
                        Username = session.Username,
                        CurrentDisc = session.CurrentDisc,
                        IsAdmin = session.IsAdmin,
                        CurrentDirectory = session.CurrentDirectoryUnix
                    });
                }
            }

            EconetSessionsResponse response = new EconetSessionsResponse
            {
                Sessions = sessions,
                Total = sessions.Count
            };

            return Ok(ApiResponse<EconetSessionsResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Econet sessions");
            return StatusCode(500, ApiResponse<EconetSessionsResponse>.Fail("Internal server error"));
        }
    }

    /// <summary>
    /// Returns the most recent Econet commands and file transfers.
    /// </summary>
    /// <param name="count">
    /// How many of each to return.
    /// </param>
    /// <returns>
    /// The activity log, or empty lists when this process does not run Econet.
    /// </returns>
    /// <remarks>
    /// The last thing RetroServeUI read straight off an in-process object. Without this endpoint
    /// the UI could not show its Econet monitor against a server in another process, which is the
    /// whole point of it being a client.
    /// </remarks>
    [HttpGet("activity")]
    public ActionResult<ApiResponse<EconetActivityResponse>> GetActivity([FromQuery] int count = 30)
    {
        if (count < 1) count = 1;
        if (count > 500) count = 500;

        EconetActivityResponse response = new EconetActivityResponse();

        if (_activityLog == null)
        {
            // Econet disabled in this host. An empty log is the honest answer, and it keeps the
            // client from having to special-case a 404.
            return Ok(ApiResponse<EconetActivityResponse>.Ok(response));
        }

        IReadOnlyList<EconetCommandEntry> commands = _activityLog.GetRecentCommands(count);

        for (int i = 0; i < commands.Count; i++)
        {
            EconetCommandEntry entry = commands[i];
            response.Commands.Add(new EconetCommandEntryDto
            {
                TimestampUtc = entry.TimestampUtc,
                Station = entry.Station,
                Username = entry.Username,
                Opcode = entry.Opcode,
                Command = entry.Command,
                Detail = entry.Detail,
                Success = entry.Success,
                Result = entry.Result
            });
        }

        IReadOnlyList<EconetFileTransferEntry> transfers = _activityLog.GetRecentTransfers(count);

        for (int i = 0; i < transfers.Count; i++)
        {
            EconetFileTransferEntry entry = transfers[i];
            response.Transfers.Add(new EconetFileTransferEntryDto
            {
                TimestampUtc = entry.TimestampUtc,
                Station = entry.Station,
                Username = entry.Username,
                Direction = entry.DirectionLabel,
                FilePath = entry.FilePath,
                Bytes = entry.Bytes,
                Success = entry.Success,
                Status = entry.Status
            });
        }

        return Ok(ApiResponse<EconetActivityResponse>.Ok(response));
    }
}
