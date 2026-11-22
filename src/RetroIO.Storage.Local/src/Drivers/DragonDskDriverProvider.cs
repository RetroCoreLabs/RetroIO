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
    /// Provider for Dragon 32/64 DSK/JVC disk images with DragonDOS filesystem.
    /// Handles format detection and driver instantiation for headerless or
    /// variable-header DSK/JVC format.
    /// </summary>
    public sealed class DragonDskDriverProvider : FileSystemDriverProviderBase, IBlankImageProvider
    {
        // The machines this format's disks come from, shown to users.
        private static readonly string[] MachineList = new[] { "Dragon 32", "Dragon 64", "TRS-80 Color Computer" };

        // Blank DragonDOS disks as DSKINIT formats them (see DragonDosFormat).
        private static readonly BlankImageLayout[] Layouts =
        {
            new BlankImageLayout { Id = "dragon-dsk-40ss", Machine = "Dragon 32/64", Name = "DragonDOS 40 tracks, 1 side, 18 x 256 - 180 KB", Extension = ".dsk", SizeBytes = 184320 },
            new BlankImageLayout { Id = "dragon-dsk-40ds", Machine = "Dragon 32/64", Name = "DragonDOS 40 tracks, 2 sides, 18 x 256 - 360 KB", Extension = ".dsk", SizeBytes = 368642 },
            new BlankImageLayout { Id = "dragon-dsk-80ss", Machine = "Dragon 32/64", Name = "DragonDOS 80 tracks, 1 side, 18 x 256 - 360 KB", Extension = ".dsk", SizeBytes = 368640 },
            new BlankImageLayout { Id = "dragon-dsk-80ds", Machine = "Dragon 32/64", Name = "DragonDOS 80 tracks, 2 sides, 18 x 256 - 720 KB", Extension = ".dsk", SizeBytes = 737280 },
        };

        // The one extension list for this format: detection below uses it, and programs read it
        // through FileExtensions (for example to link these files to themselves).
        private static readonly string[] KnownExtensions = { ".dsk", ".jvc" };

        /// <summary>
        /// Gets the file system type identifier of this provider: <c>DragonDOS-DSK</c>.
        /// </summary>
        public override string FileSystemType => "DragonDOS-DSK";

        /// <summary>
        /// Gets the extensions this format is stored under: .dsk, .jvc.
        /// </summary>
        /// <remarks>
        /// Detection uses the same list as its extension signal.
        /// </remarks>
        public override IReadOnlyList<string> FileExtensions => KnownExtensions;

        /// <summary>
        /// Gets the human-readable description of the Dragon DSK and JVC image formats.
        /// </summary>
        public override string Description => "Dragon 32/64 DSK/JVC disk images (35/40/80 tracks, DragonDOS)";

        /// <inheritdoc/>
        public override System.Collections.Generic.IReadOnlyList<string> Machines => MachineList;

        /// <inheritdoc/>
        public override bool SupportsWrite => true;

        /// <summary>
        /// Scores the file from its extension (.dsk or .jvc), a plausible JVC header, its size, and an OS-9 identification sector in a headerless
        /// image. Returns 0 when the header starts with the VDK signature.
        /// </summary>
        /// <param name="hints">
        /// The file name, size and header bytes.
        /// </param>
        /// <returns>
        /// The confidence from 0 to 1.
        /// </returns>
        public override float DetectConfidence(DetectionHints hints)
        {
            float confidence = 0.0f;

            // 1. Check file extension
            if (HasKnownFileExtension(hints))
            {
                confidence += 0.3f;
            }

            // 2. Exclude VDK files (they have 'dk' signature)
            if (hints.HeaderBytes != null && hints.HeaderBytes.Length >= 2)
            {
                if (hints.HeaderBytes[0] == 0x64 && hints.HeaderBytes[1] == 0x6B)
                {
                    return 0.0f;
                }
            }

            // 3. Calculate header size from file size modulo 128 (as XRoar does)
            int headerSize = (int)(hints.FileSize % 128);

            // 4. Attempt to parse JVC header if present
            if (headerSize > 0 && hints.HeaderBytes != null && hints.HeaderBytes.Length >= headerSize)
            {
                byte sectorsPerTrack = headerSize >= 1 ? hints.HeaderBytes[0] : (byte)18;
                byte sides = headerSize >= 2 ? hints.HeaderBytes[1] : (byte)1;
                byte sectorSizeCode = headerSize >= 3 ? hints.HeaderBytes[2] : (byte)1;

                // Validate geometry
                if (sectorsPerTrack == 18 && (sides == 1 || sides == 2) && sectorSizeCode == 1)
                {
                    confidence += 0.3f;
                }
            }

            // 5. Check file size matches known configurations
            float sizeConfidence = DetectConfidenceFromSize(hints.FileSize);
            confidence += sizeConfidence;

            // 6. A headerless image whose sector 0 is a consistent OS-9 identification sector is an OS-9 volume this driver mounts, whatever
            //    its size: CoCo SDC and DriveWire images are not floppy sized, and DriveWire files are shorter than their volume. That alone is
            //    enough to pick this driver.
            if (headerSize == 0 && hints.HeaderBytes != null
                && DragonDskDriver.TryReadOs9Volume(hints.HeaderBytes, hints.FileSize, out _))
            {
                confidence += 0.5f;
            }

            return Math.Min(confidence, 1.0f);
        }

        /// <summary>
        /// Creates a new, unmounted Dragon DSK driver.
        /// </summary>
        /// <returns>
        /// A new <see cref="DragonDskDriver"/>.
        /// </returns>
        public override IFileSystemDriver CreateDriver()
        {
            return new DragonDskDriver();
        }

        /// <inheritdoc/>
        public IReadOnlyList<BlankImageLayout> BlankLayouts => Layouts;

        /// <inheritdoc/>
        public byte[] CreateBlank(string layoutId, IReadOnlyDictionary<string, string> options)
        {
            GeometryOf(layoutId, out int tracks, out int sides);
            return Wrap(DragonDosFormat.CreateBlank(tracks, sides), tracks, sides);
        }

        /// <inheritdoc/>
        public IFileSystemDriver Mount(Stream stream, bool readOnly, string layoutId)
        {
            GeometryOf(layoutId, out _, out _);
            DragonDskDriver driver = new DragonDskDriver();
            driver.Mount(stream, readOnly);
            return driver;
        }

        private static void GeometryOf(string layoutId, out int tracks, out int sides)
        {
            switch (layoutId)
            {
                case "dragon-dsk-40ss": tracks = 40; sides = 1; return;
                case "dragon-dsk-40ds": tracks = 40; sides = 2; return;
                case "dragon-dsk-80ss": tracks = 80; sides = 1; return;
                case "dragon-dsk-80ds": tracks = 80; sides = 2; return;
                default: throw new ArgumentException("not a DragonDOS blank-image layout: " + layoutId, nameof(layoutId));
            }
        }

        // A raw DSK file. A double-sided 40-track disk is the same size as a single-sided 80-track
        // one, which a headerless file is read as, so it gets the two-byte JVC header that says 18
        // sectors per track and two sides (the header as XRoar documents it; the DSK reader takes the
        // header length from the size).
        private static byte[] Wrap(byte[] sectors, int tracks, int sides)
        {
            if (tracks != 40 || sides != 2)
            {
                return sectors;
            }

            byte[] file = new byte[2 + sectors.Length];
            file[0] = 18;
            file[1] = 2;
            sectors.CopyTo(file, 2);
            return file;
        }

        /// <summary>
        /// Detects confidence based on file size matching known DSK/JVC formats.
        /// Formula: (tracks × sides × 18 spt × 256 bytes) + header_size
        /// where header_size = file_size % 128
        /// </summary>
        private float DetectConfidenceFromSize(long fileSize)
        {
            int headerSize = (int)(fileSize % 128);
            long dataSize = fileSize - headerSize;

            // Check if data size matches known configurations
            // Single-sided configurations
            if (dataSize == 35 * 18 * 256) return 0.25f;  // CoCo: 35T/SS = 161,280
            if (dataSize == 40 * 18 * 256) return 0.25f;  // Dragon: 40T/SS = 184,320
            if (dataSize == 80 * 18 * 256) return 0.25f;  // 80T/SS = 368,640

            // Double-sided configurations
            if (dataSize == 40 * 2 * 18 * 256) return 0.25f;  // 40T/DS = 368,640
            if (dataSize == 80 * 2 * 18 * 256) return 0.25f;  // 80T/DS = 737,280

            // Check common file sizes (exact multiples of 256 = no header)
            if (fileSize == 161280) return 0.2f;  // CoCo 35T/SS, no header
            if (fileSize == 184320) return 0.2f;  // Dragon 40T/SS, no header
            if (fileSize == 368640) return 0.2f;  // 80T/SS or 40T/DS, no header
            if (fileSize == 737280) return 0.2f;  // 80T/DS, no header

            return 0.0f;
        }
    }
}
