//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Storage.Ndfs.PropertyBags
{
    /// <summary>
    /// Property bag keys for NDFS-specific metadata.
    /// These keys are used to preserve NDFS metadata when copying files across filesystems.
    /// </summary>
    public static class NdfsPropertyBagKeys
    {
        // Base prefix for all NDFS properties
        private const string Prefix = "ndfs.";

        // ObjectEntry properties

        /// <summary>
        /// The object entry's 16-bit header word, taken whole from bytes 0-1 of the on-disk entry.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Bit 15 marks the entry as used and bit 12 marks the file as modified. A real entry
        /// recorded off an ND-100 carries <c>0x90</c> in byte 0, which is both of those bits, so the
        /// whole word is kept rather than the single used flag.
        /// </para>
        /// <para>
        /// The other bits the entry class models are bit 14 (currently opened for write), bit 13
        /// (reserved), bit 8 (the user/object entry discriminator, clear for an object entry) and
        /// bits 10-0 (the terminal number of the last user that opened the file, zero for an RT
        /// program). Bits 10-0 overlap bit 8 because they share the same word.
        /// </para>
        /// </remarks>
        public const string Header = Prefix + "header";

        /// <summary>
        /// The slot this file occupies in the object table.
        /// </summary>
        /// <remarks>
        /// Recorded on export. It is not restored on a fresh write, because the slot is assigned by
        /// the create path in the destination directory and is not a portable property of the file.
        /// </remarks>
        public const string ObjectIndex = Prefix + "object_index";

        /// <summary>
        /// The file name, up to 16 characters, held at bytes 2-17 of the entry.
        /// </summary>
        /// <remarks>
        /// On disk the name is terminated by an apostrophe (<c>0x27</c>) when it is shorter than 16
        /// characters. The property carries the decoded name without that terminator.
        /// </remarks>
        public const string ObjectName = Prefix + "object_name";

        /// <summary>
        /// The file's type text, up to 4 characters, held at bytes 18-21 of the entry.
        /// </summary>
        /// <remarks>
        /// This is the text after the colon in a SINTRAN file name, such as <c>DATA</c> or
        /// <c>SYMB</c>. An empty type is preserved as empty and is never defaulted. It is distinct
        /// from <see cref="FileType"/>, which is a numeric code.
        /// </remarks>
        public const string Type = Prefix + "type";

        /// <summary>
        /// The name of the user that owns the file.
        /// </summary>
        /// <remarks>
        /// Also written by the user-entry path, where it is the user's own name from bytes 2-17 of
        /// the user entry. It is not restored onto a fresh write, because NDFS ties ownership to the
        /// object-table slot the file physically occupies.
        /// </remarks>
        public const string UserName = Prefix + "user_name";

        /// <summary>
        /// The owning user's index, byte 34 of the object entry.
        /// </summary>
        /// <remarks>
        /// The user-entry path writes this key too, taking the user's own index from byte 37 of the
        /// user entry. Like <see cref="UserName"/> it is recorded but not reapplied to a new write.
        /// </remarks>
        public const string UserIndex = Prefix + "user_index";

        /// <summary>
        /// Link to the next version of this file, at bytes 22-23 of the entry.
        /// </summary>
        /// <remarks>
        /// The value references an object table slot. It is recorded but deliberately never restored
        /// on import, because slots are reassigned and the old number would point at an unrelated
        /// file. See <see cref="ObjectEntryPrevVersion"/> for the other end of the chain.
        /// </remarks>
        public const string ObjectEntryNextVersion = Prefix + "next_version";

        /// <summary>
        /// Link to the previous version of this file, at bytes 24-25 of the entry.
        /// </summary>
        /// <remarks>
        /// Like <see cref="ObjectEntryNextVersion"/> this references an object table slot, and is
        /// recorded but never restored on import.
        /// </remarks>
        public const string ObjectEntryPrevVersion = Prefix + "prev_version";

        /// <summary>
        /// The raw access permission word at bytes 26-27 of the entry.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The word holds three tiers. Public access is bits 14-9 and friend access is bits 8-3;
        /// both are read with a six-bit mask and neither is in doubt.
        /// </para>
        /// <para>
        /// The width of the owner's own tier is UNKNOWN. <c>AccessPermissions.ParseBits</c> extracts
        /// it as <c>AccessBits AND 0x1F</c>, which is five bits and therefore overlaps bits 3 and 4
        /// of the friend tier, while the comment beside that line says bits 2-0 and the constructor
        /// documents it as five bits. Three of the file's own statements disagree, so the safe
        /// reading is that bits 2-0 are certainly owner bits and bits 3-4 are contested. Do not rely
        /// on the upper two without checking against a real pack.
        /// </para>
        /// <para>
        /// Within a tier the bits are, from high to low: bit 5 delete, bit 4 execute or common, bit 3
        /// append, bit 2 write, bit 1 read, bit 0 list. See
        /// <see cref="AccessPermissionString"/> for the readable rendering of the same word.
        /// </para>
        /// <para>
        /// This is one of the few keys that is safe to reapply to a fresh write, and
        /// <c>ApplyPortableMetadata</c> does so.
        /// </para>
        /// </remarks>
        public const string AccessBits = Prefix + "access_bits";

        /// <summary>
        /// The file type flag word at bytes 28-29 of the entry, stored as a 16-bit value.
        /// </summary>
        /// <remarks>
        /// The flags are terminal file (<c>0x0001</c>), peripheral file (<c>0x0002</c>), spooling
        /// file (<c>0x0004</c>), indexed file (<c>0x0008</c>), contiguous file (<c>0x0010</c>),
        /// allocated file (<c>0x0020</c>), magnetic tape file (<c>0x0040</c>) and library file
        /// (<c>0x0080</c>). More than one may be set. Indexed is the common case, and it decides
        /// whether the file's data is reached through index blocks or lies in one contiguous run.
        /// See <see cref="FileTypeString"/> for the readable rendering.
        /// </remarks>
        public const string FileTypeFlags = Prefix + "file_type_flags";

        /// <summary>
        /// The device number at bytes 30-31 of the entry, which identifies the hardware a device
        /// file stands for.
        /// </summary>
        /// <remarks>
        /// Meaningful for the device flags in <see cref="FileTypeFlags"/>. The mapping from a number
        /// to a particular device is UNKNOWN - not determined from the code.
        /// </remarks>
        public const string DeviceNumber = Prefix + "device_number";

        /// <summary>
        /// The index of the user that has the file reserved.
        /// </summary>
        /// <remarks>
        /// Read from byte 34 of the entry, the same byte the parser uses for
        /// <see cref="UserIndex"/>, so the two always come back equal. Whether the on-disk field
        /// genuinely shares that byte, or whether the parser reads the wrong offset here, is UNKNOWN
        /// - not determined from the code.
        /// </remarks>
        public const string UserIndexOfReservingUser = Prefix + "reserving_user";

        /// <summary>
        /// How many times the file is open right now, from bytes 36-37 of the entry.
        /// </summary>
        /// <remarks>
        /// A live count belonging to the pack it was read from. It describes the state of that
        /// system at that moment, so it is recorded but not reapplied to a new write.
        /// </remarks>
        public const string CurrentOpenCount = Prefix + "current_open_count";

        /// <summary>
        /// How many times the file has been opened in total, from bytes 38-39 of the entry.
        /// </summary>
        public const string TotalOpenCount = Prefix + "total_open_count";

        /// <summary>
        /// When the file was created, from bytes 40-43 of the entry, in the ND date format.
        /// </summary>
        /// <remarks>
        /// One of the three timestamps that survive a copy: <c>ApplyPortableMetadata</c> reapplies
        /// this alongside <see cref="LastDateOpenedForRead"/> and
        /// <see cref="LastDateOpenedForWrite"/>, because none of them depend on how the file was
        /// physically written.
        /// </remarks>
        public const string DateCreated = Prefix + "date_created";

        /// <summary>
        /// When the file was last opened for reading, from bytes 44-47 of the entry, in the ND date
        /// format.
        /// </summary>
        public const string LastDateOpenedForRead = Prefix + "last_read_date";

        /// <summary>
        /// When the file was last opened for writing, from bytes 48-51 of the entry, in the ND date
        /// format.
        /// </summary>
        public const string LastDateOpenedForWrite = Prefix + "last_write_date";

        /// <summary>
        /// The count of pages actually allocated to the file, from bytes 52-55 of the entry.
        /// </summary>
        /// <remarks>
        /// A page is 1024 words (2048 bytes). This counts only the pages that exist on disk, so it
        /// falls short of the file's logical extent whenever the file is sparse. Compare it against
        /// <see cref="BytesInFile"/> to tell: if <c>BytesInFile / 2048</c> exceeds this value the
        /// file has holes, and <see cref="Holes"/> says where they are.
        /// </remarks>
        public const string PagesInFile = Prefix + "pages_in_file";

        /// <summary>
        /// The file's length in bytes, spanning its whole logical extent.
        /// </summary>
        /// <remarks>
        /// Stored at bytes 56-59 of the entry as the length minus one, so the parser adds one back
        /// and the writer subtracts one again. For a sparse file this reaches past the last
        /// allocated page; see <see cref="PagesInFile"/> and <see cref="Holes"/>.
        /// </remarks>
        public const string BytesInFile = Prefix + "bytes_in_file";

        /// <summary>
        /// The numeric file type code at byte 32 of the entry.
        /// </summary>
        /// <remarks>
        /// The codes the entry class names are 0 for <c>DATA</c>, 1 for <c>PROG</c>, 2 for
        /// <c>SYMB</c> and 3 for <c>TEXT</c>; any other value is rendered as <c>TYPE</c> followed by
        /// the number. This is the legacy numeric field and is separate from both the
        /// <see cref="Type"/> text and the <see cref="FileTypeFlags"/> bits.
        /// </remarks>
        public const string FileType = Prefix + "file_type";

        /// <summary>
        /// Ascending logical page indices of a file's sparse holes, as an <c>int[]</c>. Empty when
        /// the file is solid; absent when the holes were not determined.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Nothing else in the sidecar says WHERE the holes are. <see cref="PagesInFile"/> and
        /// <see cref="BytesInFile"/> together reveal THAT a file is sparse - if
        /// <c>BytesInFile / 2048</c> exceeds <c>PagesInFile</c> there must be holes - but not their
        /// positions. Without this a sparse file copied out to a host and back returns with a
        /// different layout from the one it left with.
        /// </para>
        /// <para>
        /// It also preserves a distinction the file contents cannot carry: a hole and a page of
        /// genuine zeros read back identically, but they are not the same thing on disk.
        /// </para>
        /// <para>
        /// Recorded, not restored. The holes live in the file's index, which only the write path can
        /// build, and an <c>ObjectEntry</c> has nowhere to put them. A writer that wants to honour
        /// them must force a hole at each listed page rather than rely on all-zero detection.
        /// </para>
        /// </remarks>
        public const string Holes = Prefix + "holes";

        // UserEntry properties (for user metadata files)

        /// <summary>
        /// The user entry's flags byte, byte 0 of the on-disk entry, taken whole.
        /// </summary>
        /// <remarks>
        /// Bit 7 marks the entry as used and bit 0 marks it as a user entry rather than an object
        /// entry, so a valid entry has both bits of <c>0x81</c> set. Any other bit is unknown to the
        /// entry class and is preserved rather than interpreted, which is why the whole byte travels
        /// in this property.
        /// </remarks>
        public const string UserFlags = Prefix + "user_flags";

        /// <summary>
        /// How many times the user has logged in, from byte 1 of the user entry.
        /// </summary>
        /// <remarks>
        /// A single byte, and the login recorder stops incrementing it at 255 rather than wrapping.
        /// </remarks>
        public const string EnterCount = Prefix + "enter_count";

        /// <summary>
        /// The user's 16-bit password value, from bytes 18-19 of the user entry.
        /// </summary>
        /// <remarks>
        /// The password is checked by comparing this value directly, so it is the stored form and
        /// not a modern hash. It is carried so a user entry survives a copy unchanged.
        /// </remarks>
        public const string Password = Prefix + "password";

        /// <summary>
        /// When the user was created, from bytes 20-23 of the user entry, in the ND date format.
        /// </summary>
        /// <remarks>
        /// This is the user's own creation date and is a separate key from
        /// <see cref="DateCreated"/>, which belongs to a file, so both can sit in one bag without
        /// colliding.
        /// </remarks>
        public const string UserDateCreated = Prefix + "user_date_created";

        /// <summary>
        /// When the user last logged in, from bytes 24-27 of the user entry, in the ND date format.
        /// </summary>
        public const string LastDateEntered = Prefix + "last_entered";

        /// <summary>
        /// The user's page quota, from bytes 28-31 of the user entry.
        /// </summary>
        /// <remarks>
        /// A page is 1024 words (2048 bytes). The user is over quota once
        /// <see cref="PagesUsed"/> passes this value.
        /// </remarks>
        public const string PagesReserved = Prefix + "pages_reserved";

        /// <summary>
        /// The pages the user currently occupies, from bytes 32-35 of the user entry.
        /// </summary>
        /// <remarks>
        /// Measured in the same 1024-word (2048-byte) pages as <see cref="PagesReserved"/>; the
        /// difference between the two is the user's remaining allowance.
        /// </remarks>
        public const string PagesUsed = Prefix + "pages_used";

        /// <summary>
        /// The user's directory index, byte 36 of the user entry.
        /// </summary>
        /// <remarks>
        /// It selects which directory the user belongs to on a pack that carries more than one.
        /// </remarks>
        public const string DirectoryIndex = Prefix + "directory_index";

        /// <summary>
        /// The access word given to files this user creates, from bytes 40-41 of the user entry.
        /// </summary>
        /// <remarks>
        /// It has the same three-tier layout as <see cref="AccessBits"/>. A new entry starts at
        /// <c>0x4FF</c>. Bytes 38-39 of the entry are unused, and this field's position at 40 was
        /// verified against real images.
        /// </remarks>
        public const string DefaultFileAccess = Prefix + "default_file_access";

        // Friend list (stored as JSON array)

        /// <summary>
        /// The user's friend list, flattened to a string.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The user entry holds eight friend slots of two bytes each at bytes 48-63. Only the slots
        /// in use are written out, as <c>index:bits</c> pairs separated by commas, and reading the
        /// string back always yields eight slots with the unused ones cleared.
        /// </para>
        /// <para>
        /// The friend order is not preserved: entries are packed from the first slot on the way
        /// back in, so a friend that sat in slot 5 can return in slot 0.
        /// </para>
        /// </remarks>
        public const string Friends = Prefix + "friends";

        // Computed/derived properties

        /// <summary>
        /// The readable rendering of <see cref="AccessBits"/>.
        /// </summary>
        /// <remarks>
        /// Written as <c>Own:DLXAWR Friend:DLXAWR Public:DLXAWR</c>, where each letter stands for
        /// delete, list, execute, append, write or read, and a hyphen replaces any letter whose bit
        /// is clear. Derived on export for readability; <see cref="AccessBits"/> is what is read
        /// back.
        /// </remarks>
        public const string AccessPermissionString = Prefix + "access_string";

        /// <summary>
        /// The readable rendering of <see cref="FileTypeFlags"/>.
        /// </summary>
        /// <remarks>
        /// A comma-separated list of the flag names that are set, such as <c>Indexed</c>, or
        /// <c>Standard file</c> when none are. Derived on export for readability;
        /// <see cref="FileTypeFlags"/> is what is read back.
        /// </remarks>
        public const string FileTypeString = Prefix + "file_type_string";

        /// <summary>
        /// Whether the file sits in a version chain, as a <c>bool</c>.
        /// </summary>
        /// <remarks>
        /// True when either <see cref="ObjectEntryNextVersion"/> or
        /// <see cref="ObjectEntryPrevVersion"/> is non-zero. Derived on export and never read back,
        /// since the links it summarises are themselves never restored.
        /// </remarks>
        public const string IsVersioned = Prefix + "is_versioned";

        /// <summary>
        /// Intended to count the versions in a file's version chain.
        /// </summary>
        /// <remarks>
        /// Nothing writes this key and nothing reads it. What value it would hold, and whether it
        /// would count the whole chain or only the versions after this one, is UNKNOWN - not
        /// determined from the code. It appears only in the list returned by
        /// <see cref="GetAllKeys"/>.
        /// </remarks>
        public const string VersionCount = Prefix + "version_count";

        /// <summary>
        /// Checks if a property key is an NDFS-specific key.
        /// </summary>
        /// <param name="key">
        /// The property name to test. A null <paramref name="key"/> is accepted and reported as not
        /// an NDFS key.
        /// </param>
        /// <returns>
        /// <c>true</c> when <paramref name="key"/> starts with the NDFS prefix, otherwise
        /// <c>false</c>.
        /// </returns>
        public static bool IsNdfsProperty(string key)
        {
            return key != null && key.StartsWith(Prefix);
        }

        /// <summary>
        /// Gets all NDFS property keys.
        /// </summary>
        /// <returns>
        /// A new array holding every key this class defines, in a fresh array on each call so a
        /// caller may sort or otherwise modify it.
        /// </returns>
        public static string[] GetAllKeys()
        {
            return new[]
            {
                Header, ObjectIndex, ObjectName, Type, UserName, UserIndex,
                ObjectEntryNextVersion, ObjectEntryPrevVersion, AccessBits,
                FileTypeFlags, DeviceNumber, UserIndexOfReservingUser,
                CurrentOpenCount, TotalOpenCount, DateCreated,
                LastDateOpenedForRead, LastDateOpenedForWrite,
                PagesInFile, BytesInFile, FileType, Holes,
                UserFlags, EnterCount, Password, UserDateCreated,
                LastDateEntered, PagesReserved, PagesUsed,
                DirectoryIndex, DefaultFileAccess, Friends,
                AccessPermissionString, FileTypeString, IsVersioned, VersionCount
            };
        }
    }
}
