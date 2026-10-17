//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BSD.Structures;
using RetroIO.Storage.Local.V7.Structures;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Provider for UNIX V7 filesystem driver (used in BSD 2.11).
    ///
    /// IMPORTANT NAMING NOTE:
    /// This detects the V7 FILESYSTEM FORMAT (from UNIX Version 7, 1979), NOT BSD Disk Labels.
    /// V7 is NOT part of the UFS/FFS family - it's a separate, older filesystem format.
    ///
    /// Detects V7 filesystems by:
    /// - Checking BSD Disk Labels (partition table format) for V7 filesystem type codes
    /// - Checking Sun VTOC labels (partition table format on SunOS)
    /// - Directly validating V7 superblock structures at multiple offsets (0, 512, 1024, 8192)
    ///
    /// Note: BSD Disk Label is a PARTITION TABLE FORMAT (like MBR/GPT), not a filesystem.
    /// This provider detects the V7 FILESYSTEM that may exist within those partitions.
    ///
    /// See also: FfsDriverProvider (for Berkeley Fast File System - newer than V7)
    /// </summary>
    public sealed class V7DriverProvider : FileSystemDriverProviderBase
    {
        public override string FileSystemType => "V7";

        public override string Description => "UNIX V7 filesystem (1979)";

        /// <summary>
        /// Analyzes detection hints to determine if this is a BSD filesystem.
        /// Uses the BSD library's parsing and validation functions.
        /// </summary>
        public override float DetectConfidence(DetectionHints hints)
        {
            float confidence = 0.0f;

            // 1. Check file extension (weak signal)
            string ext = hints.FileExtension.ToLowerInvariant();
            if (ext == ".bsd" || ext == ".img" || ext == ".dsk")
            {
                confidence += 0.05f;  // Very weak signal (many formats use these extensions)
            }

            // 2. Check for BSD disk label at sector 1 (offset 512) using library code
            if (hints.HeaderBytes.Length >= 1024)
            {
                try
                {
                    byte[] labelBytes = new byte[512];
                    Array.Copy(hints.HeaderBytes, 512, labelBytes, 0, Math.Min(512, hints.HeaderBytes.Length - 512));

                    // Use BSD library to parse and validate disk label
                    BsdDiskLabel? diskLabel = null;
                    try
                    {
                        diskLabel = BsdDiskLabel.Parse(labelBytes);
                    }
                    catch
                    {
                        // Parse failed - not a valid disk label
                    }

                    if (diskLabel != null && diskLabel.IsValid())
                    {
                        confidence += 0.6f;  // Very strong signal - valid disk label

                        // Check if partition 'a' contains a V7 filesystem
                        if (diskLabel.NumPartitions > 0)
                        {
                            var partitionA = diskLabel.Partitions[0];
                            if (partitionA.IsV7Filesystem)
                            {
                                confidence += 0.3f;  // Excellent signal - V7 filesystem in partition
                            }
                        }
                    }
                }
                catch
                {
                    // Failed to parse label
                }
            }

            // 3. Check for Sun VTOC disk label at sector 0 (magic 0xDABE at offset 508-509)
            if (hints.HeaderBytes.Length >= 512)
            {
                try
                {
                    // Sun VTOC magic is at offset 508-509 (big-endian 0xDABE)
                    ushort sunMagicLE = BitConverter.ToUInt16(hints.HeaderBytes, 508);
                    ushort sunMagicBE = (ushort)((hints.HeaderBytes[508] << 8) | hints.HeaderBytes[509]);

                    if (sunMagicLE == 0xDABE || sunMagicBE == 0xDABE)
                    {
                        confidence += 0.6f;  // Very strong signal - Sun VTOC label found
                    }
                }
                catch
                {
                    // Failed to check Sun magic
                }
            }

            // 4. Check for V7 superblock - raw filesystem (no disk label)
            // V7Superblock.Parse() automatically tries PDP-endian first, then big-endian (SunOS)
            // Check multiple offsets: 0 (SunOS no boot block), 512, 1024 (standard BSD), 8192 (Sun FFS)
            if (hints.HeaderBytes.Length >= 1024)
            {
                // Check at offset 0 (SunOS - no boot block, superblock starts immediately)
                try
                {
                    byte[] superblockBytes = new byte[1024];
                    Array.Copy(hints.HeaderBytes, 0, superblockBytes, 0, 1024);

                    // Pass buffer unchanged - Parse() handles endianness detection
                    var superblock = V7Superblock.Parse(superblockBytes);

                    if (superblock.Validate())
                    {
                        confidence += 0.7f;  // Very strong signal - valid superblock structure
                    }
                }
                catch
                {
                    // Parse failed - not a valid superblock at this offset
                }

                // Check at offset 1024 (filesystem block 1) - standard BSD location (after 512-byte boot block)
                if (confidence < 0.5f && hints.HeaderBytes.Length >= 2048)
                {
                    try
                    {
                        byte[] superblockBytes = new byte[1024];
                        Array.Copy(hints.HeaderBytes, 1024, superblockBytes, 0, 1024);

                        // Pass buffer unchanged - Parse() handles endianness detection
                        var superblock = V7Superblock.Parse(superblockBytes);

                        if (superblock.Validate())
                        {
                            confidence += 0.7f;  // Very strong signal - valid superblock structure
                        }
                    }
                    catch
                    {
                        // Parse failed - not a valid superblock at this offset
                    }
                }

                // Also check at offset 512 (second half of boot block) - some images have superblock starting here
                if (confidence < 0.5f && hints.HeaderBytes.Length >= 1536)
                {
                    try
                    {
                        byte[] superblockBytes = new byte[1024];
                        Array.Copy(hints.HeaderBytes, 512, superblockBytes, 0, 1024);

                        // Pass buffer unchanged - Parse() handles endianness detection
                        var superblock = V7Superblock.Parse(superblockBytes);

                        if (superblock.Validate())
                        {
                            confidence += 0.7f;  // Very strong signal - valid superblock structure
                        }
                    }
                    catch
                    {
                        // Parse failed
                    }
                }

                // Check at offset 8192 (Sun FFS - superblock at 16 sectors from start, after VTOC label)
                if (confidence < 0.5f && hints.HeaderBytes.Length >= 9216)
                {
                    try
                    {
                        byte[] superblockBytes = new byte[1024];
                        Array.Copy(hints.HeaderBytes, 8192, superblockBytes, 0, 1024);

                        // Pass buffer unchanged - Parse() handles endianness detection
                        var superblock = V7Superblock.Parse(superblockBytes);

                        if (superblock.Validate())
                        {
                            confidence += 0.7f;  // Very strong signal - valid superblock structure
                        }
                    }
                    catch
                    {
                        // Parse failed - not a valid superblock at this offset
                    }
                }
            }

            return Math.Min(confidence, 1.0f);
        }

        public override IFileSystemDriver CreateDriver()
        {
            return new V7Driver();
        }
    }
}

