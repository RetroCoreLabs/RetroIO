//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.FFS.Core;
using RetroIO.Storage.Local.FFS.Structures;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Provider for Berkeley Fast File System (FFS/UFS) driver.
    ///
    /// IMPORTANT NAMING NOTE:
    /// FFS (Fast File System) = UFS (Unix File System) - they are the SAME filesystem.
    /// This is a modern BSD filesystem format, distinct from the older V7 filesystem.
    ///
    /// FFS/UFS is NOT the same as:
    /// - V7 filesystem (older, from UNIX Version 7, 1979)
    /// - BSD Disk Label (a partition table format, like MBR/GPT)
    ///
    /// Detects FFS/UFS filesystems by:
    /// - Checking for FFS superblock magic (0x011954) at offset 8192
    /// - Validating superblock structure (cylinder groups, fragments, etc.)
    /// - Optionally checking for Sun VTOC labels (partition table format on SunOS)
    ///
    /// See also: V7DriverProvider (for V7 filesystem - older than FFS)
    /// </summary>
    public sealed class FfsDriverProvider : FileSystemDriverProviderBase
    {
        public override string FileSystemType => "FFS";

        public override string Description => "Berkeley Fast File System (FFS/UFS)";

        /// <summary>
        /// FFS requires reading the superblock at offset 8192 + full superblock size (2048 bytes).
        /// Total: 8192 + 2048 = 10240 bytes minimum.
        /// </summary>
        public override int RequiredHeaderBytes => 10240;

        /// <summary>
        /// Analyzes detection hints to determine if this is an FFS filesystem.
        /// </summary>
        public override float DetectConfidence(DetectionHints hints)
        {
            float confidence = 0.0f;

            // 1. Check file extension (weak signal)
            string ext = hints.FileExtension.ToLowerInvariant();
            if (ext == ".ffs" || ext == ".ufs" || ext == ".img" || ext == ".dsk")
            {
                confidence += 0.05f;  // Very weak signal (many formats use these extensions)
            }

            // 2. Check for FFS superblock at offset 8192 (standard location)
            // FFS superblock magic is at offset 8192 + 1372 = 9564 bytes from start
            const int SBLOCK_OFFSET = 8192;
            const int MAGIC_OFFSET = 1372;
            int totalOffset = SBLOCK_OFFSET + MAGIC_OFFSET;

            if (hints.HeaderBytes.Length >= totalOffset + 4)
            {
                try
                {
                    // Read magic number at offset 9564 (big-endian)
                    uint magic = ReadUInt32BE(hints.HeaderBytes, totalOffset);

                    if (magic == FfsConstants.FS_MAGIC)
                    {
                        confidence += 0.8f;  // Very strong signal - valid FFS magic

                        // Try to parse and validate superblock for additional confirmation
                        if (hints.HeaderBytes.Length >= SBLOCK_OFFSET + FfsConstants.SBSIZE)
                        {
                            try
                            {
                                byte[] superblockBytes = new byte[FfsConstants.SBSIZE];
                                Array.Copy(hints.HeaderBytes, SBLOCK_OFFSET, superblockBytes, 0,
                                    Math.Min(FfsConstants.SBSIZE, hints.HeaderBytes.Length - SBLOCK_OFFSET));

                                var superblock = FfsSuperblock.Parse(superblockBytes);

                                if (superblock.Validate())
                                {
                                    confidence += 0.15f;  // Excellent signal - valid superblock structure
                                }
                            }
                            catch
                            {
                                // Parse failed - but magic was valid, so still likely FFS
                            }
                        }
                    }
                }
                catch
                {
                    // Failed to check magic
                }
            }

            // 2b. No superblock at the standard place. The filesystem may still be present but
            // start further into the image, with no partition table to say where — a flat NDIX
            // di70 image begins one cylinder in, at byte 90 * 1024 = 92160. Scan for it.
            // This costs a bounded read (1 MB) and only runs once the cheap check has failed.
            if (confidence < 0.5f && hints.HeaderBytes.Length >= totalOffset + 4 && hints.FilePath.Length > 0)
            {
                try
                {
                    using (var stream = new FileStream(hints.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (FfsSuperblockLocator.TryLocate(stream, out long baseOffset, out _) && baseOffset > 0)
                        {
                            // Same weight as a superblock found at offset 8192: the locator has
                            // already parsed, validated and size-checked it before saying yes.
                            confidence += 0.95f;
                        }
                    }
                }
                catch
                {
                    // Unreadable file - leave confidence as-is
                }
            }

            // 3. Check for Sun VTOC disk label at sector 0 (magic 0xDABE at offset 508-509)
            // FFS is commonly used with Sun VTOC labels
            if (hints.HeaderBytes.Length >= 512)
            {
                try
                {
                    // Sun VTOC magic is at offset 508-509 (big-endian 0xDABE)
                    ushort sunMagicBE = ReadUInt16BE(hints.HeaderBytes, 508);

                    if (sunMagicBE == 0xDABE)
                    {
                        confidence += 0.1f;  // Additional signal - Sun VTOC label present
                    }
                }
                catch
                {
                    // Failed to check Sun magic
                }
            }

            return Math.Min(confidence, 1.0f);
        }

        public override IFileSystemDriver CreateDriver()
        {
            return new FfsDriver();
        }

        private static uint ReadUInt32BE(byte[] buffer, int offset)
        {
            return ((uint)buffer[offset] << 24) |
                   ((uint)buffer[offset + 1] << 16) |
                   ((uint)buffer[offset + 2] << 8) |
                   buffer[offset + 3];
        }

        private static ushort ReadUInt16BE(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }
    }
}
