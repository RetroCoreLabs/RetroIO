//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Server.Core.FileSystem;

/// <summary>
/// Security validation for file paths in TNFS protocol.
/// Prevents directory traversal attacks and ensures paths stay within root.
/// </summary>
public static class PathValidator
{
    /// <summary>
    /// Validate that the requested path is safe and within the root directory.
    /// </summary>
    /// <param name="requestedPath">
    /// Path requested by client (relative to session root)
    /// </param>
    /// <param name="sessionRoot">
    /// Session root directory (absolute path)
    /// </param>
    /// <param name="fileSystem">
    /// File system backend (optional, uses System.IO if null)
    /// </param>
    /// <returns>
    /// True if path is safe, false if potentially malicious
    /// </returns>
    public static bool IsPathSafe(string requestedPath, string sessionRoot, IFileSystemBackend? fileSystem = null)
    {
        try
        {
            // Use file system backend if provided, otherwise use System.IO
            if (fileSystem != null)
            {
                var normalizedRoot = fileSystem.GetFullPath(sessionRoot);
                var combinedPath = fileSystem.CombinePath(sessionRoot, requestedPath.TrimStart('/'));
                var normalizedPath = fileSystem.GetFullPath(combinedPath);

                // Normalize separators - convert backslashes to forward slashes for consistent comparison
                normalizedRoot = normalizedRoot.Replace('\\', '/');
                normalizedPath = normalizedPath.Replace('\\', '/');

                // Ensure both paths have trailing separators for comparison
                if (!normalizedRoot.EndsWith("/"))
                    normalizedRoot += "/";
                if (!normalizedPath.EndsWith("/"))
                    normalizedPath += "/";

                return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // Original implementation using System.IO
                var normalizedRoot = Path.GetFullPath(sessionRoot);

                // Ensure root has trailing separator for correct comparison
                if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar))
                    normalizedRoot += Path.DirectorySeparatorChar;

                // Combine and normalize the requested path
                // Path.Combine + Path.GetFullPath will automatically resolve ".." and prevent escaping
                var combinedPath = Path.Combine(sessionRoot, requestedPath.TrimStart('/'));
                var normalizedPath = Path.GetFullPath(combinedPath);

                // Ensure normalized path has trailing separator for comparison
                if (!normalizedPath.EndsWith(Path.DirectorySeparatorChar))
                    normalizedPath += Path.DirectorySeparatorChar;

                // Path must start with root (Path.GetFullPath already resolved all "..")
                return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Any exception during path validation = unsafe
            return false;
        }
    }

    /// <summary>
    /// Normalize path by removing duplicate slashes and converting backslashes.
    /// Similar to C implementation normalize_path().
    /// </summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "/";

        // Replace backslashes with forward slashes
        path = path.Replace('\\', '/');

        // Remove duplicate slashes
        while (path.Contains("//"))
            path = path.Replace("//", "/");

        // Ensure leading slash
        if (!path.StartsWith('/'))
            path = "/" + path;

        // Remove trailing slash (except for root)
        if (path.Length > 1 && path.EndsWith('/'))
            path = path.TrimEnd('/');

        return path;
    }

    /// <summary>
    /// Get the full filesystem path for a TNFS request.
    /// Validates security and returns null if unsafe.
    /// </summary>
    public static string? GetFullPath(string requestedPath, string sessionRoot)
    {
        if (!IsPathSafe(requestedPath, sessionRoot))
            return null;

        var normalized = NormalizePath(requestedPath);
        return Path.Combine(sessionRoot, normalized.TrimStart('/'));
    }

    /// <summary>
    /// Validate directory exists and is accessible.
    /// </summary>
    public static bool IsValidDirectory(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validate file exists and is accessible.
    /// </summary>
    public static bool IsValidFile(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
