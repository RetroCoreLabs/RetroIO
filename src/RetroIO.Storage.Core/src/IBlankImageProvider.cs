//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using System.IO;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// A provider that can create blank disk images and says, as data, which ones it can make.
    /// </summary>
    /// <remarks>
    /// <para><b>The rule</b></para>
    /// Every provider whose driver can write implements this interface with at least one layout,
    /// and a test over every registered provider checks, for each layout:
    ///  - the created image is exactly <see cref="BlankImageLayout.SizeBytes"/> long.
    ///  - detection over all providers picks this provider's file system.
    ///  - when <see cref="BlankImageLayout.SameBytesAs"/> is empty, plain detection mounts it as this
    ///    exact layout.
    ///  - <see cref="Mount"/> opens it writable, it lists empty, takes a file and reads it back.
    /// <para>
    /// Layouts come only from real format tables (a manual, a ROM, a kernel, or measured on real
    /// disks). A format whose table is not known is left out, never guessed. A user interface
    /// builds its "new image" window from <see cref="BlankLayouts"/> and the options of each layout.
    /// </para>
    /// </remarks>
    public interface IBlankImageProvider
    {
        /// <summary>
        /// Gets the blank images this provider can create.
        /// </summary>
        IReadOnlyList<BlankImageLayout> BlankLayouts { get; }

        /// <summary>
        /// Creates a blank image of one layout.
        /// </summary>
        /// <param name="layoutId">
        /// The <see cref="BlankImageLayout.Id"/> of the layout.
        /// </param>
        /// <param name="options">
        /// Option values by <see cref="BlankImageOption.Id"/>; a missing option takes its default.
        /// </param>
        /// <returns>
        /// The whole image, as the real machine would have formatted it.
        /// </returns>
        /// <exception cref="System.ArgumentException">
        /// Thrown when <paramref name="layoutId"/> is not one of <see cref="BlankLayouts"/>, or an
        /// option value is not allowed.
        /// </exception>
        byte[] CreateBlank(string layoutId, IReadOnlyDictionary<string, string> options);

        /// <summary>
        /// Mounts an image as one named layout, without detection.
        /// </summary>
        /// <remarks>
        /// Needed where blank images of different machines are the same bytes (see
        /// <see cref="BlankImageLayout.SameBytesAs"/>): the image is opened as the layout the user
        /// picked, not as whichever detection would choose.
        /// </remarks>
        /// <param name="stream">
        /// The image.
        /// </param>
        /// <param name="readOnly">
        /// Whether the mount is read-only.
        /// </param>
        /// <param name="layoutId">
        /// The <see cref="BlankImageLayout.Id"/> of the layout.
        /// </param>
        /// <returns>
        /// The mounted driver.
        /// </returns>
        /// <exception cref="System.ArgumentException">
        /// Thrown when <paramref name="layoutId"/> is not one of <see cref="BlankLayouts"/>.
        /// </exception>
        IFileSystemDriver Mount(Stream stream, bool readOnly, string layoutId);
    }
}
