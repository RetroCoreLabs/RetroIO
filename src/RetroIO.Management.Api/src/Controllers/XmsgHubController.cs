//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Net;
using Microsoft.AspNetCore.Mvc;
using RetroIO.Management.Contracts;
using RetroIO.Protocols.Xmsg.Hub;
using RetroIO.Protocols.Xmsg.Hub.Server;
using RetroIO.Server.Hosting;
using RetroIO.Server.Hosting.Configuration;
using RetroIO.Storage.Utilities;

namespace RetroIO.Management.Api.Controllers;

/// <summary>
/// The COSMOS Ethernet hub: its state, members, log lines, the frames it carried (as JSON or as a pcap file) and its
/// settings.
/// </summary>
/// <remarks>
/// When the server does not host the hub (switched off in xmsghub-config.json) every read answers "not hosted" with empty
/// lists rather than an error, the same as the DriveWire controller. The settings can be read and written either way, so
/// the hub can be switched on from the admin UI.
/// </remarks>
[ApiController]
[Route("api/v1/[controller]")]
public class XmsgHubController : ControllerBase
{
    /// <summary>
    /// The most frames or log lines one request returns.
    /// </summary>
    public const int MaxPage = 5000;

    private readonly XmsgHubService? _hub;
    private readonly string _retroFsPath;

    /// <summary>
    /// Initialises the controller.
    /// </summary>
    /// <param name="hub">
    /// The hosted hub, or null when this server does not host it.
    /// </param>
    /// <param name="retroFsPath">
    /// The data folder that holds xmsghub-config.json; null uses the server's RetroFS folder. Tests pass a temporary one.
    /// </param>
    public XmsgHubController(XmsgHubService? hub = null, string? retroFsPath = null)
    {
        _hub = hub;
        _retroFsPath = retroFsPath ?? PathHelper.GetRetroFSPath();
    }

    /// <summary>
    /// Gets the hub's state and counters.
    /// </summary>
    /// <returns>
    /// The status.
    /// </returns>
    [HttpGet("status")]
    public ActionResult<ApiResponse<XmsgHubStatusResponse>> GetStatus()
    {
        XmsgHubStatusResponse response = new XmsgHubStatusResponse();
        if (_hub != null)
        {
            response.IsHosted = true;
            response.State = _hub.State.ToString();
            response.FailureReason = _hub.FailureReason;
            response.Listening = _hub.LocalEndPoint?.ToString() ?? string.Empty;
            response.Uplink = _hub.Options.Uplink ?? string.Empty;
            response.CaptureFile = _hub.Options.CaptureFile ?? string.Empty;
            response.FrameBufferSize = _hub.Frames.Capacity;
            response.LastFrame = _hub.Frames.LastSequence;
            response.LastLogLine = _hub.LogLines.LastSequence;
            if (_hub.StartedUtc is DateTime started)
            {
                response.Uptime = FormatUptime(DateTime.UtcNow - started);
            }

            HubServer? server = _hub.Hub;
            if (server != null)
            {
                response.Members = server.MemberCount;
                response.Machines = server.MachineMemberCount;
                response.FramesIn = server.FramesIn;
                response.FramesForwarded = server.FramesForwarded;
                response.FramesDroppedSlow = server.FramesDroppedSlow;
                response.FramesDroppedLoop = server.FramesDroppedLoop;
                response.FramesDroppedTtl = server.FramesDroppedTtl;
                response.FramesCaptured = server.FramesCaptured;
            }
        }

        return Ok(ApiResponse<XmsgHubStatusResponse>.Ok(response));
    }

    /// <summary>
    /// Gets the members joined now.
    /// </summary>
    /// <returns>
    /// The members.
    /// </returns>
    [HttpGet("members")]
    public ActionResult<ApiResponse<XmsgHubMembersResponse>> GetMembers()
    {
        XmsgHubMembersResponse response = new XmsgHubMembersResponse();
        if (_hub != null)
        {
            HubMemberInfo[] members = _hub.GetMembers();
            for (int i = 0; i < members.Length; i++)
            {
                HubMemberInfo m = members[i];
                response.Members.Add(new XmsgHubMemberInfo
                {
                    Name = m.Name,
                    Kind = m.IsUplink ? "Uplink" : (m.IsHub ? "Hub" : "Machine"),
                    JoinedUtc = m.JoinedUtc,
                    FramesFromMember = m.FramesFromMember,
                    FramesToMember = m.FramesToMember,
                    FramesDropped = m.FramesDropped,
                    QueueLength = m.QueueLength,
                });
            }
        }

        return Ok(ApiResponse<XmsgHubMembersResponse>.Ok(response));
    }

