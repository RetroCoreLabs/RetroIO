//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Tests.TNFS;

namespace RetroIO.Tests.Econet;

/// <summary>
/// One recorded call into the file-system backend.
/// </summary>
/// <param name="Operation">
/// The <see cref="IFileSystemBackend"/> method name, e.g. "OpenFile" or "FileExists".
/// </param>
/// <param name="Path">
/// The path argument the operation was called with.
/// </param>
/// <param name="Mode">
/// The <see cref="FileMode"/> for <c>OpenFile</c>; null for operations that take none.
/// </param>
/// <param name="Access">
/// The <see cref="FileAccess"/> for <c>OpenFile</c>; null for operations that take none.
/// </param>
public readonly record struct BackendCall(string Operation, string Path, FileMode? Mode, FileAccess? Access);

/// <summary>
/// An <see cref="IFileSystemBackend"/> decorator that records every call and can override any
/// operation's behaviour per test. Storage delegates to an inner <see cref="MockFileSystemBackend"/>,
/// so seeding stays trivial while tests can assert exactly which backend operations an fsop triggered
/// (and simulate errors/canned results without a real filesystem or a running server).
/// </summary>
public sealed class RecordingFileSystemBackend : IFileSystemBackend
{
    private readonly MockFileSystemBackend _inner;
    private readonly List<BackendCall> _calls = new();

    /// <summary>
    /// Wraps a fresh in-memory mock.
    /// </summary>
    public RecordingFileSystemBackend()
        : this(new MockFileSystemBackend())
    {
    }

    /// <summary>
    /// Wraps a caller-supplied mock (use when the test needs to pre-seed it directly).
    /// </summary>
    public RecordingFileSystemBackend(MockFileSystemBackend inner)
    {
        _inner = inner;
    }

    /// <summary>
    /// The inner in-memory store — seed it (or let the harness seed it) before driving the handler.
    /// </summary>
    public MockFileSystemBackend Inner => _inner;

    /// <summary>
    /// Every backend call, in the order it was made.
    /// </summary>
    public IReadOnlyList<BackendCall> Calls => _calls;

    // Optional per-test behaviour overrides. When set, the hook runs INSTEAD of the inner mock (the
    // call is still recorded first). These are the operations whose behaviour a protocol test most
    // often needs to bend — e.g. force a not-found, a locked file, or canned content.
    public Func<string, bool>? FileExistsHandler { get; set; }
    public Func<string, bool>? DirectoryExistsHandler { get; set; }
    public Func<string, FileMode, FileAccess, FileStream>? OpenFileHandler { get; set; }
    public Func<string, BackendFileInfo?>? GetFileInfoHandler { get; set; }
    public Func<string, IEnumerable<string>>? EnumerateHandler { get; set; }

    /// <summary>
    /// Forgets all recorded calls (e.g. to ignore the login's backend traffic before the opcode).
    /// </summary>
    public void ResetCalls() => _calls.Clear();

    /// <summary>
    /// All recorded calls to the named operation, in order.
    /// </summary>
    public IReadOnlyList<BackendCall> CallsTo(string operation) =>
        _calls.Where(c => c.Operation == operation).ToList();

    /// <summary>
    /// True if any recorded call targeted the given operation and path.
    /// </summary>
    public bool WasCalled(string operation, string path) =>
        _calls.Any(c => c.Operation == operation && c.Path == path);

    // ---- IFileSystemBackend: record then delegate (or run the override) -------------------------

    public bool DirectoryExists(string path)
    {
        _calls.Add(new BackendCall(nameof(DirectoryExists), path, null, null));
        return DirectoryExistsHandler?.Invoke(path) ?? _inner.DirectoryExists(path);
    }

    public void CreateDirectory(string path)
    {
        _calls.Add(new BackendCall(nameof(CreateDirectory), path, null, null));
        _inner.CreateDirectory(path);
    }

    public void DeleteFile(string path)
    {
        _calls.Add(new BackendCall(nameof(DeleteFile), path, null, null));
        _inner.DeleteFile(path);
    }

    public void DeleteDirectory(string path)
    {
        _calls.Add(new BackendCall(nameof(DeleteDirectory), path, null, null));
        _inner.DeleteDirectory(path);
    }

    public void MoveFile(string sourcePath, string destinationPath)
    {
        _calls.Add(new BackendCall(nameof(MoveFile), sourcePath + " -> " + destinationPath, null, null));
        _inner.MoveFile(sourcePath, destinationPath);
    }

    public void MoveDirectory(string sourcePath, string destinationPath)
    {
        _calls.Add(new BackendCall(nameof(MoveDirectory), sourcePath + " -> " + destinationPath, null, null));
        _inner.MoveDirectory(sourcePath, destinationPath);
    }

    public bool FileExists(string path)
    {
        _calls.Add(new BackendCall(nameof(FileExists), path, null, null));
        return FileExistsHandler?.Invoke(path) ?? _inner.FileExists(path);
    }

    public IEnumerable<string> EnumerateFileSystemEntries(string path)
    {
        _calls.Add(new BackendCall(nameof(EnumerateFileSystemEntries), path, null, null));
        return EnumerateHandler?.Invoke(path) ?? _inner.EnumerateFileSystemEntries(path);
    }

    public string GetFileName(string path)
    {
        _calls.Add(new BackendCall(nameof(GetFileName), path, null, null));
        return _inner.GetFileName(path);
    }

    public bool IsDirectory(string path)
    {
        _calls.Add(new BackendCall(nameof(IsDirectory), path, null, null));
        return _inner.IsDirectory(path);
    }

    public BackendFileInfo? GetFileInfo(string path)
    {
        _calls.Add(new BackendCall(nameof(GetFileInfo), path, null, null));
        return GetFileInfoHandler?.Invoke(path) ?? _inner.GetFileInfo(path);
    }

    public FileStream OpenFile(string path, FileMode mode, FileAccess access)
    {
        _calls.Add(new BackendCall(nameof(OpenFile), path, mode, access));
        return OpenFileHandler?.Invoke(path, mode, access) ?? _inner.OpenFile(path, mode, access);
    }

    public string GetFullPath(string path)
    {
        // Not recorded: pure path arithmetic, called constantly by the resolver — recording it would
        // drown the interesting calls. Delegate straight through.
        return _inner.GetFullPath(path);
    }

    public string CombinePath(params string[] paths)
    {
        // Not recorded: pure path arithmetic (see GetFullPath).
        return _inner.CombinePath(paths);
    }
}
