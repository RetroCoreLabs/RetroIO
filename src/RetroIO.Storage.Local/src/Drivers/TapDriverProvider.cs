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
    /// Provider for Commodore TAP tape images (detected by the "C64-TAPE-RAW" magic).
    /// </summary>
    public sealed class TapDriverProvider : FileSystemDriverProviderBase
    {
        /// <summary>
        /// Gets the file system type identifier of this provider: <c>TAP</c>.
        /// </summary>
        public override string FileSystemType => "TAP";
        /// <summary>
        /// Gets the human-readable description of the Commodore TAP tape image format.
        /// </summary>
        public override string Description => "Commodore TAP tape image";
        /// <summary>
        /// Gets 20, the number of header bytes the detection needs.
        /// </summary>
        public override int RequiredHeaderBytes => 20;

        /// <summary>
        /// Returns 0 unless the header holds the TAP signature. A match scores 0.7, and the extension .tap adds 0.25.
        /// </summary>
        /// <param name="hints">
        /// The file name and header bytes.
        /// </param>
        /// <returns>
        /// The confidence from 0 to 1.
        /// </returns>
        public override float DetectConfidence(DetectionHints hints)
        {
            float confidence = 0.0f;
            if (hints.HeaderBytes != null && TapDriver.HasMagic(hints.HeaderBytes))
            {
                confidence += 0.7f;
            }
            else
            {
                return 0.0f;
            }
            if (hints.FileExtension.Equals(".tap", StringComparison.OrdinalIgnoreCase))
            {
                confidence += 0.25f;
            }
            return Math.Min(confidence, 1.0f);
        }

        /// <summary>
        /// Creates a new, unmounted TAP driver.
        /// </summary>
        /// <returns>
        /// A new <see cref="TapDriver"/>.
        /// </returns>
        public override IFileSystemDriver CreateDriver() => new TapDriver();
    }
}
