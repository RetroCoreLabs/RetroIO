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
    /// <summary>Provider for Commodore CRT cartridge images (detected by the "C64 CARTRIDGE" magic).</summary>
    public sealed class CrtDriverProvider : FileSystemDriverProviderBase
    {
        public override string FileSystemType => "CRT";
        public override string Description => "Commodore CRT cartridge image";
        public override int RequiredHeaderBytes => 64;

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

        public override IFileSystemDriver CreateDriver() => new CrtDriver();
    }
}
