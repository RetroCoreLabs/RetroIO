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
    /// Unified provider for Commodore disk drivers (D64, D71, D81).
    /// Replaces separate D64DriverProvider, D71DriverProvider, and D81DriverProvider.
    /// </summary>
    public sealed class CommodoreDiskDriverProvider : FileSystemDriverProviderBase
    {
        /// <inheritdoc/>
        public override string FileSystemType => "Commodore";

        /// <inheritdoc/>
        public override string Description => "Commodore disk images (D64/D71/D81)";

        /// <summary>
        /// Analyzes detection hints to determine confidence that file matches Commodore disk format.
        /// </summary>
        public override float DetectConfidence(DetectionHints hints)
        {
            float confidence = 0.0f;

            // 1. Check file extension
            string ext = hints.FileExtension.ToLowerInvariant();
            if (ext == ".d64" || ext == ".d71" || ext == ".d81")
            {
                confidence += 0.3f;
            }

            // 2. Check file size (strongest signal)
            float sizeConfidence = DetectConfidenceFromSize(hints.FileSize);
            confidence += sizeConfidence;

            // If size doesn't match any format, return low confidence
            if (sizeConfidence < 0.4f)
            {
                return confidence;
            }

            return Math.Min(confidence, 1.0f);
        }

        /// <inheritdoc/>
        public override IFileSystemDriver CreateDriver()
        {
            return new CommodoreDiskDriver();
        }

        /// <summary>
        /// Detects confidence based on file size matching known Commodore formats.
        /// </summary>
        private float DetectConfidenceFromSize(long length)
        {
            // D64 35-track (174,848 bytes = 683 sectors × 256)
            if (length == 174848 || length == 175531) // With/without error table
            {
                return 0.6f;
            }

            // D64 40-track (196,608 bytes = 768 sectors × 256)
            if (length == 196608 || length == 197376)
            {
                return 0.6f;
            }

            // D64 42-track (205,312 bytes = 802 sectors × 256)
            if (length == 205312 || length == 206114)
            {
                return 0.6f;
            }

            // D71 (349,696 bytes = 1366 sectors × 256)
            if (length == 349696 || length == 351062)
            {
                return 0.6f;
            }

            // D81 (819,200 bytes = 3200 sectors × 256)
            if (length == 819200 || length == 822400)
            {
                return 0.6f;
            }

            // Check if size is close to expected (within 2% tolerance)
            long[] expectedSizes = new long[]
            {
                174848, // D64-35
                196608, // D64-40
                205312, // D64-42
                349696, // D71
                819200  // D81
            };

            for (int i = 0; i < expectedSizes.Length; i++)
            {
                long diff = Math.Abs(length - expectedSizes[i]);
                float tolerance = expectedSizes[i] * 0.02f;

                if (diff < tolerance)
                {
                    return 0.3f; // Moderate confidence
                }
            }

            return 0.0f; // No match
        }
    }
}
