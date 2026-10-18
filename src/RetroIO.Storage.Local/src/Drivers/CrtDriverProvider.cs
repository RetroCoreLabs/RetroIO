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
    /// Provider for Commodore CRT cartridge images (detected by the "C64 CARTRIDGE" magic).
    /// </summary>
    public sealed class CrtDriverProvider : FileSystemDriverProviderBase
    {
        /// <summary>
        /// Gets the file system type identifier of this provider: <c>CRT</c>.
        /// </summary>
        public override string FileSystemType => "CRT";
        /// <summary>
        /// Gets the human-readable description of the Commodore CRT cartridge format.
        /// </summary>
        public override string Description => "Commodore CRT cartridge image";
        /// <summary>
        /// Gets 64, the number of header bytes the detection needs.
        /// </summary>
        public override int RequiredHeaderBytes => 64;

        /// <summary>
        /// Returns 0 unless the header holds the CRT signature. A match scores 0.7, and the extension .crt adds 0.25.
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
            if (hints.HeaderBytes != null && CrtDriver.HasMagic(hints.HeaderBytes))
            {
                confidence += 0.7f;
            }
            else
            {
                return 0.0f;
            }
            if (hints.FileExtension.Equals(".crt", StringComparison.OrdinalIgnoreCase))
            {
                confidence += 0.25f;
            }
            return Math.Min(confidence, 1.0f);
        }

        /// <summary>
        /// Creates a new, unmounted CRT driver.
        /// </summary>
        /// <returns>
        /// A new <see cref="CrtDriver"/>.
        /// </returns>
        public override IFileSystemDriver CreateDriver() => new CrtDriver();
    }
}
