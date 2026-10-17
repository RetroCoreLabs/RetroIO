//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text.RegularExpressions;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Protocols.Econet.FileServer.Discs;

public sealed class DiscCatalog
{
    private static readonly Regex DiscDirectoryPattern = new(@"^([0-9A-F])(.{0,16})$", RegexOptions.Compiled);

    private readonly IFileSystemBackend _fileSystem;
    private readonly string _rootDirectory;
    private readonly DiscEntry[] _discs = new DiscEntry[16];

    public DiscCatalog(IFileSystemBackend fileSystem, string rootDirectory, string defaultDiscName)
    {
        _fileSystem = fileSystem;
        _rootDirectory = rootDirectory;
        FileServerBootstrap.EnsureLayout(fileSystem, rootDirectory, defaultDiscName);
        Scan();
    }

    public int Count
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _discs.Length; i++)
            {
                if (_discs[i] != null)
                    count++;
            }

            return count;
        }
    }

    public DiscEntry? GetByIndex(int index)
    {
        if (index < 0 || index >= _discs.Length)
            return null;

        return _discs[index];
    }

    public string GetDiscDirectoryPath(int index)
    {
        DiscEntry? disc = GetByIndex(index);
        if (disc == null)
            return _rootDirectory;

        return _fileSystem.CombinePath(_rootDirectory, disc.DirectoryName);
    }

    public IReadOnlyList<DiscEntry> GetVisibleDiscs(ushort discMask, int start, int count)
    {
        List<DiscEntry> result = new List<DiscEntry>();
        for (int index = start; index < _discs.Length && result.Count < count; index++)
        {
            DiscEntry? disc = _discs[index];
            if (disc == null)
                continue;

            ushort bit = (ushort)(1 << index);
            if ((discMask & bit) == 0)
                continue;

            result.Add(disc);
        }

        return result;
    }

    public bool TryFind(string discReference, out DiscEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(discReference))
            return false;

        string reference = discReference.Trim();
        if (reference.StartsWith(':'))
            reference = reference.Substring(1);

        int dotIndex = reference.IndexOf('.');
        if (dotIndex > 0)
            reference = reference.Substring(0, dotIndex);

        if (reference.Length == 1 && IsHexDigit(reference[0]))
        {
            int index = Convert.ToInt32(reference, 16);
            entry = GetByIndex(index);
            return entry != null;
        }

        for (int i = 0; i < _discs.Length; i++)
        {
            DiscEntry? candidate = _discs[i];
            if (candidate == null)
                continue;

            if (string.Equals(candidate.AcornName, reference, StringComparison.OrdinalIgnoreCase))
            {
                entry = candidate;
                return true;
            }

            if (string.Equals(candidate.DirectoryName, reference, StringComparison.OrdinalIgnoreCase))
            {
                entry = candidate;
                return true;
            }
        }

        return false;
    }

    public static bool IsDiscVisible(ushort discMask, int discIndex)
    {
        if (discIndex < 0 || discIndex >= 16)
            return false;

        ushort bit = (ushort)(1 << discIndex);
        return (discMask & bit) != 0;
    }

    private static bool IsHexDigit(char value)
    {
        return value is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';
    }

    private void Scan()
    {
        if (!_fileSystem.DirectoryExists(_rootDirectory))
            return;

        List<string> entryList = new List<string>();
        foreach (string entry in _fileSystem.EnumerateFileSystemEntries(_rootDirectory))
            entryList.Add(entry);

        for (int i = 0; i < entryList.Count; i++)
        {
            string entry = entryList[i];
            if (!_fileSystem.IsDirectory(entry))
                continue;

            string name = _fileSystem.GetFileName(entry);
            Match match = DiscDirectoryPattern.Match(name);
            if (!match.Success)
                continue;

            int index = Convert.ToInt32(match.Groups[1].Value, 16);
            _discs[index] = new DiscEntry(index, match.Groups[2].Value, name);
        }
    }
}

public sealed class DiscEntry
{
    public DiscEntry(int index, string acornName, string directoryName)
    {
        Index = index;
        AcornName = acornName;
        DirectoryName = directoryName;
    }

    public int Index { get; }
    public string AcornName { get; }
    public string DirectoryName { get; }
}
