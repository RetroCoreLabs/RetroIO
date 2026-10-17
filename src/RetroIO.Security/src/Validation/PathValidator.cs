//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Text;

namespace RetroIO.Security.Validation;

/// <summary>
/// Path validation result
/// </summary>
public sealed class PathValidationResult
{
    /// <summary>
    /// Whether path is valid
    /// </summary>
    public bool Valid { get; set; }

    /// <summary>
    /// Normalized path (absolute, cleaned)
    /// </summary>
    public string NormalizedPath { get; set; } = string.Empty;

    /// <summary>
    /// Error message (if invalid)
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Create valid result
    /// </summary>
    public static PathValidationResult Success(string normalizedPath)
    {
        return new PathValidationResult
        {
            Valid = true,
            NormalizedPath = normalizedPath
        };
    }

    /// <summary>
    /// Create invalid result
    /// </summary>
    public static PathValidationResult Failure(string error)
    {
        return new PathValidationResult
        {
            Valid = false,
            Error = error
        };
    }
}

/// <summary>
/// Path validator to prevent directory traversal and other path-based attacks
/// </summary>
public static class PathValidator
{
    /// <summary>
    /// Validate and normalize a filesystem path
    /// </summary>
    /// <param name="path">
    /// Path to validate
    /// </param>
    /// <param name="rootPath">
    /// Root directory path (paths must be within this root)
    /// </param>
    /// <returns>
    /// Validation result with normalized path or error
    /// </returns>
    public static PathValidationResult ValidatePath(string path, string rootPath)
    {
        // Check for null or empty
        if (string.IsNullOrWhiteSpace(path))
            return PathValidationResult.Failure("Path cannot be null or empty");

        // Check for null bytes (common attack vector)
        if (path.IndexOf('\0') >= 0)
            return PathValidationResult.Failure("Path contains null byte");

        // Normalize slashes to forward slashes
        string normalizedPath = path.Replace('\\', '/');

        // Check for multiple consecutive slashes
        if (normalizedPath.Contains("//"))
            return PathValidationResult.Failure("Path contains consecutive slashes");

        // Check for parent directory references
        if (normalizedPath.Contains("../") || normalizedPath.Contains("/..") ||
            normalizedPath == ".." || normalizedPath.StartsWith("../"))
            return PathValidationResult.Failure("Path contains parent directory reference (..)");

        // Check for current directory references (not dangerous but unnecessary)
        normalizedPath = normalizedPath.Replace("/./", "/");
        if (normalizedPath.StartsWith("./"))
            normalizedPath = normalizedPath.Substring(2);

        // Ensure path starts with /
        if (!normalizedPath.StartsWith('/'))
            normalizedPath = "/" + normalizedPath;

        // Remove trailing slash (except for root)
        if (normalizedPath.Length > 1 && normalizedPath.EndsWith('/'))
            normalizedPath = normalizedPath.Substring(0, normalizedPath.Length - 1);

        // Combine with root path and get full path
        string fullPath;
        try
        {
            // Normalize root path
            string normalizedRoot = Path.GetFullPath(rootPath);

            // Combine paths
            string relativePath = normalizedPath.TrimStart('/');
            fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));

            // Ensure result is within root directory
            if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return PathValidationResult.Failure("Path traversal attempt detected - path escapes root directory");
        }
        catch (Exception ex)
        {
            return PathValidationResult.Failure($"Invalid path: {ex.Message}");
        }

        // Check for reserved device names (Windows)
        string fileName = Path.GetFileName(normalizedPath);
        if (IsReservedDeviceName(fileName))
            return PathValidationResult.Failure($"Path contains reserved device name: {fileName}");

        return PathValidationResult.Success(normalizedPath);
    }

    /// <summary>
    /// Check if filename is a reserved device name (Windows)
    /// </summary>
    private static bool IsReservedDeviceName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return false;

        // Remove extension for check
        int dotIndex = fileName.IndexOf('.');
        string nameWithoutExt = dotIndex >= 0 ? fileName.Substring(0, dotIndex) : fileName;
        nameWithoutExt = nameWithoutExt.ToUpperInvariant();

        // Reserved names
        string[] reserved = {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        for (int i = 0; i < reserved.Length; i++)
        {
            if (nameWithoutExt == reserved[i])
                return true;
        }

        return false;
    }

    /// <summary>
    /// Validate filename (not full path, just the name)
    /// </summary>
    /// <param name="fileName">
    /// Filename to validate
    /// </param>
    /// <returns>
    /// Validation result
    /// </returns>
    public static PathValidationResult ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return PathValidationResult.Failure("Filename cannot be null or empty");

        // Check for null bytes
        if (fileName.IndexOf('\0') >= 0)
            return PathValidationResult.Failure("Filename contains null byte");

        // Check for path separators
        if (fileName.IndexOf('/') >= 0 || fileName.IndexOf('\\') >= 0)
            return PathValidationResult.Failure("Filename cannot contain path separators");

        // Check for invalid characters
        char[] invalidChars = Path.GetInvalidFileNameChars();
        for (int i = 0; i < fileName.Length; i++)
        {
            char c = fileName[i];
            for (int j = 0; j < invalidChars.Length; j++)
            {
                if (c == invalidChars[j])
                    return PathValidationResult.Failure($"Filename contains invalid character: {c}");
            }
        }

        // Check for reserved device names
        if (IsReservedDeviceName(fileName))
            return PathValidationResult.Failure($"Filename is a reserved device name: {fileName}");

        // Check length (Windows limit is 255, use 250 to be safe)
        if (fileName.Length > 250)
            return PathValidationResult.Failure("Filename is too long (max 250 characters)");

        // Check for leading/trailing spaces or dots (problematic on Windows)
        if (fileName[0] == ' ' || fileName[0] == '.' ||
            fileName[fileName.Length - 1] == ' ' || fileName[fileName.Length - 1] == '.')
            return PathValidationResult.Failure("Filename cannot start or end with space or dot");

        return PathValidationResult.Success(fileName);
    }

    /// <summary>
    /// Sanitize filename by removing invalid characters
    /// </summary>
    /// <param name="fileName">
    /// Filename to sanitize
    /// </param>
    /// <returns>
    /// Sanitized filename
    /// </returns>
    public static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "unnamed";

        StringBuilder sb = new StringBuilder(fileName.Length);
        char[] invalidChars = Path.GetInvalidFileNameChars();

        for (int i = 0; i < fileName.Length; i++)
        {
            char c = fileName[i];
            bool invalid = false;

            // Check if character is invalid
            for (int j = 0; j < invalidChars.Length; j++)
            {
                if (c == invalidChars[j])
                {
                    invalid = true;
                    break;
                }
            }

            if (!invalid && c != '\0')
                sb.Append(c);
        }

        string sanitized = sb.ToString().Trim();

        // Ensure not empty
        if (string.IsNullOrEmpty(sanitized))
            return "unnamed";

        // Ensure not reserved name
        if (IsReservedDeviceName(sanitized))
            sanitized = "_" + sanitized;

        // Ensure not too long
        if (sanitized.Length > 250)
            sanitized = sanitized.Substring(0, 250);

        return sanitized;
    }
}
