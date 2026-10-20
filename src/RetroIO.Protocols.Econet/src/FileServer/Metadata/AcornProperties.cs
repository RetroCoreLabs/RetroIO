//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Storage.Core;

namespace RetroIO.Protocols.Econet.FileServer.Metadata;

/// <summary>
/// The keys and descriptors of the Acorn properties that the Econet file server keeps for each object.
/// </summary>
/// <remarks>
/// <para>
/// The properties are kept in the property bag of the object, through the shared extended-attribute mechanism that NDFS also uses
/// (see IFileSystemBackend.GetObjectProperties and XatSidecar). The keys follow the NDFS pattern of a prefix, a dot and a lower case name.
/// </para>
/// <para>
/// Source for the values: PiEconetBridge utilities/fs.c, struct objattr (line 318), which holds owner, load, exec, perm and homeof. The length is not in
/// that structure, because PiFS takes the length from the file. It is kept here as well, because this server has always stored a length next to the other
/// attributes and a client can set it with the set-object-info call.
/// </para>
/// </remarks>
public static class AcornProperties
{
    private const string Prefix = "acorn.";

    /// <summary>
    /// The load address of the object (an unsigned 32-bit number).
    /// </summary>
    public const string Load = Prefix + "load";

    /// <summary>
    /// The execution address of the object (an unsigned 32-bit number).
    /// </summary>
    public const string Exec = Prefix + "exec";

    /// <summary>
    /// The length the attributes record for the object, in bytes.
    /// </summary>
    public const string Length = Prefix + "length";

    /// <summary>
    /// The access byte of the object (fs.c calls it perm).
    /// </summary>
    public const string Access = Prefix + "access";

    /// <summary>
    /// The user number of the owner of the object.
    /// </summary>
    public const string Owner = Prefix + "owner";

    /// <summary>
    /// The user number whose home directory the object is, 0 for none (fs.c calls it homeof).
    /// </summary>
    public const string HomeOf = Prefix + "homeof";

    /// <summary>
    /// The descriptors of all Acorn properties.
    /// </summary>
    public static readonly PropertyDescriptor[] All =
    [
        new PropertyDescriptor
        {
            Key = Load,
            ShortName = "LOAD",
            ValueType = typeof(uint),
            Description = "Acorn load address",
            Category = PropertyCategory.Attributes,
            DefaultValue = 0u,
            ValidationHint = "0 to 4294967295"
        },
        new PropertyDescriptor
        {
            Key = Exec,
            ShortName = "EXEC",
            ValueType = typeof(uint),
            Description = "Acorn execution address",
            Category = PropertyCategory.Attributes,
            DefaultValue = 0u,
            ValidationHint = "0 to 4294967295"
        },
        new PropertyDescriptor
        {
            Key = Length,
            ShortName = "LENGTH",
            ValueType = typeof(int),
            Description = "Acorn length recorded with the attributes",
            Category = PropertyCategory.Attributes,
            ValidationHint = "0 or more bytes"
        },
        new PropertyDescriptor
        {
            Key = Access,
            ShortName = "ACCESS",
            ValueType = typeof(int),
            Description = "Acorn access byte",
            Category = PropertyCategory.Access,
            DefaultValue = 0x33,
            ValidationHint = "0 to 255"
        },
        new PropertyDescriptor
        {
            Key = Owner,
            ShortName = "OWNER",
            ValueType = typeof(int),
            Description = "User number of the owner",
            Category = PropertyCategory.Ownership,
            DefaultValue = 0,
            ValidationHint = "0 to 65535"
        },
        new PropertyDescriptor
        {
            Key = HomeOf,
            ShortName = "HOMEOF",
            ValueType = typeof(int),
            Description = "User number whose home directory this is, 0 for none",
            Category = PropertyCategory.Ownership,
            DefaultValue = 0,
            ValidationHint = "0 to 65535"
        }
    ];

    /// <summary>
    /// Gets the property keys, for a FileSystemCapabilities value.
    /// </summary>
    /// <returns>
    /// The key of each descriptor in <see cref="All"/>.
    /// </returns>
    public static string[] GetKeys()
    {
        string[] keys = new string[All.Length];
        for (int i = 0; i < All.Length; i++)
            keys[i] = All[i].Key;

        return keys;
    }
}
