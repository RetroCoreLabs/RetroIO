//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Flags describing filesystem driver capabilities.
    /// Used for runtime capability detection and feature negotiation.
    /// </summary>
    [Flags]
    public enum DriverCapabilities : ulong
    {
        /// <summary>
        /// No capabilities.
        /// </summary>
        None = 0,

        // === File Operations ===

        /// <summary>
        /// Driver supports reading files.
        /// </summary>
        Read = 1 << 0,

        /// <summary>
        /// Driver supports writing new files.
        /// </summary>
        Write = 1 << 1,

        /// <summary>
        /// Driver supports deleting files.
        /// </summary>
        Delete = 1 << 2,

        /// <summary>
        /// Driver supports renaming files.
        /// </summary>
        Rename = 1 << 3,

        /// <summary>
        /// Driver supports appending to existing files.
        /// </summary>
        Append = 1 << 4,

        /// <summary>
        /// Driver supports truncating files.
        /// </summary>
        Truncate = 1 << 5,

        // === Directory Operations ===

        /// <summary>
        /// Driver supports listing directory contents.
        /// </summary>
        ListDirectory = 1 << 6,

        /// <summary>
        /// Driver supports nested directories (subdirectories).
        /// </summary>
        Subdirectories = 1 << 7,

        /// <summary>
        /// Driver supports creating new directories.
        /// </summary>
        CreateDirectory = 1 << 8,

        /// <summary>
        /// Driver supports deleting directories.
        /// </summary>
        DeleteDirectory = 1 << 9,

        /// <summary>
        /// Driver supports renaming directories.
        /// </summary>
        RenameDirectory = 1 << 10,

        // === Metadata Operations ===

        /// <summary>
        /// Driver supports file timestamps (creation, modification, access).
        /// </summary>
        Timestamps = 1 << 11,

        /// <summary>
        /// Driver supports file permissions/attributes.
        /// </summary>
        Permissions = 1 << 12,

        /// <summary>
        /// Driver supports file locking.
        /// </summary>
        FileLocking = 1 << 13,

        /// <summary>
        /// Driver supports extended attributes or metadata.
        /// </summary>
        ExtendedAttributes = 1 << 14,

        /// <summary>
        /// Driver supports file type identification (PRG, SEQ, etc.).
        /// </summary>
        FileTypes = 1 << 15,

        // === Block-Level Operations ===

        /// <summary>
        /// Driver supports block-level read access.
        /// </summary>
        BlockRead = 1 << 16,

        /// <summary>
        /// Driver supports block-level write access.
        /// </summary>
        BlockWrite = 1 << 17,

        /// <summary>
        /// Driver supports formatting/initializing volumes.
        /// </summary>
        Format = 1 << 18,

        /// <summary>
        /// Driver supports BAM (Block Availability Map) access.
        /// </summary>
        BlockAllocationMap = 1 << 19,

        // === Special Features ===

        /// <summary>
        /// Driver supports symbolic links.
        /// </summary>
        SymbolicLinks = 1 << 20,

        /// <summary>
        /// Driver supports hard links.
        /// </summary>
        HardLinks = 1 << 21,

        /// <summary>
        /// Driver supports case-sensitive filenames.
        /// </summary>
        CaseSensitive = 1 << 22,

        /// <summary>
        /// Driver supports long filenames (> 16 characters).
        /// </summary>
        LongFilenames = 1 << 23,

        /// <summary>
        /// Driver supports Unicode filenames.
        /// </summary>
        UnicodeFilenames = 1 << 24,

        /// <summary>
        /// Driver supports sparse files.
        /// </summary>
        SparseFiles = 1 << 25,

        /// <summary>
        /// Driver supports compression.
        /// </summary>
        Compression = 1 << 26,

        /// <summary>
        /// Driver supports encryption.
        /// </summary>
        Encryption = 1 << 27,

        // === Volume Operations ===

        /// <summary>
        /// Driver supports volume labels.
        /// </summary>
        VolumeLabel = 1 << 28,

        /// <summary>
        /// Driver supports volume IDs (serial numbers).
        /// </summary>
        VolumeId = 1 << 29,

        /// <summary>
        /// Driver supports volume quotas.
        /// </summary>
        Quotas = 1 << 30,

        /// <summary>
        /// Driver supports multiple partitions per image.
        /// </summary>
        Partitions = 1UL << 31,

        // === Advanced Features ===

        /// <summary>
        /// Driver supports journaling/transactions.
        /// </summary>
        Journaling = 1UL << 32,

        /// <summary>
        /// Driver supports snapshots.
        /// </summary>
        Snapshots = 1UL << 33,

        /// <summary>
        /// Driver supports defragmentation.
        /// </summary>
        Defragmentation = 1UL << 34,

        /// <summary>
        /// Driver supports error correction/recovery.
        /// </summary>
        ErrorCorrection = 1UL << 35,

        // === Character Encoding ===

        /// <summary>
        /// Driver uses ASCII encoding for filenames.
        /// </summary>
        AsciiEncoding = 1UL << 36,

        /// <summary>
        /// Driver uses PETSCII encoding for filenames (Commodore).
        /// </summary>
        PetsciiEncoding = 1UL << 37,

        /// <summary>
        /// Driver uses ATASCII encoding for filenames (Atari).
        /// </summary>
        AtasciiEncoding = 1UL << 38,

        // === Common Combinations ===

        /// <summary>
        /// Basic read-only capabilities (most drivers).
        /// </summary>
        ReadOnly = Read | ListDirectory | VolumeLabel,

        /// <summary>
        /// Basic read-write capabilities.
        /// </summary>
        ReadWrite = Read | Write | Delete | ListDirectory | VolumeLabel,

        /// <summary>
        /// Full file operations (no directories).
        /// </summary>
        FullFileOps = Read | Write | Delete | Rename | Append | Truncate,

        /// <summary>
        /// Full directory operations.
        /// </summary>
        FullDirOps = ListDirectory | Subdirectories | CreateDirectory | DeleteDirectory | RenameDirectory,

        /// <summary>
        /// Full block operations.
        /// </summary>
        FullBlockOps = BlockRead | BlockWrite | Format | BlockAllocationMap,

        /// <summary>
        /// All metadata features.
        /// </summary>
        FullMetadata = Timestamps | Permissions | FileLocking | ExtendedAttributes | FileTypes
    }

    /// <summary>
    /// Extension methods for DriverCapabilities.
    /// </summary>
    public static class DriverCapabilitiesExtensions
    {
        /// <summary>
        /// Checks if driver has specified capability.
        /// </summary>
        public static bool Has(this DriverCapabilities caps, DriverCapabilities required)
        {
            return (caps & required) == required;
        }

        /// <summary>
        /// Checks if driver has any of the specified capabilities.
        /// </summary>
        public static bool HasAny(this DriverCapabilities caps, DriverCapabilities options)
        {
            return (caps & options) != 0;
        }

        /// <summary>
        /// Checks if driver supports basic read operations.
        /// </summary>
        public static bool CanRead(this DriverCapabilities caps)
        {
            return caps.Has(DriverCapabilities.Read);
        }

        /// <summary>
        /// Checks if driver supports basic write operations.
        /// </summary>
        public static bool CanWrite(this DriverCapabilities caps)
        {
            return caps.Has(DriverCapabilities.Write);
        }

        /// <summary>
        /// Checks if driver supports subdirectories.
        /// </summary>
        public static bool HasSubdirectories(this DriverCapabilities caps)
        {
            return caps.Has(DriverCapabilities.Subdirectories);
        }

        /// <summary>
        /// Checks if driver supports block-level access.
        /// </summary>
        public static bool HasBlockAccess(this DriverCapabilities caps)
        {
            return caps.HasAny(DriverCapabilities.BlockRead | DriverCapabilities.BlockWrite);
        }
    }
}
