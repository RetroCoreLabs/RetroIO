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
        string configPath = Path.Combine(retroFsPath, "tnfs-config.json");
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
        string configPath = Path.Combine(retroFsPath, "econet-config.json");
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
        string configPath = Path.Combine(retroFsPath, "drivewire-config.json");
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
