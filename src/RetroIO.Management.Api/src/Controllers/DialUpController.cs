//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.Mvc;
using RetroIO.Management.Contracts;
using RetroIO.Server.Hosting;

namespace RetroIO.Management.Api.Controllers;

/// <summary>
/// The dial-up ISP's counters. When the server does not run it, the answer says "not hosted" with
/// zeros rather than an error, the same as the hub controller.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class DialUpController : ControllerBase
{
    private readonly IDialUpStatistics? _server;

    /// <summary>
    /// Initialises the controller.
    /// </summary>
    /// <param name="server">
    /// The running dial-up server, or null when this server does not run it.
    /// </param>
    public DialUpController(IDialUpStatistics? server = null)
    {
        _server = server;
    }

    /// <summary>
    /// Gets the dial-up ISP's state and counters.
    /// </summary>
    /// <returns>
    /// The status.
    /// </returns>
    [HttpGet("status")]
    public ActionResult<ApiResponse<DialUpStatusResponse>> GetStatus()
    {
        DialUpStatusResponse response = new DialUpStatusResponse();
        if (_server != null)
        {
            response.IsHosted = true;
            response.Port = _server.ListeningPort;
            response.CallsAnswered = _server.CallsAnswered;
            response.CallsActive = _server.CallsActive;
            response.CallsRefusedFull = _server.CallsRefusedFull;
            response.LoginsRefused = _server.LoginsRefused;
            response.BytesFromCallers = _server.BytesFromCallers;
            response.BytesToCallers = _server.BytesToCallers;
        }

        return Ok(ApiResponse<DialUpStatusResponse>.Ok(response));
    }
}