    /// <summary>
    /// Gets the hub's log lines after a given line number.
    /// </summary>
    /// <param name="after">
    /// The last line number the caller has; 0 for all lines still kept.
    /// </param>
    /// <param name="limit">
    /// The most lines to return, oldest first; at most <see cref="MaxPage"/>.
    /// </param>
    /// <returns>
    /// The lines.
    /// </returns>
    [HttpGet("log")]
    public ActionResult<ApiResponse<XmsgHubLogResponse>> GetLog([FromQuery] long after = 0, [FromQuery] int limit = 1000)
    {
        XmsgHubLogResponse response = new XmsgHubLogResponse();
        if (_hub != null)
        {
            HubLogLine[] lines = _hub.LogLines.GetAfter(after, Clamp(limit));
            for (int i = 0; i < lines.Length; i++)
            {
                response.Lines.Add(new XmsgHubLogLine { Sequence = lines[i].Sequence, TimeUtc = lines[i].TimeUtc, Message = lines[i].Message });
            }

            response.LastLine = _hub.LogLines.LastSequence;
        }

        return Ok(ApiResponse<XmsgHubLogResponse>.Ok(response));
    }

    /// <summary>
    /// Gets the frames the hub carried after a given frame number, with their bytes as hexadecimal.
    /// </summary>
    /// <param name="after">
    /// The last frame number the caller has; 0 for all frames still kept.
    /// </param>
    /// <param name="limit">
    /// The most frames to return, oldest first; at most <see cref="MaxPage"/>.
    /// </param>
    /// <param name="system">
    /// Only frames from or to this system number; 0 for all.
    /// </param>
    /// <returns>
    /// The frames.
    /// </returns>
    [HttpGet("frames")]
    public ActionResult<ApiResponse<XmsgHubFramesResponse>> GetFrames(
        [FromQuery] long after = 0,
        [FromQuery] int limit = 500,
        [FromQuery] int system = 0)
    {
        XmsgHubFramesResponse response = new XmsgHubFramesResponse();
        if (_hub != null)
        {
            HubFrameRecord[] records = Select(_hub.Frames.GetAfter(after, int.MaxValue), system, Clamp(limit));
            for (int i = 0; i < records.Length; i++)
            {
                HubFrameRecord r = records[i];
                response.Frames.Add(new XmsgHubFrameInfo
                {
                    Sequence = r.Sequence,
                    TimeUtc = r.TimeUtc,
                    Member = r.Member,
                    SourceSystem = r.SourceSystem,
                    DestinationSystem = r.DestinationSystem,
                    IsNdFrame = r.IsNdFrame,
                    Length = r.Frame.Length,
                    Outcome = r.Outcome.ToString(),
                    CopiesSent = r.CopiesSent,
                    CopiesDropped = r.CopiesDropped,
                    Hex = Convert.ToHexString(r.Frame),
                });
            }

            response.LastFrame = _hub.Frames.LastSequence;
            response.Gap = HasGap(_hub.Frames, after);
        }

        return Ok(ApiResponse<XmsgHubFramesResponse>.Ok(response));
    }

