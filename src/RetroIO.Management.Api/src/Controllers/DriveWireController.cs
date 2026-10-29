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

    /// <summary>
    /// Creates the controller.
    /// </summary>
    /// <param name="server">
    /// The DriveWire server, or null when none runs in this process.
    /// </param>
    public DriveWireController(DriveWireServerService? server = null)
    {
        _server = server;
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
}
