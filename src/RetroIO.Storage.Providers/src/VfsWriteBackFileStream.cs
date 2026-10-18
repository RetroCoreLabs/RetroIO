//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using RetroIO.Storage.Core.VFS;

namespace RetroIO.Storage.Providers;

/// <summary>
/// A writable <see cref="FileStream"/> over a temp file that, on close, writes the temp's final content
/// back to a VFS mount via <see cref="VfsCore.WriteFileAsync"/> and deletes the temp.
/// </summary>
/// <remarks>
/// This is the write half of <see cref="VfsFileSystemBackend"/>'s materialize-to-temp model (design step
/// 2). The caller writes/seeks against a local temp — so Econet's random-access <c>PutBytes</c> works —
/// and the mount is updated exactly once when the handle closes. Concurrent handles to the same path are
/// last-writeback-wins (documented, acceptable single-tenant).
/// </remarks>
internal sealed class VfsWriteBackFileStream : FileStream
{
    private readonly VfsCore _vfs;
    private readonly string _urn;
    private readonly string _tempPath;
    private bool _wroteBack;

    /// <summary>
    /// Opens the temp file read-write and, when <paramref name="append"/>, seeks to its end.
    /// </summary>
    public VfsWriteBackFileStream(string tempPath, VfsCore vfs, string urn, bool append)
        : base(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
    {
        _vfs = vfs;
        _urn = urn;
        _tempPath = tempPath;
        if (append)
            Seek(0, SeekOrigin.End);
    }

    /// <summary>
    /// Closes the stream and, the first time it is called with <paramref name="disposing"/> set,
    /// writes the whole temp file back to the VFS mount and deletes the temp file.
    /// </summary>
    /// <remarks>
    /// The stream is flushed and its handle closed before the temp file is read back. If the read or
    /// the write-back fails, the exception comes out of this call and the temp file is left in place.
    /// Failure to delete the temp file is ignored. Later calls, and calls with
    /// <paramref name="disposing"/> set to <c>false</c>, only run the base <see cref="FileStream"/> dispose.
    /// </remarks>
    /// <param name="disposing">
    /// <c>true</c> to release managed resources and write the content back.
    /// </param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_wroteBack)
        {
            _wroteBack = true;

            Flush();
            base.Dispose(true); // close the handle so the temp can be read back

            // Push the final content to the mount, then remove the temp.
            byte[] data = File.ReadAllBytes(_tempPath);
            _vfs.WriteFileAsync(_urn, data).GetAwaiter().GetResult();
            try { File.Delete(_tempPath); } catch { /* best effort */ }
            return;
        }

        base.Dispose(disposing);
    }
}
