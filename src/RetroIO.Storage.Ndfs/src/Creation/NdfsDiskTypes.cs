//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Storage.Ndfs.Creation
{
    /// <summary>
    /// Controller family of a SINTRAN disk type, from the generation source.
    /// </summary>
    public enum NdfsDiskFamily
    {
        /// <summary>
        /// Named in the manual only; not in the carved name table.
        /// </summary>
        Unknown,

        /// <summary>
        /// ST506 style controller (IOX 500 family).
        /// </summary>
        Winchester,

        /// <summary>
        /// Big disc controller (IOX 1540 family).
        /// </summary>
        Smd
    }

    /// <summary>
    /// One SINTRAN disk type name with the directory size SINTRAN gives it.
    /// </summary>
    /// <remarks>
    /// Names and sizes come from the device-name table (segment 101-S3DNAM) of the SINTRAN L and M
    /// carves: each disk unit name carries one fixed 32-bit number, the directory size in pages. SCSI
    /// disks are not in the table because SINTRAN sets their size from the drive.
    /// <see cref="LayoutPages"/> is the length of the full SINTRAN disk layout (cylinders times heads
    /// times sectors divided by 2) of the DT table entry whose comment names the type. The emulator's
    /// disk controllers choose their geometry from the length of the image file and need it. See
    /// docs/DISK-GEOMETRY-AND-BOOT-FINDINGS.md in the norskdata-ndfs repository.
    /// </remarks>
    public sealed class NdfsDiskType
    {
        /// <summary>
        /// Name without unit number, for example DISC-75MB.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Directory size in pages (the declared capacity); 0 when the size is not known.
        /// </summary>
        public uint Pages { get; }

        /// <summary>
        /// Controller family.
        /// </summary>
        public NdfsDiskFamily Family { get; }

        /// <summary>
        /// True when real SINTRAN M created a directory on this type, or entered an image of it made by
        /// this library, in the nd100x emulator.
        /// </summary>
        public bool Tested { get; }

        /// <summary>
        /// Drive model from the SINTRAN manual.
        /// </summary>
        public string Drive { get; }

        /// <summary>
        /// Pages in the full SINTRAN disk layout; 0 when not known for this name.
        /// </summary>
        public uint LayoutPages { get; }

        internal NdfsDiskType(string name, uint pages, NdfsDiskFamily family, bool tested, string drive, uint layoutPages)
        {
            Name = name;
            Pages = pages;
            Family = family;
            Tested = tested;
            Drive = drive;
            LayoutPages = layoutPages;
        }
    }

    /// <summary>
    /// The table of SINTRAN SMD and Winchester disk types.
    /// </summary>
    public static class NdfsDiskTypes
    {
        /// <summary>
        /// All known disk types, in the order of the SINTRAN name table.
        /// </summary>
        public static IReadOnlyList<NdfsDiskType> All { get; } = new List<NdfsDiskType>
        {
            new NdfsDiskType("DISC-14MB", 6912, NdfsDiskFamily.Winchester, true, "CDC 9410 Finch", 6912),
            new NdfsDiskType("DISC-16MB", 8000, NdfsDiskFamily.Winchester, false, "Micropolis 1304 (ST506)", 0),
            new NdfsDiskType("DISC-21MB", 10728, NdfsDiskFamily.Winchester, true, "CDC 9410 Finch", 10890),
            new NdfsDiskType("DISC-23MB", 11016, NdfsDiskFamily.Winchester, true, "Micropolis 1304 (ST506)", 11205),
            new NdfsDiskType("DISC-28MB", 13648, NdfsDiskFamily.Winchester, true, "Micropolis 1325 (ST506)", 13797),
            new NdfsDiskType("DISC-45MB", 22032, NdfsDiskFamily.Winchester, true, "Micropolis 1304/1325 (ST506)", 22410),
            new NdfsDiskType("DISC-74MB", 36396, NdfsDiskFamily.Winchester, true, "Micropolis 1325 (ST506)", 36792),
            new NdfsDiskType("DISC-30MB", 7389, NdfsDiskFamily.Smd, false, "CDC 9448 CMD big cartridge", 0),
            new NdfsDiskType("DISC-60MB", 7389, NdfsDiskFamily.Smd, false, "CDC 9762 SMD (ND-IO)", 0),
            new NdfsDiskType("DISC-90MB", 7389, NdfsDiskFamily.Smd, false, "CDC 9448 CMD", 0),
            new NdfsDiskType("DISC-38MB", 18486, NdfsDiskFamily.Smd, true, "CDC 9762 SMD (ND-IO)", 18495),
            new NdfsDiskType("DISC-70MB", 34765, NdfsDiskFamily.Smd, true, "CDC 9710 RSD", 37035),
            new NdfsDiskType("DISC-75MB", 36945, NdfsDiskFamily.Smd, true, "CDC 9762 SMD", 37035),
            new NdfsDiskType("DISC-2-75MB", 36945, NdfsDiskFamily.Smd, false, "CDC 9730 MMD (150 MB, sub-divided)", 0),
            new NdfsDiskType("DISC-3-75MB", 36945, NdfsDiskFamily.Smd, false, "CDC 9766 SMD (225 MB, sub-divided)", 0),
            new NdfsDiskType("DISC-140MB-F", 69530, NdfsDiskFamily.Smd, false, "Fujitsu M2322K", 0),
            new NdfsDiskType("DISC-2-70MB-F", 34765, NdfsDiskFamily.Smd, false, "Fujitsu M2322K (140 MB, sub-divided)", 0),
            new NdfsDiskType("DISC-225MB-R", 110292, NdfsDiskFamily.Smd, false, "CDC 9766 SMD", 0),
            new NdfsDiskType("DISC-225MB-F", 110292, NdfsDiskFamily.Smd, false, "not named in the manual", 0),
            new NdfsDiskType("DISC-225MB-E", 110292, NdfsDiskFamily.Smd, false, "not named in the manual", 0),
            new NdfsDiskType("DISC-225MB-N", 110292, NdfsDiskFamily.Smd, false, "not named in the manual", 0),
            new NdfsDiskType("DISC-288MB-R", 140391, NdfsDiskFamily.Smd, true, "CDC 9766 SMD", 140733),
            new NdfsDiskType("DISC-288MB-F", 140391, NdfsDiskFamily.Smd, true, "CDC 9715 FSD 340", 153576),
            new NdfsDiskType("DISC-288MB-E", 140391, NdfsDiskFamily.Smd, false, "CDC 9720 EMD 368", 158210),
            new NdfsDiskType("DISC-288MB-N", 140391, NdfsDiskFamily.Smd, false, "not named in the manual", 144025),
            new NdfsDiskType("DISC-4-70MB-R", 34765, NdfsDiskFamily.Smd, false, "CDC 9766 SMD (280 MB, sub-divided)", 0),
            new NdfsDiskType("DISC-4-70MB-F", 34765, NdfsDiskFamily.Smd, false, "CDC 9715 FSD 340 (280 MB, sub-divided)", 0),
            new NdfsDiskType("DISC-4-70MB-E", 34765, NdfsDiskFamily.Smd, false, "CDC 9720 EMD 368 (280 MB, sub-divided)", 0),
            new NdfsDiskType("DISC-4-70MB-N", 34765, NdfsDiskFamily.Smd, false, "not named in the manual", 0),
            new NdfsDiskType("DISC-6-70MB-F", 34765, NdfsDiskFamily.Smd, false, "CDC 9715 FSD 515 (450 MB, sub-divided)", 0),
            new NdfsDiskType("DISC-6-70MB-N", 34765, NdfsDiskFamily.Smd, false, "not named in the manual", 0),
            new NdfsDiskType("DISC-450MB-F", 220584, NdfsDiskFamily.Smd, true, "CDC 9715 FSD 515", 230364),
            new NdfsDiskType("DISC-450MB-N", 220584, NdfsDiskFamily.Smd, true, "not named in the manual", 223820),
            new NdfsDiskType("DISC-10MB", 0, NdfsDiskFamily.Unknown, false, "CDC 9427 Hawk cartridge (manual only)", 0),
            new NdfsDiskType("DISC-33MB", 0, NdfsDiskFamily.Unknown, false, "CDC 9762 SMD, ND-IO (manual only)", 0),
            new NdfsDiskType("DISC-50MB", 0, NdfsDiskFamily.Unknown, false, "CDC 9448 CMD (manual only)", 0),
            new NdfsDiskType("DISC-2-225MB-F", 0, NdfsDiskFamily.Unknown, false, "CDC 9715 FSD 515 (450 MB, sub-divided; manual only)", 0),
            new NdfsDiskType("DISC-2-225MB-E", 0, NdfsDiskFamily.Unknown, false, "NEC D3525-H (450 MB, sub-divided; manual only)", 0),
            new NdfsDiskType("DISC-6-70MB-E", 0, NdfsDiskFamily.Unknown, false, "NEC D3525-H (450 MB, sub-divided; manual only)", 0),
            new NdfsDiskType("DISC-450MB-E", 0, NdfsDiskFamily.Unknown, false, "NEC D3525-H (manual only)", 0),
        };

        /// <summary>
        /// Looks a disk type up by name, ignoring case.
        /// </summary>
        /// <param name="name">
        /// Name without unit number, for example DISC-75MB or disc-288mb-r.
        /// </param>
        /// <returns>
        /// The disk type, or null when the name is not in the table.
        /// </returns>
        public static NdfsDiskType? Find(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            for (int i = 0; i < All.Count; i++)
            {
                if (string.Equals(All[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return All[i];
            }

            return null;
        }
    }
}
