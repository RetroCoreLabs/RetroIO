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

[ApiController]
[Route("api/v1/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly ILogger<SettingsController> _logger;
    private readonly string _configPath;

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
    public SettingsController(ILogger<SettingsController> logger, string? configPath = null)
    {
        _logger = logger;
        _configPath = configPath ?? GetConfigPath();
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

            if (request.UdpPort.HasValue) settings.UdpPort = request.UdpPort.Value;
            if (request.TcpPort.HasValue) settings.TcpPort = request.TcpPort.Value;
            if (request.UdpEnabled.HasValue) settings.UdpEnabled = request.UdpEnabled.Value;
            if (request.TcpEnabled.HasValue) settings.TcpEnabled = request.TcpEnabled.Value;
            if (request.MaxConnections.HasValue) settings.MaxConnections = request.MaxConnections.Value;
            if (request.SessionTimeoutSeconds.HasValue) settings.SessionTimeoutSeconds = request.SessionTimeoutSeconds.Value;
            if (request.RootPath != null) settings.RootPath = request.RootPath;

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

    private TnfsSettingsDto LoadTnfsSettings()
    {
        var tnfsConfigPath = Path.Combine(_configPath, "tnfs-settings.json");

        if (!System.IO.File.Exists(tnfsConfigPath))
        {
            // Return defaults
            return new TnfsSettingsDto
            {
                UdpPort = 16384,
                TcpPort = 16384,
                UdpEnabled = true,
                TcpEnabled = true,
                MaxConnections = 100,
                SessionTimeoutSeconds = 600,
                RootPath = GetDefaultRootPath()
            };
        }

        var json = System.IO.File.ReadAllText(tnfsConfigPath);
        return JsonSerializer.Deserialize<TnfsSettingsDto>(json) ?? new TnfsSettingsDto();
    }

    private void SaveTnfsSettings(TnfsSettingsDto settings)
    {
        Directory.CreateDirectory(_configPath);
        var tnfsConfigPath = Path.Combine(_configPath, "tnfs-settings.json");
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        System.IO.File.WriteAllText(tnfsConfigPath, json);
    }

    private EconetSettingsDto LoadEconetSettings()
    {
        string retroFsPath = PathHelper.GetRetroFSPath();
        string configPath = Path.Combine(retroFsPath, "econet-config.json");

        if (!System.IO.File.Exists(configPath))
        {
            EconetServerConfiguration defaults = EconetServerConfiguration.GetDefault();
            return MapEconetSettings(defaults);
        }

        string json = System.IO.File.ReadAllText(configPath);
        EconetServerConfiguration? config = JsonSerializer.Deserialize<EconetServerConfiguration>(json);
        if (config == null)
            return MapEconetSettings(EconetServerConfiguration.GetDefault());

        return MapEconetSettings(config);
    }

    private void SaveEconetSettings(EconetSettingsDto settings)
    {
        string retroFsPath = PathHelper.GetRetroFSPath();
        PathHelper.EnsureDirectory(retroFsPath);
        string configPath = Path.Combine(retroFsPath, "econet-config.json");

        EconetServerConfiguration config = new EconetServerConfiguration
        {
            RootDirectory = settings.RootPath,
            DiscName = settings.DiscName,
            VersionString = settings.VersionString,
            UdpPort = settings.UdpPort,
            Net = settings.Net,
            Station = settings.Station,
            EnableUdp = settings.UdpEnabled
        };

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
            VersionString = config.VersionString
        };
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

    private static string GetDefaultRootPath()
    {
        if (OperatingSystem.IsWindows())
        {
            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(documentsPath, "RetroFS", "volumes");
        }
        else
        {
            string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string? xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

            if (string.IsNullOrEmpty(xdgDataHome))
            {
                xdgDataHome = Path.Combine(homeDir, ".local", "share");
            }

            return Path.Combine(xdgDataHome, "retrofs", "volumes");
        }
    }
}
