//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// One kind of blank disk image a provider can create.
    /// </summary>
    public sealed class BlankImageLayout
    {
        /// <summary>
        /// Gets the stable id of the layout, for example <c>tiki100-400k</c>.
        /// </summary>
        public required string Id { get; init; }

        /// <summary>
        /// Gets the machine the disk is for, used to group layouts, for example <c>TIKI-100</c>.
        /// </summary>
        public required string Machine { get; init; }

        /// <summary>
        /// Gets the name shown to the user, for example <c>400 KB floppy - 40 tracks, 2 sides, 10 x 512</c>.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Gets the usual file extension, lower case with the dot, for example <c>.dsk</c>.
        /// </summary>
        public required string Extension { get; init; }

        /// <summary>
        /// Gets the size of the created image in bytes.
        /// </summary>
        public required long SizeBytes { get; init; }

        /// <summary>
        /// Gets the options the user can set when creating the image.
        /// </summary>
        public IReadOnlyList<BlankImageOption> Options { get; init; } = Array.Empty<BlankImageOption>();

        /// <summary>
        /// Gets the ids of other layouts whose blank image is the same bytes as this one.
        /// </summary>
        /// <remarks>
        /// A freshly formatted CP/M disk is all E5h, so for example a blank Kaypro II, Osborne 1
        /// and TIKI-100 200 KB disk are identical. Such an image can only be opened as the right
        /// machine by naming the layout (<see cref="IBlankImageProvider.Mount"/>).
        /// </remarks>
        public IReadOnlyList<string> SameBytesAs { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Gets more about the disk for the user, such as its geometry, as free text (may be several
        /// lines, may be empty).
        /// </summary>
        public string Details { get; init; } = string.Empty;
    }
}
