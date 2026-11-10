//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.Mvc;
using RetroIO.Management.Contracts;
using RetroIO.Protocols.DriveWire.Drives;
using RetroIO.Protocols.DriveWire.Server;
using RetroIO.Server.Hosting;
using RetroIO.Server.Hosting.Configuration;
using RetroIO.Storage.Utilities;

namespace RetroIO.Management.Api.Controllers;

/// <summary>
/// Reports the counters and drives of the DriveWire server running in this process.
/// </summary>
/// <remarks>
/// The server is optional. When it is not hosted here, both endpoints answer with empty values and IsHosted false rather
/// than an error, as the TNFS endpoints do.
/// </remarks>
[ApiController]
[Route("api/v1/[controller]")]
public class DriveWireController : ControllerBase
{
    private readonly DriveWireServerService? _server;
    private readonly string _retroFsPath;

    /// <summary>
    /// Creates the controller.
    /// </summary>
    /// <param name="server">
    /// The DriveWire server, or null when none runs in this process.
    /// </param>
    /// <param name="retroFsPath">
    /// The data folder holding drivewire-config.json; null (what DI does) means the real one. Tests pass a temp folder.
    /// </param>
    public DriveWireController(DriveWireServerService? server = null, string? retroFsPath = null)
    {
        _server = server;
        _retroFsPath = retroFsPath ?? PathHelper.GetRetroFSPath();
    }

    /// <summary>
    /// Gets the DriveWire server counters.
    /// </summary>
    /// <returns>
    /// The counters.
    /// </returns>
    [HttpGet("statistics")]
    public ActionResult<ApiResponse<DriveWireStatisticsResponse>> GetStatistics()
    {
        DriveWireStatisticsResponse response = new DriveWireStatisticsResponse();
        if (_server != null)
        {
            DriveWireServerStatistics s = _server.Statistics;
            response.IsHosted = true;
            response.Listening = _server.LocalEndPoint?.ToString() ?? string.Empty;
            response.ActiveConnections = s.ActiveConnections;
            response.TotalConnections = s.Connections;
            response.Transactions = s.Transactions;
            response.SectorsRead = s.SectorsRead;
            response.SectorsWritten = s.SectorsWritten;
            response.ReadErrors = s.ReadErrors;
            response.WriteErrors = s.WriteErrors;
            response.ChecksumErrors = s.ChecksumErrors;
            response.Timeouts = s.Timeouts;
            response.UnknownOpCodes = s.UnknownOpCodes;
            response.BytesReceived = s.BytesReceived;
            response.BytesSent = s.BytesSent;
            response.Uptime = FormatUptime(s.Uptime);
        }

        return Ok(ApiResponse<DriveWireStatisticsResponse>.Ok(response));
    }

