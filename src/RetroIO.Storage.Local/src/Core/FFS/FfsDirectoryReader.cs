//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using RetroIO.Storage.Local.FFS.Structures;

namespace RetroIO.Storage.Local.FFS.Core
{
    /// <summary>
    /// Reads and parses FFS directory entries from directory inodes.
    /// Handles variable-length directory entries with 4-byte alignment.
    /// </summary>
    public class FfsDirectoryReader
    {
        private readonly FfsBlockMapper _mapper;

        public FfsDirectoryReader(FfsBlockMapper mapper)
        {
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        }

        /// <summary>
        /// Lists all entries in a directory inode.
        /// </summary>
        /// <param name="dirInode">Directory inode to read.</param>
        /// <param name="includeDots">If true, include "." and ".." entries.</param>
        /// <returns>Array of directory entries.</returns>
        public FfsDirectoryEntry[] ListEntries(FfsInode dirInode, bool includeDots = true)
        {
            if (!dirInode.IsDirectory)
                throw new ArgumentException("Inode is not a directory", nameof(dirInode));

            // Read entire directory contents
            byte[] dirData = _mapper.ReadFile(dirInode);

            if (dirData.Length == 0)
                return Array.Empty<FfsDirectoryEntry>();

            // Parse all entries
            return FfsDirectoryEntry.ParseAll(dirData, includeDots);
        }

        /// <summary>
        /// Finds a specific entry in a directory by name.
        /// </summary>
        /// <param name="dirInode">Directory inode to search.</param>
        /// <param name="name">Entry name to find (case-sensitive).</param>
        /// <returns>Directory entry if found, null otherwise.</returns>
        public FfsDirectoryEntry? FindEntry(FfsInode dirInode, string name)
        {
            if (!dirInode.IsDirectory)
                throw new ArgumentException("Inode is not a directory", nameof(dirInode));

            if (string.IsNullOrEmpty(name))
                return null;

            // Read directory data
            byte[] dirData = _mapper.ReadFile(dirInode);

            if (dirData.Length == 0)
                return null;

            // Parse and search for entry
            int offset = 0;
            while (offset < dirData.Length)
            {
                // Ensure we have enough space for header
                if (offset + FfsConstants.DIRENT_HEADER_SIZE > dirData.Length)
                    break;

                // Parse entry
                var entry = FfsDirectoryEntry.Parse(dirData, offset);

                // Stop only on a genuinely malformed record. d_reclen legitimately runs PAST the
                // end of the data we hold: the last entry in a directory block pads its reclen out
                // to the block boundary, while the data we read is bounded by di_size. On NDIX
                // that gap is guaranteed, because di_size is the packed sum of DIRSIZ rather than a
                // DIRBLKSIZ multiple - root reports 68 while its final entry ('dev') carries
                // reclen 968.
                //
                // Treating that overrun as "stop" silently dropped the LAST entry of every
                // directory, which is why ListDirectory("/") could show 'dev' while
                // ResolvePath("/dev") reported "Directory not found". This mirrors the rule
                // FfsDirectoryEntry.ParseAll already applies, so listing and lookup now agree.
                if (entry.d_reclen == 0 || entry.d_reclen > MAX_REASONABLE_RECLEN)
                    break;

                // Check if this is the entry we're looking for
                if (!entry.IsUnused && string.Equals(entry.d_name, name, StringComparison.Ordinal))
                {
                    return entry;
                }

                // Move to next entry. When reclen overruns the buffer this ends the loop, which is
                // correct - there is nothing after it that we hold.
                offset += entry.d_reclen;
            }

            return null;
        }

        /// <summary>
        /// Upper bound on a plausible d_reclen. The last entry of a block pads out to the block
        /// boundary, so a large value is normal; anything beyond a maximum-size FFS block means
        /// the directory data is corrupt. Matches the limit used by FfsDirectoryEntry.ParseAll.
        /// </summary>
        private const int MAX_REASONABLE_RECLEN = 8192;

        /// <summary>
        /// Finds a specific entry in a directory by name (case-insensitive).
        /// </summary>
        public FfsDirectoryEntry? FindEntryIgnoreCase(FfsInode dirInode, string name)
        {
            if (!dirInode.IsDirectory)
                throw new ArgumentException("Inode is not a directory", nameof(dirInode));

            if (string.IsNullOrEmpty(name))
                return null;

            // Read directory data
            byte[] dirData = _mapper.ReadFile(dirInode);

            if (dirData.Length == 0)
                return null;

            // Parse and search for entry
            int offset = 0;
            while (offset < dirData.Length)
            {
                // Ensure we have enough space for header
                if (offset + FfsConstants.DIRENT_HEADER_SIZE > dirData.Length)
                    break;

                // Parse entry
                var entry = FfsDirectoryEntry.Parse(dirData, offset);

                // Same rule as FindEntry: a reclen that overruns the data we hold is normal for the
                // last entry in a block, not a reason to stop before examining it.
                if (entry.d_reclen == 0 || entry.d_reclen > MAX_REASONABLE_RECLEN)
                    break;

                // Check if this is the entry we're looking for (case-insensitive)
                if (!entry.IsUnused && string.Equals(entry.d_name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }

                // Move to next entry
                offset += entry.d_reclen;
            }

            return null;
        }

        /// <summary>
        /// Counts the number of entries in a directory.
        /// </summary>
        public int CountEntries(FfsInode dirInode, bool includeDots = false)
        {
            if (!dirInode.IsDirectory)
                throw new ArgumentException("Inode is not a directory", nameof(dirInode));

            var entries = ListEntries(dirInode, includeDots);
            return entries.Length;
        }

        /// <summary>
        /// Checks if a directory is empty (contains only "." and "..").
        /// </summary>
        public bool IsEmpty(FfsInode dirInode)
        {
            return CountEntries(dirInode, includeDots: false) == 0;
        }
    }
}
