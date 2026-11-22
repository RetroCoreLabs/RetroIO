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
    /// Provider for Dragon 32/64 VDK disk images with DragonDOS filesystem.
    /// Handles format detection and driver instantiation for VDK format.
    /// </summary>
    public sealed class DragonVdkDriverProvider : FileSystemDriverProviderBase, IBlankImageProvider
    {
        // The machines this format's disks come from, shown to users.
        private static readonly string[] MachineList = new[] { "Dragon 32", "Dragon 64" };

        // Blank DragonDOS disks as DSKINIT formats them (see DragonDosFormat).
        private static readonly BlankImageLayout[] Layouts =
        {
            new BlankImageLayout { Id = "dragon-vdk-40ss", Machine = "Dragon 32/64", Name = "DragonDOS 40 tracks, 1 side, 18 x 256 - 180 KB", Extension = ".vdk", SizeBytes = 184332 },
            new BlankImageLayout { Id = "dragon-vdk-40ds", Machine = "Dragon 32/64", Name = "DragonDOS 40 tracks, 2 sides, 18 x 256 - 360 KB", Extension = ".vdk", SizeBytes = 368652 },
            new BlankImageLayout { Id = "dragon-vdk-80ss", Machine = "Dragon 32/64", Name = "DragonDOS 80 tracks, 1 side, 18 x 256 - 360 KB", Extension = ".vdk", SizeBytes = 368652 },
            new BlankImageLayout { Id = "dragon-vdk-80ds", Machine = "Dragon 32/64", Name = "DragonDOS 80 tracks, 2 sides, 18 x 256 - 720 KB", Extension = ".vdk", SizeBytes = 737292 },
        };

        // The one extension list for this format: detection below uses it, and programs read it
        // through FileExtensions (for example to link these files to themselves).
        private static readonly string[] KnownExtensions = { ".vdk" };

        /// <summary>
        /// Gets the file system type identifier of this provider: <c>DragonDOS</c>.
        /// </summary>
        public override string FileSystemType => "DragonDOS";

        /// <summary>
        /// Gets the extensions this format is stored under: .vdk.
        /// </summary>
        /// <remarks>
        /// Detection uses the same list as its extension signal.
        /// </remarks>
        public override IReadOnlyList<string> FileExtensions => KnownExtensions;

        /// <summary>
        /// Gets the human-readable description of the Dragon VDK image format.
        /// </summary>
        public override string Description => "Dragon 32/64 VDK disk images (40/80 tracks, DragonDOS)";

        /// <inheritdoc/>
        public override System.Collections.Generic.IReadOnlyList<string> Machines => MachineList;

        /// <inheritdoc/>
        public override bool SupportsWrite => true;

        /// <summary>
        /// Scores the file from its extension (.vdk), the VDK header fields and its size.
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

            // 2. Check VDK signature in header
            if (hints.HeaderBytes != null && hints.HeaderBytes.Length >= 12)
            {
                // Check for 'dk' signature at bytes 0-1
                if (hints.HeaderBytes[0] == 0x64 && hints.HeaderBytes[1] == 0x6B)
                {
                    confidence += 0.5f;

                    // Verify header size (bytes 2-3, little-endian, should be 12)
                    ushort headerSize = (ushort)(hints.HeaderBytes[2] | (hints.HeaderBytes[3] << 8));
                    if (headerSize == 12)
                    {
                        confidence += 0.1f;

                        // Check track count (byte 8, should be 40 or 80)
                        byte tracks = hints.HeaderBytes[8];
                        if (tracks == 40 || tracks == 80)
                        {
                            confidence += 0.05f;

                            // Check side count (byte 9, should be 1 or 2)
                            byte sides = hints.HeaderBytes[9];
                            if (sides == 1 || sides == 2)
                            {
                                confidence += 0.05f;
                            }
                        }
                    }
                }
            }

            // 3. Check file size (strongest signal after signature)
            float sizeConfidence = DetectConfidenceFromSize(hints.FileSize);
            confidence += sizeConfidence;

            return Math.Min(confidence, 1.0f);
        }

        /// <summary>
        /// Creates a new, unmounted Dragon VDK driver.
        /// </summary>
        /// <returns>
        /// A new <see cref="DragonVdkDriver"/>.
        /// </returns>
        public override IFileSystemDriver CreateDriver()
        {
            return new DragonVdkDriver();
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
            DragonVdkDriver driver = new DragonVdkDriver();
            driver.Mount(stream, readOnly);
            return driver;
        }

        private static void GeometryOf(string layoutId, out int tracks, out int sides)
        {
            switch (layoutId)
            {
                case "dragon-vdk-40ss": tracks = 40; sides = 1; return;
                case "dragon-vdk-40ds": tracks = 40; sides = 2; return;
                case "dragon-vdk-80ss": tracks = 80; sides = 1; return;
                case "dragon-vdk-80ds": tracks = 80; sides = 2; return;
                default: throw new ArgumentException("not a DragonDOS blank-image layout: " + layoutId, nameof(layoutId));
            }
        }

        // A VDK file: the 12-byte header of the PC-Dragon images in the test folder ("dk", header
        // length 12, versions 10h 10h, then tracks and sides), then the sectors.
        private static byte[] Wrap(byte[] sectors, int tracks, int sides)
        {
            byte[] file = new byte[12 + sectors.Length];
            file[0] = 0x64;
            file[1] = 0x6B;
            file[2] = 12;
            file[4] = 0x10;
            file[5] = 0x10;
            file[8] = (byte)tracks;
            file[9] = (byte)sides;
            sectors.CopyTo(file, 12);
            return file;
        }

        /// <summary>
        /// Detects confidence based on file size matching known VDK formats.
        /// </summary>
        private float DetectConfidenceFromSize(long length)
        {
            // VDK file size = 12-byte header + raw sector data
            return length switch
            {
                184332 => 0.2f,   // SS 40 track: 12 + (40*18*256)
                368652 => 0.2f,   // SS 80 track OR DS 40 track: 12 + (80*18*256) OR 12 + (40*36*256)
                737292 => 0.2f,   // DS 80 track: 12 + (80*36*256)
                _ => 0.0f
            };
        }
    }
}
