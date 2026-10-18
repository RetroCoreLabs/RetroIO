//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Provider for Dragon 32/64 DSK/JVC disk images with DragonDOS filesystem.
    /// Handles format detection and driver instantiation for headerless or
    /// variable-header DSK/JVC format.
    /// </summary>
    public sealed class DragonDskDriverProvider : FileSystemDriverProviderBase
    {
        /// <summary>
        /// Gets the file system type identifier of this provider: <c>DragonDOS-DSK</c>.
        /// </summary>
        public override string FileSystemType => "DragonDOS-DSK";

        /// <summary>
        /// Gets the human-readable description of the Dragon DSK and JVC image formats.
        /// </summary>
        public override string Description => "Dragon 32/64 DSK/JVC disk images (35/40/80 tracks, DragonDOS)";

        /// <summary>
        /// Scores the file from its extension (.dsk or .jvc), a plausible JVC header and its size. Returns 0 when the header starts with the VDK signature.
        /// </summary>
        /// <param name="hints">
        /// The file name, size and header bytes.
        /// </param>
        /// <returns>
        /// The confidence from 0 to 1.
        /// </returns>
        public override float DetectConfidence(DetectionHints hints)
        {
            float confidence = 0.0f;

            // 1. Check file extension
            string ext = hints.FileExtension.ToLowerInvariant();
            if (ext == ".dsk" || ext == ".jvc")
            {
                confidence += 0.3f;
            }

            // 2. Exclude VDK files (they have 'dk' signature)
            if (hints.HeaderBytes != null && hints.HeaderBytes.Length >= 2)
            {
                if (hints.HeaderBytes[0] == 0x64 && hints.HeaderBytes[1] == 0x6B)
                {
                    return 0.0f;
                }
            }

            // 3. Calculate header size from file size modulo 256
            int headerSize = (int)(hints.FileSize % 256);

            // 4. Attempt to parse JVC header if present
            if (headerSize > 0 && hints.HeaderBytes != null && hints.HeaderBytes.Length >= headerSize)
            {
                byte sectorsPerTrack = headerSize >= 1 ? hints.HeaderBytes[0] : (byte)18;
                byte sides = headerSize >= 2 ? hints.HeaderBytes[1] : (byte)1;
                byte sectorSizeCode = headerSize >= 3 ? hints.HeaderBytes[2] : (byte)1;

                // Validate geometry
                if (sectorsPerTrack == 18 && (sides == 1 || sides == 2) && sectorSizeCode == 1)
                {
                    confidence += 0.3f;
                }
            }

            // 5. Check file size matches known configurations
            float sizeConfidence = DetectConfidenceFromSize(hints.FileSize);
            confidence += sizeConfidence;

            return Math.Min(confidence, 1.0f);
        }

        /// <summary>
        /// Creates a new, unmounted Dragon DSK driver.
        /// </summary>
        /// <returns>
        /// A new <see cref="DragonDskDriver"/>.
        /// </returns>
        public override IFileSystemDriver CreateDriver()
        {
            return new DragonDskDriver();
        }

        /// <summary>
        /// Detects confidence based on file size matching known DSK/JVC formats.
        /// Formula: (tracks × sides × 18 spt × 256 bytes) + header_size
        /// where header_size = file_size % 256
        /// </summary>
        private float DetectConfidenceFromSize(long fileSize)
        {
            int headerSize = (int)(fileSize % 256);
            long dataSize = fileSize - headerSize;

            // Check if data size matches known configurations
            // Single-sided configurations
            if (dataSize == 35 * 18 * 256) return 0.25f;  // CoCo: 35T/SS = 161,280
            if (dataSize == 40 * 18 * 256) return 0.25f;  // Dragon: 40T/SS = 184,320
            if (dataSize == 80 * 18 * 256) return 0.25f;  // 80T/SS = 368,640

            // Double-sided configurations
            if (dataSize == 40 * 2 * 18 * 256) return 0.25f;  // 40T/DS = 368,640
            if (dataSize == 80 * 2 * 18 * 256) return 0.25f;  // 80T/DS = 737,280

            // Check common file sizes (exact multiples of 256 = no header)
            if (fileSize == 161280) return 0.2f;  // CoCo 35T/SS, no header
            if (fileSize == 184320) return 0.2f;  // Dragon 40T/SS, no header
            if (fileSize == 368640) return 0.2f;  // 80T/SS or 40T/DS, no header
            if (fileSize == 737280) return 0.2f;  // 80T/DS, no header

            return 0.0f;
        }
    }
}
