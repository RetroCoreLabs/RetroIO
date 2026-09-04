//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Server.Core.FileSystem;

/// <summary>
/// Real file system backend implementation using System.IO.
/// </summary>
public sealed class LocalFileSystemBackend : IFileSystemBackend
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool FileExists(string path) => File.Exists(path);

    public IEnumerable<string> EnumerateFileSystemEntries(string path)
        => Directory.EnumerateFileSystemEntries(path);

    public string GetFileName(string path) => Path.GetFileName(path);

    public bool IsDirectory(string path) => Directory.Exists(path);

    public FileInfo? GetFileInfo(string path)
    {
        try
        {
            return new FileInfo(path);
        }
        catch
        {
            return null;
        }
    }

    public FileStream OpenFile(string path, FileMode mode, FileAccess access)
        => File.Open(path, mode, access);

    public string GetFullPath(string path) => Path.GetFullPath(path);

    public string CombinePath(params string[] paths) => Path.Combine(paths);
}
