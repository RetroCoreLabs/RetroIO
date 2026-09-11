//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Text.Json;
using RetroIO.Storage.Utilities;

namespace RetroIO.Storage.Local
{
    /// <summary>
    /// Manages persistent storage of mount configurations.
    /// Saves/loads mount points to/from JSON configuration file.
    /// </summary>
    public static class MountConfigStore
    {
        private const string ConfigFileName = "mounts.json";

        /// <summary>
        /// Gets the default configuration file path.
        /// Windows: Documents\RetroFS\mounts.json
        /// Linux/macOS: ~/.local/share/retrofs/mounts.json
        /// </summary>
        public static string GetDefaultConfigPath()
        {
            string retroFsPath = PathHelper.GetRetroFSPath();
            return Path.Combine(retroFsPath, ConfigFileName);
        }

        /// <summary>
        /// Saves mount configuration to JSON file.
        /// Creates directory if it doesn't exist.
        /// </summary>
        /// <param name="config">Configuration to save</param>
        /// <param name="filePath">Path to configuration file (null = use default)</param>
        /// <exception cref="IOException">Thrown when save operation fails</exception>
        public static void Save(MountConfigRoot config, string? filePath = null)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            filePath ??= GetDefaultConfigPath();

            // Create directory if it doesn't exist
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Serialize to JSON with pretty formatting
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            string json = JsonSerializer.Serialize(config, options);

            // Write atomically using temporary file + rename
            string tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, overwrite: true);
        }

        /// <summary>
        /// Loads mount configuration from JSON file.
        /// Returns empty configuration if file doesn't exist.
        /// </summary>
        /// <param name="filePath">Path to configuration file (null = use default)</param>
        /// <returns>Loaded configuration, or empty if file doesn't exist</returns>
        /// <exception cref="IOException">Thrown when load operation fails</exception>
        public static MountConfigRoot Load(string? filePath = null)
        {
            filePath ??= GetDefaultConfigPath();

            // Return empty config if file doesn't exist
            if (!File.Exists(filePath))
            {
                return new MountConfigRoot();
            }

            try
            {
                string json = File.ReadAllText(filePath);

                JsonSerializerOptions options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                MountConfigRoot? config = JsonSerializer.Deserialize<MountConfigRoot>(json, options);
                return config ?? new MountConfigRoot();
            }
            catch (JsonException ex)
            {
                throw new IOException($"Failed to parse mount configuration: {filePath}", ex);
            }
        }

        /// <summary>
        /// Deletes the mount configuration file.
        /// </summary>
        /// <param name="filePath">Path to configuration file (null = use default)</param>
        public static void Delete(string? filePath = null)
        {
            filePath ??= GetDefaultConfigPath();

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }
}
