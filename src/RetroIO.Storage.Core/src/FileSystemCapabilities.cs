//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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
        /// Array of supported property keys (e.g., "ndfs:UserName", "ndfs:AccessBits").
        /// Empty if SupportsExtendedAttributes is false.
        /// </summary>
        public string[] SupportedPropertyKeys { get; set; } = System.Array.Empty<string>();

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
