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
    /// Provider for PC64 ".P00" program containers (a PRG wrapped with the "C64File" signature and
    /// the original 16-byte CBM filename). Detected by the signature, strengthened by the extension.
    /// </summary>
    public sealed class P00DriverProvider : FileSystemDriverProviderBase
    {
        /// <inheritdoc/>
        public override string FileSystemType => "P00";
        /// <inheritdoc/>
        public override string Description => "Commodore PC64 program container (.P00)";
        /// <summary>
        /// Gets 26, the number of header bytes the detection needs.
        /// </summary>
        public override int RequiredHeaderBytes => 26;

        /// <summary>
        /// Returns 0 unless the header holds the C64File signature. A match scores 0.7, and the extension .p00 adds 0.25.
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

            if (hints.HeaderBytes != null && ProgramFileDriver.IsP00(hints.HeaderBytes))
            {
                confidence += 0.7f; // the "C64File" signature is definitive
            }
            else
            {
                return 0.0f; // without the signature it is not a P00
            }

            if (hints.FileExtension.Equals(".p00", StringComparison.OrdinalIgnoreCase))
            {
                confidence += 0.25f;
            }

            return Math.Min(confidence, 1.0f);
        }

        /// <inheritdoc/>
        public override IFileSystemDriver CreateDriver() => new ProgramFileDriver();
    }
}
