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
/// Reads and writes the Acorn attributes of a file or directory: load address, execution address, length, access byte, owner and home owner.
/// </summary>
/// <remarks>
/// <para>
/// The attributes are kept in the property bag of the object (see <see cref="AcornProperties"/>), through the shared extended-attribute mechanism of
/// IFileSystemBackend, the one NDFS uses. Where the backend cannot store attributes on the object, the bag lives in the .xat file of the object.
/// This follows PiFS (PiEconetBridge utilities/fs.c, fs_read_xattr and the functions above it, lines 1589 to 1690): the attributes go in an extended
/// attribute, and a side file is used only where extended attributes are not available.
/// </para>
/// <para>
/// Defaults follow fs_read_xattr (lines 1675 to 1686): owner, load, exec and home owner start at 0, and only what the store holds replaces them.
/// The access byte defaults to 0x33 here, as it always has in this server.
/// </para>
/// <para>
/// Volumes made by older versions keep the attributes in a file with the object name followed by a comma and inf, one KEY=value pair per line,
/// with the keys LOAD, EXEC, LENGTH, ACCESS, OWNER and HOMEOF. When an object has no property bag but has such a file, the values are read from it, written
/// to the property bag, and the old file is removed. See <see cref="TryConvertLegacy"/>.
/// </para>
/// </remarks>
public static class AcornAttributeStore
{
    /// <summary>
    /// The owner of an object that has no stored owner. The value is 0, which fs.c calls syst (fs_read_xattr, line 1678).
    /// </summary>
    public const ushort DefaultOwner = 0;

    /// <summary>
    /// The access byte of an object that has no stored access byte.
    /// </summary>
    public const byte DefaultAccess = 0x33;

    /// <summary>
    /// The suffix of the helper file that older versions kept the attributes in. Only the conversion reads it.
    /// </summary>
    public const string LegacySuffix = ",inf";

    /// <summary>
    /// Reads the load address, execution address, length and access byte of an object.
    /// </summary>
    /// <param name="fileSystem">
    /// The file system the files are stored on.
    /// </param>
    /// <param name="unixPath">
    /// The host path of the object.
    /// </param>
    /// <param name="load">
    /// Receives the load address, 0 when not stored.
    /// </param>
    /// <param name="exec">
    /// Receives the execution address, 0 when not stored.
    /// </param>
    /// <param name="length">
    /// Receives the stored length, or the real file size when none is stored.
    /// </param>
    /// <param name="access">
    /// Receives the access byte, 0x33 when not stored.
    /// </param>
    /// <returns>
    /// True when the object has attributes or the file itself exists, otherwise false.
    /// </returns>
    public static bool TryReadAttributes(IFileSystemBackend fileSystem, string unixPath, out uint load, out uint exec, out int length, out byte access)
    {
        load = 0;
        exec = 0;
        length = 0;
        access = DefaultAccess;

        if (fileSystem.FileExists(unixPath))
        {
            BackendFileInfo? info = fileSystem.GetFileInfo(unixPath);
            length = (int)(info?.Length ?? 0);
        }

        IDictionary<string, object>? bag = LoadBag(fileSystem, unixPath);
        if (bag == null)
            return fileSystem.FileExists(unixPath);

        if (TryGetNumber(bag, AcornProperties.Load, out long storedLoad))
            load = (uint)storedLoad;
        if (TryGetNumber(bag, AcornProperties.Exec, out long storedExec))
            exec = (uint)storedExec;
        if (TryGetNumber(bag, AcornProperties.Length, out long storedLength))
            length = (int)storedLength;
        if (TryGetNumber(bag, AcornProperties.Access, out long storedAccess))
            access = (byte)(storedAccess & 0xFF);

        return true;
    }

