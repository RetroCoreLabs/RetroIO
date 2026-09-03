//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Collections.Generic;

namespace RetroIO.Storage.Ndfs.Creation
{
    /// <summary>
    /// Configuration for creating a new NDFS disk image.
    /// </summary>
    public class NdfsImageCreationOptions
    {
        /// <summary>
        /// Template to use for image creation.
        /// </summary>
        public NdfsImageTemplate Template { get; set; }

        /// <summary>
        /// Controller type (auto-detected from template, overridable for Custom).
        /// Determines overhead calculation for spare sectors.
        /// </summary>
        public NdfsControllerType ControllerType { get; set; }

        /// <summary>
        /// Directory name (max 16 characters, default "EMPTY-DISK").
        /// Terminated with ' (ASCII 0x27) per NDFS spec.
        /// </summary>
        public string DirectoryName { get; set; } = "EMPTY-DISK";

        /// <summary>
        /// System number for Extended Info block (hard disks only, default 0).
        /// Represents CPU serial number (e.g., 100, 102, 246).
        /// </summary>
        public ushort SystemNumber { get; set; } = 0;

        /// <summary>
        /// Flag word for Extended Info block (default 0).
        /// Purpose documented in SINTRAN III but always 0 in test images.
        /// </summary>
        public ushort FlagWord { get; set; } = 0;

        /// <summary>
        /// Boot loader type to include.
        /// If null, uses template default. Set to NdfsBootLoaderType.None for no boot loader.
        /// </summary>
        public NdfsBootLoaderType? BootLoaderType { get; set; } = null;

        /// <summary>
        /// Custom NDFS page count (only used with Template=Custom).
        /// This is the filesystem capacity reported by SINTRAN.
        /// </summary>
        public uint CustomNdfsPages { get; set; }

        /// <summary>
        /// Optional explicit bit-file block address (only used with Template=Custom),
        /// mirroring the address argument of SINTRAN's @CREATE-DIRECTORY. When set,
        /// placement follows the kernel's OVERRIDE path (ALBIT 137517B): the bit file goes
        /// at this address and the object/user index blocks are packed just below it
        /// (object = address − 4, user = address − 2). When null, placement follows the
        /// DEFAULT path: the bit file lands at floor(pages/2) rounded down to a multiple
        /// of 9 (mid-disk on a track boundary). The branch is address-supplied vs not —
        /// NOT device size.
        /// </summary>
        public uint? BitFileAddress { get; set; } = null;

        /// <summary>
        /// Users to create in the image (optional).
        /// If null or empty, only SYSTEM user (ID=0) is created.
        /// </summary>
        public List<NdfsUserCreationInfo>? Users { get; set; }

        /// <summary>
        /// Whether to calculate and include Extended Info block (hard disks only).
        /// Auto-set based on template. FLOMON floppies have ExtValid=false.
        /// </summary>
        public bool IncludeExtendedInfo { get; set; } = true;

        /// <summary>
        /// Optional: Custom boot sector (1024 bytes).
        /// If provided, overrides BootLoaderType.
        /// </summary>
        public byte[]? CustomBootSector { get; set; }

        /// <summary>
        /// When true, produce a completely BLANK image: the file is created at the correct size for
        /// the chosen template/geometry but filled entirely with 0x00 — no boot sector, no master
        /// block (no directory name, no object/user/bit-file pointers), no extended-info block, no
        /// allocation bitmap.
        /// </summary>
        /// <remarks>
        /// Useful when you only need a correctly-sized empty device — for example to let SINTRAN's
        /// own @CREATE-DIRECTORY format it, to dd real disk contents into, or as a scratch target.
        /// A blank image is deliberately NOT a mountable NDFS volume: with all-zero pointers it is
        /// exactly the shape the hardened detector rejects (the real disk22.img / WD0.img blanks
        /// look the same), so it will not auto-mount. That is intended, not a defect.
        /// Overrides <see cref="IncludeExtendedInfo"/>, <see cref="BootLoaderType"/> and
        /// <see cref="CustomBootSector"/>.
        /// </remarks>
        public bool CreateBlankImage { get; set; } = false;

        /// <summary>
        /// When false, the boot sector is left as zeros even though the rest of the filesystem
        /// (master block, extended info, bitmap) IS written. Lets you create a valid, mountable but
        /// NON-BOOTABLE NDFS volume. Ignored when <see cref="CreateBlankImage"/> is true.
        /// Setting <see cref="BootLoaderType"/> to <c>None</c> has the same effect.
        /// </summary>
        public bool IncludeBootSector { get; set; } = true;

        /// <summary>
        /// When false, the 32-byte master block (directory name + object/user/bit-file pointers) is
        /// left as zeros. The image will not be a mountable NDFS volume — use only when you want the
        /// geometry and boot area but intend to format the directory elsewhere.
        /// Ignored when <see cref="CreateBlankImage"/> is true.
        /// </summary>
        public bool IncludeMasterBlock { get; set; } = true;
    }
}
