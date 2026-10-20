//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using RetroIO.Storage.Ndfs.PropertyBags;

namespace RetroIO.Server.Core.FileSystem;

/// <summary>
/// The .xat file: the declared fallback that keeps the property bag of an object when the host cannot store attributes on the object itself.
/// </summary>
/// <remarks>
/// <para>
/// This is the same mechanism NDFS uses. The file holds the JSON that <see cref="NdfsXatSerializer"/> writes and reads, and its name is made with
/// <see cref="NdfsXatSerializer.GetXatFileName(string, string)"/>. No new format is defined here.
/// </para>
/// <para>
/// The .xat file lies in the same directory as the object it describes. For the object <c>HELLO</c> it is <c>HELLO.xat</c>.
/// </para>
/// <para>
/// Why a listing may hide it: PiFS hides its own side file because an Acorn name cannot hold a dot, since the dot is the directory separator in the Acorn world
/// (PiEconetBridge utilities/fs.c, comment above pathname_to_dotfile, lines 1589 to 1595). The suffix of a .xat file starts with a dot, so no Acorn name can be
/// the name of a .xat file. <see cref="IsXatName"/> is the test for that.
/// </para>
/// <para>
/// A .xat file that is empty or does not hold a property bag is damaged. <see cref="Read"/> then returns null, as if the object had no bag, and moves the
/// damaged file to its name followed by <c>.bad</c> (FOO.xat becomes FOO.xat.bad), so the next write makes a fresh .xat file. The .bad file is kept for
/// a person to look at. <see cref="IsDamagedXatName"/> is the test for that name.
/// </para>
/// </remarks>
public static class XatSidecar
{
    /// <summary>
    /// The suffix of a .xat file.
    /// </summary>
    public const string Extension = ".xat";

    /// <summary>
    /// The suffix that is added to the name of a damaged .xat file when it is moved aside.
    /// </summary>
    public const string DamagedSuffix = ".bad";

    /// <summary>
    /// Tells whether a directory entry name is a .xat file.
    /// </summary>
    /// <param name="name">
    /// The name of the directory entry.
    /// </param>
    /// <returns>
    /// True when the name ends in <c>.xat</c>.
    /// </returns>
    public static bool IsXatName(string name)
    {
        return NdfsXatSerializer.IsXatFile(name, Extension);
    }

