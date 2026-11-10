//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.Mvc;
using RetroIO.Management.Contracts;
using RetroIO.Server.Hosting;
using RetroIO.Server.Hosting.Configuration;
using RetroIO.Storage.Utilities;

namespace RetroIO.Management.Api.Controllers;

/// <summary>
/// The traffic limits and the audit log settings in security-config.json.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class SecurityController : ControllerBase
{
    private readonly string _retroFsPath;

    /// <summary>
    /// Creates the controller.
    /// </summary>
    /// <param name="retroFsPath">
    /// The data folder holding security-config.json; null (what DI does) means the real one. Tests pass a temp folder.
    /// </param>
    public SecurityController(string? retroFsPath = null)
    {
        _retroFsPath = retroFsPath ?? PathHelper.GetRetroFSPath();
    }

    /// <summary>
    /// Gets the settings as stored (what applies at the next start).
    /// </summary>
    /// <returns>
    /// The settings.
    /// </returns>
    [HttpGet("settings")]
    public ActionResult<ApiResponse<SecuritySettingsDto>> GetSettings()
    {
        return Ok(ApiResponse<SecuritySettingsDto>.Ok(ToDto(Read())));
    }

    /// <summary>
    /// Writes the settings to security-config.json. They apply after RetroServer restarts.
    /// </summary>
    /// <param name="request">
    /// Every setting.
    /// </param>
    /// <returns>
    /// The settings as saved, or why they were refused.
    /// </returns>
    [HttpPut("settings")]
    public ActionResult<ApiResponse<SecuritySettingsDto>> PutSettings([FromBody] SecuritySettingsDto request)
    {
        if (request == null)
        {
            return BadRequest(ApiResponse<SecuritySettingsDto>.Fail("No settings were sent."));
        }

        if (!LimitOk(request.PerUserRequestsPerSecond) || !LimitOk(request.PerUserMegabytesPerHour)
            || !LimitOk(request.PerIpRequestsPerSecond) || !LimitOk(request.PerIpMegabytesPerHour)
            || !LimitOk(request.ServerRequestsPerSecond) || !LimitOk(request.ServerMegabytesPerHour))
        {
            return BadRequest(ApiResponse<SecuritySettingsDto>.Fail("A limit is at least 1, or empty for no limit."));
        }

        if (request.RateLimitSaveIntervalSeconds < 1 || request.AuditFlushIntervalSeconds < 1 || request.AuditBatchSize < 1)
        {
            return BadRequest(ApiResponse<SecuritySettingsDto>.Fail("The save interval, the audit batch size and the audit interval must be at least 1."));
        }

        SecurityServerConfiguration config = new SecurityServerConfiguration
        {
            PerUserRequestsPerSecond = request.PerUserRequestsPerSecond,
            PerUserMegabytesPerHour = request.PerUserMegabytesPerHour,
            SkipLimitsForAdmins = request.SkipLimitsForAdmins,
            PerIpRequestsPerSecond = request.PerIpRequestsPerSecond,
            PerIpMegabytesPerHour = request.PerIpMegabytesPerHour,
            ServerRequestsPerSecond = request.ServerRequestsPerSecond,
            ServerMegabytesPerHour = request.ServerMegabytesPerHour,
            RateLimitSaveIntervalSeconds = request.RateLimitSaveIntervalSeconds,
            RestoreRateLimitStateOnStartup = request.RestoreRateLimitStateOnStartup,
            AuditBatchSize = request.AuditBatchSize,
            AuditFlushIntervalSeconds = request.AuditFlushIntervalSeconds,
        };

        ProtocolConfigurationLoader.SaveSettingsFile(_retroFsPath, ProtocolConfigurationLoader.SecurityConfigurationFileName, config);
        return Ok(ApiResponse<SecuritySettingsDto>.Ok(ToDto(config)));
    }

    private static bool LimitOk(int? limit)
    {
        return !limit.HasValue || limit.Value >= 1;
    }

    private SecurityServerConfiguration Read()
    {
        return ProtocolConfigurationLoader.ReadSettingsFile(
            _retroFsPath, ProtocolConfigurationLoader.SecurityConfigurationFileName, SecurityServerConfiguration.GetDefault);
    }

    private static SecuritySettingsDto ToDto(SecurityServerConfiguration config)
    {
        return new SecuritySettingsDto
        {
            PerUserRequestsPerSecond = config.PerUserRequestsPerSecond,
            PerUserMegabytesPerHour = config.PerUserMegabytesPerHour,
            SkipLimitsForAdmins = config.SkipLimitsForAdmins,
            PerIpRequestsPerSecond = config.PerIpRequestsPerSecond,
            PerIpMegabytesPerHour = config.PerIpMegabytesPerHour,
            ServerRequestsPerSecond = config.ServerRequestsPerSecond,
            ServerMegabytesPerHour = config.ServerMegabytesPerHour,
            RateLimitSaveIntervalSeconds = config.RateLimitSaveIntervalSeconds,
            RestoreRateLimitStateOnStartup = config.RestoreRateLimitStateOnStartup,
            AuditBatchSize = config.AuditBatchSize,
            AuditFlushIntervalSeconds = config.AuditFlushIntervalSeconds,
        };
    }
}
