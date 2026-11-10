//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Net;
using Microsoft.AspNetCore.Mvc;
using RetroIO.Management.Contracts;
using RetroIO.Server.Hosting;
using RetroIO.Server.Hosting.Configuration;
using RetroIO.Storage.Utilities;

namespace RetroIO.Management.Api.Controllers;

/// <summary>
/// RetroServer's own XMSG node: its settings in xmsgnode-config.json.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class XmsgNodeController : ControllerBase
{
    private static readonly string[] LogLevels = { "Trace", "Debug", "Information", "Warning", "Error", "Critical", "None" };

    private readonly string _retroFsPath;

    /// <summary>
    /// Creates the controller.
    /// </summary>
    /// <param name="retroFsPath">
    /// The data folder holding xmsgnode-config.json; null (what DI does) means the real one. Tests pass a temp folder.
    /// </param>
    public XmsgNodeController(string? retroFsPath = null)
    {
        _retroFsPath = retroFsPath ?? PathHelper.GetRetroFSPath();
    }

    /// <summary>
    /// Gets the node settings as stored (what applies at the next start). The program token is never sent back; only
    /// whether one is set.
    /// </summary>
    /// <returns>
    /// The settings.
    /// </returns>
    [HttpGet("settings")]
    public ActionResult<ApiResponse<XmsgNodeSettingsDto>> GetSettings()
    {
        return Ok(ApiResponse<XmsgNodeSettingsDto>.Ok(ToDto(Read())));
    }

    /// <summary>
    /// Writes the node settings to xmsgnode-config.json. They apply after RetroServer restarts.
    /// </summary>
    /// <param name="request">
    /// The settings. <see cref="XmsgNodeSettingsDto.NewProgramToken"/> null keeps the stored token, empty removes it.
    /// </param>
    /// <returns>
    /// The settings as saved, or why they were refused.
    /// </returns>
    [HttpPut("settings")]
    public ActionResult<ApiResponse<XmsgNodeSettingsDto>> PutSettings([FromBody] XmsgNodeSettingsDto request)
    {
        if (request == null)
        {
            return BadRequest(ApiResponse<XmsgNodeSettingsDto>.Fail("No settings were sent."));
        }

        // A system number is 16 bits on the wire (the high half of a magic number).
        if (request.SystemNumber < 0 || request.SystemNumber > 65535 || request.PeerSystemNumber < 0 || request.PeerSystemNumber > 65535)
        {
            return BadRequest(ApiResponse<XmsgNodeSettingsDto>.Fail("A system number is 0 to 65535."));
        }

        if (request.Enabled && (request.SystemNumber == 0 || request.PeerSystemNumber == 0))
        {
            return BadRequest(ApiResponse<XmsgNodeSettingsDto>.Fail("The node needs its own system number and the peer's before it can be switched on."));
        }

        if (request.Enabled && request.SystemNumber == request.PeerSystemNumber)
        {
            return BadRequest(ApiResponse<XmsgNodeSettingsDto>.Fail("The node's system number and the peer's must differ."));
        }

        if (request.ProgramPort < 1 || request.ProgramPort > 65535)
        {
            return BadRequest(ApiResponse<XmsgNodeSettingsDto>.Fail("The program port must be 1 to 65535."));
        }

        if (string.IsNullOrWhiteSpace(request.ProgramBindAddress) || !IPAddress.TryParse(request.ProgramBindAddress.Trim(), out _))
        {
            return BadRequest(ApiResponse<XmsgNodeSettingsDto>.Fail("'" + request.ProgramBindAddress + "' is not an IP address."));
        }

        if (string.IsNullOrWhiteSpace(request.Ethernet))
        {
            return BadRequest(ApiResponse<XmsgNodeSettingsDto>.Fail("The Ethernet segment is needed, for example tcp:127.0.0.1:5010."));
        }

        string? level = null;
        for (int i = 0; i < LogLevels.Length; i++)
        {
            if (string.Equals(LogLevels[i], request.ProgramLogMinimum, StringComparison.OrdinalIgnoreCase))
            {
                level = LogLevels[i];
            }
        }

        if (level == null)
        {
            return BadRequest(ApiResponse<XmsgNodeSettingsDto>.Fail("The program log level must be one of: " + string.Join(", ", LogLevels) + "."));
        }

        XmsgNodeServerConfiguration stored = Read();
        XmsgNodeServerConfiguration config = new XmsgNodeServerConfiguration
        {
            Enabled = request.Enabled,
            SystemNumber = request.SystemNumber,
            PeerSystemNumber = request.PeerSystemNumber,
            Ethernet = request.Ethernet.Trim(),
            ProgramBindAddress = request.ProgramBindAddress.Trim(),
            ProgramPort = request.ProgramPort,
            ProgramToken = request.NewProgramToken == null
                ? stored.ProgramToken
                : (request.NewProgramToken.Length == 0 ? null : request.NewProgramToken),
            ProgramLogMinimum = level,
            Motd = string.IsNullOrWhiteSpace(request.Motd) ? null : request.Motd,
        };

        ProtocolConfigurationLoader.SaveSettingsFile(_retroFsPath, ProtocolConfigurationLoader.XmsgNodeConfigurationFileName, config);
        return Ok(ApiResponse<XmsgNodeSettingsDto>.Ok(ToDto(config)));
    }

    private XmsgNodeServerConfiguration Read()
    {
        return ProtocolConfigurationLoader.ReadSettingsFile(
            _retroFsPath, ProtocolConfigurationLoader.XmsgNodeConfigurationFileName, XmsgNodeServerConfiguration.GetDefault);
    }

    private static XmsgNodeSettingsDto ToDto(XmsgNodeServerConfiguration config)
    {
        return new XmsgNodeSettingsDto
        {
            Enabled = config.Enabled,
            SystemNumber = config.SystemNumber,
            PeerSystemNumber = config.PeerSystemNumber,
            Ethernet = config.Ethernet,
            ProgramBindAddress = config.ProgramBindAddress,
            ProgramPort = config.ProgramPort,
            ProgramTokenSet = !string.IsNullOrEmpty(config.ProgramToken),
            ProgramLogMinimum = config.ProgramLogMinimum,
            Motd = config.Motd,
        };
    }
}
