//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Finds CP/M disks for <see cref="CpmDriver"/>. Today: Commodore 128 CP/M disks on D64 and D71 images.
    /// </summary>
    /// <remarks>
    /// A C128 CP/M disk is also a readable CBM DOS disk (it has a Commodore header), so it matches
    /// the Commodore provider too. It is reported here with a higher confidence because the mark it
    /// is found by, "CBM" at the start of track 1 sector 0, is the C128 BIOS's own test for a CP/M
    /// disk; the C128 itself treats such a disk as CP/M.
    /// </remarks>
    public sealed class CpmDriverProvider : FileSystemDriverProviderBase
    {
        private static readonly string[] KnownExtensions = { ".d64", ".d71" };

        /// <summary>
        /// Gets the filesystem type name, "CPM".
        /// </summary>
        public override string FileSystemType => "CPM";

        /// <summary>
        /// Gets the file extensions CP/M images are found under.
        /// </summary>
        public override IReadOnlyList<string> FileExtensions => KnownExtensions;

        /// <summary>
        /// Gets a short description of the format.
        /// </summary>
        public override string Description => "CP/M disk (Commodore 128 CP/M 3 on 1541 and 1571 images)";

        /// <summary>
        /// Says how sure the provider is that an image is a C128 CP/M disk.
        /// </summary>
        /// <param name="hints">
        /// Size and first bytes of the image.
        /// </param>
        /// <returns>
        /// 0.95 for a D64 or D71 sized image whose track 1 sector 0 starts with "CBM"; otherwise 0.
        /// </returns>
        public override float DetectConfidence(DetectionHints hints)
        {
            if (!CpmDriver.IsC128CpmBootSector(hints.HeaderBytes, out bool doubleSided))
            {
                return 0.0f;
            }

            long sectors = doubleSided ? 1366 : 683;
            return hints.FileSize == sectors * 256 || hints.FileSize == sectors * 257 ? 0.95f : 0.0f;
        }

        /// <summary>
        /// Creates a CP/M driver.
        /// </summary>
        /// <returns>
        /// A new <see cref="CpmDriver"/>.
        /// </returns>
        public override IFileSystemDriver CreateDriver() => new CpmDriver();
    }
}
