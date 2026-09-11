//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Describes a single metadata property supported by a filesystem.
    /// </summary>
    public sealed class PropertyDescriptor
    {
        /// <summary>
        /// Property key (internal identifier, e.g., "ndfs.friends", "commodore.filetype").
        /// Used in FileMetadata.Properties dictionary. Not used in filenames.
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Short name for sidecar files and restricted filesystems (e.g., "FRIENDS", "FTYPE", "RECLEN").
        /// Must be filesystem-safe: no dots, max 8 chars, alphanumeric.
        /// </summary>
        public string ShortName { get; set; } = string.Empty;

        /// <summary>
        /// .NET type of the property value (e.g., typeof(string), typeof(int), typeof(string[])).
        /// </summary>
        public Type ValueType { get; set; } = typeof(object);

        /// <summary>
        /// Human-readable description of the property.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Category of the property for grouping in UI.
        /// </summary>
        public PropertyCategory Category { get; set; } = PropertyCategory.Custom;

        /// <summary>
        /// True if the property is read-only (filesystem-managed, not user-settable).
        /// </summary>
        public bool IsReadOnly { get; set; }

        /// <summary>
        /// True if the property is required (must be set when creating files).
        /// </summary>
        public bool IsRequired { get; set; }

        /// <summary>
        /// Default value when property is not set (null = no default).
        /// </summary>
        public object? DefaultValue { get; set; }

        /// <summary>
        /// Optional validation pattern or constraints description.
        /// Examples: "Must be 1-16 characters", "Valid values: R,W,E,D", "0-65535"
        /// </summary>
        public string? ValidationHint { get; set; }
    }

    /// <summary>
    /// Categories for grouping metadata properties.
    /// </summary>
    public enum PropertyCategory
    {
        /// <summary>File type and format information.</summary>
        FileType,

        /// <summary>Timestamps (created, modified, accessed).</summary>
        Timestamps,

        /// <summary>Access control and permissions.</summary>
        Access,

        /// <summary>Ownership and user information.</summary>
        Ownership,

        /// <summary>File attributes and flags.</summary>
        Attributes,

        /// <summary>Filesystem-specific custom properties.</summary>
        Custom
    }
}
