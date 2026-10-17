//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Describes the metadata schema for a specific filesystem type.
    /// Each filesystem driver provides a descriptor that defines what metadata properties
    /// it supports, their types, and how to access them.
    /// </summary>
    /// <remarks>
    /// This allows:
    /// - Discovery: Applications can query what properties are available
    /// - Validation: Property values can be validated before setting
    /// - Type Safety: Properties are accessed with correct types
    /// - Documentation: Property descriptions for UI/tools
    /// - Interoperability: Cross-filesystem operations know what metadata exists
    ///
    /// Examples:
    /// - NDFS: "ndfs.friends" (string[]), "ndfs.owner" (string), "ndfs.protection" (string)
    /// - Commodore: "commodore.filetype" (string), "commodore.recordlength" (int)
    /// - Macintosh: "mac.creator" (string), "mac.type" (string)
    /// - RISC OS: "riscos.loadaddr" (uint), "riscos.execaddr" (uint), "riscos.filetype" (ushort)
    /// </remarks>
    public interface IFileSystemMetadataDescriptor
    {
        /// <summary>
        /// Gets the filesystem type this descriptor is for (e.g., "NDFS", "CBM-DOS", "HFS").
        /// </summary>
        string FileSystemType { get; }

        /// <summary>
        /// Gets all properties supported by this filesystem.
        /// </summary>
        /// <returns>Array of property descriptors</returns>
        PropertyDescriptor[] GetSupportedProperties();

        /// <summary>
        /// Gets a specific property descriptor by name.
        /// </summary>
        /// <param name="propertyName">Property name (e.g., "ndfs.friends")</param>
        /// <returns>Property descriptor, or null if not found</returns>
        PropertyDescriptor? GetProperty(string propertyName);

        /// <summary>
        /// Checks if a property is supported by this filesystem.
        /// </summary>
        /// <param name="propertyName">Property name</param>
        /// <returns>True if supported</returns>
        bool SupportsProperty(string propertyName);

        /// <summary>
        /// Gets a property value from file metadata with type safety.
        /// </summary>
        /// <typeparam name="T">Expected type of the property value</typeparam>
        /// <param name="metadata">File metadata containing properties</param>
        /// <param name="propertyName">Property name</param>
        /// <returns>Property value, or default(T) if not found or type mismatch</returns>
        T? GetPropertyValue<T>(FileMetadata metadata, string propertyName);

        /// <summary>
        /// Sets a property value in file metadata with validation.
        /// </summary>
        /// <param name="metadata">File metadata to update</param>
        /// <param name="propertyName">Property name</param>
        /// <param name="value">Property value</param>
        /// <param name="securityContext">Security context for permission checking (optional)</param>
        /// <exception cref="ArgumentException">Thrown when property is not supported or read-only</exception>
        /// <exception cref="ArgumentException">Thrown when value type is incorrect</exception>
        /// <exception cref="UnauthorizedAccessException">Thrown when user lacks permission to set this property</exception>
        void SetPropertyValue(FileMetadata metadata, string propertyName, object value, ISecurityContext? securityContext = null);

        /// <summary>
        /// Validates a property value without setting it.
        /// </summary>
        /// <param name="propertyName">Property name</param>
        /// <param name="value">Property value to validate</param>
        /// <param name="errorMessage">Error message if validation fails</param>
        /// <returns>True if valid, false otherwise</returns>
        bool ValidateProperty(string propertyName, object value, out string? errorMessage);

        /// <summary>
        /// Extracts filesystem-specific properties from generic FileMetadata.
        /// Filters Properties dictionary to only include properties this filesystem recognizes.
        /// </summary>
        /// <param name="metadata">Source metadata</param>
        /// <returns>Dictionary containing only recognized properties for this filesystem</returns>
        System.Collections.Generic.Dictionary<string, object> ExtractFileSystemProperties(FileMetadata metadata);

        /// <summary>
        /// Applies default values for required properties that are not set.
        /// </summary>
        /// <param name="metadata">File metadata to update</param>
        /// <param name="securityContext">Security context to use for ownership/permissions defaults</param>
        void ApplyDefaults(FileMetadata metadata, ISecurityContext? securityContext = null);

        /// <summary>
        /// Checks if a user has permission to read a specific property.
        /// Some properties may be restricted (e.g., reading "ndfs.friends" requires ownership).
        /// </summary>
        /// <param name="propertyName">Property name</param>
        /// <param name="metadata">File metadata containing property</param>
        /// <param name="securityContext">Security context of requesting user</param>
        /// <returns>True if user can read the property</returns>
        bool CanReadProperty(string propertyName, FileMetadata metadata, ISecurityContext securityContext);

        /// <summary>
        /// Checks if a user has permission to write a specific property.
        /// Some properties are restricted (e.g., only owner can change "ndfs.friends").
        /// </summary>
        /// <param name="propertyName">Property name</param>
        /// <param name="metadata">File metadata containing property</param>
        /// <param name="securityContext">Security context of requesting user</param>
        /// <returns>True if user can write the property</returns>
        bool CanWriteProperty(string propertyName, FileMetadata metadata, ISecurityContext securityContext);
    }
}
