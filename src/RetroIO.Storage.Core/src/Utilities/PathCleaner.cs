//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;

namespace RetroIO.Storage.Core.Utilities
{
    /// <summary>
    /// Utility class for cleaning and normalizing file paths.
    /// </summary>
    public static class PathCleaner
    {
        /// <summary>
        /// Extracts a clean file path from a URN by removing scheme and fragment.
        /// </summary>
        /// <param name="uri">URN (e.g., "bsd:///E:/path#a", "file:///C:/test.txt")</param>
        /// <returns>Clean file path (e.g., "E:/path", "C:/test.txt")</returns>
        public static string ExtractFilePathFromUri(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return string.Empty;

            string path = uri;

            // Remove scheme prefix if present (e.g., "bsd://", "file://")
            int schemeEnd = uri.IndexOf("://");
            if (schemeEnd > 0)
            {
                path = uri.Substring(schemeEnd + 3);
            }

            // Remove fragment (e.g., "#a", "#partition1")
            int fragmentIndex = path.IndexOf('#');
            if (fragmentIndex >= 0)
            {
                path = path.Substring(0, fragmentIndex);
            }

            // Normalize path separators and remove leading slash
            return NormalizePath(path);
        }

        /// <summary>
        /// Normalizes a file path by converting separators and removing invalid prefixes.
        /// </summary>
        /// <param name="path">Input path (e.g., "/E:/path", "/C:/folder/file.txt", "/home/user/file")</param>
        /// <returns>Normalized path (e.g., "E:/path", "C:/folder/file.txt", "/home/user/file")</returns>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            // Windows-specific: Remove leading slash for Windows drive letters only
            if (OperatingSystem.IsWindows())
            {
                // Handle cases like "/E:/path" -> "E:/path"
                if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
                {
                    path = path.Substring(1);
                }

                // Handle double leading slash for custom schemes on Windows
                // Handles cases like "//E:/path" -> "E:/path" (from bsd:///E:/path)
                if (path.Length >= 4 && path[0] == '/' && path[1] == '/' && char.IsLetter(path[2]) && path[3] == ':')
                {
                    path = path.Substring(2);
                }
            }

            // Convert forward slashes to platform-appropriate separators
            // On Windows: / -> \, On Unix: / stays /
            path = path.Replace('/', Path.DirectorySeparatorChar);

            return path;
        }

        /// <summary>
        /// Validates that a file path exists and is accessible.
        /// </summary>
        /// <param name="path">File path to validate</param>
        /// <param name="errorMessage">Error message if validation fails</param>
        /// <returns>True if path is valid, false otherwise</returns>
        public static bool ValidateFilePath(string path, out string? errorMessage)
        {
            errorMessage = null;

            if (string.IsNullOrEmpty(path))
            {
                errorMessage = "File path is null or empty";
                return false;
            }

            try
            {
                if (!File.Exists(path))
                {
                    errorMessage = $"File not found: {path}";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"Error accessing file '{path}': {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Creates a URN from components.
        /// </summary>
        /// <param name="scheme">Scheme (e.g., "bsd", "file")</param>
        /// <param name="path">File path</param>
        /// <param name="fragment">Optional fragment (e.g., "a", "partition1")</param>
        /// <returns>Complete URN (e.g., "bsd:///E:/path#a")</returns>
        public static string CreateUri(string scheme, string path, string? fragment = null)
        {
            if (string.IsNullOrEmpty(scheme) || string.IsNullOrEmpty(path))
                return string.Empty;

            // Normalize the path
            path = NormalizePath(path);

            // Convert backslashes to forward slashes for URI format
            path = path.Replace('\\', '/');

            // Ensure path starts with /
            if (!path.StartsWith("/"))
                path = "/" + path;

            string uri = $"{scheme}://{path}";

            if (!string.IsNullOrEmpty(fragment))
            {
                uri += $"#{fragment}";
            }

            return uri;
        }
    }
}
