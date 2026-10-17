//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Metadata about a file or directory in a retro filesystem.
    /// Uses property bag pattern for filesystem-specific attributes.
    /// </summary>
    public sealed class FileMetadata
    {
        /// <summary>
        /// File or directory name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Full path to the file or directory.
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// True if this is a directory.
        /// </summary>
        public bool IsDirectory { get; set; }

        /// <summary>
        /// File size in bytes (0 for directories).
        /// </summary>
        public long Size { get; set; }

        /// <summary>
        /// Creation timestamp (null if not supported by filesystem).
        /// </summary>
        public DateTime? CreatedTime { get; set; }

        /// <summary>
        /// Last modification timestamp (null if not supported).
        /// </summary>
        public DateTime? ModifiedTime { get; set; }

        /// <summary>
        /// Last access timestamp (null if not supported).
        /// </summary>
        public DateTime? AccessedTime { get; set; }

        /// <summary>
        /// File type (e.g., "PRG", "SEQ", "USR" for Commodore; "TEXT", "BINA" for Macintosh).
        /// </summary>
        public string? FileType { get; set; }

        /// <summary>
        /// File attributes as string (e.g., "R" for read-only, "H" for hidden).
        /// </summary>
        public string? Attributes { get; set; }

        /// <summary>
        /// URN-safe identifier for this file.
        /// Generated from the filename with URL-safe encoding.
        /// Used for constructing URN references: urn:retrofs:local::volume:/identifier
        /// </summary>
        public string? UrnIdentifier { get; set; }

        /// <summary>
        /// Property bag for filesystem-specific metadata.
        /// Examples:
        /// - Commodore: ["RecordLength"] = 80, ["Splat"] = false
        /// - Macintosh: ["Creator"] = "MSWW", ["Type"] = "TEXT"
        /// - NDFS: ["Owner"] = "SYSTEM", ["Protection"] = "RWED"
        /// </summary>
        public Dictionary<string, object> Properties { get; } = new Dictionary<string, object>();

        /// <summary>
        /// Gets a property value cast to the specified type.
        /// </summary>
        /// <typeparam name="T">
        /// Type to cast to
        /// </typeparam>
        /// <param name="key">
        /// Property key
        /// </param>
        /// <returns>
        /// Property value, or default(T) if not found
        /// </returns>
        public T? GetProperty<T>(string key)
        {
            if (Properties.TryGetValue(key, out object? value))
            {
                if (value is T typedValue)
                {
                    return typedValue;
                }
            }
            return default;
        }

        /// <summary>
        /// Sets a property value.
        /// </summary>
        /// <param name="key">
        /// Property key
        /// </param>
        /// <param name="value">
        /// Property value
        /// </param>
        public void SetProperty(string key, object value)
        {
            Properties[key] = value;
        }

        /// <summary>
        /// Checks if a property exists.
        /// </summary>
        /// <param name="key">
        /// Property key
        /// </param>
        /// <returns>
        /// True if property exists
        /// </returns>
        public bool HasProperty(string key)
        {
            return Properties.ContainsKey(key);
        }
    }
}
