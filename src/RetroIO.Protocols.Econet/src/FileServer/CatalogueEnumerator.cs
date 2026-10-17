//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using RetroIO.Protocols.Econet.FileServer.Metadata;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Protocols.Econet.FileServer;

internal static class CatalogueEnumerator
{
    public static int CountEntries(IFileSystemBackend fileSystem, string unixDirectory)
    {
        int count = 0;
        foreach (string _ in fileSystem.EnumerateFileSystemEntries(unixDirectory))
            count++;
        return count;
    }

    public static List<string> ListSortedNames(IFileSystemBackend fileSystem, string unixDirectory)
    {
        List<string> names = new List<string>();
        foreach (string entry in fileSystem.EnumerateFileSystemEntries(unixDirectory))
            names.Add(fileSystem.GetFileName(entry));

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// PiFS/DNFS catalogue dir name. Behaviour observed at <c>fs.c:10388</c>: when the normalised path
    /// has no components (<c>npath == 0</c>) the tail is <c>$</c>; otherwise it is the LAST component
    /// of the path.
    /// </summary>
    /// <remarks>
    /// "$" means the DISC ROOT — nothing else. A directory below it reports its own leaf name,
    /// so the user's home <c>USER</c> reports as <c>"USER"</c> (it is <c>$.USER</c> in Acorn terms).
    ///
    /// This previously keyed off the user's HOME directory instead of the disc root, which inverted
    /// PiFS: home printed as <c>$</c>, while the disc root — having no special case — fell through to
    /// the unix leaf and leaked the HOST folder name <c>0RetroFS</c> (PiFS's on-disk "disc index +
    /// disc name" convention) to the BBC as if it were an Acorn directory name. Seen live in BeebEm
    /// 2026-07-17 via <c>*DIR :RetroFS</c> then <c>*CAT</c>. It also meant two different directories
    /// both called themselves <c>$</c>. Changed to PiFS parity on the user's instruction 2026-07-17.
    ///
    /// <paramref name="discRootUnix"/> is the unix path of the current disc's root directory
    /// (<c>DiscCatalog.GetDiscDirectoryPath(session.CurrentDisc)</c>).
    /// </remarks>
    public static void WriteCatalogueDirName10(Span<byte> destination, IFileSystemBackend fileSystem, FileServerSession session, string unixPath, string discRootUnix)
    {
        destination.Fill((byte)' ');

        // npath == 0 in PiFS terms: we are AT the disc root.
        if (IsDiscRootDirectory(unixPath, discRootUnix))
        {
            destination[0] = (byte)'$';
            return;
        }

        string tail = fileSystem.GetFileName(unixPath);
        if (string.IsNullOrEmpty(tail) || tail == "$")
        {
            destination[0] = (byte)'$';
            return;
        }

        byte[] ascii = Encoding.ASCII.GetBytes(tail);
        ascii.AsSpan(0, Math.Min(10, ascii.Length)).CopyTo(destination);
    }

    /// <summary>
    /// Reports whether a path is the root directory of the session's current disc.
    /// </summary>
    /// <param name="unixPath">
    /// The host path being named.
    /// </param>
    /// <param name="discRootUnix">
    /// The host path of the current disc's root directory.
    /// </param>
    /// <returns>
    /// True when the two name the same directory, which is the condition the reference calls
    /// <c>npath == 0</c> and reports as <c>$</c>.
    /// </returns>
    public static bool IsDiscRootDirectory(string unixPath, string discRootUnix) =>
        string.Equals(unixPath.TrimEnd('/', '\\'), discRootUnix.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

    public static bool IsUserHomeDirectory(FileServerSession session, string unixPath) =>
        string.Equals(unixPath, session.HomeDirectoryUnix, StringComparison.OrdinalIgnoreCase);

    public static bool TryResolveCatalogueDirectory(
        FileServerContext context,
        FileServerSession session,
        string acornPath,
        byte relativeTo,
        out string unixPath)
    {
        string trimmed = acornPath.Trim();
        if (trimmed.Length == 0 || trimmed == ".")
        {
            unixPath = session.CurrentDirectoryUnix;
            return context.FileSystem.DirectoryExists(unixPath);
        }

        return context.Paths.TryResolve(session, acornPath, relativeTo, out unixPath, out _, out _)
            && context.FileSystem.DirectoryExists(unixPath);
    }

    public static void WriteEntry(
        IFileSystemBackend fileSystem,
        Span<byte> destination,
        string unixDirectory,
        string name,
        byte defaultPerm)
    {
        string fullPath = fileSystem.CombinePath(unixDirectory, name);
        bool isDirectory = fileSystem.IsDirectory(fullPath);
        long length = 0;
        uint load = 0;
        uint exec = 0;
        byte perm = defaultPerm;

        if (!isDirectory)
        {
            System.IO.FileInfo? info = fileSystem.GetFileInfo(fullPath);
            length = info?.Length ?? 0;
            if (AcornInfStore.TryReadAttributes(fileSystem, fullPath, out load, out exec, out int infLength, out perm))
            {
                if (infLength > 0)
                    length = infLength;
            }
            else
            {
                perm = defaultPerm;
            }
        }

        FileServerReply.WriteCatalogEntry(destination, name, isDirectory, length, perm);
        if (!isDirectory)
        {
            destination[10] = (byte)load;
            destination[11] = (byte)(load >> 8);
            destination[12] = (byte)(load >> 16);
            destination[13] = (byte)(load >> 24);
            destination[14] = (byte)exec;
            destination[15] = (byte)(exec >> 8);
            destination[16] = (byte)(exec >> 16);
            destination[17] = (byte)(exec >> 24);
        }
    }
}
