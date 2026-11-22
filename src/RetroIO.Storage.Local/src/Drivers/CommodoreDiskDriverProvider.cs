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

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Unified provider for Commodore disk drivers (D64, D71, D81).
    /// Replaces separate D64DriverProvider, D71DriverProvider, and D81DriverProvider.
    /// </summary>
    public sealed class CommodoreDiskDriverProvider : FileSystemDriverProviderBase, IBlankImageProvider
    {
        // The machines this format's disks come from, shown to users.
        private static readonly string[] MachineList = new[] { "Commodore 64 (1541)", "Commodore 128 (1571, 1581)" };

        // Disk name (16 characters) and ID (2) of the BAM header, as the drive's NEW command takes
        // them ("N:name,id").
        private static readonly BlankImageOption DiskName = new BlankImageOption { Id = "diskName", Label = "Disk name", Kind = BlankImageOptionKind.Text, Default = "BLANK", MaxLength = 16 };
        private static readonly BlankImageOption DiskId = new BlankImageOption { Id = "diskId", Label = "Disk ID", Kind = BlankImageOptionKind.Text, Default = "00", MaxLength = 2 };
        private static readonly BlankImageOption[] NameAndId = { DiskName, DiskId };

        private static readonly BlankImageLayout[] Layouts =
        {
            new BlankImageLayout { Id = "cbm-d64", Machine = "Commodore 1541", Name = "D64 - 35 tracks, 170 KB", Extension = ".d64", SizeBytes = 174848, Options = NameAndId },
            new BlankImageLayout { Id = "cbm-d71", Machine = "Commodore 1571", Name = "D71 - 70 tracks, double-sided, 340 KB", Extension = ".d71", SizeBytes = 349696, Options = NameAndId },
            new BlankImageLayout { Id = "cbm-d81", Machine = "Commodore 1581", Name = "D81 - 80 tracks, 800 KB", Extension = ".d81", SizeBytes = 819200, Options = NameAndId },
        };

        // The one extension list for this format: detection below uses it, and programs read it
        // through FileExtensions (for example to link these files to themselves).
        private static readonly string[] KnownExtensions = { ".d64", ".d71", ".d81" };

        /// <summary>
        /// Gets the file system type identifier of this provider: <c>Commodore</c>.
        /// </summary>
        public override string FileSystemType => "Commodore";

        /// <summary>
        /// Gets the extensions this format is stored under: .d64, .d71, .d81.
        /// </summary>
        /// <remarks>
        /// Detection uses the same list as its extension signal.
        /// </remarks>
        public override IReadOnlyList<string> FileExtensions => KnownExtensions;

        /// <summary>
        /// Gets the human-readable description of the Commodore disk image formats.
        /// </summary>
        public override string Description => "Commodore disk images (D64/D71/D81)";

        /// <inheritdoc/>
        public override System.Collections.Generic.IReadOnlyList<string> Machines => MachineList;

        /// <summary>
        /// Analyzes detection hints to determine confidence that file matches Commodore disk format.
        /// </summary>
        public override float DetectConfidence(DetectionHints hints)
        {
            float confidence = 0.0f;

            // 1. Check file extension
            if (HasKnownFileExtension(hints))
            {
                confidence += 0.3f;
            }

            // 2. Check file size (strongest signal)
            float sizeConfidence = DetectConfidenceFromSize(hints.FileSize);
            confidence += sizeConfidence;

            // If size doesn't match any format, return low confidence
            if (sizeConfidence < 0.4f)
            {
                return confidence;
            }

            return Math.Min(confidence, 1.0f);
        }

        /// <summary>
        /// Creates a new, unmounted Commodore disk driver.
        /// </summary>
        /// <returns>
        /// A new <see cref="CommodoreDiskDriver"/>.
        /// </returns>
        public override IFileSystemDriver CreateDriver()
        {
            return new CommodoreDiskDriver();
        }

        /// <inheritdoc/>
        public override bool SupportsWrite => true;

        /// <inheritdoc/>
        public IReadOnlyList<BlankImageLayout> BlankLayouts => Layouts;

        /// <inheritdoc/>
        public byte[] CreateBlank(string layoutId, IReadOnlyDictionary<string, string> options)
        {
            CbmDiskType type = TypeOf(layoutId);
            return CbmImageFactory.CreateBlank(type, DiskName.ValueIn(options), DiskId.ValueIn(options));
        }

        /// <inheritdoc/>
        public IFileSystemDriver Mount(Stream stream, bool readOnly, string layoutId)
        {
            TypeOf(layoutId);
            CommodoreDiskDriver driver = new CommodoreDiskDriver();
            driver.Mount(stream, readOnly);
            return driver;
        }

        private static CbmDiskType TypeOf(string layoutId)
        {
            return layoutId switch
            {
                "cbm-d64" => CbmDiskType.D64,
                "cbm-d71" => CbmDiskType.D71,
                "cbm-d81" => CbmDiskType.D81,
                _ => throw new ArgumentException("not a Commodore blank-image layout: " + layoutId, nameof(layoutId)),
            };
        }

        /// <summary>
        /// Detects confidence based on file size matching known Commodore formats.
        /// </summary>
        private float DetectConfidenceFromSize(long length)
        {
            // D64 35-track (174,848 bytes = 683 sectors × 256)
            if (length == 174848 || length == 175531) // With/without error table
            {
                return 0.6f;
            }

            // D64 40-track (196,608 bytes = 768 sectors × 256)
            if (length == 196608 || length == 197376)
            {
                return 0.6f;
            }

            // D64 42-track (205,312 bytes = 802 sectors × 256)
            if (length == 205312 || length == 206114)
            {
                return 0.6f;
            }

            // D71 (349,696 bytes = 1366 sectors × 256)
            if (length == 349696 || length == 351062)
            {
                return 0.6f;
            }

            // D81 (819,200 bytes = 3200 sectors × 256)
            if (length == 819200 || length == 822400)
            {
                return 0.6f;
            }

            // Check if size is close to expected (within 2% tolerance)
            long[] expectedSizes = new long[]
            {
                174848, // D64-35
                196608, // D64-40
                205312, // D64-42
                349696, // D71
                819200  // D81
            };

            for (int i = 0; i < expectedSizes.Length; i++)
            {
                long diff = Math.Abs(length - expectedSizes[i]);
                float tolerance = expectedSizes[i] * 0.02f;

                if (diff < tolerance)
                {
                    return 0.3f; // Moderate confidence
                }
            }

            return 0.0f; // No match
        }
    }
}
