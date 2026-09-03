//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;

namespace RetroIO.Storage.Utilities
{
    /// <summary>
    /// Helper class for managing RetroFS user data paths across platforms.
    /// Follows platform conventions: Documents\RetroFS on Windows, XDG on Linux/macOS.
    /// </summary>
    public static class PathHelper
    {
        /// <summary>
        /// Name of the environment variable that overrides the user data directory.
        /// </summary>
        /// <remarks>
        /// Set this on a CHILD process (ProcessStartInfo.Environment) to point a spawned server at
        /// a throwaway directory. The TNFS integration tests need it: the server rewrites its own
        /// tnfs-config.json on every start, so without the override a test run silently replaces
        /// the developer's real server configuration with the test ports and the test root.
        /// </remarks>
        public const string DataPathEnvironmentVariable = "RETROFS_DATA_PATH";

        /// <summary>
        /// Gets the base RetroFS user data directory path.
        /// Windows: Documents\RetroFS
        /// Linux/macOS: ~/.local/share/retrofs (follows XDG Base Directory specification)
        /// Overridden in full by the RETROFS_DATA_PATH environment variable when it is set.
        /// </summary>
        /// <returns>Full path to the RetroFS user data directory</returns>
        public static string GetRetroFSPath()
        {
            string basePath;

            // Checked before the platform defaults so a caller can redirect everything -- config,
            // identity provider, logs -- with one variable.
            string? dataPathOverride = Environment.GetEnvironmentVariable(DataPathEnvironmentVariable);
            if (!string.IsNullOrEmpty(dataPathOverride))
            {
                return dataPathOverride;
            }

            if (OperatingSystem.IsWindows())
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                basePath = Path.Combine(documentsPath, "RetroFS");
            }
            else
            {
                // Linux/macOS: Follow XDG Base Directory Specification
                string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string? xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

                if (string.IsNullOrEmpty(xdgDataHome))
                {
                    xdgDataHome = Path.Combine(homeDir, ".local", "share");
                }

                basePath = Path.Combine(xdgDataHome, "retrofs");
            }

            return basePath;
        }

        /// <summary>
        /// Gets the test images directory path.
        /// Windows: Documents\RetroFS\test-images
        /// Linux/macOS: ~/.local/share/retrofs/test-images
        /// </summary>
        /// <returns>Full path to the test images directory</returns>
        public static string GetTestImagesPath()
        {
            string basePath = GetRetroFSPath();
            return Path.Combine(basePath, "test-images");
        }

        /// <summary>
        /// Gets the test images path for a specific filesystem type.
        /// </summary>
        /// <param name="fileSystemType">Filesystem type (e.g., "d64", "hfs", "adf")</param>
        /// <returns>Full path to the filesystem-specific test images directory</returns>
        public static string GetTestImagesPath(string fileSystemType)
        {
            if (string.IsNullOrEmpty(fileSystemType))
            {
                throw new ArgumentException("Filesystem type cannot be null or empty.", nameof(fileSystemType));
            }

            string testImagesPath = GetTestImagesPath();
            return Path.Combine(testImagesPath, fileSystemType.ToLowerInvariant());
        }

        /// <summary>
        /// Gets the configuration directory path.
        /// Windows: Documents\RetroFS\config
        /// Linux/macOS: ~/.config/retrofs (XDG_CONFIG_HOME)
        /// </summary>
        /// <returns>Full path to the configuration directory</returns>
        public static string GetConfigPath()
        {
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(GetRetroFSPath(), "config");
            }
            else
            {
                // Linux/macOS: Follow XDG Base Directory Specification for config
                string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string? xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

                if (string.IsNullOrEmpty(xdgConfigHome))
                {
                    xdgConfigHome = Path.Combine(homeDir, ".config");
                }

                return Path.Combine(xdgConfigHome, "retrofs");
            }
        }

        /// <summary>
        /// Gets the cache directory path.
        /// Windows: Documents\RetroFS\cache
        /// Linux/macOS: ~/.cache/retrofs (XDG_CACHE_HOME)
        /// </summary>
        /// <returns>Full path to the cache directory</returns>
        public static string GetCachePath()
        {
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(GetRetroFSPath(), "cache");
            }
            else
            {
                // Linux/macOS: Follow XDG Base Directory Specification for cache
                string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string? xdgCacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");

                if (string.IsNullOrEmpty(xdgCacheHome))
                {
                    xdgCacheHome = Path.Combine(homeDir, ".cache");
                }

                return Path.Combine(xdgCacheHome, "retrofs");
            }
        }

        /// <summary>
        /// Ensures a directory exists, creating it if necessary.
        /// </summary>
        /// <param name="path">Directory path to ensure exists</param>
        /// <returns>The same path that was passed in</returns>
        public static string EnsureDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            return path;
        }

        /// <summary>
        /// Ensures all RetroFS directories exist.
        /// Creates base path, test images, config, and cache directories.
        /// </summary>
        public static void EnsureRetroFSDirectories()
        {
            EnsureDirectory(GetRetroFSPath());
            EnsureDirectory(GetTestImagesPath());
            EnsureDirectory(GetConfigPath());
            EnsureDirectory(GetCachePath());

            // Create filesystem-specific test image directories
            string[] fileSystems = new[] { "d64", "ndfs", "hfs", "adf", "prodos", "dfs" };
            for (int i = 0; i < fileSystems.Length; i++)
            {
                EnsureDirectory(GetTestImagesPath(fileSystems[i]));
            }
        }

        /// <summary>
        /// Gets a README file path for the test images directory.
        /// </summary>
        /// <returns>Full path to TestImages README.md</returns>
        public static string GetTestImagesReadmePath()
        {
            return Path.Combine(GetTestImagesPath(), "README.md");
        }
    }
}
