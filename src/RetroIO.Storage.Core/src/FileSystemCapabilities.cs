//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Describes the capabilities of a filesystem implementation.
    /// Used to determine if extended attributes (.xat files) are needed during cross-filesystem operations.
    /// </summary>
    public class FileSystemCapabilities
    {
        /// <summary>
        /// Indicates if this filesystem supports extended attributes beyond basic file properties.
        /// Basic properties: FileName, FileSize, CreatedDate, ModifiedDate
        /// Extended properties: User permissions, quotas, file types, custom metadata
        /// </summary>
        public bool SupportsExtendedAttributes { get; set; }

        /// <summary>
        /// Gets or sets the keys <see cref="IExtendedAttributeSupport.GetFileProperties"/> gives for a
        /// file (for example <c>ndfs.access_bits</c>, <c>cbm.file_type</c>). Empty if
        /// <see cref="SupportsExtendedAttributes"/> is false.
        /// </summary>
        /// <remarks>
        /// These are the exact keys of the property bag, nothing else. A rule test checks that every
        /// key a driver's files carry is listed here and every key listed here occurs.
        /// </remarks>
        public string[] SupportedPropertyKeys { get; set; } = System.Array.Empty<string>();

        /// <summary>
        /// Gets or sets the keys <see cref="IExtendedAttributeSupport.WriteFileWithProperties"/>
        /// puts back on the disk; a subset of <see cref="SupportedPropertyKeys"/>.
        /// </summary>
        /// <remarks>
        /// A key that is read but not written back is a fact the disk decides itself (where the
        /// file starts, how many blocks it uses) or one the path already holds. A rule test writes a
        /// file with these keys and checks that each one reads back the same.
        /// </remarks>
        public string[] WritablePropertyKeys { get; set; } = System.Array.Empty<string>();

        /// <summary>
        /// Gets a default capabilities instance for filesystems without extended attributes.
        /// </summary>
        public static FileSystemCapabilities Basic => new FileSystemCapabilities
        {
            SupportsExtendedAttributes = false,
            SupportedPropertyKeys = System.Array.Empty<string>()
        };

        /// <summary>
        /// Checks if a specific property key is supported by this filesystem.
        /// </summary>
        public bool SupportsProperty(string propertyKey)
        {
            if (!SupportsExtendedAttributes)
                return false;

            for (int i = 0; i < SupportedPropertyKeys.Length; i++)
            {
                if (string.Equals(SupportedPropertyKeys[i], propertyKey, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
