//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Filesystem URI scheme identifiers.
    /// Used in URNs to specify explicit filesystem types (e.g., bsd:///path, d64:///path).
    /// </summary>
    public enum FileSystemScheme
    {
        /// <summary>
        /// Unknown or unrecognized scheme.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// file:/// - Native filesystem or auto-detect.
        /// </summary>
        File = 1,

        /// <summary>
        /// v7:/// - UNIX V7 filesystem (1979). Also accepts "bsd:///" for backward compatibility.
        /// </summary>
        V7 = 2,

        /// <summary>
        /// ufs:/// - Unix File System (UFS/FFS) - alias for FFS.
        /// </summary>
        Ufs = 3,

        /// <summary>
        /// ffs:/// - Berkeley Fast File System (FFS/UFS) for SunOS 3.4 / 4.2BSD.
        /// </summary>
        Ffs = 18,

        /// <summary>
        /// d64:/// - Commodore 64 D64 disk image.
        /// </summary>
        D64 = 4,

        /// <summary>
        /// d71:/// - Commodore 64 D71 disk image.
        /// </summary>
        D71 = 5,

        /// <summary>
        /// d81:/// - Commodore 64 D81 disk image.
        /// </summary>
        D81 = 6,

        /// <summary>
        /// ndfs:/// - Nord Disk File System.
        /// </summary>
        Ndfs = 7,

        /// <summary>
        /// t64:/// - Commodore T64 tape archive.
        /// </summary>
        T64 = 8,

        /// <summary>
        /// dsk:/// - Dragon 32/64 DSK/JVC disk image.
        /// </summary>
        Dsk = 9,

        /// <summary>
        /// vdk:/// - Dragon 32/64 VDK disk image.
        /// </summary>
        Vdk = 10,

        /// <summary>
        /// hdd:/// - PC hard disk image (auto-detect partition/filesystem).
        /// </summary>
        Hdd = 11,

        /// <summary>
        /// fat:/// - FAT filesystem (auto-detect FAT12/16/32).
        /// </summary>
        Fat = 12,

        /// <summary>
        /// ftp:/// - FTP server.
        /// </summary>
        Ftp = 13,

        /// <summary>
        /// ftps:/// - FTPS server (FTP over SSL/TLS).
        /// </summary>
        Ftps = 14,

        /// <summary>
        /// sftp:/// - SFTP/SSH server.
        /// </summary>
        Sftp = 15,

        /// <summary>
        /// tnfs:/// - TNFS (Trivial Network File System) server.
        /// </summary>
        Tnfs = 16,

        /// <summary>
        /// zip:/// - ZIP archive.
        /// </summary>
        Zip = 17
    }

    /// <summary>
    /// Extension methods for FileSystemScheme.
    /// </summary>
    public static class FileSystemSchemeExtensions
    {
        /// <summary>
        /// Converts scheme enum to URI scheme string (e.g., V7 → "v7").
        /// </summary>
        public static string ToSchemeString(this FileSystemScheme scheme)
        {
            return scheme switch
            {
                FileSystemScheme.File => "file",
                FileSystemScheme.V7 => "v7",
                FileSystemScheme.Ufs => "ufs",
                FileSystemScheme.Ffs => "ffs",
                FileSystemScheme.D64 => "d64",
                FileSystemScheme.D71 => "d71",
                FileSystemScheme.D81 => "d81",
                FileSystemScheme.Ndfs => "ndfs",
                FileSystemScheme.T64 => "t64",
                FileSystemScheme.Dsk => "dsk",
                FileSystemScheme.Vdk => "vdk",
                FileSystemScheme.Hdd => "hdd",
                FileSystemScheme.Fat => "fat",
                FileSystemScheme.Ftp => "ftp",
                FileSystemScheme.Ftps => "ftps",
                FileSystemScheme.Sftp => "sftp",
                FileSystemScheme.Tnfs => "tnfs",
                FileSystemScheme.Zip => "zip",
                _ => "unknown"
            };
        }

        /// <summary>
        /// Parses URI scheme string to enum (e.g., "v7" → V7, "bsd" → V7 for backward compatibility).
        /// </summary>
        public static FileSystemScheme ParseScheme(string schemeString)
        {
            if (string.IsNullOrEmpty(schemeString))
                return FileSystemScheme.Unknown;

            return schemeString.ToLowerInvariant() switch
            {
                "file" => FileSystemScheme.File,
                "v7" => FileSystemScheme.V7,
                "bsd" => FileSystemScheme.V7,  // Backward compatibility
                "ufs" => FileSystemScheme.Ufs,
                "ffs" => FileSystemScheme.Ffs,
                "d64" => FileSystemScheme.D64,
                "d71" => FileSystemScheme.D71,
                "d81" => FileSystemScheme.D81,
                "ndfs" => FileSystemScheme.Ndfs,
                "t64" => FileSystemScheme.T64,
                "dsk" => FileSystemScheme.Dsk,
                "vdk" => FileSystemScheme.Vdk,
                "hdd" => FileSystemScheme.Hdd,
                "fat" => FileSystemScheme.Fat,
                "ftp" => FileSystemScheme.Ftp,
                "ftps" => FileSystemScheme.Ftps,
                "sftp" => FileSystemScheme.Sftp,
                "tnfs" => FileSystemScheme.Tnfs,
                "zip" => FileSystemScheme.Zip,
                _ => FileSystemScheme.Unknown
            };
        }

        /// <summary>
        /// Maps scheme to filesystem driver type name.
        /// Returns null for schemes that should use auto-detection.
        /// </summary>
        public static string? ToDriverTypeName(this FileSystemScheme scheme)
        {
            return scheme switch
            {
                FileSystemScheme.V7 => "V7",
                FileSystemScheme.Ufs => "FFS",   // Alias for FFS
                FileSystemScheme.Ffs => "FFS",   // Berkeley Fast File System
                FileSystemScheme.D64 => "Commodore",
                FileSystemScheme.D71 => "Commodore",
                FileSystemScheme.D81 => "Commodore",
                FileSystemScheme.Ndfs => "NDFS",
                FileSystemScheme.T64 => "T64",
                FileSystemScheme.Dsk => "DragonDOS-DSK",
                FileSystemScheme.Vdk => "DragonDOS",
                FileSystemScheme.File => null,   // Auto-detect
                FileSystemScheme.Hdd => null,    // Auto-detect partition/filesystem
                FileSystemScheme.Fat => null,    // Auto-detect FAT12/16/32
                FileSystemScheme.Ftp => null,    // Network (not disk image)
                FileSystemScheme.Ftps => null,   // Network
                FileSystemScheme.Sftp => null,   // Network
                FileSystemScheme.Tnfs => null,   // Network
                FileSystemScheme.Zip => null,    // Archive (future)
                _ => null
            };
        }

        /// <summary>
        /// Checks if scheme represents a local disk image.
        /// </summary>
        public static bool IsLocalDiskImage(this FileSystemScheme scheme)
        {
            return scheme switch
            {
                FileSystemScheme.V7 or
                FileSystemScheme.Ufs or
                FileSystemScheme.Ffs or
                FileSystemScheme.D64 or
                FileSystemScheme.D71 or
                FileSystemScheme.D81 or
                FileSystemScheme.Ndfs or
                FileSystemScheme.T64 or
                FileSystemScheme.Dsk or
                FileSystemScheme.Vdk or
                FileSystemScheme.Hdd or
                FileSystemScheme.Fat => true,
                _ => false
            };
        }

        /// <summary>
        /// Checks if scheme represents a network filesystem.
        /// </summary>
        public static bool IsNetworkFilesystem(this FileSystemScheme scheme)
        {
            return scheme switch
            {
                FileSystemScheme.Ftp or
                FileSystemScheme.Ftps or
                FileSystemScheme.Sftp or
                FileSystemScheme.Tnfs => true,
                _ => false
            };
        }
    }
}

