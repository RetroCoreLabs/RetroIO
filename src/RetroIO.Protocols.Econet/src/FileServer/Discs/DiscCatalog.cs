//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text.RegularExpressions;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Protocols.Econet.FileServer.Discs;

/// <summary>
/// The list of discs found as sub directories of the file server root directory.
/// </summary>
public sealed class DiscCatalog
{
    private static readonly Regex DiscDirectoryPattern = new(@"^([0-9A-F])(.{0,16})$", RegexOptions.Compiled);

    private readonly IFileSystemBackend _fileSystem;
    private readonly string _rootDirectory;
    private readonly DiscEntry[] _discs = new DiscEntry[16];

    /// <summary>
    /// Creates the catalogue, makes sure the default directory layout exists and scans the root directory for discs.
    /// </summary>
    /// <param name="fileSystem">
    /// The file system the discs are stored on.
    /// </param>
    /// <param name="rootDirectory">
    /// The root directory that holds one directory per disc.
    /// </param>
    /// <param name="defaultDiscName">
    /// The name of the disc directory created when the layout is missing.
    /// </param>
    public DiscCatalog(IFileSystemBackend fileSystem, string rootDirectory, string defaultDiscName)
    {
        _fileSystem = fileSystem;
        _rootDirectory = rootDirectory;
        FileServerBootstrap.EnsureLayout(fileSystem, rootDirectory, defaultDiscName);
        Scan();
    }

    /// <summary>
    /// Gets the number of discs found.
    /// </summary>
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

    /// <summary>
    /// Gets the disc with a given index.
    /// </summary>
    /// <param name="index">
    /// The disc index, 0 to 15.
    /// </param>
    /// <returns>
    /// The disc, or null when the index is out of range or no disc has that index.
    /// </returns>
    public DiscEntry? GetByIndex(int index)
    {
        if (index < 0 || index >= _discs.Length)
            return null;

        return _discs[index];
    }

    /// <summary>
    /// Gets the path of the directory that holds a disc.
    /// </summary>
    /// <param name="index">
    /// The disc index, 0 to 15.
    /// </param>
    /// <returns>
    /// The path of the disc directory, or the root directory when there is no disc with that index.
    /// </returns>
    public string GetDiscDirectoryPath(int index)
    {
        DiscEntry? disc = GetByIndex(index);
        if (disc == null)
            return _rootDirectory;

        return _fileSystem.CombinePath(_rootDirectory, disc.DirectoryName);
    }

    /// <summary>
    /// Lists the discs that are allowed by a disc mask, in index order.
    /// </summary>
    /// <param name="discMask">
    /// Bit n of the mask allows the disc with index n.
    /// </param>
    /// <param name="start">
    /// The first disc index to look at.
    /// </param>
    /// <param name="count">
    /// The largest number of discs to return.
    /// </param>
    /// <returns>
    /// The discs found, at most <paramref name="count"/>.
    /// </returns>
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

    /// <summary>
    /// Finds a disc from a name or a single hex digit index.
    /// </summary>
    /// <param name="discReference">
    /// The disc reference. A leading colon and anything from the first dot are removed. A single hex digit is taken as the disc index, anything else is compared with the Acorn name and the directory name, ignoring case.
    /// </param>
    /// <param name="entry">
    /// Receives the disc that was found, or null.
    /// </param>
    /// <returns>
    /// True when a disc was found, otherwise false.
    /// </returns>
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

    /// <summary>
    /// Reports whether a disc mask allows a disc index.
    /// </summary>
    /// <param name="discMask">
    /// Bit n of the mask allows the disc with index n.
    /// </param>
    /// <param name="discIndex">
    /// The disc index, 0 to 15.
    /// </param>
    /// <returns>
    /// True when the index is in range and its bit is set in the mask.
    /// </returns>
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

/// <summary>
/// One disc of the catalogue.
/// </summary>
public sealed class DiscEntry
{
    /// <summary>
    /// Creates a disc entry.
    /// </summary>
    /// <param name="index">
    /// The disc index, 0 to 15.
    /// </param>
    /// <param name="acornName">
    /// The disc name as seen from Acorn.
    /// </param>
    /// <param name="directoryName">
    /// The name of the disc directory, which is the index digit followed by the Acorn name.
    /// </param>
    public DiscEntry(int index, string acornName, string directoryName)
    {
        Index = index;
        AcornName = acornName;
        DirectoryName = directoryName;
    }

    /// <summary>
    /// Gets the disc index.
    /// </summary>
    public int Index { get; }
    /// <summary>
    /// Gets the disc name as seen from Acorn.
    /// </summary>
    public string AcornName { get; }
    /// <summary>
    /// Gets the name of the disc directory.
    /// </summary>
    public string DirectoryName { get; }
}
