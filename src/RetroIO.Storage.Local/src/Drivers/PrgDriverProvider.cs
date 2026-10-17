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
        /// <inheritdoc/>
        public override string FileSystemType => "PRG";
        /// <inheritdoc/>
        public override string Description => "Commodore PRG program file";
        /// <summary>
        /// Gets 16, the number of header bytes requested for detection.
        /// </summary>
        public override int RequiredHeaderBytes => 16;

        /// <summary>
        /// Returns 0.6 for a file of at least 2 bytes with the extension .prg, and 0 otherwise. Files with the P00 signature always score 0.
        /// </summary>
        /// <param name="hints">
        /// The file name, size and header bytes.
        /// </param>
        /// <returns>
        /// The confidence from 0 to 1.
        /// </returns>
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

        /// <inheritdoc/>
        public override IFileSystemDriver CreateDriver() => new ProgramFileDriver();
    }
}
