//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RetroIO.Server.Hosting.Configuration;

namespace RetroIO.Server.Hosting;

public static class ProtocolConfigurationLoader
{
    public static TnfsServerConfiguration LoadTnfsConfiguration(string retroFsPath, IConfiguration configuration)
    {
        string configPath = Path.Combine(retroFsPath, "tnfs-config.json");
        TnfsServerConfiguration config;

        if (File.Exists(configPath))
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
        SaveConfiguration(configPath, config, "TNFS");
        return config;
    }

    public static EconetServerConfiguration LoadEconetConfiguration(string retroFsPath, IConfiguration configuration)
    {
        string configPath = Path.Combine(retroFsPath, "econet-config.json");
        EconetServerConfiguration config;

        if (File.Exists(configPath))
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
        SaveConfiguration(configPath, config, "Econet");
        return config;
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
