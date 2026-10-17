//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.Mvc;
using RetroIO.Management.Api.Services;
using RetroIO.Management.Contracts;

namespace RetroIO.Management.Api.Controllers;

/// <summary>
/// Management API controller that reports the server status and starts, stops and restarts the server.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class ServerController : ControllerBase
{
    private readonly IServerManager _serverManager;
    private readonly ILogger<ServerController> _logger;

    /// <summary>
    /// Creates the controller.
    /// </summary>
    /// <param name="serverManager">
    /// The server manager that does the work.
    /// </param>
    /// <param name="logger">
    /// The logger for error messages.
    /// </param>
    public ServerController(IServerManager serverManager, ILogger<ServerController> logger)
    {
        _serverManager = serverManager;
        _logger = logger;
    }

    /// <summary>
    /// Get server status
    /// </summary>
    [HttpGet("status")]
    public ActionResult<ApiResponse<ServerStatusResponse>> GetStatus()
    {
        try
        {
            var status = _serverManager.GetStatus();
            return Ok(ApiResponse<ServerStatusResponse>.Ok(status));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting server status");
            return StatusCode(500, ApiResponse<ServerStatusResponse>.Fail("Internal server error"));
        }
    }

    /// <summary>
    /// Start the RetroFS server
    /// </summary>
    [HttpPost("start")]
    public async Task<ActionResult<ApiResponse<ServerActionResponse>>> StartServer()
    {
        try
        {
            var result = await _serverManager.StartAsync();
            return Ok(ApiResponse<ServerActionResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting server");
            return StatusCode(500, ApiResponse<ServerActionResponse>.Fail("Failed to start server"));
        }
    }

    /// <summary>
    /// Stop the RetroFS server
    /// </summary>
    [HttpPost("stop")]
    public async Task<ActionResult<ApiResponse<ServerActionResponse>>> StopServer()
    {
        try
        {
            var result = await _serverManager.StopAsync();
            return Ok(ApiResponse<ServerActionResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping server");
            return StatusCode(500, ApiResponse<ServerActionResponse>.Fail("Failed to stop server"));
        }
    }

    /// <summary>
    /// Restart the RetroFS server
    /// </summary>
    [HttpPost("restart")]
    public async Task<ActionResult<ApiResponse<ServerActionResponse>>> RestartServer()
    {
        try
        {
            var result = await _serverManager.RestartAsync();
            return Ok(ApiResponse<ServerActionResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restarting server");
            return StatusCode(500, ApiResponse<ServerActionResponse>.Fail("Failed to restart server"));
        }
    }
}
