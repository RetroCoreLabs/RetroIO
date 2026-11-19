//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using RetroIO.Storage.Local.Streams;

namespace RetroIO.Storage.Local.Images
{
    /// <summary>
    /// Opens a disk image file and hands back only its disk bytes: a raw image as it is, and a fixed
    /// VHD without its 512-byte footer.
    /// </summary>
    /// <remarks>
    /// A fixed VHD is recognised by the footer at the end of the file (the <c>conectix</c> cookie, disk
    /// type fixed, a correct checksum), never by the file name, so a <c>.img</c> that is really a VHD is
    /// opened right too. The stream handed back cannot reach the footer: reads stop before it, and a
    /// write past the disk bytes is refused, so the footer is never changed. A VHD that cannot be used
    /// (damaged footer, dynamic or differencing disk) is refused with the reason.
    /// </remarks>
    public static class DiskImageFile
    {
        /// <summary>
        /// Opens a disk image file.
        /// </summary>
        /// <param name="path">
        /// The image file.
        /// </param>
        /// <param name="access">
        /// Read, or read and write.
        /// </param>
        /// <param name="share">
        /// What other openers may do with the file.
        /// </param>
        /// <returns>
        /// The disk bytes. Disposing it closes the file.
        /// </returns>
        /// <exception cref="InvalidDataException">
        /// Thrown when the file ends in a VHD footer that cannot be used; the message names the reason.
        /// </exception>
        public static Stream Open(string path, FileAccess access, FileShare share)
        {
            FileStream file = new FileStream(path, FileMode.Open, access, share);
            try
            {
                return Wrap(file, path);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Returns the disk bytes of an already open image: the stream itself for a raw image, or a view
        /// without the footer for a fixed VHD. The view owns <paramref name="image"/> and disposes it.
        /// </summary>
        /// <param name="image">
        /// The open image, readable and seekable.
        /// </param>
        /// <param name="name">
        /// A name for the image, used in error messages.
        /// </param>
        /// <returns>
        /// The disk bytes.
        /// </returns>
        /// <exception cref="InvalidDataException">
        /// Thrown when the image ends in a VHD footer that cannot be used.
        /// </exception>
        public static Stream Wrap(Stream image, string name)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            VhdProbeResult kind = FixedVhd.Probe(image, out FixedVhdFooter footer, out string? problem);
            if (kind == VhdProbeResult.NotUsable)
            {
                throw new InvalidDataException($"{name}: {problem}.");
            }
            if (kind == VhdProbeResult.Fixed)
            {
                return new OwningSubStream(image, footer.DiskBytes);
            }
            return image;
        }

        /// <summary>
        /// Returns the number of disk bytes in an image file: the file length for a raw image, the
        /// length without the footer for a fixed VHD.
        /// </summary>
        /// <param name="path">
        /// The image file.
        /// </param>
        /// <returns>
        /// The disk bytes.
        /// </returns>
        public static long GetDiskLength(string path)
        {
            using FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (FixedVhd.Probe(file, out FixedVhdFooter footer, out _) == VhdProbeResult.Fixed)
            {
                return footer.DiskBytes;
            }
            return file.Length;
        }

        // A view of the first bytes of a stream that also closes that stream when it is closed.
        private sealed class OwningSubStream : SubStream
        {
            private readonly Stream _owned;

            public OwningSubStream(Stream baseStream, long length)
                : base(baseStream, 0, length)
            {
                _owned = baseStream;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _owned.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
