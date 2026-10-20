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
/// </remarks>
public static class XatSidecar
{
    /// <summary>
    /// The suffix of a .xat file.
    /// </summary>
    public const string Extension = ".xat";

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
    /// The property bag, or null when the object has no .xat file.
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the .xat file exists but does not hold a property bag.
    /// </exception>
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
        if (json.Length == 0)
            throw new InvalidDataException("The attribute file is empty: " + xatPath);

        try
        {
            return NdfsXatSerializer.DeserializeFromJson(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            // Not swallowed: the caller must see that the store is damaged, not get an empty bag.
            throw new InvalidDataException("The attribute file is not valid: " + xatPath, ex);
        }
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
