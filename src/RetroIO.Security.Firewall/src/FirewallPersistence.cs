//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text.Json;

namespace RetroIO.Security.Firewall;

/// <summary>
/// Firewall configuration persistence with atomic writes
/// </summary>
public sealed class FirewallPersistence
{
    private readonly string _configPath;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates the persistence object for a configuration file.
    /// </summary>
    /// <param name="configPath">
    /// The path of the configuration file.
    /// </param>
    /// <exception cref="System.ArgumentNullException">
    /// Thrown when <paramref name="configPath"/> is null.
    /// </exception>
    public FirewallPersistence(string configPath)
    {
        _configPath = configPath ?? throw new ArgumentNullException(nameof(configPath));
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    /// <summary>
    /// Load configuration from JSON file
    /// </summary>
    public FirewallConfiguration? Load()
    {
        try
        {
            if (!File.Exists(_configPath))
                return null;

            string json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<FirewallConfiguration>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load firewall config: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Save configuration to JSON file with atomic write
    /// </summary>
    public async Task SaveAsync(FirewallConfiguration config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        string tempPath = _configPath + ".tmp";

        try
        {
            // Write to temp file first
            string json = JsonSerializer.Serialize(config, _jsonOptions);
            await File.WriteAllTextAsync(tempPath, json);

            // Atomic rename (overwrites existing file)
            File.Move(tempPath, _configPath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Cleanup temp file on error
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }

            throw new IOException($"Failed to save firewall config: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Create default configuration file if not exists
    /// </summary>
    public async Task<FirewallConfiguration> EnsureDefaultConfigAsync()
    {
        var config = Load();
        if (config != null)
            return config;

        // Create default configuration
        config = new FirewallConfiguration
        {
            Enabled = true,
            DefaultPolicy = FirewallPolicy.Deny,
            AllowLocalhost = true,
            AllowPrivateNetworks = true,
            CacheTTLSeconds = 60,
            AllowedNetworks = new List<string>
            {
                "192.168.0.0/16",
                "10.0.0.0/8"
            }
        };

        await SaveAsync(config);
        Console.WriteLine($"Created default firewall config at: {_configPath}");

        return config;
    }
}
