//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Storage.Ndfs.Elements;

namespace RetroIO.Storage.Ndfs.FileSystems
{
    /// <summary>
    /// Represents an open file handle for random access operations.
    /// Maintains file metadata and state for efficient read/write operations.
    /// </summary>
    public class NdfsFileHandle
    {
        /// <summary>
        /// Unique handle ID for this open file.
        /// </summary>
        public int HandleId { get; set; }

        /// <summary>
        /// Full path to the file (e.g., "USERNAME/FILENAME.TXT").
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// Reference to the ObjectEntry for this file.
        /// </summary>
        public ObjectEntry Entry { get; set; } = null!;

        /// <summary>
        /// Current file position for sequential operations (optional).
        /// </summary>
        public long Position { get; set; }

        /// <summary>
        /// Whether this handle is writable.
        /// </summary>
        public bool CanWrite { get; set; }

        /// <summary>
        /// Whether the file has been modified.
        /// </summary>
        public bool IsDirty { get; set; }
    }
}
