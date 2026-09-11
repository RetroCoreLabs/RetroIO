//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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
        public override string FileSystemType => "P00";
        public override string Description => "Commodore PC64 program container (.P00)";
        public override int RequiredHeaderBytes => 26;

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

        public override IFileSystemDriver CreateDriver() => new ProgramFileDriver();
    }
}
