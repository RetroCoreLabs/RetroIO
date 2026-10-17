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
    /// Provider for Dragon 32/64 VDK disk images with DragonDOS filesystem.
    /// Handles format detection and driver instantiation for VDK format.
    /// </summary>
    public sealed class DragonVdkDriverProvider : FileSystemDriverProviderBase
    {
        public override string FileSystemType => "DragonDOS";

        public override string Description => "Dragon 32/64 VDK disk images (40/80 tracks, DragonDOS)";

        public override float DetectConfidence(DetectionHints hints)
        {
            float confidence = 0.0f;

            // 1. Check file extension
            string ext = hints.FileExtension.ToLowerInvariant();
            if (ext == ".vdk")
            {
                confidence += 0.3f;
            }

            // 2. Check VDK signature in header
            if (hints.HeaderBytes != null && hints.HeaderBytes.Length >= 12)
            {
                // Check for 'dk' signature at bytes 0-1
                if (hints.HeaderBytes[0] == 0x64 && hints.HeaderBytes[1] == 0x6B)
                {
                    confidence += 0.5f;

                    // Verify header size (bytes 2-3, little-endian, should be 12)
                    ushort headerSize = (ushort)(hints.HeaderBytes[2] | (hints.HeaderBytes[3] << 8));
                    if (headerSize == 12)
                    {
                        confidence += 0.1f;

                        // Check track count (byte 8, should be 40 or 80)
                        byte tracks = hints.HeaderBytes[8];
                        if (tracks == 40 || tracks == 80)
                        {
                            confidence += 0.05f;

                            // Check side count (byte 9, should be 1 or 2)
                            byte sides = hints.HeaderBytes[9];
                            if (sides == 1 || sides == 2)
                            {
                                confidence += 0.05f;
                            }
                        }
                    }
                }
            }

            // 3. Check file size (strongest signal after signature)
            float sizeConfidence = DetectConfidenceFromSize(hints.FileSize);
            confidence += sizeConfidence;

            return Math.Min(confidence, 1.0f);
        }

        public override IFileSystemDriver CreateDriver()
        {
            return new DragonVdkDriver();
        }

        /// <summary>
        /// Detects confidence based on file size matching known VDK formats.
        /// </summary>
        private float DetectConfidenceFromSize(long length)
        {
            // VDK file size = 12-byte header + raw sector data
            return length switch
            {
                184332 => 0.2f,   // SS 40 track: 12 + (40*18*256)
                368652 => 0.2f,   // SS 80 track OR DS 40 track: 12 + (80*18*256) OR 12 + (40*36*256)
                737292 => 0.2f,   // DS 80 track: 12 + (80*36*256)
                _ => 0.0f
            };
        }
    }
}
