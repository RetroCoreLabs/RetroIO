//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Represents an entry in a directory listing.
    /// Combines name with full metadata about the file or directory.
    /// </summary>
    public sealed class DirectoryEntry
    {
        /// <summary>
        /// File or directory name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Full metadata for this entry.
        /// </summary>
        public FileMetadata Metadata { get; set; } = new FileMetadata();
    }
}
