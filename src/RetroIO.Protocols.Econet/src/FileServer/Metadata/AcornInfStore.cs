//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Globalization;
using System.Text;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Protocols.Econet.FileServer.Metadata;

/// <summary>
/// Reads and writes the sidecar file that holds the Acorn attributes of a file or directory: load address, execution address, length, access byte, owner and home owner.
/// </summary>
/// <remarks>
/// The sidecar has the object name followed by a comma and inf. It holds one KEY=value pair per line. The keys LOAD, EXEC, LENGTH and ACCESS were there first.
/// OWNER and HOMEOF were added to follow the PiFS reference (PiEconetBridge utilities/fs.c), which keeps an owner and a home owner for each object in its attribute store.
/// Source: struct objattr (line 318), and fs_read_attr_from_file and fs_write_attr_to_file (lines 1608 to 1648), which hold the five numbers owner, load, exec, perm and homeof in hex.
/// A sidecar without OWNER or HOMEOF reads as owner 0 and home owner 0, which is what fs_read_xattr sets before it reads anything (lines 1675 to 1686).
/// Older code skips the two new keys, because the reader ignores keys it does not know.
/// </remarks>
public static class AcornInfStore
{
    /// <summary>
    /// The owner of an object that has no stored owner. The value is 0, which fs.c calls syst (fs_read_xattr, line 1678).
    /// </summary>
    public const ushort DefaultOwner = 0;

    /// <summary>
    /// The suffix of the sidecar file, added to the name of the object.
    /// </summary>
    public const string SidecarSuffix = ",inf";

    private const string OwnerKey = "OWNER";
    private const string HomeOfKey = "HOMEOF";

