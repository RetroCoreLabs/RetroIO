//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Text;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Provider for T64 filesystem driver.
    /// Handles format detection and driver instantiation.
    /// Detection uses pre-read hints (filename, size, first 2048 bytes) for efficiency.
    /// </summary>
    public sealed class T64DriverProvider : FileSystemDriverProviderBase
    {
        public override string FileSystemType => "T64";

        public override string Description => "Commodore T64 tape archive";

        /// <summary>
        /// Analyzes detection hints to determine confidence that file matches T64 format.
        /// Factory provides pre-read hints - no file I/O performed here.
        /// </summary>
        public override float DetectConfidence(DetectionHints hints)
        {
            float confidence = 0.0f;

            // 1. Check file extension (weak signal, fast)
            if (hints.FileExtension.Equals(".t64", StringComparison.OrdinalIgnoreCase))
            {
                confidence += 0.3f;
            }

            // 2. Check file size (T64 must be at least 64 bytes for header)
            if (hints.FileSize < 64)
            {
                return 0.0f;
            }

            // 3. Check T64 signature in header bytes
            if (hints.HeaderBytes != null && hints.HeaderBytes.Length >= 32)
            {
                // Check for T64 signature: "C64 tape image file" or "C64S tape file"
                string signature = Encoding.ASCII.GetString(hints.HeaderBytes, 0, Math.Min(32, hints.HeaderBytes.Length)).TrimEnd('\0');

                if (signature.StartsWith("C64 tape image file", StringComparison.OrdinalIgnoreCase))
                {
                    confidence += 0.65f; // Very high confidence - standard signature
                }
                else if (signature.StartsWith("C64S tape file", StringComparison.OrdinalIgnoreCase))
                {
                    confidence += 0.6f; // High confidence - alternate signature
                }
                else if (signature.StartsWith("C64", StringComparison.OrdinalIgnoreCase) &&
                         signature.Contains("tape"))
                {
                    confidence += 0.4f; // Moderate confidence - partial match
                }
                else
                {
                    return confidence; // No signature match
                }

                // Additional validation: check version and entry counts (if we have enough bytes)
                if (hints.HeaderBytes.Length >= 40)
                {
                    ushort version = BitConverter.ToUInt16(hints.HeaderBytes, 32);
                    ushort maxEntries = BitConverter.ToUInt16(hints.HeaderBytes, 34);
                    ushort usedEntries = BitConverter.ToUInt16(hints.HeaderBytes, 36);

                    // Sanity checks on header values
                    if (maxEntries > 0 && maxEntries <= 1000 && usedEntries <= maxEntries)
                    {
                        confidence = Math.Min(confidence + 0.05f, 1.0f);
                    }
                }
            }

            return Math.Min(confidence, 1.0f);
        }

        public override IFileSystemDriver CreateDriver()
        {
            return new T64Driver();
        }
    }
}
