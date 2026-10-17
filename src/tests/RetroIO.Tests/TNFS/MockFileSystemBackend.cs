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

namespace RetroIO.Tests.TNFS;

/// <summary>
/// Mock file system backend for testing.
/// Allows creating in-memory file system structure without touching disk.
/// </summary>
public sealed class MockFileSystemBackend : IFileSystemBackend
{
    private readonly Dictionary<string, MockEntry> _entries = new();
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    public class MockEntry
    {
        public bool IsDirectory { get; set; }
        public byte[] Content { get; set; } = Array.Empty<byte>();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public void AddDirectory(string path)
    {
        var normalized = NormalizePath(path);
        _entries[normalized] = new MockEntry { IsDirectory = true };
    }

    public void CreateDirectory(string path)
    {
        // Mirror Directory.CreateDirectory: create the target and any missing parents.
        var normalized = NormalizePath(path);
        if (normalized.Length == 0)
            return;

        int index = normalized.IndexOf('/', 1);
        while (index > 0)
        {
            string parent = normalized.Substring(0, index);
            if (parent.Length > 0 && !_entries.ContainsKey(parent))
                _entries[parent] = new MockEntry { IsDirectory = true };
            index = normalized.IndexOf('/', index + 1);
        }

        _entries[normalized] = new MockEntry { IsDirectory = true };
    }

    public void AddFile(string path, byte[] content = null!)
    {
        var normalized = NormalizePath(path);
        _entries[normalized] = new MockEntry
        {
            IsDirectory = false,
            Content = content ?? Array.Empty<byte>()
        };
    }

    private string NormalizePath(string path)
    {
        // Normalize to forward slashes and remove trailing slash
        path = path.Replace('\\', '/');
        if (path.Length > 1 && path.EndsWith('/'))
            path = path.TrimEnd('/');
        return path;
    }

    public bool DirectoryExists(string path)
    {
        var normalized = NormalizePath(path);
        return _entries.TryGetValue(normalized, out var entry) && entry.IsDirectory;
    }

    public bool FileExists(string path)
    {
        var normalized = NormalizePath(path);
        return _entries.TryGetValue(normalized, out var entry) && !entry.IsDirectory;
    }

    public IEnumerable<string> EnumerateFileSystemEntries(string path)
    {
        var normalized = NormalizePath(path);

        // Find all entries that are direct children
        foreach (var kvp in _entries)
        {
            if (kvp.Key == normalized) continue; // Skip the directory itself

            // Check if this is a direct child
            var relativePath = kvp.Key;
            if (relativePath.StartsWith(normalized + "/"))
            {
                var childPath = relativePath.Substring(normalized.Length + 1);

                // Only return direct children (no slashes in child path)
                if (!childPath.Contains('/'))
                {
                    yield return kvp.Key;
                }
            }
        }
    }

    public string GetFileName(string path)
    {
        var normalized = NormalizePath(path);
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash < 0 ? normalized : normalized.Substring(lastSlash + 1);
    }

    public bool IsDirectory(string path)
    {
        var normalized = NormalizePath(path);
        return _entries.TryGetValue(normalized, out var entry) && entry.IsDirectory;
    }

    public FileInfo? GetFileInfo(string path)
    {
        string normalized = NormalizePath(path);
        if (!_entries.TryGetValue(normalized, out MockEntry? entry) || entry.IsDirectory)
            return null;

        string tempPath = GetTempPath(normalized);
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        File.WriteAllBytes(tempPath, entry.Content);
        return new FileInfo(tempPath);
    }

    public FileStream OpenFile(string path, FileMode mode, FileAccess access)
    {
        string normalized = NormalizePath(path);

        if (mode == FileMode.Create || mode == FileMode.CreateNew || mode == FileMode.OpenOrCreate)
        {
            if (!_entries.TryGetValue(normalized, out MockEntry? created) || created.IsDirectory)
            {
                _entries[normalized] = new MockEntry
                {
                    IsDirectory = false,
                    Content = Array.Empty<byte>(),
                };
            }
        }

        if (!_entries.TryGetValue(normalized, out MockEntry? entry) || entry.IsDirectory)
            throw new FileNotFoundException(path);

        string tempPath = GetTempPath(normalized);
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

        if (access == FileAccess.Write || mode == FileMode.Create || mode == FileMode.CreateNew)
        {
            entry.Content = Array.Empty<byte>();
            return new MockWriteFileStream(tempPath, normalized, this);
        }

        File.WriteAllBytes(tempPath, entry.Content);
        FileStream stream = new FileStream(tempPath, FileMode.Open, access, FileShare.ReadWrite);
        return stream;
    }

    private sealed class MockWriteFileStream : FileStream
    {
        private readonly string _normalized;
        private readonly MockFileSystemBackend _backend;
        private bool _synced;

        public MockWriteFileStream(string path, string normalized, MockFileSystemBackend backend)
            : base(path, FileMode.Create, FileAccess.Write)
        {
            _normalized = normalized;
            _backend = backend;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_synced)
            {
                _synced = true;
                base.Dispose(disposing);
                _backend.SyncFromTempFile(_normalized);
                return;
            }

            base.Dispose(disposing);
        }
    }

    internal void UpdateFileContent(string path, byte[] content)
    {
        string normalized = NormalizePath(path);
        if (_entries.TryGetValue(normalized, out MockEntry? entry) && !entry.IsDirectory)
            entry.Content = content;
    }

    internal void SyncFromTempFile(string path)
    {
        string normalized = NormalizePath(path);
        string tempPath = GetTempPath(normalized);
        if (File.Exists(tempPath))
            UpdateFileContent(normalized, File.ReadAllBytes(tempPath));
    }

    private string GetTempPath(string normalized)
    {
        string safeName = normalized.TrimStart('/').Replace('/', '_');
        return Path.Combine(Path.GetTempPath(), "retrofs-mock", _instanceId, safeName);
    }

    public string GetFullPath(string path)
    {
        // For mock, normalize and ensure it starts with /
        var normalized = NormalizePath(path);
        if (!normalized.StartsWith("/"))
            normalized = "/" + normalized;
        return normalized;
    }

    public string CombinePath(params string[] paths)
    {
        // Join paths with / and normalize
        var parts = paths.Select(p => p.Trim('/', '\\')).Where(p => !string.IsNullOrEmpty(p));
        var combined = "/" + string.Join("/", parts);
        return NormalizePath(combined);
    }
}
