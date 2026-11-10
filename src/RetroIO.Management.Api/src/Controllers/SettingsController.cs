//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RetroIO.Management.Contracts;
using RetroIO.Server.Hosting;
using RetroIO.Server.Hosting.Configuration;
using RetroIO.Storage.Utilities;

namespace RetroIO.Management.Api.Controllers;

/// <summary>
/// Management API controller for the server settings.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly ILogger<SettingsController> _logger;
    private readonly string _configPath;

    // RetroServer's data folder, where the protocol loaders read their *-config.json files. The
    // Econet settings are read from and written to the SAME file the server reads, in this folder.
    private readonly string _dataPath;

    /// <summary>
    /// Creates the controller. ASP.NET's DI calls this with only the logger, so settings land in
    /// the per-user config directory (see <see cref="GetConfigPath"/>).
    /// </summary>
    /// <param name="logger">
    /// Log sink.
    /// </param>
    /// <param name="configPath">
    /// Directory the settings JSON files are read from and written to. Pass null (the default, and
    /// what DI does) for the real per-user directory. Tests MUST pass a temp directory here --
    /// without it the tests overwrite the developer's own live settings under Documents\RetroFS.
    /// </param>
    /// <param name="dataPath">
    /// RetroServer's data folder, where econet-config.json lives. Null (what DI does) means the
    /// real one (<see cref="PathHelper.GetRetroFSPath"/>). Tests MUST pass a temp directory, for the
    /// same reason as above.
    /// </param>
    public SettingsController(ILogger<SettingsController> logger, string? configPath = null, string? dataPath = null)
    {
        _logger = logger;
        _configPath = configPath ?? GetConfigPath();
        _dataPath = dataPath ?? PathHelper.GetRetroFSPath();
    }

    /// <summary>
    /// Get TNFS settings
    /// </summary>
    [HttpGet("tnfs")]
    public ActionResult<ApiResponse<TnfsSettingsDto>> GetTnfsSettings()
    {
        try
        {
            var settings = LoadTnfsSettings();
            return Ok(ApiResponse<TnfsSettingsDto>.Ok(settings));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting TNFS settings");
            return StatusCode(500, ApiResponse<TnfsSettingsDto>.Fail("Failed to get TNFS settings"));
        }
    }

    /// <summary>
    /// Update TNFS settings
    /// </summary>
    [HttpPut("tnfs")]
    public ActionResult<ApiResponse<TnfsSettingsDto>> UpdateTnfsSettings([FromBody] UpdateTnfsSettingsRequest request)
    {
        try
        {
            var settings = LoadTnfsSettings();

            if (request.Enabled.HasValue) settings.Enabled = request.Enabled.Value;
            if (request.UdpPort.HasValue) settings.UdpPort = request.UdpPort.Value;
            if (request.TcpPort.HasValue) settings.TcpPort = request.TcpPort.Value;
            if (request.UdpEnabled.HasValue) settings.UdpEnabled = request.UdpEnabled.Value;
            if (request.TcpEnabled.HasValue) settings.TcpEnabled = request.TcpEnabled.Value;
            if (request.MaxConnections.HasValue) settings.MaxConnections = request.MaxConnections.Value;
            if (request.SessionTimeoutSeconds.HasValue) settings.SessionTimeoutSeconds = request.SessionTimeoutSeconds.Value;
            if (request.MaxSessions.HasValue) settings.MaxSessions = request.MaxSessions.Value;
            if (request.MaxSessionsPerIP.HasValue) settings.MaxSessionsPerIP = request.MaxSessionsPerIP.Value;
            if (request.RootPath != null) settings.RootPath = request.RootPath;

            string? problem = CheckTnfsSettings(settings);
            if (problem != null)
            {
                return BadRequest(ApiResponse<TnfsSettingsDto>.Fail(problem));
            }

            SaveTnfsSettings(settings);

            return Ok(ApiResponse<TnfsSettingsDto>.Ok(settings, "TNFS settings updated successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating TNFS settings");
            return StatusCode(500, ApiResponse<TnfsSettingsDto>.Fail("Failed to update TNFS settings"));
        }
    }

    /// <summary>
    /// Get Econet file server settings
    /// </summary>
    [HttpGet("econet")]
    public ActionResult<ApiResponse<EconetSettingsDto>> GetEconetSettings()
    {
        try
        {
            return Ok(ApiResponse<EconetSettingsDto>.Ok(LoadEconetSettings()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Econet settings");
            return StatusCode(500, ApiResponse<EconetSettingsDto>.Fail("Failed to get Econet settings"));
        }
    }

    /// <summary>
    /// Update Econet file server settings
    /// </summary>
    [HttpPut("econet")]
    public ActionResult<ApiResponse<EconetSettingsDto>> UpdateEconetSettings([FromBody] UpdateEconetSettingsRequest request)
    {
        try
        {
            EconetSettingsDto settings = LoadEconetSettings();

            if (request.UdpPort.HasValue) settings.UdpPort = request.UdpPort.Value;
            if (request.UdpEnabled.HasValue) settings.UdpEnabled = request.UdpEnabled.Value;
            if (request.Net.HasValue) settings.Net = request.Net.Value;
            if (request.Station.HasValue) settings.Station = request.Station.Value;
            if (request.RootPath != null) settings.RootPath = request.RootPath;
            if (request.DiscName != null) settings.DiscName = request.DiscName;
            if (request.VersionString != null) settings.VersionString = request.VersionString;
            if (request.Enabled.HasValue) settings.Enabled = request.Enabled.Value;
            if (request.AunStationOctetIndex.HasValue) settings.AunStationOctetIndex = request.AunStationOctetIndex.Value;
            if (request.WireProfile != null) settings.WireProfile = request.WireProfile;
            if (request.WireProfileOverrides != null) settings.WireProfileOverrides = request.WireProfileOverrides;
            if (request.Discs != null) settings.Discs = request.Discs;

            string? problem = CheckEconetSettings(settings);
            if (problem != null)
            {
                return BadRequest(ApiResponse<EconetSettingsDto>.Fail(problem));
            }

            SaveEconetSettings(settings);
            return Ok(ApiResponse<EconetSettingsDto>.Ok(settings, "Econet settings updated successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating Econet settings");
            return StatusCode(500, ApiResponse<EconetSettingsDto>.Fail("Failed to update Econet settings"));
        }
    }

    /// <summary>
    /// Get server settings
    /// </summary>
    [HttpGet("server")]
    public ActionResult<ApiResponse<ServerSettingsDto>> GetServerSettings()
    {
        try
        {
            var settings = LoadServerSettings();
            return Ok(ApiResponse<ServerSettingsDto>.Ok(settings));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting server settings");
            return StatusCode(500, ApiResponse<ServerSettingsDto>.Fail("Failed to get server settings"));
        }
    }

    /// <summary>
    /// Update server settings
    /// </summary>
    [HttpPut("server")]
    public ActionResult<ApiResponse<ServerSettingsDto>> UpdateServerSettings([FromBody] UpdateServerSettingsRequest request)
    {
        try
        {
            var settings = LoadServerSettings();

            if (request.AutoStart.HasValue) settings.AutoStart = request.AutoStart.Value;
            if (request.LogLevel != null) settings.LogLevel = request.LogLevel;
            if (request.MaxLogEntries.HasValue) settings.MaxLogEntries = request.MaxLogEntries.Value;

            SaveServerSettings(settings);

            return Ok(ApiResponse<ServerSettingsDto>.Ok(settings, "Server settings updated successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating server settings");
            return StatusCode(500, ApiResponse<ServerSettingsDto>.Fail("Failed to update server settings"));
        }
    }

    // Until 2026-11-10 the TNFS page read and wrote a file of its own (tnfs-settings.json in the
    // per-user config folder) that the TNFS server never opened, so no change on the page ever took
    // effect. It now reads and writes tnfs-config.json in the data folder, the file the server loads.

    private TnfsServerConfiguration ReadTnfsConfiguration()
    {
        return ProtocolConfigurationLoader.ReadSettingsFile(
            _dataPath, ProtocolConfigurationLoader.TnfsConfigurationFileName, TnfsServerConfiguration.GetDefault);
    }

    private TnfsSettingsDto LoadTnfsSettings()
    {
        TnfsServerConfiguration config = ReadTnfsConfiguration();
        return new TnfsSettingsDto
        {
            Enabled = config.Enabled,
            UdpPort = config.UdpPort,
            TcpPort = config.TcpPort,
            UdpEnabled = config.EnableUdp,
            TcpEnabled = config.EnableTcp,
            MaxConnections = config.MaxTcpConnections,
            MaxSessions = config.MaxSessions,
            MaxSessionsPerIP = config.MaxSessionsPerIP,
            SessionTimeoutSeconds = config.SessionTimeoutSeconds,
            RootPath = config.RootDirectory,
        };
    }

    /// <summary>
    /// Writes the page's fields into tnfs-config.json, keeping any field the page does not show.
    /// </summary>
    /// <param name="settings">
    /// The edited settings.
    /// </param>
    private void SaveTnfsSettings(TnfsSettingsDto settings)
    {
        TnfsServerConfiguration config = ReadTnfsConfiguration();
        config.Enabled = settings.Enabled;
        config.UdpPort = settings.UdpPort;
        config.TcpPort = settings.TcpPort;
        config.EnableUdp = settings.UdpEnabled;
        config.EnableTcp = settings.TcpEnabled;
        config.MaxTcpConnections = settings.MaxConnections;
        config.MaxSessions = settings.MaxSessions;
        config.MaxSessionsPerIP = settings.MaxSessionsPerIP;
        config.SessionTimeoutSeconds = settings.SessionTimeoutSeconds;
        config.RootDirectory = settings.RootPath;
        ProtocolConfigurationLoader.SaveSettingsFile(_dataPath, ProtocolConfigurationLoader.TnfsConfigurationFileName, config);
    }

    /// <summary>
    /// Checks the TNFS settings before they are written.
    /// </summary>
    /// <param name="settings">
    /// The settings.
    /// </param>
    /// <returns>
    /// Null when they are good, else what is wrong.
    /// </returns>
    private static string? CheckTnfsSettings(TnfsSettingsDto settings)
    {
        if (settings.UdpPort < 1 || settings.UdpPort > 65535 || settings.TcpPort < 1 || settings.TcpPort > 65535)
        {
            return "A port must be 1 to 65535.";
        }

        if (settings.MaxConnections < 1 || settings.MaxSessions < 1 || settings.MaxSessionsPerIP < 1)
        {
            return "The connection and session limits must be at least 1.";
        }

        if (settings.SessionTimeoutSeconds < 1)
        {
            return "The session timeout must be at least 1 second.";
        }

        if (string.IsNullOrWhiteSpace(settings.RootPath))
        {
            return "The root folder is needed.";
        }

        return null;
    }

    private EconetSettingsDto LoadEconetSettings()
    {
        return MapEconetSettings(ReadEconetConfiguration());
    }

    /// <summary>
    /// Reads econet-config.json as the server reads it, or the defaults when it is not there.
    /// </summary>
    /// <returns>
    /// The whole configuration, every field.
    /// </returns>
    private EconetServerConfiguration ReadEconetConfiguration()
    {
        string configPath = Path.Combine(_dataPath, "econet-config.json");
        if (!System.IO.File.Exists(configPath))
        {
            return EconetServerConfiguration.GetDefault();
        }

        string json = System.IO.File.ReadAllText(configPath);
        return JsonSerializer.Deserialize<EconetServerConfiguration>(json) ?? EconetServerConfiguration.GetDefault();
    }

    /// <summary>
    /// Writes the fields this page edits into econet-config.json and KEEPS EVERY OTHER FIELD.
    /// </summary>
    /// <param name="settings">
    /// The edited settings.
    /// </param>
    /// <remarks>
    /// Until 2026-11-10 this built a new configuration from the seven fields alone and wrote it over
    /// the file, so one Save from RetroAdmin deleted the disc list (Discs), the wire profiles and
    /// AunStationOctetIndex. It now changes the file the server reads, field by field.
    /// </remarks>
    private void SaveEconetSettings(EconetSettingsDto settings)
    {
        PathHelper.EnsureDirectory(_dataPath);
        string configPath = Path.Combine(_dataPath, "econet-config.json");

        EconetServerConfiguration config = ReadEconetConfiguration();
        config.RootDirectory = settings.RootPath;
        config.DiscName = settings.DiscName;
        // Empty from the page means none, which the file holds as null.
        config.VersionString = string.IsNullOrEmpty(settings.VersionString) ? null : settings.VersionString;
        config.UdpPort = settings.UdpPort;
        config.Net = settings.Net;
        config.Station = settings.Station;
        config.EnableUdp = settings.UdpEnabled;
        config.Enabled = settings.Enabled;
        config.AunStationOctetIndex = settings.AunStationOctetIndex;
        config.WireProfile = ParseWireProfile(settings.WireProfile);

        List<EconetMachineWireProfile> overrides = new List<EconetMachineWireProfile>();
        for (int i = 0; i < settings.WireProfileOverrides.Count; i++)
        {
            EconetWireProfileOverrideDto o = settings.WireProfileOverrides[i];
            overrides.Add(new EconetMachineWireProfile { Net = o.Net, Station = o.Station, Profile = ParseWireProfile(o.Profile) });
        }

        // Null, not an empty list, when there are none: that is what the loader treats as "not set".
        config.WireProfileOverrides = overrides.Count == 0 ? null : overrides;

        List<EconetDiscConfiguration> discs = new List<EconetDiscConfiguration>();
        for (int i = 0; i < settings.Discs.Count; i++)
        {
            EconetDiscDto d = settings.Discs[i];
            discs.Add(new EconetDiscConfiguration
            {
                Index = d.Index,
                AcornName = d.AcornName,
                DirName = string.IsNullOrWhiteSpace(d.DirName) ? null : d.DirName,
                Provider = string.IsNullOrWhiteSpace(d.Provider) ? null : d.Provider,
                Uri = string.IsNullOrWhiteSpace(d.Uri) ? null : d.Uri,
                ReadOnly = d.ReadOnly,
                MountRef = string.IsNullOrWhiteSpace(d.MountRef) ? null : d.MountRef,
            });
        }

        // Null, not empty: no disc list means one folder per disc under the root.
        config.Discs = discs.Count == 0 ? null : discs;

        string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        System.IO.File.WriteAllText(configPath, json);
    }

    private static EconetSettingsDto MapEconetSettings(EconetServerConfiguration config)
    {
        return new EconetSettingsDto
        {
            UdpPort = config.UdpPort,
            UdpEnabled = config.EnableUdp,
            Net = config.Net,
            Station = config.Station,
            RootPath = config.RootDirectory,
            DiscName = config.DiscName,
            VersionString = config.VersionString,
            Enabled = config.Enabled,
            AunStationOctetIndex = config.AunStationOctetIndex,
            WireProfile = config.WireProfile.ToString(),
            WireProfileOverrides = MapOverrides(config.WireProfileOverrides),
            Discs = MapDiscs(config.Discs),
        };
    }

    private static List<EconetWireProfileOverrideDto> MapOverrides(List<EconetMachineWireProfile>? overrides)
    {
        List<EconetWireProfileOverrideDto> result = new List<EconetWireProfileOverrideDto>();
        if (overrides == null)
        {
            return result;
        }

        for (int i = 0; i < overrides.Count; i++)
        {
            result.Add(new EconetWireProfileOverrideDto
            {
                Net = overrides[i].Net,
                Station = overrides[i].Station,
                Profile = overrides[i].Profile.ToString(),
            });
        }

        return result;
    }

    private static List<EconetDiscDto> MapDiscs(List<EconetDiscConfiguration>? discs)
    {
        List<EconetDiscDto> result = new List<EconetDiscDto>();
        if (discs == null)
        {
            return result;
        }

        for (int i = 0; i < discs.Count; i++)
        {
            EconetDiscConfiguration d = discs[i];
            result.Add(new EconetDiscDto
            {
                Index = d.Index,
                AcornName = d.AcornName,
                DirName = d.DirName,
                Provider = d.Provider,
                Uri = d.Uri,
                ReadOnly = d.ReadOnly,
                MountRef = d.MountRef,
            });
        }

        return result;
    }

    private static RetroIO.Protocols.Econet.FileServer.Configuration.WireProfile ParseWireProfile(string name)
    {
        // Checked by CheckEconetSettings before any save, so a bad name never reaches here.
        return Enum.Parse<RetroIO.Protocols.Econet.FileServer.Configuration.WireProfile>(name, true);
    }

    private static bool IsWireProfile(string? name)
    {
        return !string.IsNullOrWhiteSpace(name)
            && !int.TryParse(name, out _)
            && Enum.TryParse<RetroIO.Protocols.Econet.FileServer.Configuration.WireProfile>(name, true, out _);
    }

    /// <summary>
    /// Checks the Econet settings before they are written.
    /// </summary>
    /// <param name="settings">
    /// The settings.
    /// </param>
    /// <returns>
    /// Null when they are good, else what is wrong.
    /// </returns>
    private static string? CheckEconetSettings(EconetSettingsDto settings)
    {
        if (settings.AunStationOctetIndex > 3)
        {
            return "The station byte is 0 to 3 (which of the four bytes of an IP address).";
        }

        if (!IsWireProfile(settings.WireProfile))
        {
            return "The reply format must be LaterFS or Classic1983.";
        }

        for (int i = 0; i < settings.WireProfileOverrides.Count; i++)
        {
            EconetWireProfileOverrideDto o = settings.WireProfileOverrides[i];
            if (!IsWireProfile(o.Profile))
            {
                return "Machine " + o.Net + "." + o.Station + ": the reply format must be LaterFS or Classic1983.";
            }

            for (int j = 0; j < i; j++)
            {
                if (settings.WireProfileOverrides[j].Net == o.Net && settings.WireProfileOverrides[j].Station == o.Station)
                {
                    return "Machine " + o.Net + "." + o.Station + " is listed twice.";
                }
            }
        }

        for (int i = 0; i < settings.Discs.Count; i++)
        {
            EconetDiscDto d = settings.Discs[i];
            if (string.IsNullOrWhiteSpace(d.AcornName))
            {
                return "Disc " + d.Index + " has no name.";
            }

            bool inline = !string.IsNullOrWhiteSpace(d.Provider) || !string.IsNullOrWhiteSpace(d.Uri);
            bool reference = !string.IsNullOrWhiteSpace(d.MountRef);
            if (inline == reference)
            {
                return "Disc " + d.Index + " needs either a storage kind and address, or a mounted volume - one of the two.";
            }

            if (inline && (string.IsNullOrWhiteSpace(d.Provider) || string.IsNullOrWhiteSpace(d.Uri)))
            {
                return "Disc " + d.Index + " needs both a storage kind and an address.";
            }

            // EconetVfsDiscs.AddEconetVfsDiscs refuses anything else at start (ftp and sftp are not wired yet).
            if (inline && d.Provider != "native" && d.Provider != "tnfs")
            {
                return "Disc " + d.Index + ": the storage kind must be native or tnfs.";
            }

            for (int j = 0; j < i; j++)
            {
                if (settings.Discs[j].Index == d.Index)
                {
                    return "Disc number " + d.Index + " is used twice.";
                }
            }
        }

        return null;
    }

    private ServerSettingsDto LoadServerSettings()
    {
        var serverConfigPath = Path.Combine(_configPath, "server-settings.json");

        if (!System.IO.File.Exists(serverConfigPath))
        {
            // Return defaults
            return new ServerSettingsDto
            {
                AutoStart = false,
                LogLevel = "Information",
                MaxLogEntries = 1000
            };
        }

        var json = System.IO.File.ReadAllText(serverConfigPath);
        return JsonSerializer.Deserialize<ServerSettingsDto>(json) ?? new ServerSettingsDto();
    }

    private void SaveServerSettings(ServerSettingsDto settings)
    {
        Directory.CreateDirectory(_configPath);
        var serverConfigPath = Path.Combine(_configPath, "server-settings.json");
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        System.IO.File.WriteAllText(serverConfigPath, json);
    }

    private static string GetConfigPath()
    {
        string basePath;

        if (OperatingSystem.IsWindows())
        {
            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            basePath = Path.Combine(documentsPath, "RetroFS", "config");
        }
        else
        {
            string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string? xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

            if (string.IsNullOrEmpty(xdgDataHome))
            {
                xdgDataHome = Path.Combine(homeDir, ".local", "share");
            }

            basePath = Path.Combine(xdgDataHome, "retrofs", "config");
        }

        return basePath;
    }
}
