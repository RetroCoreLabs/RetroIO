//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Text.Json;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Manages persistent storage of unified mount configurations.
    /// Saves/loads mount points for ALL providers to/from a single JSON file.
    /// </summary>
    public static class UnifiedMountConfigStore
    {
        private const string ConfigFileName = "mounts.json";

        /// <summary>
        /// Gets the default configuration file path.
        /// Uses PathHelper if available, otherwise falls back to user profile.
        /// </summary>
        public static string GetDefaultConfigPath()
        {
            // Try to use PathHelper.GetRetroFSPath() via reflection to avoid circular dependency
            try
            {
                Type? pathHelperType = Type.GetType("RetroIO.Storage.Utilities.PathHelper, RetroIO.Storage.Utilities");
                if (pathHelperType != null)
                {
                    System.Reflection.MethodInfo? method = pathHelperType.GetMethod("GetRetroFSPath", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (method != null)
                    {
                        object? result = method.Invoke(null, null);
                        if (result is string retroFsPath)
                        {
                            return Path.Combine(retroFsPath, ConfigFileName);
                        }
                    }
                }
            }
            catch
            {
                // Fall through to default behavior
            }

            // Fallback: use Documents folder
            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string retroFsDir = Path.Combine(documentsPath, "RetroFS");
            return Path.Combine(retroFsDir, ConfigFileName);
        }

        /// <summary>
        /// Saves unified mount configuration to JSON file.
        /// Creates directory if it doesn't exist.
        /// </summary>
        /// <param name="config">Configuration to save</param>
        /// <param name="filePath">Path to configuration file (null = use default)</param>
        /// <exception cref="IOException">Thrown when save operation fails</exception>
        public static void Save(UnifiedMountConfig config, string? filePath = null)
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
        /// Loads unified mount configuration from JSON file.
        /// Returns empty configuration if file doesn't exist.
        /// </summary>
        /// <param name="filePath">Path to configuration file (null = use default)</param>
        /// <returns>Loaded configuration, or empty if file doesn't exist</returns>
        /// <exception cref="IOException">Thrown when load operation fails</exception>
        public static UnifiedMountConfig Load(string? filePath = null)
        {
            filePath ??= GetDefaultConfigPath();

            // Return empty config if file doesn't exist
            if (!File.Exists(filePath))
            {
                return new UnifiedMountConfig();
            }

            try
            {
                string json = File.ReadAllText(filePath);

                JsonSerializerOptions options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                UnifiedMountConfig? config = JsonSerializer.Deserialize<UnifiedMountConfig>(json, options);
                return config ?? new UnifiedMountConfig();
            }
            catch (JsonException ex)
            {
                throw new IOException($"Failed to parse unified mount configuration: {filePath}", ex);
            }
        }

        /// <summary>
        /// Deletes the unified mount configuration file.
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
