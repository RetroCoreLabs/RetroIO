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
    /// Provider for raw Commodore PRG program files (2-byte load address + program). PRG has no
    /// magic signature, so detection relies on the .prg extension. P00-wrapped files are deferred to
    /// the P00 provider.
    /// </summary>
    public sealed class PrgDriverProvider : FileSystemDriverProviderBase
    {
        public override string FileSystemType => "PRG";
        public override string Description => "Commodore PRG program file";
        public override int RequiredHeaderBytes => 16;

        public override float DetectConfidence(DetectionHints hints)
        {
            // A PRG needs at least a 2-byte load address.
            if (hints.FileSize < 2)
            {
                return 0.0f;
            }
            // Let the P00 provider claim PC64-wrapped files.
            if (hints.HeaderBytes != null && ProgramFileDriver.IsP00(hints.HeaderBytes))
            {
                return 0.0f;
            }
            // PRG has no content signature — the extension is the only reliable hint.
            if (hints.FileExtension.Equals(".prg", StringComparison.OrdinalIgnoreCase))
            {
                return 0.6f;
            }
            return 0.0f;
        }

        public override IFileSystemDriver CreateDriver() => new ProgramFileDriver();
    }
}
