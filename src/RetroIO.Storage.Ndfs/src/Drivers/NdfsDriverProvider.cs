//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RetroIO.Storage.Core;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Creation;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Storage.Ndfs.Drivers
{
    /// <summary>
    /// NDFS driver provider for registration with FileSystemDriverFactory.
    /// </summary>
    public sealed class NdfsDriverProvider : FileSystemDriverProviderBase, IBlankImageProvider
    {
        // The machines this format's disks come from, shown to users.
        private static readonly string[] MachineList = new[] { "Norsk Data ND-100", "Norsk Data ND-500" };

        // The directory name in the master block: 1 to 16 printable characters.
        private static readonly BlankImageOption DirectoryName = new BlankImageOption { Id = "directoryName", Label = "Directory name", Kind = BlankImageOptionKind.Text, Default = "EMPTY-DISK", MaxLength = 16 };

        // The boot loader written to the first page; the disk type's own one unless another is picked.
        private static readonly BlankImageOption BootLoader = new BlankImageOption { Id = "bootLoader", Label = "Boot loader", Kind = BlankImageOptionKind.Choice, Default = "Disk type default", Choices = new[] { "Disk type default", "None", "Floppy", "SMD", "Winchester", "SCSI" } };
        private static readonly BlankImageOption[] NdfsOptions = { DirectoryName, BootLoader };

        // One layout per disk template of the image creator; size is its page count times 2048.
        private static readonly BlankImageLayout[] Layouts =
        {
            new BlankImageLayout { Id = "ndfs-floppy-360k", Machine = "Norsk Data ND-100", Name = "Floppy (360 KB type) - 154 pages", Extension = ".img", SizeBytes = 154L * 2048, Options = NdfsOptions },
            new BlankImageLayout { Id = "ndfs-floppy-1200k", Machine = "Norsk Data ND-100", Name = "Floppy (1.2 MB type) - 616 pages", Extension = ".img", SizeBytes = 616L * 2048, Options = NdfsOptions },
            new BlankImageLayout { Id = "ndfs-smd-75m", Machine = "Norsk Data ND-100", Name = "SMD disk, 75 MB - 38400 pages", Extension = ".img", SizeBytes = 38400L * 2048, Options = NdfsOptions },
            new BlankImageLayout { Id = "ndfs-winchester-74m", Machine = "Norsk Data ND-100", Name = "Winchester disk, 74 MB (Micropolis 1325) - 36864 pages", Extension = ".img", SizeBytes = 36864L * 2048, Options = NdfsOptions },
            new BlankImageLayout { Id = "ndfs-scsi-126m", Machine = "Norsk Data ND-100", Name = "SCSI disk, 126 MB - 64656 pages", Extension = ".img", SizeBytes = 64656L * 2048, Options = NdfsOptions },
        };

        // The one extension list for this format: detection below uses it, and programs read it
        // through FileExtensions (for example to link these files to themselves).
        private static readonly string[] SupportedExtensions = { ".nd", ".ndfs", ".nord", ".ndf" };

        /// <summary>
        /// Gets the extensions this format is stored under: .nd, .ndfs, .nord, .ndf.
        /// </summary>
        /// <remarks>
        /// Detection uses the same list as its extension signal.
        /// </remarks>
        public override IReadOnlyList<string> FileExtensions => SupportedExtensions;

        /// <summary>
        /// Gets the filesystem type identifier this provider handles.
        /// </summary>
        /// <value>
        /// Always <c>"NDFS"</c>.
        /// </value>
        public override string FileSystemType => "NDFS";

        /// <summary>
        /// Gets a human-readable description of the filesystem.
        /// </summary>
        /// <value>
        /// Always <c>"Nord Disk File System"</c>.
        /// </value>
        public override string Description => "Nord Disk File System";

        /// <inheritdoc/>
        public override System.Collections.Generic.IReadOnlyList<string> Machines => MachineList;

        /// <inheritdoc/>
        public override bool SupportsWrite => true;

        /// <summary>
        /// NDFS requires at least one page (2048 bytes) to validate the master block.
        /// </summary>
        public override int RequiredHeaderBytes => NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;

        /// <summary>
        /// Scores how likely the file is an NDFS image.
        /// </summary>
        /// <param name="hints">
        /// The file name, size and header bytes of the file.
        /// </param>
        /// <returns>
        /// A score from 0.0 to 1.0, where 0.0 means the file is not NDFS.
        /// </returns>
        public override float DetectConfidence(DetectionHints hints)
        {
            if (hints == null)
                return 0.0f;

            // Quick size check - NDFS needs at least one page
            if (hints.FileSize < NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE)
                return 0.0f;

            // Check if size is a multiple of page size
            if (hints.FileSize % NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE != 0)
                return 0.0f;

            // Check extension first for confidence boost
            bool hasNdfsExtension = HasKnownFileExtension(hints);

            // Check if we have enough header data
            if (hints.HeaderBytes == null || hints.HeaderBytes.Length < NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE)
            {
                // If we have the right extension but not enough data, give low confidence
                return hasNdfsExtension ? 0.2f : 0.0f;
            }

            // Try to parse master block
            MasterBlock? masterBlock = null;
            try
            {
                masterBlock = MasterBlock.FromBytes(hints.HeaderBytes);
            }
            catch
            {
                // Failed to parse - if we have extension, give very low confidence
                return hasNdfsExtension ? 0.1f : 0.0f;
            }

            if (masterBlock == null)
                return hasNdfsExtension ? 0.3f : 0.0f;

            // Score against the kernel-verified invariants, bounding the block pointers by the real
            // device size. MasterBlock.IsValid() is deliberately NOT used here: it accepts a page
            // whose 32 bytes at offset 2016 merely hold a printable name OR any single non-zero
            // pointer-shaped word, so nearly any 2048-aligned file would score as NDFS.
            uint devicePages = (uint)(hints.FileSize / NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);
            float score = masterBlock.DetectionScore(devicePages);

            if (score <= 0.0f)
            {
                // Structurally not NDFS. A matching extension is not evidence on its own — a
                // renamed file must not auto-mount — so keep this below any mount threshold.
                return hasNdfsExtension ? 0.2f : 0.0f;
            }

            // A matching extension is a small corroborating boost, never the deciding factor.
            if (hasNdfsExtension)
            {
                score += 0.01f;
                if (score > 1.0f)
                    score = 1.0f;
            }

            return score;
        }

        /// <summary>
        /// Creates a new <see cref="NdfsDriver"/>.
        /// </summary>
        /// <returns>
        /// A new NDFS driver that is not mounted.
        /// </returns>
        public override IFileSystemDriver CreateDriver()
        {
            return new NdfsDriver();
        }

        /// <inheritdoc/>
        public IReadOnlyList<BlankImageLayout> BlankLayouts => Layouts;

        /// <inheritdoc/>
        public byte[] CreateBlank(string layoutId, IReadOnlyDictionary<string, string> options)
        {
            NdfsImageCreationOptions create = new NdfsImageCreationOptions
            {
                Template = TemplateOf(layoutId),
                DirectoryName = DirectoryName.ValueIn(options),
                BootLoaderType = BootLoader.ValueIn(options) switch
                {
                    "None" => NdfsBootLoaderType.None,
                    "Floppy" => NdfsBootLoaderType.Floppy,
                    "SMD" => NdfsBootLoaderType.Smd,
                    "Winchester" => NdfsBootLoaderType.Winchester,
                    "SCSI" => NdfsBootLoaderType.Scsi,
                    _ => null,
                },
            };
            return NdfsImageCreator.CreateImageBytes(create);
        }

        /// <inheritdoc/>
        public IFileSystemDriver Mount(Stream stream, bool readOnly, string layoutId)
        {
            TemplateOf(layoutId);
            NdfsDriver driver = new NdfsDriver();
            driver.Mount(stream, readOnly);
            return driver;
        }

        private static NdfsImageTemplate TemplateOf(string layoutId)
        {
            return layoutId switch
            {
                "ndfs-floppy-360k" => NdfsImageTemplate.Floppy360KB,
                "ndfs-floppy-1200k" => NdfsImageTemplate.Floppy12MB,
                "ndfs-smd-75m" => NdfsImageTemplate.Smd75MB,
                "ndfs-winchester-74m" => NdfsImageTemplate.Winchester74MB,
                "ndfs-scsi-126m" => NdfsImageTemplate.Scsi126MB,
                _ => throw new ArgumentException("not an NDFS blank-image layout: " + layoutId, nameof(layoutId)),
            };
        }
    }
}
