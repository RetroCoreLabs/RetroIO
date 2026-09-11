//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using RetroIO.Storage.Local.BSD.Structures;
using RetroIO.Storage.Local.V7.Structures;

namespace RetroIO.Storage.Local.V7.Core
{
    /// <summary>
    /// Reads and parses directory entries from directory inodes.
    /// </summary>
    public class V7DirectoryReader
    {
        private readonly V7BlockMapper _mapper;

        public V7DirectoryReader(V7BlockMapper mapper)
        {
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        }

        /// <summary>
        /// Reads all directory entries from a directory inode.
        /// </summary>
        /// <param name="dirInode">Directory inode.</param>
        /// <returns>Array of directory entries (including "." and "..").</returns>
        public V7DirectoryEntry[] ReadDirectory(V7Inode dirInode)
        {
            if (!dirInode.IsDirectory)
                throw new ArgumentException("Inode is not a directory", nameof(dirInode));

            // Read entire directory contents
            byte[] dirData = _mapper.ReadFile(dirInode);

            // Parse entries
            var entries = new List<V7DirectoryEntry>();
            int offset = 0;

            while (offset < dirData.Length)
            {
                var entry = V7DirectoryEntry.Parse(dirData, offset);

                if (entry == null || entry.RecLen == 0)
                    break;

                // Skip deleted entries (inode == 0), but process them for potential recovery
                // For now, we'll include only valid entries
                if (!entry.IsDeleted)
                {
                    entries.Add(entry);
                }

                offset += entry.RecLen;

                // Safety check to prevent infinite loops
                if (entry.RecLen < V7Constants.MIN_DIRENT_SIZE)
                    break;
            }

            return entries.ToArray();
        }

        /// <summary>
        /// Finds a directory entry by name within a directory.
        /// </summary>
        /// <param name="dirInode">Directory inode.</param>
        /// <param name="name">Entry name to find.</param>
        /// <returns>Directory entry if found, null otherwise.</returns>
        public V7DirectoryEntry? FindEntry(V7Inode dirInode, string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            var entries = ReadDirectory(dirInode);

            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Name.Equals(name, StringComparison.Ordinal))
                {
                    return entries[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Checks if a directory is empty (contains only "." and "..").
        /// </summary>
        public bool IsDirectoryEmpty(V7Inode dirInode)
        {
            var entries = ReadDirectory(dirInode);

            for (int i = 0; i < entries.Length; i++)
            {
                if (!entries[i].IsDot && !entries[i].IsDotDot)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Resolves a path starting from a given directory inode.
        /// </summary>
        /// <param name="startInode">Starting directory inode.</param>
        /// <param name="inodeReader">Inode reader for following references.</param>
        /// <param name="path">Path to resolve (e.g., "usr/bin/ls").</param>
        /// <returns>Inode number of the target, or 0 if not found.</returns>
        public ushort ResolvePath(V7Inode startInode, V7InodeReader inodeReader, string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/" || path == ".")
            {
                // Return current directory (we don't have inode number here, caller must track it)
                return 0;
            }

            // Split path into components
            string[] components = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            if (components.Length == 0)
                return 0;

            V7Inode currentInode = startInode;
            ushort currentInodeNum = V7Constants.ROOT_INODE; // Caller should provide this

            for (int i = 0; i < components.Length; i++)
            {
                if (!currentInode.IsDirectory)
                    return 0; // Not a directory

                var entry = FindEntry(currentInode, components[i]);

                if (entry == null)
                    return 0; // Component not found

                currentInodeNum = entry.Inode;
                currentInode = inodeReader.ReadInode(currentInodeNum);
            }

            return currentInodeNum;
        }

        /// <summary>
        /// Lists all non-deleted entries (excluding "." and "..").
        /// </summary>
        public V7DirectoryEntry[] ListEntries(V7Inode dirInode, bool includeDots = false)
        {
            var allEntries = ReadDirectory(dirInode);
            var filteredEntries = new List<V7DirectoryEntry>();

            for (int i = 0; i < allEntries.Length; i++)
            {
                if (includeDots || (!allEntries[i].IsDot && !allEntries[i].IsDotDot))
                {
                    filteredEntries.Add(allEntries[i]);
                }
            }

            return filteredEntries.ToArray();
        }
    }
}