    /// <summary>
    /// Writes the load address, execution address, length and access byte of an object. The owner and the home owner are kept.
    /// </summary>
    /// <param name="fileSystem">
    /// The file system the files are stored on.
    /// </param>
    /// <param name="unixPath">
    /// The host path of the object.
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
        IDictionary<string, object> bag = LoadBag(fileSystem, unixPath) ?? new Dictionary<string, object>();
        SetAttributeKeys(bag, load, exec, length, access);
        fileSystem.SetObjectProperties(unixPath, bag);
    }

    /// <summary>
    /// Writes the attributes of an object with a new owner. The home owner is kept.
    /// </summary>
    /// <param name="fileSystem">
    /// The file system the files are stored on.
    /// </param>
    /// <param name="unixPath">
    /// The host path of the object.
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
        IDictionary<string, object> bag = LoadBag(fileSystem, unixPath) ?? new Dictionary<string, object>();
        SetAttributeKeys(bag, load, exec, length, access);
        bag[AcornProperties.Owner] = (int)owner;
        fileSystem.SetObjectProperties(unixPath, bag);
    }

    /// <summary>
    /// Reads the owner and the home owner of a file or directory.
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
        owner = DefaultOwner;
        homeOf = 0;

        IDictionary<string, object>? bag = LoadBag(fileSystem, unixPath);
        if (bag == null)
            return;

        if (TryGetNumber(bag, AcornProperties.Owner, out long storedOwner) && storedOwner >= 0 && storedOwner <= ushort.MaxValue)
            owner = (ushort)storedOwner;
        if (TryGetNumber(bag, AcornProperties.HomeOf, out long storedHomeOf) && storedHomeOf >= 0 && storedHomeOf <= ushort.MaxValue)
            homeOf = (ushort)storedHomeOf;
    }

    /// <summary>
    /// Sets the owner and the home owner of a file or directory. The other properties of an existing bag are kept. A bag is made when there is none.
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
        IDictionary<string, object> bag = LoadBag(fileSystem, unixPath) ?? new Dictionary<string, object>();
        bag[AcornProperties.Owner] = (int)owner;
        bag[AcornProperties.HomeOf] = (int)homeOf;
        fileSystem.SetObjectProperties(unixPath, bag);
    }

    /// <summary>
    /// Moves the attributes of an object to the new name of the object.
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
    /// An older helper file of the object is converted first, so that its values move too.
    /// </remarks>
    public static void MoveAttributes(IFileSystemBackend fileSystem, string fromPath, string toPath)
    {
        LoadBag(fileSystem, fromPath);
        fileSystem.MoveObjectProperties(fromPath, toPath);
    }

    /// <summary>
    /// Removes the attributes of an object that was deleted.
    /// </summary>
    /// <param name="fileSystem">
    /// The file system the files are stored on.
    /// </param>
    /// <param name="unixPath">
    /// The host path of the object that was deleted.
    /// </param>
    /// <remarks>
    /// Source: fs.c lines 7087 and 7088 (dotfile and unlink), which remove the attribute file with the object.
    /// An older helper file of the object is removed as well, so a new object of the same name does not inherit it.
    /// </remarks>
    public static void DeleteAttributes(IFileSystemBackend fileSystem, string unixPath)
    {
        fileSystem.DeleteObjectProperties(unixPath);

        string legacyPath = unixPath + LegacySuffix;
        if (fileSystem.FileExists(legacyPath))
            fileSystem.DeleteFile(legacyPath);
    }

    /// <summary>
    /// Tells whether a directory entry is the file that keeps the property bag of another entry, and so is not an entry of the Acorn tree.
    /// </summary>
    /// <param name="name">
    /// The name of the directory entry.
    /// </param>
    /// <returns>
    /// True when the name ends in <c>.xat</c>.
    /// </returns>
    /// <remarks>
    /// PiFS keeps its own side file out of sight because an Acorn name cannot hold a dot (fs.c comment at lines 1589 to 1595: the dot is a directory
    /// separator in the Acorn world). The suffix <c>.xat</c> has a dot, so no Acorn name can be the name of a .xat file, and hiding it cannot hide an Acorn object.
    /// </remarks>
    public static bool IsStoreName(string name)
    {
        return XatSidecar.IsXatName(name);
    }

    /// <summary>
    /// Tells whether a directory entry name has the suffix of the helper file that older versions used.
    /// </summary>
    /// <param name="name">
    /// The name of the directory entry.
    /// </param>
    /// <returns>
    /// True when the name is longer than the suffix and ends in it.
    /// </returns>
    public static bool IsLegacyName(string name)
    {
        return name.Length > LegacySuffix.Length && name.EndsWith(LegacySuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Converts the helper file of an object from an older version into a property bag.
    /// </summary>
    /// <param name="fileSystem">
    /// The file system the files are stored on.
    /// </param>
    /// <param name="unixPath">
    /// The host path of the object the helper file describes.
    /// </param>
    /// <returns>
    /// The property bag that was written, or null when there is nothing to convert: the object has a property bag already, it has no helper file, or the
    /// helper file holds none of the keys LOAD, EXEC, LENGTH, ACCESS, OWNER and HOMEOF (then it is some other file and is left alone).
    /// </returns>
    /// <remarks>
    /// The bag is written before the helper file is removed, so a failure cannot lose the values.
    /// </remarks>
    public static IDictionary<string, object>? TryConvertLegacy(IFileSystemBackend fileSystem, string unixPath)
    {
        if (fileSystem.GetObjectProperties(unixPath) != null)
            return null;

        string legacyPath = unixPath + LegacySuffix;
        if (!fileSystem.FileExists(legacyPath))
            return null;

        IDictionary<string, object> bag = ParseLegacy(ReadText(fileSystem, legacyPath));
        if (bag.Count == 0)
            return null;

        fileSystem.SetObjectProperties(unixPath, bag);
        fileSystem.DeleteFile(legacyPath);
        return bag;
    }

    // Returns the bag of the object. An object that has none but has an older helper file is converted here, so every read and write goes through it.
    private static IDictionary<string, object>? LoadBag(IFileSystemBackend fileSystem, string unixPath)
    {
        IDictionary<string, object>? bag = fileSystem.GetObjectProperties(unixPath);
        if (bag != null)
            return bag;

        return TryConvertLegacy(fileSystem, unixPath);
    }

    private static void SetAttributeKeys(IDictionary<string, object> bag, uint load, uint exec, int length, byte access)
    {
        bag[AcornProperties.Load] = load;
        bag[AcornProperties.Exec] = exec;
        bag[AcornProperties.Length] = length;
        bag[AcornProperties.Access] = (int)access;
    }

    // The serializer returns a JSON number as int, long or double, so the value is read back as a long whatever the type.
    private static bool TryGetNumber(IDictionary<string, object> bag, string key, out long value)
    {
        value = 0;
        if (!bag.TryGetValue(key, out object? raw) || raw == null)
            return false;

        switch (raw)
        {
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            case uint u:
                value = u;
                return true;
            case ulong ul when ul <= long.MaxValue:
                value = (long)ul;
                return true;
            case double d when d >= long.MinValue && d <= long.MaxValue:
                value = (long)d;
                return true;
            case string s:
                return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            default:
                return false;
        }
    }

    // Reads the KEY=value lines of an older helper file. LOAD, EXEC, ACCESS, OWNER and HOMEOF are hex, with an optional leading ampersand; LENGTH is decimal.
    // A line that is not understood is skipped, as it always was.
    private static IDictionary<string, object> ParseLegacy(string text)
    {
        Dictionary<string, object> bag = new Dictionary<string, object>();
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            string key = line.Substring(0, eq).Trim().ToUpperInvariant();
            string value = line.Substring(eq + 1).Trim();

            if (key == "LOAD" && TryParseHex(value, out uint load))
                bag[AcornProperties.Load] = load;
            else if (key == "EXEC" && TryParseHex(value, out uint exec))
                bag[AcornProperties.Exec] = exec;
            else if (key == "LENGTH" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int length))
                bag[AcornProperties.Length] = length;
            else if (key == "ACCESS" && TryParseHex(value, out uint access))
                bag[AcornProperties.Access] = (int)(access & 0xFF);
            else if (key == "OWNER" && TryParseHex(value, out uint owner) && owner <= ushort.MaxValue)
                bag[AcornProperties.Owner] = (int)owner;
            else if (key == "HOMEOF" && TryParseHex(value, out uint homeOf) && homeOf <= ushort.MaxValue)
                bag[AcornProperties.HomeOf] = (int)homeOf;
        }

        return bag;
    }

    private static bool TryParseHex(string value, out uint result)
    {
        if (value.StartsWith('&'))
            value = value.Substring(1);

        return uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
    }

    private static string ReadText(IFileSystemBackend fileSystem, string path)
    {
        byte[] buffer;
        using (FileStream stream = fileSystem.OpenFile(path, FileMode.Open, FileAccess.Read))
        {
            buffer = new byte[stream.Length];
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read < buffer.Length)
                Array.Resize(ref buffer, read);
        }

        return Encoding.ASCII.GetString(buffer);
    }
}
