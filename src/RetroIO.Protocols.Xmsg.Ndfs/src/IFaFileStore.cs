//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Ndfs
{
    /// <summary>
    /// The files a COSMOS file server (<c>*FA-SERVER</c>) serves: what the server asks of its storage,
    /// and nothing about the wire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="FolderFileStore"/> (one local folder) was the only store, and the file server took it
    /// by its class. This interface lets the server's root be any storage location - a local folder, a
    /// TNFS endpoint or a <c>mounts.json</c> volume (Ronny, 2026-11-08; plan Phase 6).
    /// </para>
    /// <para>
    /// The promises every store keeps, written down on <see cref="FolderFileStore"/>: a file number is
    /// handed out once and never reused, requests name files by SINTRAN specification, and reads and
    /// writes are by byte offset.
    /// </para>
    /// </remarks>
    public interface IFaFileStore
    {
        /// <summary>
        /// Gets where the files are, as text for log lines and status pages (a folder, a URI).
        /// </summary>
        string Location { get; }

        /// <summary>
        /// Lists the files.
        /// </summary>
        /// <returns>
        /// The files, in name order.
        /// </returns>
        IReadOnlyList<FaFileInfo> ListFiles();

        /// <summary>
        /// Creates an empty file.
        /// </summary>
        /// <param name="specification">
        /// The SINTRAN file specification.
        /// </param>
        /// <param name="fileNumber">
        /// Receives the new file's stable number.
        /// </param>
        /// <returns>
        /// The outcome.
        /// </returns>
        FaStoreStatus Create(string specification, out ushort fileNumber);

        /// <summary>
        /// Deletes a file. Its number is not given to a later file.
        /// </summary>
        /// <param name="specification">
        /// The SINTRAN file specification.
        /// </param>
        /// <returns>
        /// The outcome.
        /// </returns>
        FaStoreStatus Delete(string specification);

        /// <summary>
        /// Opens a file for reading or writing.
        /// </summary>
        /// <param name="specification">
        /// The SINTRAN file specification.
        /// </param>
        /// <param name="forWrite">
        /// True to open for writing, false for reading.
        /// </param>
        /// <param name="handle">
        /// Receives the open-file handle.
        /// </param>
        /// <returns>
        /// The outcome.
        /// </returns>
        FaStoreStatus Open(string specification, bool forWrite, out ushort handle);

        /// <summary>
        /// Closes an open file.
        /// </summary>
        /// <param name="handle">
        /// The handle from <see cref="Open"/>.
        /// </param>
        /// <returns>
        /// The outcome.
        /// </returns>
        FaStoreStatus Close(ushort handle);

        /// <summary>
        /// Reads bytes from an open file at a byte offset.
        /// </summary>
        /// <param name="handle">
        /// The handle from <see cref="Open"/>.
        /// </param>
        /// <param name="offset">
        /// The byte offset to read from.
        /// </param>
        /// <param name="destination">
        /// The buffer to fill.
        /// </param>
        /// <param name="bytesRead">
        /// Receives how many bytes were read; 0 at end of file.
        /// </param>
        /// <returns>
        /// The outcome.
        /// </returns>
        FaStoreStatus Read(ushort handle, long offset, Span<byte> destination, out int bytesRead);

        /// <summary>
        /// Writes bytes to an open file at a byte offset.
        /// </summary>
        /// <param name="handle">
        /// The handle from <see cref="Open"/>.
        /// </param>
        /// <param name="offset">
        /// The byte offset to write at.
        /// </param>
        /// <param name="source">
        /// The bytes to write.
        /// </param>
        /// <returns>
        /// The outcome.
        /// </returns>
        FaStoreStatus Write(ushort handle, long offset, ReadOnlySpan<byte> source);

        /// <summary>
        /// Truncates or extends an open file to a byte length (a COSMOS writer states the true length
        /// after its last padded 2048-byte block).
        /// </summary>
        /// <param name="handle">
        /// The handle from <see cref="Open"/>.
        /// </param>
        /// <param name="byteLength">
        /// The length the file is to have.
        /// </param>
        /// <returns>
        /// The outcome.
        /// </returns>
        FaStoreStatus SetLength(ushort handle, long byteLength);
    }
}