    /// <summary>
    /// Downloads the frames the hub carried as a pcap file (link type Ethernet), for Wireshark.
    /// </summary>
    /// <param name="after">
    /// The last frame number the caller already has; 0 for all frames still kept.
    /// </param>
    /// <param name="system">
    /// Only frames from or to this system number; 0 for all.
    /// </param>
    /// <returns>
    /// The file, or 404 when the hub is not hosted.
    /// </returns>
    /// <remarks>
    /// Each frame keeps the time it arrived. Only the frames still in the buffer can be exported; for a complete record of
    /// a run, set a capture file in the settings.
    /// </remarks>
    [HttpGet("frames.pcap")]
    public IActionResult GetFramesPcap([FromQuery] long after = 0, [FromQuery] int system = 0)
    {
        if (_hub == null)
        {
            return NotFound(ApiResponse<object>.Fail("The XMSG Ethernet hub is not hosted by this server."));
        }

        HubFrameRecord[] records = Select(_hub.Frames.GetAfter(after, int.MaxValue), system, int.MaxValue);
        MemoryStream pcap = new MemoryStream();
        HubFrameRing.WritePcap(records, pcap);
        pcap.Position = 0;
        string name = $"xmsghub-{DateTime.UtcNow:yyyyMMdd-HHmmss}.pcap";
        return File(pcap, "application/vnd.tcpdump.pcap", name);
    }

    /// <summary>
    /// Gets the hub settings from xmsghub-config.json.
    /// </summary>
    /// <returns>
    /// The settings.
    /// </returns>
    [HttpGet("settings")]
    public ActionResult<ApiResponse<XmsgHubSettingsDto>> GetSettings()
    {
        XmsgHubServerConfiguration config = ReadSettingsFile();
        return Ok(ApiResponse<XmsgHubSettingsDto>.Ok(ToDto(config)));
    }

    /// <summary>
    /// Writes the hub settings to xmsghub-config.json.
    /// </summary>
    /// <param name="request">
    /// The new settings. Every field is written.
    /// </param>
    /// <returns>
    /// The settings as saved, with <c>RestartRequired</c> set when they differ from what the running server uses.
    /// </returns>
    /// <remarks>
    /// The server reads the file when it starts, so a change takes effect after a restart of RetroServer, the same as
    /// editing the file by hand. A bad port or address is refused here, before it can stop the hub from starting.
    /// </remarks>
    [HttpPut("settings")]
    public ActionResult<ApiResponse<XmsgHubSettingsDto>> PutSettings([FromBody] XmsgHubSettingsDto request)
    {
        if (request == null)
        {
            return BadRequest(ApiResponse<XmsgHubSettingsDto>.Fail("No settings were sent."));
        }

        if (request.Port < 0 || request.Port > 65535)
        {
            return BadRequest(ApiResponse<XmsgHubSettingsDto>.Fail("Port must be 0 to 65535."));
        }

        if (!string.IsNullOrWhiteSpace(request.BindAddress) && !IPAddress.TryParse(request.BindAddress.Trim(), out _))
        {
            return BadRequest(ApiResponse<XmsgHubSettingsDto>.Fail($"'{request.BindAddress}' is not an IP address."));
        }

        if (!string.IsNullOrWhiteSpace(request.Uplink) && !IsHostAndPort(request.Uplink))
        {
            return BadRequest(ApiResponse<XmsgHubSettingsDto>.Fail("Uplink must be host:port."));
        }

        if (request.FrameBufferSize < 1 || request.LogBufferSize < 1 || request.StatusIntervalSeconds < 1)
        {
            return BadRequest(ApiResponse<XmsgHubSettingsDto>.Fail("The buffer sizes and the status interval must be at least 1."));
        }

        XmsgHubServerConfiguration config = new XmsgHubServerConfiguration
        {
            Enabled = request.Enabled,
            Port = request.Port,
            BindAddress = string.IsNullOrWhiteSpace(request.BindAddress) ? "127.0.0.1" : request.BindAddress.Trim(),
            Uplink = string.IsNullOrWhiteSpace(request.Uplink) ? null : request.Uplink.Trim(),
            CaptureFile = string.IsNullOrWhiteSpace(request.CaptureFile) ? null : request.CaptureFile.Trim(),
            FrameBufferSize = request.FrameBufferSize,
            LogBufferSize = request.LogBufferSize,
            StatusIntervalSeconds = request.StatusIntervalSeconds,
        };

        PathHelper.EnsureDirectory(_retroFsPath);
        ProtocolConfigurationLoader.SaveXmsgHubConfiguration(_retroFsPath, config);
        return Ok(ApiResponse<XmsgHubSettingsDto>.Ok(ToDto(config)));
    }

