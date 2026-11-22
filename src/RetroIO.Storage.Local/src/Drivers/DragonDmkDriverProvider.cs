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
    /// Provider for Dragon and Color Computer DMK disk images. The format is described in <see cref="DmkDiskImage"/>.
    /// </summary>
    public sealed class DragonDmkDriverProvider : FileSystemDriverProviderBase
    {
        // The machines this format's disks come from, shown to users.
        private static readonly string[] MachineList = new[] { "Dragon 32", "Dragon 64", "TRS-80 Color Computer" };

        // The one extension list for this format: detection below uses it, and programs read it
        // through FileExtensions (for example to link these files to themselves).
        private static readonly string[] KnownExtensions = { ".dmk" };

        /// <summary>
        /// Gets the name of the file system type this provider handles.
        /// </summary>
        public override string FileSystemType => "DragonDOS-DMK";

        /// <summary>
        /// Gets the extensions this format is stored under: .dmk.
        /// </summary>
        /// <remarks>
        /// Detection uses the same list as its extension signal.
        /// </remarks>
        public override IReadOnlyList<string> FileExtensions => KnownExtensions;

        /// <summary>
        /// Gets the description of the provider.
        /// </summary>
        public override string Description => "Dragon and Color Computer DMK disk images (DragonDOS, DECB, OS-9)";

        /// <inheritdoc/>
        public override System.Collections.Generic.IReadOnlyList<string> Machines => MachineList;

        /// <inheritdoc/>
        public override bool SupportsWrite => true;

        /// <summary>
        /// Rates how likely a file is a DMK image.
        /// </summary>
        /// <param name="hints">
        /// The extension, size and first bytes of the file.
        /// </param>
        /// <returns>
        /// 0.4 for the extension .dmk plus 0.5 when the first 16 bytes and the file size fit a DMK header, which has no signature of its own.
        /// A file with the right header and a different extension still gets 0.5.
        /// </returns>
        public override float DetectConfidence(DetectionHints hints)
        {
            float confidence = 0.0f;

            if (HasKnownFileExtension(hints))
            {
                confidence += 0.4f;
            }

            if (DmkDiskImage.LooksLikeDmk(hints.HeaderBytes, hints.FileSize))
            {
                confidence += 0.5f;
            }

            return confidence;
        }

        /// <summary>
        /// Creates a driver for a DMK image.
        /// </summary>
        /// <returns>
        /// A new, unmounted <see cref="DragonDmkDriver"/>.
        /// </returns>
        public override IFileSystemDriver CreateDriver()
        {
            return new DragonDmkDriver();
        }
    }
}