    /// <summary>
    /// Reads the load address, execution address, length and access byte of a file from its sidecar file, which has the file name followed by a comma and inf.
    /// </summary>
    /// <param name="fileSystem">
    /// The file system the files are stored on.
    /// </param>
    /// <param name="unixPath">
    /// The host path of the file.
    /// </param>
    /// <param name="load">
    /// Receives the load address, 0 when not stored.
    /// </param>
    /// <param name="exec">
    /// Receives the execution address, 0 when not stored.
    /// </param>
    /// <param name="length">
    /// Receives the length from the sidecar file, or the real file size when the sidecar file has none.
    /// </param>
    /// <param name="access">
    /// Receives the access byte, 0x33 when not stored.
    /// </param>
    /// <returns>
    /// True when the sidecar file exists or the file itself exists, otherwise false.
    /// </returns>
    public static bool TryReadAttributes(IFileSystemBackend fileSystem, string unixPath, out uint load, out uint exec, out int length, out byte access)
    {
        load = 0;
        exec = 0;
        length = 0;
        access = 0x33;

        if (fileSystem.FileExists(unixPath))
        {
            RetroIO.Server.Core.FileSystem.BackendFileInfo? info = fileSystem.GetFileInfo(unixPath);
            length = (int)(info?.Length ?? 0);
        }

        string? text = ReadSidecarText(fileSystem, unixPath);
        if (text == null)
            return fileSystem.FileExists(unixPath);

        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
                continue;

            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            string key = line.Substring(0, eq).Trim().ToLowerInvariant();
            string value = line.Substring(eq + 1).Trim();

            if (key == "load" && TryParseAcornHex(value, out uint parsedLoad))
                load = parsedLoad;
            else if (key == "exec" && TryParseAcornHex(value, out uint parsedExec))
                exec = parsedExec;
            else if (key == "length" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLength))
                length = parsedLength;
            else if (key == "access" && TryParseAcornHex(value, out uint parsedAccess))
                access = (byte)(parsedAccess & 0xFF);
        }

        return true;
    }

    /// <summary>
    /// Writes the sidecar file of a file with its load address, execution address, length and access byte. An existing sidecar file is replaced, but its owner and home owner are kept.
    /// </summary>
    /// <param name="fileSystem">
    /// The file system the files are stored on.
    /// </param>
    /// <param name="unixPath">
    /// The host path of the file.
    /// </param>
    /// <param name="load">
    /// The load address.
    /// </param>
    /// <param name="exec">
    /// The execution address.
    /// </param>
    /// <param name="length">
    /// The file length.
    /// </param>
    /// <param name="access">
    /// The access byte.
    /// </param>
    public static void WriteAttributes(IFileSystemBackend fileSystem, string unixPath, uint load, uint exec, int length, byte access)
    {
        ReadOwnershipLines(fileSystem, unixPath, out ushort? owner, out ushort? homeOf);
        WriteSidecar(fileSystem, unixPath, load, exec, length, access, owner, homeOf);
    }

    /// <summary>
    /// Writes the sidecar file of a file with its attributes and a new owner. An existing sidecar file is replaced, but its home owner is kept.
    /// </summary>
    /// <param name="fileSystem">
    /// The file system the files are stored on.
    /// </param>
    /// <param name="unixPath">
    /// The host path of the file.
    /// </param>
    /// <param name="load">
    /// The load address.
    /// </param>
    /// <param name="exec">
    /// The execution address.
    /// </param>
    /// <param name="length">
    /// The file length.
    /// </param>
    /// <param name="access">
    /// The access byte.
    /// </param>
    /// <param name="owner">
    /// The user number of the new owner.
    /// </param>
    public static void WriteAttributes(IFileSystemBackend fileSystem, string unixPath, uint load, uint exec, int length, byte access, ushort owner)
    {
        ReadOwnershipLines(fileSystem, unixPath, out _, out ushort? homeOf);
        WriteSidecar(fileSystem, unixPath, load, exec, length, access, owner, homeOf);
    }

    /// <summary>
    /// Reads the owner and the home owner of a file or directory from its sidecar file.
    /// </summary>
    /// <param name="fileSystem">
    /// The file system the files are stored on.
    /// </param>
    /// <param name="unixPath">
    /// The host path of the file or directory.
    /// </param>
    /// <param name="owner">
    /// Receives the owner, DefaultOwner when none is stored.
    /// </param>
    /// <param name="homeOf">
    /// Receives the user number whose home directory this is, 0 when none is stored.
    /// </param>
    /// <remarks>
    /// Source: fs.c fs_read_xattr, lines 1675 to 1686: owner and homeof start at 0 and are replaced only by what the attribute store holds.
    /// </remarks>
    public static void ReadOwnership(IFileSystemBackend fileSystem, string unixPath, out ushort owner, out ushort homeOf)
    {
        ReadOwnershipLines(fileSystem, unixPath, out ushort? storedOwner, out ushort? storedHomeOf);
        owner = storedOwner ?? DefaultOwner;
        homeOf = storedHomeOf ?? 0;
    }

    /// <summary>
    /// Sets the owner and the home owner of a file or directory. The other lines of an existing sidecar file are kept. A sidecar file is made when there is none.
    /// </summary>
    /// <param name="fileSystem">
    /// The file system the files are stored on.
    /// </param>
    /// <param name="unixPath">
    /// The host path of the file or directory.
    /// </param>
    /// <param name="owner">
    /// The user number of the owner.
    /// </param>
    /// <param name="homeOf">
    /// The user number whose home directory this is, 0 for none.
    /// </param>
    public static void WriteOwnership(IFileSystemBackend fileSystem, string unixPath, ushort owner, ushort homeOf)
    {
        StringBuilder builder = new StringBuilder();
        string? existing = ReadSidecarText(fileSystem, unixPath);
        if (existing != null)
        {
            string[] lines = existing.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0)
                    continue;

                int eq = line.IndexOf('=');
                string key = eq > 0 ? line.Substring(0, eq).Trim() : string.Empty;
                if (string.Equals(key, OwnerKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, HomeOfKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                builder.Append(line);
                builder.AppendLine();
            }
        }

        AppendOwnership(builder, owner, homeOf);
        WriteSidecarText(fileSystem, unixPath, builder.ToString());
    }

    /// <summary>
    /// Moves the sidecar file of an object to the new name of the object, when it has one.
    /// </summary>
    /// <param name="fileSystem">
    /// The file system the files are stored on.
    /// </param>
    /// <param name="fromPath">
    /// The host path the object had.
    /// </param>
    /// <param name="toPath">
    /// The host path the object has now.
    /// </param>
    /// <remarks>
    /// Source: fs.c lines 6982 and 6983 (olddot and newdot), which rename the attribute file with the object.
    /// </remarks>
    public static void MoveSidecar(IFileSystemBackend fileSystem, string fromPath, string toPath)
    {
        string from = fromPath + SidecarSuffix;
        string to = toPath + SidecarSuffix;
        if (!fileSystem.FileExists(from))
            return;

        if (fileSystem.FileExists(to))
            fileSystem.DeleteFile(to);

        fileSystem.MoveFile(from, to);
    }

    /// <summary>
    /// Removes the sidecar file of an object, when it has one.
    /// </summary>
    /// <param name="fileSystem">
    /// The file system the files are stored on.
    /// </param>
    /// <param name="unixPath">
    /// The host path of the object that was deleted.
    /// </param>
    /// <remarks>
    /// Source: fs.c lines 7087 and 7088 (dotfile and unlink), which remove the attribute file with the object.
    /// </remarks>
    public static void DeleteSidecar(IFileSystemBackend fileSystem, string unixPath)
    {
        string infPath = unixPath + SidecarSuffix;
        if (fileSystem.FileExists(infPath))
            fileSystem.DeleteFile(infPath);
    }

    /// <summary>
    /// Tells whether a directory entry is the sidecar file of another entry of the same directory.
    /// </summary>
    /// <param name="fileSystem">
    /// The file system the files are stored on.
    /// </param>
    /// <param name="directory">
    /// The host path of the directory that holds the entry.
    /// </param>
    /// <param name="name">
    /// The name of the entry.
    /// </param>
    /// <returns>
    /// True when the name ends in the sidecar suffix and the object it describes is in the same directory.
    /// </returns>
    /// <remarks>
    /// PiFS keeps its attribute file out of sight because an Acorn name cannot hold the dot of its suffix (fs.c comment at lines 1591 to 1594). A comma is allowed in an Acorn name, so the suffix alone cannot decide: a lone entry that ends in the suffix is a real file.
    /// </remarks>
    public static bool IsSidecarName(IFileSystemBackend fileSystem, string directory, string name)
    {
        if (name.Length <= SidecarSuffix.Length || !name.EndsWith(SidecarSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        string described = fileSystem.CombinePath(directory, name.Substring(0, name.Length - SidecarSuffix.Length));
        return fileSystem.FileExists(described) || fileSystem.DirectoryExists(described);
    }

    private static void WriteSidecar(IFileSystemBackend fileSystem, string unixPath, uint load, uint exec, int length, byte access, ushort? owner, ushort? homeOf)
    {
        StringBuilder builder = new StringBuilder();
        builder.Append("LOAD=");
        builder.Append(load.ToString("X8", CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.Append("EXEC=");
        builder.Append(exec.ToString("X8", CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.Append("LENGTH=");
        builder.Append(length.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.Append("ACCESS=");
        builder.Append(access.ToString("X2", CultureInfo.InvariantCulture));
        builder.AppendLine();
        if (owner.HasValue || homeOf.HasValue)
            AppendOwnership(builder, owner ?? DefaultOwner, homeOf ?? 0);

        WriteSidecarText(fileSystem, unixPath, builder.ToString());
    }

    // fs.c writes owner and homeof in hex (fs_write_attr_to_file line 1644, and the "%04X" attribute text at lines 1779 and 1797).
    private static void AppendOwnership(StringBuilder builder, ushort owner, ushort homeOf)
    {
        builder.Append(OwnerKey);
        builder.Append('=');
        builder.Append(owner.ToString("X4", CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.Append(HomeOfKey);
        builder.Append('=');
        builder.Append(homeOf.ToString("X4", CultureInfo.InvariantCulture));
        builder.AppendLine();
    }

    private static void WriteSidecarText(IFileSystemBackend fileSystem, string unixPath, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        using FileStream stream = fileSystem.OpenFile(unixPath + SidecarSuffix, FileMode.Create, FileAccess.Write);
        stream.Write(bytes, 0, bytes.Length);
    }

    // Returns the text of the sidecar file, or null when the object has none.
    private static string? ReadSidecarText(IFileSystemBackend fileSystem, string unixPath)
    {
        string infPath = unixPath + SidecarSuffix;
        if (!fileSystem.FileExists(infPath))
            return null;

        byte[] buffer;
        using (FileStream stream = fileSystem.OpenFile(infPath, FileMode.Open, FileAccess.Read))
        {
            buffer = new byte[stream.Length];
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read < buffer.Length)
                Array.Resize(ref buffer, read);
        }

        return Encoding.ASCII.GetString(buffer);
    }

    // Reads the OWNER and HOMEOF lines. A value is null when the sidecar has no such line, or no sidecar exists.
    private static void ReadOwnershipLines(IFileSystemBackend fileSystem, string unixPath, out ushort? owner, out ushort? homeOf)
    {
        owner = null;
        homeOf = null;
        string? text = ReadSidecarText(fileSystem, unixPath);
        if (text == null)
            return;

        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            string key = line.Substring(0, eq).Trim();
            string value = line.Substring(eq + 1).Trim();
            if (string.Equals(key, OwnerKey, StringComparison.OrdinalIgnoreCase) && TryParseAcornHex(value, out uint parsedOwner) && parsedOwner <= ushort.MaxValue)
                owner = (ushort)parsedOwner;
            else if (string.Equals(key, HomeOfKey, StringComparison.OrdinalIgnoreCase) && TryParseAcornHex(value, out uint parsedHome) && parsedHome <= ushort.MaxValue)
                homeOf = (ushort)parsedHome;
        }
    }

    private static bool TryParseAcornHex(string value, out uint result)
    {
        result = 0;
        if (value.StartsWith('&'))
            value = value.Substring(1);

        return uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
    }
}