    /// <summary>
    /// Reads the settings file, or the defaults when it is missing.
    /// </summary>
    /// <returns>
    /// The settings.
    /// </returns>
    private XmsgHubServerConfiguration ReadSettingsFile()
    {
        string path = Path.Combine(_retroFsPath, ProtocolConfigurationLoader.XmsgHubConfigurationFileName);
        if (!System.IO.File.Exists(path))
        {
            return XmsgHubServerConfiguration.GetDefault();
        }

        string json = System.IO.File.ReadAllText(path);
        return System.Text.Json.JsonSerializer.Deserialize<XmsgHubServerConfiguration>(json) ?? XmsgHubServerConfiguration.GetDefault();
    }

    /// <summary>
    /// Turns stored settings into the transfer shape, and says whether the running hub differs from them.
    /// </summary>
    /// <param name="config">
    /// The stored settings.
    /// </param>
    /// <returns>
    /// The settings.
    /// </returns>
    private XmsgHubSettingsDto ToDto(XmsgHubServerConfiguration config)
    {
        XmsgHubSettingsDto dto = new XmsgHubSettingsDto
        {
            Enabled = config.Enabled,
            Port = config.Port,
            BindAddress = config.BindAddress,
            Uplink = config.Uplink ?? string.Empty,
            CaptureFile = config.CaptureFile ?? string.Empty,
            FrameBufferSize = config.FrameBufferSize,
            LogBufferSize = config.LogBufferSize,
            StatusIntervalSeconds = config.StatusIntervalSeconds,
        };

        if (_hub == null)
        {
            dto.RestartRequired = config.Enabled;
        }
        else
        {
            XmsgHubOptions running = _hub.Options;
            dto.RestartRequired = !config.Enabled
                || config.Port != running.Port
                || !string.Equals(config.BindAddress, running.BindAddress, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(config.Uplink ?? string.Empty, running.Uplink ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(config.CaptureFile ?? string.Empty, running.CaptureFile ?? string.Empty, StringComparison.Ordinal)
                || config.FrameBufferSize != running.FrameBufferSize
                || config.LogBufferSize != running.LogBufferSize
                || config.StatusIntervalSeconds != running.StatusIntervalSeconds;
        }

        return dto;
    }

    /// <summary>
    /// Keeps the frames from or to one system, up to a count.
    /// </summary>
    /// <param name="records">
    /// The frames, oldest first.
    /// </param>
    /// <param name="system">
    /// The system number; 0 keeps all.
    /// </param>
    /// <param name="limit">
    /// The most to keep.
    /// </param>
    /// <returns>
    /// The kept frames, oldest first.
    /// </returns>
    private static HubFrameRecord[] Select(HubFrameRecord[] records, int system, int limit)
    {
        List<HubFrameRecord> kept = new List<HubFrameRecord>(Math.Min(records.Length, limit));
        for (int i = 0; i < records.Length && kept.Count < limit; i++)
        {
            HubFrameRecord r = records[i];
            if (system == 0 || r.SourceSystem == system || r.DestinationSystem == system)
            {
                kept.Add(r);
            }
        }

        return kept.ToArray();
    }

    /// <summary>
    /// Says whether some frames after <paramref name="after"/> have already left the buffer.
    /// </summary>
    /// <param name="ring">
    /// The buffer.
    /// </param>
    /// <param name="after">
    /// The number the caller asked after.
    /// </param>
    /// <returns>
    /// True when the oldest frame still held is later than the one right after <paramref name="after"/>.
    /// </returns>
    private static bool HasGap(HubFrameRing ring, long after)
    {
        HubFrameRecord[] oldest = ring.GetAfter(0, 1);
        return oldest.Length == 1 && oldest[0].Sequence > after + 1;
    }

    private static int Clamp(int limit)
    {
        if (limit < 1)
        {
            return 1;
        }

        return limit > MaxPage ? MaxPage : limit;
    }

    private static bool IsHostAndPort(string text)
    {
        string t = text.Trim();
        int colon = t.LastIndexOf(':');
        return colon > 0 && int.TryParse(t.AsSpan(colon + 1), out int port) && port > 0 && port <= 65535;
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