    /// <summary>
    /// Gets the drives that hold an image.
    /// </summary>
    /// <returns>
    /// The drives, in drive number order.
    /// </returns>
    [HttpGet("drives")]
    public ActionResult<ApiResponse<DriveWireDrivesResponse>> GetDrives()
    {
        DriveWireDrivesResponse response = new DriveWireDrivesResponse();
        if (_server != null)
        {
            DriveWireDriveTable table = _server.Drives;
            for (int i = 0; i < DriveWireDriveTable.DriveCount; i++)
            {
                IDriveWireDrive? drive = table.Get((byte)i);
                if (drive == null)
                {
                    continue;
                }

                DriveWireDriveCounters counters = table.GetCounters((byte)i);
                response.Drives.Add(new DriveWireDriveInfo
                {
                    Drive = i,
                    Path = drive.Name,
                    ReadOnly = drive.IsReadOnly,
                    TotalSectors = drive.TotalSectors,
                    SectorsRead = counters.SectorsRead,
                    SectorsWritten = counters.SectorsWritten,
                    Errors = counters.ReadErrors + counters.WriteErrors
                });
            }
        }

        return Ok(ApiResponse<DriveWireDrivesResponse>.Ok(response));
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

    /// <summary>
    /// Gets the DriveWire settings as stored in drivewire-config.json (what applies at the next start).
    /// </summary>
    /// <returns>
    /// The settings.
    /// </returns>
    [HttpGet("settings")]
    public ActionResult<ApiResponse<DriveWireSettingsDto>> GetSettings()
    {
        DriveWireServerConfiguration config = ProtocolConfigurationLoader.ReadSettingsFile(
            _retroFsPath, ProtocolConfigurationLoader.DriveWireConfigurationFileName, DriveWireServerConfiguration.GetDefault);
        return Ok(ApiResponse<DriveWireSettingsDto>.Ok(ToDto(config)));
    }

    /// <summary>
    /// Writes the DriveWire settings to drivewire-config.json. They apply after RetroServer restarts.
    /// </summary>
    /// <param name="request">
    /// The settings; every field is written.
    /// </param>
    /// <returns>
    /// The settings as saved, or why they were refused.
    /// </returns>
    [HttpPut("settings")]
    public ActionResult<ApiResponse<DriveWireSettingsDto>> PutSettings([FromBody] DriveWireSettingsDto request)
    {
        if (request == null)
        {
            return BadRequest(ApiResponse<DriveWireSettingsDto>.Fail("No settings were sent."));
        }

        if (request.Port < 1 || request.Port > 65535)
        {
            return BadRequest(ApiResponse<DriveWireSettingsDto>.Fail("Port must be 1 to 65535."));
        }

        if (string.IsNullOrWhiteSpace(request.BindAddress) || !System.Net.IPAddress.TryParse(request.BindAddress.Trim(), out _))
        {
            return BadRequest(ApiResponse<DriveWireSettingsDto>.Fail("'" + request.BindAddress + "' is not an IP address."));
        }

        if (request.TransactionTimeoutMilliseconds < 1)
        {
            return BadRequest(ApiResponse<DriveWireSettingsDto>.Fail("The transaction time limit must be at least 1 ms."));
        }

        DriveWireServerConfiguration config = new DriveWireServerConfiguration
        {
            Enabled = request.Enabled,
            Port = request.Port,
            BindAddress = request.BindAddress.Trim(),
            TransactionTimeoutMilliseconds = request.TransactionTimeoutMilliseconds,
            NamedObjectDirectory = string.IsNullOrWhiteSpace(request.NamedObjectDirectory) ? null : request.NamedObjectDirectory.Trim(),
            PrintDirectory = string.IsNullOrWhiteSpace(request.PrintDirectory) ? null : request.PrintDirectory.Trim(),
        };

        List<DriveWireDriveSettingDto> drives = request.Drives ?? new List<DriveWireDriveSettingDto>();
        for (int i = 0; i < drives.Count; i++)
        {
            DriveWireDriveSettingDto drive = drives[i];
            if (drive.Drive < 0 || drive.Drive > 255)
            {
                return BadRequest(ApiResponse<DriveWireSettingsDto>.Fail("Drive " + drive.Drive + ": a drive number is 0 to 255."));
            }

            if (string.IsNullOrWhiteSpace(drive.Path))
            {
                return BadRequest(ApiResponse<DriveWireSettingsDto>.Fail("Drive " + drive.Drive + " has no image file."));
            }

            for (int j = 0; j < i; j++)
            {
                if (drives[j].Drive == drive.Drive)
                {
                    return BadRequest(ApiResponse<DriveWireSettingsDto>.Fail("Drive " + drive.Drive + " is listed twice."));
                }
            }

            config.Drives.Add(new DriveWireDriveConfiguration { Drive = drive.Drive, Path = drive.Path.Trim(), ReadOnly = drive.ReadOnly });
        }

        ProtocolConfigurationLoader.SaveSettingsFile(_retroFsPath, ProtocolConfigurationLoader.DriveWireConfigurationFileName, config);
        return Ok(ApiResponse<DriveWireSettingsDto>.Ok(ToDto(config)));
    }

    private static DriveWireSettingsDto ToDto(DriveWireServerConfiguration config)
    {
        DriveWireSettingsDto dto = new DriveWireSettingsDto
        {
            Enabled = config.Enabled,
            Port = config.Port,
            BindAddress = config.BindAddress,
            TransactionTimeoutMilliseconds = config.TransactionTimeoutMilliseconds,
            NamedObjectDirectory = config.NamedObjectDirectory,
            PrintDirectory = config.PrintDirectory,
        };

        for (int i = 0; i < config.Drives.Count; i++)
        {
            DriveWireDriveConfiguration drive = config.Drives[i];
            dto.Drives.Add(new DriveWireDriveSettingDto { Drive = drive.Drive, Path = drive.Path, ReadOnly = drive.ReadOnly });
        }

        return dto;
    }
}
