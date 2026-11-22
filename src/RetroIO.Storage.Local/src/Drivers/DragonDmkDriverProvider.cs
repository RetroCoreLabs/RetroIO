//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.FileSystems;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Provider for Dragon and Color Computer DMK disk images. The format is described in <see cref="DmkDiskImage"/>.
    /// </summary>
    public sealed class DragonDmkDriverProvider : FileSystemDriverProviderBase, IBlankImageProvider
    {
        // The machines this format's disks come from, shown to users.
        private static readonly string[] MachineList = new[] { "Dragon 32", "Dragon 64", "TRS-80 Color Computer" };

        // Blank DragonDOS disks as DSKINIT formats them, kept as raw tracks (DmkDiskImage.CreateDragonDosFormatted) with the DragonDOS
        // sectors of DragonDosFormat.
        private static readonly BlankImageLayout[] Layouts =
        {
            new BlankImageLayout { Id = "dragon-dmk-40ss", Machine = "Dragon 32/64", Name = "DragonDOS 40 tracks, 1 side, 18 x 256 - 180 KB, raw tracks", Extension = ".dmk", SizeBytes = 256016 },
            new BlankImageLayout { Id = "dragon-dmk-40ds", Machine = "Dragon 32/64", Name = "DragonDOS 40 tracks, 2 sides, 18 x 256 - 360 KB, raw tracks", Extension = ".dmk", SizeBytes = 512016 },
            new BlankImageLayout { Id = "dragon-dmk-80ss", Machine = "Dragon 32/64", Name = "DragonDOS 80 tracks, 1 side, 18 x 256 - 360 KB, raw tracks", Extension = ".dmk", SizeBytes = 512016 },
            new BlankImageLayout { Id = "dragon-dmk-80ds", Machine = "Dragon 32/64", Name = "DragonDOS 80 tracks, 2 sides, 18 x 256 - 720 KB, raw tracks", Extension = ".dmk", SizeBytes = 1024016 },
        };

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

        /// <inheritdoc/>
        public IReadOnlyList<BlankImageLayout> BlankLayouts => Layouts;

        /// <inheritdoc/>
        public byte[] CreateBlank(string layoutId, IReadOnlyDictionary<string, string> options)
        {
            GeometryOf(layoutId, out int tracks, out int sides);
            byte[] formatted = DmkDiskImage.CreateDragonDosFormatted(tracks, sides);
            DmkDiskImage image = DmkDiskImage.Parse(new MemoryStream(formatted, writable: false));
            image.SetSectorImage(DragonDosFormat.CreateBlank(tracks, sides));
            return image.ToFileBytes();
        }

        /// <inheritdoc/>
        public IFileSystemDriver Mount(Stream stream, bool readOnly, string layoutId)
        {
            GeometryOf(layoutId, out _, out _);
            DragonDmkDriver driver = new DragonDmkDriver();
            driver.Mount(stream, readOnly);
            return driver;
        }

        private static void GeometryOf(string layoutId, out int tracks, out int sides)
        {
            switch (layoutId)
            {
                case "dragon-dmk-40ss": tracks = 40; sides = 1; return;
                case "dragon-dmk-40ds": tracks = 40; sides = 2; return;
                case "dragon-dmk-80ss": tracks = 80; sides = 1; return;
                case "dragon-dmk-80ds": tracks = 80; sides = 2; return;
                default: throw new ArgumentException("not a DragonDOS DMK blank-image layout: " + layoutId, nameof(layoutId));
            }
        }
    }
}
