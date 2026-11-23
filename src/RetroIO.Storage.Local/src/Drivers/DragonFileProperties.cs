//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.FileSystems;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// The file properties of the three file systems a Dragon or CoCo disk image can hold, shared by
    /// the DSK, VDK and DMK drivers.
    /// </summary>
    /// <remarks>
    /// What each directory entry holds beyond its name, size and dates:
    ///  - DragonDOS: the write-protect flag (bit 1 of the entry's flag byte).
    ///  - DECB: the file type (entry byte 11) and the ASCII flag (entry byte 12).
    ///  - OS-9 RBF: the attribute byte and the owner of the file descriptor.
    /// A machine-code file's load and start addresses are in the file's own first bytes on all
    /// three, so they travel with the data and are not properties.
    /// </remarks>
    public static class DragonFileProperties
    {
        /// <summary>
        /// The DragonDOS write-protect flag.
        /// </summary>
        public const string DragonDosWriteProtectedKey = "dragondos.write_protected";

        /// <summary>
        /// The DECB file type: 0 BASIC program, 1 BASIC data, 2 machine language, 3 text.
        /// </summary>
        public const string DecbFileTypeKey = "decb.file_type";

        /// <summary>
        /// The DECB ASCII flag: <c>true</c> for an ASCII file (FFh), <c>false</c> for binary (00h).
        /// </summary>
        public const string DecbAsciiKey = "decb.ascii";

        /// <summary>
        /// The OS-9 attribute byte, bit 7 (directory) left out.
        /// </summary>
        public const string Os9AttributesKey = "os9.attributes";

        /// <summary>
        /// The OS-9 owner's user number.
        /// </summary>
        public const string Os9OwnerKey = "os9.owner";

        /// <summary>
        /// Gets the property keys of a mounted file system.
        /// </summary>
        /// <param name="fileSystem">
        /// The mounted file system, or <c>null</c>.
        /// </param>
        /// <returns>
        /// The capabilities; none before a mount.
        /// </returns>
        public static FileSystemCapabilities Capabilities(IBlockFileSystem? fileSystem)
        {
            string[] keys = fileSystem switch
            {
                Os9RbfFileSystem => new[] { Os9AttributesKey, Os9OwnerKey },
                DecbFileSystem => new[] { DecbFileTypeKey, DecbAsciiKey },
                DragonDosFileSystem => new[] { DragonDosWriteProtectedKey },
                _ => Array.Empty<string>(),
            };
            return new FileSystemCapabilities
            {
                SupportsExtendedAttributes = keys.Length > 0,
                SupportedPropertyKeys = keys,
                WritablePropertyKeys = (string[])keys.Clone(),
            };
        }

        /// <summary>
        /// Gets the properties of one file.
        /// </summary>
        /// <param name="fileSystem">
        /// The mounted file system.
        /// </param>
        /// <param name="path">
        /// The file's path.
        /// </param>
        /// <returns>
        /// The properties, or <c>null</c> when there is no such file.
        /// </returns>
        public static IDictionary<string, object>? Get(IBlockFileSystem fileSystem, string path)
        {
            if (fileSystem.GetMetadata(path) == null)
            {
                return null;
            }

            switch (fileSystem)
            {
                case Os9RbfFileSystem os9:
                    os9.GetDescriptorFields(path, out byte attributes, out ushort owner);
                    return new Dictionary<string, object>
                    {
                        { Os9AttributesKey, attributes & 0x7F },
                        { Os9OwnerKey, (int)owner },
                    };
                case DecbFileSystem decb:
                    decb.GetFileType(path, out byte fileType, out byte ascii);
                    return new Dictionary<string, object>
                    {
                        { DecbFileTypeKey, (int)fileType },
                        { DecbAsciiKey, ascii == 0xFF },
                    };
                case DragonDosFileSystem dragonDos:
                    return new Dictionary<string, object>
                    {
                        { DragonDosWriteProtectedKey, dragonDos.IsWriteProtected(path) },
                    };
                default:
                    return new Dictionary<string, object>();
            }
        }

        /// <summary>
        /// Writes a file and applies its properties.
        /// </summary>
        /// <param name="fileSystem">
        /// The mounted file system.
        /// </param>
        /// <param name="path">
        /// The file's path.
        /// </param>
        /// <param name="data">
        /// The file's bytes.
        /// </param>
        /// <param name="propertyBag">
        /// The properties; a missing one keeps what a plain write gives.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when a value is out of range.
        /// </exception>
        public static void Write(IBlockFileSystem fileSystem, string path, byte[] data, IDictionary<string, object> propertyBag)
        {
            switch (fileSystem)
            {
                case DecbFileSystem decb:
                    if (ExtendedAttributeValues.TryGetLong(propertyBag, DecbFileTypeKey, out long fileType))
                    {
                        if (fileType < 0 || fileType > 3)
                        {
                            throw new ArgumentException(DecbFileTypeKey + " is 0 to 3, not " + fileType, nameof(propertyBag));
                        }

                        ExtendedAttributeValues.TryGetBool(propertyBag, DecbAsciiKey, out bool ascii);
                        decb.WriteFile(path, data, (byte)fileType, ascii ? (byte)0xFF : (byte)0x00);
                        return;
                    }

                    decb.WriteFile(path, data);
                    return;
                case DragonDosFileSystem dragonDos:
                    dragonDos.WriteFile(path, data);
                    if (ExtendedAttributeValues.TryGetBool(propertyBag, DragonDosWriteProtectedKey, out bool protect) && protect)
                    {
                        dragonDos.SetWriteProtected(path, true);
                    }

                    return;
                case Os9RbfFileSystem os9:
                    os9.WriteFile(path, data);
                    os9.GetDescriptorFields(path, out byte attributes, out ushort owner);
                    long newAttributes = attributes;
                    long newOwner = owner;
                    bool hasAttributes = ExtendedAttributeValues.TryGetLong(propertyBag, Os9AttributesKey, out long a);
                    bool hasOwner = ExtendedAttributeValues.TryGetLong(propertyBag, Os9OwnerKey, out long o);
                    if (hasAttributes)
                    {
                        if (a < 0 || a > 0x7F)
                        {
                            throw new ArgumentException(Os9AttributesKey + " is 0 to 127, not " + a, nameof(propertyBag));
                        }

                        newAttributes = a;
                    }

                    if (hasOwner)
                    {
                        if (o < 0 || o > 0xFFFF)
                        {
                            throw new ArgumentException(Os9OwnerKey + " is 0 to 65535, not " + o, nameof(propertyBag));
                        }

                        newOwner = o;
                    }

                    if (hasAttributes || hasOwner)
                    {
                        os9.SetDescriptorFields(path, (byte)newAttributes, (ushort)newOwner);
                    }

                    return;
                default:
                    throw new IOException("This disk's file system cannot be written.");
            }
        }
    }
}
