//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Provider for Dragon and Color Computer DMK disk images. The format is described in <see cref="DmkDiskImage"/>.
    /// </summary>
    public sealed class DragonDmkDriverProvider : FileSystemDriverProviderBase
    {
        /// <summary>
        /// Gets the name of the file system type this provider handles.
        /// </summary>
        public override string FileSystemType => "DragonDOS-DMK";

        /// <summary>
        /// Gets the description of the provider.
        /// </summary>
        public override string Description => "Dragon and Color Computer DMK disk images (DragonDOS, DECB, OS-9)";

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

            if (hints.FileExtension.ToLowerInvariant() == ".dmk")
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
