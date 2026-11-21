//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.FileSystems.Cpm;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Finds CP/M disks for <see cref="CpmDriver"/>. Today: Commodore 128 CP/M disks on D64, D71 and D81 images.
    /// </summary>
    /// <remarks>
    /// A C128 CP/M disk is also a readable CBM DOS disk (it has a Commodore header), so it matches
    /// the Commodore provider too. It is reported here with a higher confidence because the mark it
    /// is found by, "CBM" at the start of track 1 sector 0, is the C128 BIOS's own test for a CP/M
    /// disk; the C128 itself treats such a disk as CP/M.
    /// A 1581 disk has no such mark: the C128 BIOS treats every 1581 disk as CP/M in CP/M mode. A
    /// D81 image is claimed only when all 128 entries of its first 4 KB pass
    /// <see cref="CpmFileSystem.LooksLikeDirectory"/> and at
    /// least one is a file; an empty CP/M directory gives no sign of CP/M, so it is left to the
    /// Commodore provider.
    /// </remarks>
    public sealed class CpmDriverProvider : FileSystemDriverProviderBase
    {
        private static readonly string[] KnownExtensions = { ".d64", ".d71", ".d81" };

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
        public override string Description => "CP/M disk (Commodore 128 CP/M 3 on 1541, 1571 and 1581 images)";

        /// <summary>
        /// Gets the number of first bytes detection needs: 4096, the whole 1581 CP/M directory.
        /// </summary>
        public override int RequiredHeaderBytes => 4096;

        /// <summary>
        /// Says how sure the provider is that an image is a C128 CP/M disk.
        /// </summary>
        /// <param name="hints">
        /// Size and first bytes of the image.
        /// </param>
        /// <returns>
        /// 0.95 for a D64 or D71 sized image whose track 1 sector 0 starts with "CBM", or a D81 sized
        /// image whose first 4 KB is a CP/M directory with files; otherwise 0.
        /// </returns>
        public override float DetectConfidence(DetectionHints hints)
        {
            if (CpmDriver.IsD81Size(hints.FileSize))
            {
                CpmDiskParameters p1581 = CpmDriver.C1581Parameters();
                bool isDirectory = CpmFileSystem.LooksLikeDirectory(hints.HeaderBytes, p1581, out int files);
                return isDirectory && files > 0 ? 0.95f : 0.0f;
            }

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
