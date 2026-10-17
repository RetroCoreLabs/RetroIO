//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Filesystem-agnostic description of a mounted volume, for display in a disk-info UI.
    ///
    /// WHY THIS EXISTS
    /// ---------------
    /// RetroCommander's original disk-info window was written directly against
    /// INdfsFileSystem, so only NDFS volumes could ever show geometry, allocation or
    /// on-disk structure. Every other driver (FFS, FAT, CBM DOS, V7, DragonDOS, ...) had
    /// nothing to show but "type + read-only + path".
    ///
    /// A driver that knows interesting things about its volume implements
    /// <see cref="IVolumeInfoProvider"/> and returns them as titled sections of label/value
    /// pairs. The UI renders whatever it is given without knowing any filesystem, so adding
    /// a new driver's disk info requires no UI change at all.
    ///
    /// This is deliberately a *presentation* model, not a parsing model: values are
    /// pre-formatted strings. Callers that need typed access should read the driver's own
    /// structures instead.
    /// </summary>
    public sealed class VolumeInfo
    {
        /// <summary>
        /// Filesystem type identifier, e.g. "FFS", "NDFS", "FAT16".
        /// </summary>
        public string FileSystemType { get; set; } = string.Empty;

        /// <summary>
        /// Volume label, or an empty string when the filesystem has none.
        /// </summary>
        public string VolumeLabel { get; set; } = string.Empty;

        /// <summary>
        /// Path of the backing image file, when known.
        /// </summary>
        public string ImagePath { get; set; } = string.Empty;

        /// <summary>
        /// True when the volume was mounted read-only.
        /// </summary>
        public bool IsReadOnly { get; set; }

        /// <summary>
        /// Titled groups of label/value pairs, in display order.
        /// </summary>
        public List<VolumeInfoSection> Sections { get; } = new List<VolumeInfoSection>();

        /// <summary>
        /// Optional block/page allocation map for a visual "what is used" strip.
        /// Null when the driver cannot cheaply produce one.
        /// </summary>
        public VolumeAllocationMap? AllocationMap { get; set; }

        /// <summary>
        /// Adds a section and returns it, so callers can chain field additions.
        /// </summary>
        /// <param name="title">Section heading, e.g. "Geometry".</param>
        /// <param name="icon">Optional leading emoji/glyph for the heading.</param>
        /// <returns>The newly added section.</returns>
        public VolumeInfoSection AddSection(string title, string icon = "")
        {
            var section = new VolumeInfoSection { Title = title, Icon = icon };
            Sections.Add(section);
            return section;
        }
    }

    /// <summary>
    /// A titled group of related fields within a <see cref="VolumeInfo"/>.
    /// </summary>
    public sealed class VolumeInfoSection
    {
        /// <summary>Section heading text.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Optional leading emoji/glyph shown before the title.</summary>
        public string Icon { get; set; } = string.Empty;

        /// <summary>Fields in display order.</summary>
        public List<VolumeInfoField> Fields { get; } = new List<VolumeInfoField>();

        /// <summary>
        /// Appends a field to this section.
        /// </summary>
        /// <param name="label">Field name shown on the left.</param>
        /// <param name="value">Pre-formatted value shown on the right.</param>
        /// <param name="detail">Optional secondary text, e.g. a raw hex/octal form.</param>
        /// <returns>This section, for chaining.</returns>
        public VolumeInfoSection Add(string label, string value, string detail = "")
        {
            Fields.Add(new VolumeInfoField { Label = label, Value = value, Detail = detail });
            return this;
        }
    }

    /// <summary>
    /// One label/value row in a <see cref="VolumeInfoSection"/>.
    /// </summary>
    public sealed class VolumeInfoField
    {
        /// <summary>Field name.</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>Pre-formatted value.</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Optional secondary text shown alongside the value — typically the raw encoding
        /// (hex, octal, on-disk field name) behind a humanised value.
        /// </summary>
        public string Detail { get; set; } = string.Empty;
    }

    /// <summary>
    /// Per-unit allocation state, for rendering a "1 pixel = 1 unit" usage strip.
    /// </summary>
    public sealed class VolumeAllocationMap
    {
        /// <summary>
        /// Allocation state of each unit, indexed by unit number.
        /// </summary>
        public VolumeAllocationState[] Units { get; set; } = System.Array.Empty<VolumeAllocationState>();

        /// <summary>
        /// What one unit represents, e.g. "page", "block", "fragment". Used in the legend.
        /// </summary>
        public string UnitName { get; set; } = "block";

        /// <summary>Size of one unit in bytes, for the legend. 0 when not meaningful.</summary>
        public int UnitSizeBytes { get; set; }
    }

    /// <summary>
    /// Allocation state of a single unit in a <see cref="VolumeAllocationMap"/>.
    /// </summary>
    public enum VolumeAllocationState : byte
    {
        /// <summary>Unit is free and allocatable.</summary>
        Free = 0,

        /// <summary>Unit is in use.</summary>
        Used = 1,

        /// <summary>
        /// Unit lies beyond the volume's usable capacity — described by the allocation
        /// structures but not actually reachable on this media.
        /// </summary>
        Beyond = 2,

        /// <summary>Unit is reserved by the filesystem (metadata, boot area, superblock).</summary>
        Reserved = 3
    }

    /// <summary>
    /// Implemented by drivers that can describe their mounted volume in detail.
    /// Optional — callers must handle drivers that do not implement it, and should fall back
    /// to whatever generic information they can assemble from IFileSystemDriver itself.
    /// </summary>
    public interface IVolumeInfoProvider
    {
        /// <summary>
        /// Builds a description of the currently mounted volume.
        /// </summary>
        /// <returns>Volume info, or null when nothing is mounted.</returns>
        VolumeInfo? GetVolumeInfo();
    }
}