    /// <summary>
    /// Tells whether a directory entry name is a damaged .xat file that was moved aside by <see cref="Read"/>.
    /// </summary>
    /// <param name="name">
    /// The name of the directory entry.
    /// </param>
    /// <returns>
    /// True when the name ends in <c>.xat.bad</c>.
    /// </returns>
    public static bool IsDamagedXatName(string name)
    {
        return name.EndsWith(Extension + DamagedSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the path of the .xat file of an object.
    /// </summary>
    /// <param name="objectPath">
    /// The path of the file or directory.
    /// </param>
    /// <returns>
    /// The path of the .xat file, in the same directory as the object.
    /// </returns>
    /// <remarks>
    /// The name part goes through <see cref="NdfsXatSerializer.GetXatFileName(string, string)"/> when it holds no dot, which is every Acorn name.
    /// That method cuts at the last dot, so a name with a dot would share a .xat file with the name before the dot. For such a name the suffix is
    /// added to the whole name instead, which keeps the two apart.
    /// </remarks>
    public static string GetXatPath(string objectPath)
    {
        string trimmed = objectPath.TrimEnd('/', '\\');
        int cut = trimmed.LastIndexOfAny(['/', '\\']);
        string directoryPart = cut < 0 ? string.Empty : trimmed.Substring(0, cut + 1);
        string name = trimmed.Substring(cut + 1);
        string xatName = name.IndexOf('.') < 0
            ? NdfsXatSerializer.GetXatFileName(name, Extension)
            : name + Extension;
        return directoryPart + xatName;
    }

    /// <summary>
    /// Reads the property bag of an object from its .xat file.
    /// </summary>
    /// <param name="fileSystem">
    /// The backend the object is stored on.
    /// </param>
    /// <param name="objectPath">
    /// The path of the file or directory.
    /// </param>
    /// <returns>
    /// The property bag, or null when the object has no .xat file or its .xat file is damaged.
    /// </returns>
    /// <remarks>
    /// A .xat file that is empty or is not valid JSON for a property bag is moved to its name followed by <c>.bad</c>. An older .bad file of the same name is
    /// replaced. Only the damage is handled here: a failure to read the file or to move it is not caught and reaches the caller.
    /// </remarks>
    public static IDictionary<string, object>? Read(IFileSystemBackend fileSystem, string objectPath)
    {
        string xatPath = GetXatPath(objectPath);
        if (!fileSystem.FileExists(xatPath))
            return null;

        byte[] buffer;
        using (FileStream stream = fileSystem.OpenFile(xatPath, FileMode.Open, FileAccess.Read))
        {
            buffer = new byte[stream.Length];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read <= 0)
                    break;

                total += read;
            }

            if (total < buffer.Length)
                Array.Resize(ref buffer, total);
        }

        string json = Encoding.UTF8.GetString(buffer);
        if (json.Length > 0)
        {
            try
            {
                return NdfsXatSerializer.DeserializeFromJson(json);
            }
            catch (System.Text.Json.JsonException)
            {
                // Damaged: handled below, the same way as an empty file.
            }
        }

        // The file is empty or does not hold a bag. It reads as no bag, and the damaged file is kept aside under a .bad name so that
        // it can be looked at and so that the next write makes a fresh .xat file.
        string badPath = xatPath + DamagedSuffix;
        if (fileSystem.FileExists(badPath))
            fileSystem.DeleteFile(badPath);

        fileSystem.MoveFile(xatPath, badPath);
        return null;
    }

    /// <summary>
    /// Writes the property bag of an object to its .xat file. An empty bag removes the file.
    /// </summary>
    /// <param name="fileSystem">
    /// The backend the object is stored on.
    /// </param>
    /// <param name="objectPath">
    /// The path of the file or directory.
    /// </param>
    /// <param name="propertyBag">
    /// The properties to store. They replace the properties that were stored.
    /// </param>
    public static void Write(IFileSystemBackend fileSystem, string objectPath, IDictionary<string, object> propertyBag)
    {
        if (propertyBag.Count == 0)
        {
            Delete(fileSystem, objectPath);
            return;
        }

        string json = NdfsXatSerializer.SerializeToJson(propertyBag);
        byte[] bytes = new UTF8Encoding(false).GetBytes(json);
        using FileStream stream = fileSystem.OpenFile(GetXatPath(objectPath), FileMode.Create, FileAccess.Write);
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Moves the .xat file of an object to the new name of the object, when it has one.
    /// </summary>
    /// <param name="fileSystem">
    /// The backend the object is stored on.
    /// </param>
    /// <param name="fromPath">
    /// The path the object had.
    /// </param>
    /// <param name="toPath">
    /// The path the object has now.
    /// </param>
    public static void Move(IFileSystemBackend fileSystem, string fromPath, string toPath)
    {
        string from = GetXatPath(fromPath);
        string to = GetXatPath(toPath);
        if (!fileSystem.FileExists(from))
            return;

        if (fileSystem.FileExists(to))
            fileSystem.DeleteFile(to);

        fileSystem.MoveFile(from, to);
    }

    /// <summary>
    /// Removes the .xat file of an object, when it has one.
    /// </summary>
    /// <param name="fileSystem">
    /// The backend the object is stored on.
    /// </param>
    /// <param name="objectPath">
    /// The path of the object that was deleted.
    /// </param>
    public static void Delete(IFileSystemBackend fileSystem, string objectPath)
    {
        string xatPath = GetXatPath(objectPath);
        if (fileSystem.FileExists(xatPath))
            fileSystem.DeleteFile(xatPath);
    }
}
