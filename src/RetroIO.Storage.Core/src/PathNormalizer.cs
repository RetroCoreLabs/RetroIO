//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Path normalization and security validation for URN paths.
    /// Implements RFC 3986 section 5.2 (relative reference resolution) and
    /// section 6.2.2 (path segment normalization with dot-segments).
    ///
    /// Security Features:
    /// - Prevents directory traversal attacks (../ path escaping)
    /// - Removes dot-segments per RFC 3986
    /// - Validates paths stay within filesystem boundaries
    /// - Protects against null bytes and invalid characters
    /// </summary>
    public static class PathNormalizer
    {
        /// <summary>
        /// Normalizes a path according to RFC 3986 section 6.2.2.
        /// Removes dot-segments (. and ..) and ensures security.
        ///
        /// Examples:
        ///   "/a/b/c/./../../g"  → "/a/g"
        ///   "/../../../g"        → "/g" (cannot escape root)
        ///   "/a/b/c/.."         → "/a/b"
        ///   "/.."               → "/" (stays at root)
        ///
        /// Security: Prevents path traversal attacks by ensuring normalized
        /// paths cannot escape the root directory.
        /// </summary>
        /// <param name="path">
        /// Path to normalize (may contain . and .. segments)
        /// </param>
        /// <returns>
        /// Normalized path with dot-segments removed
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Path contains invalid characters
        /// </exception>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "/";
            }

            // Security: Reject paths with null bytes
            if (path.Contains('\0'))
            {
                throw new ArgumentException("Path contains null byte", nameof(path));
            }

            // Convert backslashes to forward slashes (Windows compatibility)
            path = path.Replace('\\', '/');

            // Ensure path starts with /
            if (!path.StartsWith("/"))
            {
                path = "/" + path;
            }

            // Split into segments
            string[] segments = path.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            // Process segments using RFC 3986 algorithm
            List<string> outputSegments = new List<string>();

            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];

                if (segment == ".")
                {
                    // Current directory - skip (RFC 3986: remove ".")
                    continue;
                }
                else if (segment == "..")
                {
                    // Parent directory - pop last segment if available
                    // Security: Cannot go above root - if stack is empty, ignore ".."
                    if (outputSegments.Count > 0)
                    {
                        outputSegments.RemoveAt(outputSegments.Count - 1);
                    }
                    // If we're at root, ".." is ignored (cannot escape root)
                }
                else
                {
                    // Normal segment - add to output
                    outputSegments.Add(segment);
                }
            }

            // Rebuild path
            if (outputSegments.Count == 0)
            {
                return "/";
            }

            StringBuilder result = new StringBuilder();
            for (int i = 0; i < outputSegments.Count; i++)
            {
                result.Append('/');
                result.Append(outputSegments[i]);
            }

            return result.ToString();
        }

        /// <summary>
        /// Resolves a relative reference against a base path per RFC 3986 section 5.2.
        /// This is used when a path doesn't start with / and needs to be resolved
        /// relative to a base directory.
        ///
        /// Examples:
        ///   base="/a/b/c/d", reference="g"     → "/a/b/c/g"
        ///   base="/a/b/c/d", reference="./g"   → "/a/b/c/g"
        ///   base="/a/b/c/d", reference="../g"  → "/a/b/g"
        ///
        /// Security: All paths are normalized after resolution to prevent traversal.
        /// </summary>
        /// <param name="basePath">
        /// Base path (must start with /)
        /// </param>
        /// <param name="reference">
        /// Relative reference
        /// </param>
        /// <returns>
        /// Resolved and normalized absolute path
        /// </returns>
        public static string ResolveRelative(string basePath, string reference)
        {
            if (string.IsNullOrEmpty(reference))
            {
                return Normalize(basePath);
            }

            // If reference is absolute (starts with /), return it normalized
            if (reference.StartsWith("/"))
            {
                return Normalize(reference);
            }

            // Ensure base path is normalized and absolute
            basePath = Normalize(basePath);

            // Remove filename from base path to get directory
            // RFC 3986: "if the base URI's path component is non-empty and does not
            // end with a slash ("/"), then remove the last segment"
            int lastSlash = basePath.LastIndexOf('/');
            string baseDir = (lastSlash >= 0) ? basePath.Substring(0, lastSlash + 1) : "/";

            // Combine and normalize
            string combined = baseDir + reference;
            return Normalize(combined);
        }

        /// <summary>
        /// Validates that a normalized path is safe and doesn't contain
        /// suspicious patterns that might indicate an attack.
        ///
        /// Checks for:
        /// - Null bytes
        /// - Control characters (0x00-0x1F)
        /// - Path traversal patterns that survived normalization
        /// - Absolute paths escaping root
        ///
        /// This is a defense-in-depth check after normalization.
        /// </summary>
        /// <param name="path">
        /// Normalized path to validate
        /// </param>
        /// <returns>
        /// True if <paramref name="path"/> is safe
        /// </returns>
        public static bool IsSafePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            // Must be absolute (normalized paths always start with /)
            if (!path.StartsWith("/"))
            {
                return false;
            }

            // Check for null bytes and control characters
            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];
                if (c == '\0' || (c < 0x20 && c != '/'))
                {
                    return false; // Control character
                }
            }

            // After normalization, these patterns should not exist
            // If they do, something is wrong
            if (path.Contains("..") || path.Contains("./") || path.Contains("//"))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Extracts the parent directory path from a file path.
        /// Returns "/" if already at root or no parent exists.
        ///
        /// Examples:
        ///   "/a/b/c/file.txt" → "/a/b/c"
        ///   "/a/file.txt"     → "/a"
        ///   "/file.txt"       → "/"
        ///   "/"               → "/"
        /// </summary>
        /// <param name="path">
        /// File path
        /// </param>
        /// <returns>
        /// Parent directory path
        /// </returns>
        public static string GetParentDirectory(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
            {
                return "/";
            }

            path = Normalize(path);

            int lastSlash = path.LastIndexOf('/');
            if (lastSlash <= 0)
            {
                return "/";
            }

            return path.Substring(0, lastSlash);
        }

        /// <summary>
        /// Extracts the filename (last segment) from a path.
        ///
        /// Examples:
        ///   "/a/b/c/file.txt" → "file.txt"
        ///   "/a/b/c/"         → ""
        ///   "/"               → ""
        ///   "/file.txt"       → "file.txt"
        /// </summary>
        /// <param name="path">
        /// File path
        /// </param>
        /// <returns>
        /// Filename without directory path
        /// </returns>
        public static string GetFileName(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            path = Normalize(path);

            int lastSlash = path.LastIndexOf('/');
            if (lastSlash < 0 || lastSlash == path.Length - 1)
            {
                return string.Empty;
            }

            return path.Substring(lastSlash + 1);
        }
    }
}
