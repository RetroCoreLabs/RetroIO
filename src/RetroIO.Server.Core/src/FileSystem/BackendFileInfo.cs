//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Server.Core.FileSystem;

/// <summary>
/// What a file system backend knows about one file: its length and its times.
/// </summary>
/// <remarks>
/// A <see cref="System.IO.FileInfo"/> can only describe a file on the local disk. A backend that serves a
/// remote store (blob, FTP, TNFS and so on) has no such file, so it returns this type instead.
/// A time is <c>null</c> when the backend does not know it.
/// </remarks>
public sealed class BackendFileInfo
{
    /// <summary>
    /// Creates the description of one file.
    /// </summary>
    /// <param name="length">
    /// The length of the file in bytes.
    /// </param>
    /// <param name="creationTimeUtc">
    /// When the file was created, in UTC, or <c>null</c> when unknown.
    /// </param>
    /// <param name="lastWriteTimeUtc">
    /// When the file was last written, in UTC, or <c>null</c> when unknown.
    /// </param>
    /// <param name="lastAccessTimeUtc">
    /// When the file was last read, in UTC, or <c>null</c> when unknown.
    /// </param>
    public BackendFileInfo(long length, DateTime? creationTimeUtc, DateTime? lastWriteTimeUtc, DateTime? lastAccessTimeUtc)
    {
        Length = length;
        CreationTimeUtc = creationTimeUtc;
        LastWriteTimeUtc = lastWriteTimeUtc;
        LastAccessTimeUtc = lastAccessTimeUtc;
    }

    /// <summary>
    /// Gets the length of the file in bytes.
    /// </summary>
    public long Length { get; }

    /// <summary>
    /// Gets when the file was created, in UTC, or <c>null</c> when the backend does not know.
    /// </summary>
    public DateTime? CreationTimeUtc { get; }

    /// <summary>
    /// Gets when the file was last written, in UTC, or <c>null</c> when the backend does not know.
    /// </summary>
    public DateTime? LastWriteTimeUtc { get; }

    /// <summary>
    /// Gets when the file was last read, in UTC, or <c>null</c> when the backend does not know.
    /// </summary>
    public DateTime? LastAccessTimeUtc { get; }
}
