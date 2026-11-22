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

        // The directory name in the master block. The image creator keeps at most 15 characters and
        // ends the name with an apostrophe (27h), so a name holds 1 to 15 printable characters and no
        // apostrophe.
        private static readonly BlankImageOption DirectoryName = new BlankImageOption { Id = "directoryName", Label = "Directory name", Kind = BlankImageOptionKind.Text, Default = "EMPTY-DISK", MinLength = 1, MaxLength = 15, PrintableAsciiOnly = true, ForbiddenCharacters = "'" };

        // The extended information page, with the system number and flag word in it; the image
        // creator writes it on hard disks only.
        private static readonly BlankImageOption ExtendedInfo = new BlankImageOption { Id = "includeExtendedInfo", Label = "Extended information (hard disks only)", Kind = BlankImageOptionKind.Flag, Default = "true" };
        private static readonly BlankImageOption SystemNumber = new BlankImageOption { Id = "systemNumber", Label = "System number (in the extended information)", Kind = BlankImageOptionKind.Number, Default = "0", Minimum = 0, Maximum = 65535 };
        private static readonly BlankImageOption FlagWord = new BlankImageOption { Id = "flagWord", Label = "Flag word (in the extended information)", Kind = BlankImageOptionKind.Number, Default = "0", Minimum = 0, Maximum = 65535 };

        // A disk of any size: the page count (default the 1.2 MB floppy's 616 pages; at least the
        // smallest template's 154, at most what fits in one image in memory) and the controller,
        // which decides the spare area after the NDFS pages.
        private static readonly BlankImageOption CustomPages = new BlankImageOption { Id = "customPages", Label = "NDFS pages (2 KB each)", Kind = BlankImageOptionKind.Number, Default = "616", Minimum = 154, Maximum = int.MaxValue / 2048 };
        private static readonly BlankImageOption Controller = new BlankImageOption { Id = "controller", Label = "Controller", Kind = BlankImageOptionKind.Choice, Default = "Floppy", Choices = new[] { "Floppy", "SMD", "Winchester", "SCSI" } };

        // The boot loader written to the first page; the disk type's own one unless another is picked.
        private static readonly BlankImageOption BootLoader = new BlankImageOption { Id = "bootLoader", Label = "Boot loader", Kind = BlankImageOptionKind.Choice, Default = "Disk type default", Choices = new[] { "Disk type default", "None", "Floppy", "SMD", "Winchester", "SCSI" } };
        private static readonly BlankImageOption[] NdfsOptions = { DirectoryName, BootLoader, ExtendedInfo, SystemNumber, FlagWord };
        private static readonly BlankImageOption[] CustomOptions = { CustomPages, Controller, DirectoryName, BootLoader, ExtendedInfo, SystemNumber, FlagWord };

        // One layout per disk template of the image creator; size is its page count times 2048.
        private static readonly BlankImageLayout[] Layouts =
        {
            new BlankImageLayout { Id = "ndfs-floppy-360k", Machine = "Norsk Data ND-100", Name = "Floppy (360 KB type) - 154 pages", Extension = ".img", SizeBytes = 154L * 2048, Options = NdfsOptions, Details = DetailsOf(NdfsTemplates.Floppy360KB) },
            new BlankImageLayout { Id = "ndfs-floppy-1200k", Machine = "Norsk Data ND-100", Name = "Floppy (1.2 MB type) - 616 pages", Extension = ".img", SizeBytes = 616L * 2048, Options = NdfsOptions, Details = DetailsOf(NdfsTemplates.Floppy12MB) },
            new BlankImageLayout { Id = "ndfs-smd-75m", Machine = "Norsk Data ND-100", Name = "SMD disk, 75 MB - 38400 pages", Extension = ".img", SizeBytes = 38400L * 2048, Options = NdfsOptions, Details = DetailsOf(NdfsTemplates.Smd75MB) },
            new BlankImageLayout { Id = "ndfs-winchester-74m", Machine = "Norsk Data ND-100", Name = "Winchester disk, 74 MB (Micropolis 1325) - 36864 pages", Extension = ".img", SizeBytes = 36864L * 2048, Options = NdfsOptions, Details = DetailsOf(NdfsTemplates.Winchester74MB) },
            new BlankImageLayout { Id = "ndfs-scsi-126m", Machine = "Norsk Data ND-100", Name = "SCSI disk, 126 MB - 64656 pages", Extension = ".img", SizeBytes = 64656L * 2048, Options = NdfsOptions, Details = DetailsOf(NdfsTemplates.Scsi126MB) },
            new BlankImageLayout { Id = "ndfs-custom", Machine = "Norsk Data ND-100", Name = "Any size - the size is set by the page count and controller options", Extension = ".img", SizeBytes = 616L * 2048, Options = CustomOptions, Details = "The size shown is for the default 616 pages on a floppy controller; a hard-disk controller adds its spare area." },
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
            bool custom = layoutId == "ndfs-custom";
            NdfsImageCreationOptions create = new NdfsImageCreationOptions
            {
                Template = custom ? NdfsImageTemplate.Custom : TemplateOf(layoutId),
                CustomNdfsPages = custom ? uint.Parse(CustomPages.ValueIn(options)) : 0,
                ControllerType = Controller.ValueIn(options) switch
                {
                    "SMD" => NdfsControllerType.Smd,
                    "Winchester" => NdfsControllerType.Winchester,
                    "SCSI" => NdfsControllerType.Scsi,
                    _ => NdfsControllerType.Floppy,
                },
                IncludeExtendedInfo = ExtendedInfo.ValueIn(options) == "true",
                SystemNumber = ushort.Parse(SystemNumber.ValueIn(options)),
                FlagWord = ushort.Parse(FlagWord.ValueIn(options)),
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

        // The numbers of a disk template, for the user.
        private static string DetailsOf(NdfsTemplateSpec spec)
        {
            return spec.NdfsPages + " NDFS pages of 2 KB, device " + spec.FileBlocks + " pages (" + spec.FileBlocks * 2L + " KB)"
                + (spec.SpareBlocks > 0 ? ", of which " + spec.SpareBlocks + " spare after the NDFS area" : string.Empty)
                + ".\nController: " + spec.ControllerType + ", boot loader: " + spec.BootLoaderType + ".";
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
                "ndfs-custom" => NdfsImageTemplate.Custom,
                _ => throw new ArgumentException("not an NDFS blank-image layout: " + layoutId, nameof(layoutId)),
            };
        }
    }
}
