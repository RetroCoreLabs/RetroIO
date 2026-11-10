//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RetroIO.Server.Hosting.Configuration;

namespace RetroIO.Server.Hosting;

/// <summary>
/// Loads the TNFS and Econet server settings from JSON files and application configuration.
/// </summary>
public static class ProtocolConfigurationLoader
{
    /// <summary>
    /// The file name of the TNFS settings inside the RetroFS data folder.
    /// </summary>
    public const string TnfsConfigurationFileName = "tnfs-config.json";

    /// <summary>
    /// The file name of the Econet settings inside the RetroFS data folder.
    /// </summary>
    public const string EconetConfigurationFileName = "econet-config.json";

    /// <summary>
    /// The file name of the DriveWire settings inside the RetroFS data folder.
    /// </summary>
    public const string DriveWireConfigurationFileName = "drivewire-config.json";

    /// <summary>
    /// Reads one settings file exactly as stored, without the command-line overrides, for the admin pages.
    /// </summary>
    /// <typeparam name="T">
    /// The settings type.
    /// </typeparam>
    /// <param name="retroFsPath">
    /// The data folder.
    /// </param>
    /// <param name="fileName">
    /// One of the file name constants here.
    /// </param>
    /// <param name="defaults">
    /// What a missing or empty file means.
    /// </param>
    /// <returns>
    /// The settings in the file, or the defaults.
    /// </returns>
    /// <remarks>
    /// The admin pages show and change what is IN the file - the thing that applies at the next start - not
    /// what a command-line switch laid over it for this run.
    /// </remarks>
    public static T ReadSettingsFile<T>(string retroFsPath, string fileName, Func<T> defaults)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(defaults);
        string path = Path.Combine(retroFsPath, fileName);
        if (!File.Exists(path))
        {
            return defaults();
        }

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? defaults();
    }

    /// <summary>
    /// Writes one settings file, whole, for the admin pages. The change applies the next time RetroServer starts.
    /// </summary>
    /// <typeparam name="T">
    /// The settings type.
    /// </typeparam>
    /// <param name="retroFsPath">
    /// The data folder; made when missing.
    /// </param>
    /// <param name="fileName">
    /// One of the file name constants here.
    /// </param>
    /// <param name="settings">
    /// The settings.
    /// </param>
    public static void SaveSettingsFile<T>(string retroFsPath, string fileName, T settings)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(retroFsPath);
        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(retroFsPath, fileName), json);
    }

    /// <summary>
    /// Loads the TNFS settings from tnfs-config.json, applies overrides from the application configuration, and writes the file only when it did not exist.
    /// </summary>
    /// <remarks>
    /// When the file is missing or cannot be read, the defaults are used. The keys Tnfs:RootDirectory, Tnfs:UdpPort and Tnfs:TcpPort override the loaded values when they are set. The file is written only when it was missing (with the overrides included), and then "Created default" is printed. An existing file, even one that could not be read, is never overwritten. A failed write is only reported on the console.
    /// </remarks>
    /// <param name="retroFsPath">
    /// The folder that holds the JSON file.
    /// </param>
    /// <param name="configuration">
    /// The application configuration with the optional overrides.
    /// </param>
    /// <returns>
    /// The TNFS settings.
    /// </returns>
    public static TnfsServerConfiguration LoadTnfsConfiguration(string retroFsPath, IConfiguration configuration)
    {
        string configPath = Path.Combine(retroFsPath, TnfsConfigurationFileName);
        bool existed = File.Exists(configPath);
        TnfsServerConfiguration config;

        if (existed)
        {
            try
            {
                string json = File.ReadAllText(configPath);
                TnfsServerConfiguration? loaded = JsonSerializer.Deserialize<TnfsServerConfiguration>(json);
                config = loaded ?? TnfsServerConfiguration.GetDefault();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load TNFS configuration: {ex.Message}");
                Console.WriteLine("Using default configuration...");
                config = TnfsServerConfiguration.GetDefault();
            }
        }
        else
        {
            config = TnfsServerConfiguration.GetDefault();
        }

        ApplyTnfsOverrides(configuration, config);
        if (!existed)
        {
            SaveConfiguration(configPath, config, "TNFS");
        }

        return config;
    }

    /// <summary>
    /// Loads the Econet settings from econet-config.json, applies overrides from the application configuration, and writes the file only when it did not exist.
    /// </summary>
    /// <remarks>
    /// When the file is missing or cannot be read, the defaults are used. The keys Econet:RootDirectory and Econet:UdpPort override the loaded values when they are set. The file is written only when it was missing (with the overrides included), and then "Created default" is printed. An existing file, even one that could not be read, is never overwritten. A failed write is only reported on the console.
    /// </remarks>
    /// <param name="retroFsPath">
    /// The folder that holds the JSON file.
    /// </param>
    /// <param name="configuration">
    /// The application configuration with the optional overrides.
    /// </param>
    /// <returns>
    /// The Econet settings.
    /// </returns>
    public static EconetServerConfiguration LoadEconetConfiguration(string retroFsPath, IConfiguration configuration)
    {
        string configPath = Path.Combine(retroFsPath, EconetConfigurationFileName);
        bool existed = File.Exists(configPath);
        EconetServerConfiguration config;

        if (existed)
        {
            try
            {
                string json = File.ReadAllText(configPath);
                config = JsonSerializer.Deserialize<EconetServerConfiguration>(json) ?? EconetServerConfiguration.GetDefault();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load Econet configuration: {ex.Message}");
                Console.WriteLine("Using default configuration...");
                config = EconetServerConfiguration.GetDefault();
            }
        }
        else
        {
            config = EconetServerConfiguration.GetDefault();
        }

        ApplyEconetOverrides(configuration, config);
        if (!existed)
        {
            SaveConfiguration(configPath, config, "Econet");
        }

        return config;
    }

    /// <summary>
    /// Loads the DriveWire settings from drivewire-config.json, applies overrides from the application configuration, and writes the file only when it did not exist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the file is missing or cannot be read, the defaults are used. The file is written only when it was missing (with the overrides included). An existing file is never overwritten.
    /// </para>
    /// <para>
    /// The keys DriveWire:Enabled, DriveWire:Port, DriveWire:BindAddress and DriveWire:TransactionTimeoutMilliseconds override the loaded values. A drive is set with DriveWire:Drives:N:Path and DriveWire:Drives:N:ReadOnly, where N is the drive number; it replaces any drive with that number from the file.
    /// </para>
    /// </remarks>
    /// <param name="retroFsPath">
    /// The folder that holds the JSON file.
    /// </param>
    /// <param name="configuration">
    /// The application configuration with the optional overrides.
    /// </param>
    /// <returns>
    /// The DriveWire settings.
    /// </returns>
    public static DriveWireServerConfiguration LoadDriveWireConfiguration(string retroFsPath, IConfiguration configuration)
    {
        string configPath = Path.Combine(retroFsPath, DriveWireConfigurationFileName);
        bool existed = File.Exists(configPath);
        DriveWireServerConfiguration config;

        if (existed)
        {
            try
            {
                string json = File.ReadAllText(configPath);
                config = JsonSerializer.Deserialize<DriveWireServerConfiguration>(json) ?? DriveWireServerConfiguration.GetDefault();
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                Console.WriteLine($"Failed to load DriveWire configuration: {ex.Message}");
                Console.WriteLine("Using default configuration...");
                config = DriveWireServerConfiguration.GetDefault();
            }
        }
        else
        {
            config = DriveWireServerConfiguration.GetDefault();
        }

        ApplyDriveWireOverrides(configuration, config);
        if (!existed)
        {
            SaveConfiguration(configPath, config, "DriveWire");
        }

        return config;
    }

    /// <summary>
    /// Loads the COSMOS Ethernet hub settings from xmsghub-config.json, applies overrides from the application configuration, and writes the file only when it did not exist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Works like <see cref="LoadDriveWireConfiguration"/>: missing or unreadable file means the defaults (hub off), and an existing file is never overwritten here.
    /// </para>
    /// <para>
    /// The keys XmsgHub:Enabled, XmsgHub:Port, XmsgHub:BindAddress, XmsgHub:Uplink, XmsgHub:CaptureFile, XmsgHub:FrameBufferSize, XmsgHub:LogBufferSize and XmsgHub:StatusIntervalSeconds override the loaded values.
    /// </para>
    /// </remarks>
    /// <param name="retroFsPath">
    /// The folder that holds the JSON file.
    /// </param>
    /// <param name="configuration">
    /// The application configuration with the optional overrides.
    /// </param>
    /// <returns>
    /// The hub settings.
    /// </returns>
    public static XmsgHubServerConfiguration LoadXmsgHubConfiguration(string retroFsPath, IConfiguration configuration)
    {
        string configPath = Path.Combine(retroFsPath, XmsgHubConfigurationFileName);
        bool existed = File.Exists(configPath);
        XmsgHubServerConfiguration config;

        if (existed)
        {
            try
            {
                string json = File.ReadAllText(configPath);
                config = JsonSerializer.Deserialize<XmsgHubServerConfiguration>(json) ?? XmsgHubServerConfiguration.GetDefault();
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                Console.WriteLine($"Failed to load XMSG hub configuration: {ex.Message}");
                Console.WriteLine("Using default configuration...");
                config = XmsgHubServerConfiguration.GetDefault();
            }
        }
        else
        {
            config = XmsgHubServerConfiguration.GetDefault();
        }

        ApplyXmsgHubOverrides(configuration, config);
        if (!existed)
        {
            SaveConfiguration(configPath, config, "XMSG hub");
        }

        return config;
    }

    /// <summary>
    /// The file name of the hub settings inside the RetroFS data folder.
    /// </summary>
    public const string XmsgHubConfigurationFileName = "xmsghub-config.json";

    /// <summary>
    /// Writes the hub settings to xmsghub-config.json, replacing the file.
    /// </summary>
    /// <param name="retroFsPath">
    /// The folder that holds the JSON file.
    /// </param>
    /// <param name="config">
    /// The settings to write.
    /// </param>
    /// <remarks>
    /// Used by the management API when the settings are changed from the admin UI. The new values take effect the next time
    /// the server starts, the same as editing the file by hand.
    /// </remarks>
    /// <exception cref="IOException">
    /// Thrown when the file cannot be written.
    /// </exception>
    public static void SaveXmsgHubConfiguration(string retroFsPath, XmsgHubServerConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        string configPath = Path.Combine(retroFsPath, XmsgHubConfigurationFileName);
        string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json);
    }

    /// <summary>
    /// The file name of RetroServer's XMSG node settings inside the RetroFS data folder.
    /// </summary>
    public const string XmsgNodeConfigurationFileName = "xmsgnode-config.json";

    /// <summary>
    /// The file holding the traffic limits and the audit log settings.
    /// </summary>
    public const string SecurityConfigurationFileName = "security-config.json";

    /// <summary>
    /// Loads RetroServer's XMSG node settings from xmsgnode-config.json, writing the defaults there when the file is missing.
    /// </summary>
    /// <remarks>
    /// Works like <see cref="LoadXmsgHubConfiguration"/>: a missing or unreadable file means the defaults (node off), and
    /// an existing file is never overwritten here. The keys XmsgNode:Enabled, XmsgNode:SystemNumber,
    /// XmsgNode:PeerSystemNumber, XmsgNode:Ethernet, XmsgNode:ProgramBindAddress, XmsgNode:ProgramPort, XmsgNode:ProgramToken, XmsgNode:ProgramLogMinimum and XmsgNode:Motd
    /// override the loaded values.
    /// </remarks>
    /// <param name="retroFsPath">
    /// The folder that holds the JSON file.
    /// </param>
    /// <param name="configuration">
    /// The application configuration with the optional overrides.
    /// </param>
    /// <returns>
    /// The node settings.
    /// </returns>
    public static XmsgNodeServerConfiguration LoadXmsgNodeConfiguration(string retroFsPath, IConfiguration configuration)
    {
        string configPath = Path.Combine(retroFsPath, XmsgNodeConfigurationFileName);
        bool existed = File.Exists(configPath);
        XmsgNodeServerConfiguration config;

        if (existed)
        {
            try
            {
                string json = File.ReadAllText(configPath);
                config = JsonSerializer.Deserialize<XmsgNodeServerConfiguration>(json) ?? XmsgNodeServerConfiguration.GetDefault();
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                Console.WriteLine($"Failed to load XMSG node configuration: {ex.Message}");
                Console.WriteLine("Using default configuration...");
                config = XmsgNodeServerConfiguration.GetDefault();
            }
        }
        else
        {
            config = XmsgNodeServerConfiguration.GetDefault();
        }

        ApplyXmsgNodeOverrides(configuration, config);
        if (!existed)
        {
            SaveConfiguration(configPath, config, "XMSG node");
        }

        return config;
    }

    private static void ApplyXmsgNodeOverrides(IConfiguration configuration, XmsgNodeServerConfiguration config)
    {
        string? enabled = configuration["XmsgNode:Enabled"];
        if (!string.IsNullOrEmpty(enabled) && bool.TryParse(enabled, out bool isEnabled))
            config.Enabled = isEnabled;

        string? self = configuration["XmsgNode:SystemNumber"];
        if (!string.IsNullOrEmpty(self) && int.TryParse(self, out int selfNumber))
            config.SystemNumber = selfNumber;

        string? peer = configuration["XmsgNode:PeerSystemNumber"];
        if (!string.IsNullOrEmpty(peer) && int.TryParse(peer, out int peerNumber))
            config.PeerSystemNumber = peerNumber;

        string? ethernet = configuration["XmsgNode:Ethernet"];
        if (!string.IsNullOrEmpty(ethernet))
            config.Ethernet = ethernet;

        string? bind = configuration["XmsgNode:ProgramBindAddress"];
        if (!string.IsNullOrEmpty(bind))
            config.ProgramBindAddress = bind;

        string? port = configuration["XmsgNode:ProgramPort"];
        if (!string.IsNullOrEmpty(port) && int.TryParse(port, out int portNumber))
            config.ProgramPort = portNumber;

        string? logMinimum = configuration["XmsgNode:ProgramLogMinimum"];
        if (!string.IsNullOrEmpty(logMinimum))
            config.ProgramLogMinimum = logMinimum;

        string? token = configuration["XmsgNode:ProgramToken"];
        if (!string.IsNullOrEmpty(token))
            config.ProgramToken = token;

        string? motd = configuration["XmsgNode:Motd"];
        if (!string.IsNullOrEmpty(motd))
            config.Motd = motd;
    }

    private static void ApplyXmsgHubOverrides(IConfiguration configuration, XmsgHubServerConfiguration config)
    {
        string? enabled = configuration["XmsgHub:Enabled"];
        if (!string.IsNullOrEmpty(enabled) && bool.TryParse(enabled, out bool isEnabled))
            config.Enabled = isEnabled;

        string? port = configuration["XmsgHub:Port"];
        if (!string.IsNullOrEmpty(port) && int.TryParse(port, out int portNumber))
            config.Port = portNumber;

        string? bindAddress = configuration["XmsgHub:BindAddress"];
        if (!string.IsNullOrEmpty(bindAddress))
            config.BindAddress = bindAddress;

        string? uplink = configuration["XmsgHub:Uplink"];
        if (!string.IsNullOrEmpty(uplink))
            config.Uplink = uplink;

        string? capture = configuration["XmsgHub:CaptureFile"];
        if (!string.IsNullOrEmpty(capture))
            config.CaptureFile = capture;

        string? frames = configuration["XmsgHub:FrameBufferSize"];
        if (!string.IsNullOrEmpty(frames) && int.TryParse(frames, out int frameCount))
            config.FrameBufferSize = frameCount;

        string? lines = configuration["XmsgHub:LogBufferSize"];
        if (!string.IsNullOrEmpty(lines) && int.TryParse(lines, out int lineCount))
            config.LogBufferSize = lineCount;

        string? interval = configuration["XmsgHub:StatusIntervalSeconds"];
        if (!string.IsNullOrEmpty(interval) && int.TryParse(interval, out int seconds))
            config.StatusIntervalSeconds = seconds;
    }

    private static void ApplyDriveWireOverrides(IConfiguration configuration, DriveWireServerConfiguration config)
    {
        string? enabled = configuration["DriveWire:Enabled"];
        if (!string.IsNullOrEmpty(enabled) && bool.TryParse(enabled, out bool isEnabled))
            config.Enabled = isEnabled;

        string? port = configuration["DriveWire:Port"];
        if (!string.IsNullOrEmpty(port) && int.TryParse(port, out int portNumber))
            config.Port = portNumber;

        string? bindAddress = configuration["DriveWire:BindAddress"];
        if (!string.IsNullOrEmpty(bindAddress))
            config.BindAddress = bindAddress;

        string? timeout = configuration["DriveWire:TransactionTimeoutMilliseconds"];
        if (!string.IsNullOrEmpty(timeout) && int.TryParse(timeout, out int timeoutMs))
            config.TransactionTimeoutMilliseconds = timeoutMs;

        // DriveWire:Drives:<drive number>:Path and :ReadOnly. The section children are read with an enumerator, as the
        // configuration API offers no indexed access.
        using IEnumerator<IConfigurationSection> drives = configuration.GetSection("DriveWire:Drives").GetChildren().GetEnumerator();
        while (drives.MoveNext())
        {
            IConfigurationSection section = drives.Current;
            string? path = section["Path"];
            if (string.IsNullOrEmpty(path) || !int.TryParse(section.Key, out int drive))
                continue;

            bool readOnly = bool.TryParse(section["ReadOnly"], out bool ro) && ro;
            config.Drives.RemoveAll(d => d.Drive == drive);
            config.Drives.Add(new DriveWireDriveConfiguration { Drive = drive, Path = path, ReadOnly = readOnly });
        }
    }

    private static void ApplyTnfsOverrides(IConfiguration configuration, TnfsServerConfiguration config)
    {
        string? rootDirOverride = configuration["Tnfs:RootDirectory"];
        if (!string.IsNullOrEmpty(rootDirOverride))
            config.RootDirectory = rootDirOverride;

        string? udpPortOverride = configuration["Tnfs:UdpPort"];
        if (!string.IsNullOrEmpty(udpPortOverride) && int.TryParse(udpPortOverride, out int udpPort))
            config.UdpPort = udpPort;

        string? tcpPortOverride = configuration["Tnfs:TcpPort"];
        if (!string.IsNullOrEmpty(tcpPortOverride) && int.TryParse(tcpPortOverride, out int tcpPort))
            config.TcpPort = tcpPort;
    }

    private static void ApplyEconetOverrides(IConfiguration configuration, EconetServerConfiguration config)
    {
        string? rootDirOverride = configuration["Econet:RootDirectory"];
        if (!string.IsNullOrEmpty(rootDirOverride))
            config.RootDirectory = rootDirOverride;

        string? udpPortOverride = configuration["Econet:UdpPort"];
        if (!string.IsNullOrEmpty(udpPortOverride) && int.TryParse(udpPortOverride, out int udpPort))
            config.UdpPort = udpPort;
    }

    private static void SaveConfiguration<T>(string configPath, T config, string label)
    {
        try
        {
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
            Console.WriteLine($"Created default {label} configuration at: {configPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save default {label} configuration: {ex.Message}");
        }
    }
}
