//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.FileServer.Configuration;

/// <summary>
/// Runtime file server configuration (*NETCONF-style flags).
/// </summary>
public sealed class FileServerConfiguration
{
    /// <summary>
    /// Bit mask 0x01 in ConfigBits. When set, the server uses Acorn home directory ownership rules.
    /// </summary>
    /// <remarks>
    /// *NETCONF item ACORNHOME. It means: use Acorn home directory ownership rules.
    /// It is checked when paths are resolved and at login, where the home owner of a directory is looked up. Menu text: Acorn home dir permissions.
    /// This code reads the bit through AcornHome. With it set, a user directory without a home owner gets the user number of the user who logs in, and every object at or below a directory that has a home owner is owned by that user. The owner and the home owner are kept in the property bag of the object (see AcornAttributeStore and ObjectOwnership). *SETHOME writes the home owner whatever the bit says.
    /// This server gives every user the USER directory of the disc as the home, unless the user has a *SETHOME value, so users who share that directory share one home owner: the first one to log in with the bit set.
    /// Default for a new configuration: off.
    /// </remarks>
    public const byte FlagAcornHome = 0x01;
    /// <summary>
    /// Bit mask 0x02 in ConfigBits. When set, the server turns on the SJ Research MDFS functions.
    /// </summary>
    /// <remarks>
    /// *NETCONF item MDFS. It means: turn on the SJ MDFS functions.
    /// With it set, function codes 64 and above are accepted, and so are the %PASSWORDS file, the hidden or private permission bit, the extra owner bytes in object information, and a library search on load and open. With it clear, function codes 64 and above are refused.
    /// This code reads the bit for: operations from 64 up refused as Unsupported, get object info command 0x40 refused, and the library search on load and on open of an existing file.
    /// It reads the bit for the owner bytes of the binary catalogue entry (EXAMINE argument 0): the owner number, bits 8 to 10 of it shifted down by 3, and 0 for the auxiliary account. With the bit clear an internal number goes there, and this server has none, so those bytes stay 0.
    /// It does not implement the %PASSWORDS file, the hidden bit conversion or the creation and modification time of get object info command 64, because this server has no MDFS password file, and it sends the stored access byte without converting it to the Acorn layout (see FlagAcornDir), so there is no conversion to add the hidden bit to.
    /// Default for a new configuration: on.
    /// </remarks>
    public const byte FlagMdfsFunctions = 0x02;
    /// <summary>
    /// Bit mask 0x04 in ConfigBits. When set, the server allows 4 KB data chunks in bulk transfers instead of 0x500 bytes.
    /// </summary>
    /// <remarks>
    /// *NETCONF item BIGCHUNKS. Menu text: GBPB Big Chunks. In the reply that starts a bulk write the server reports a maximum transfer size of 0x1000 with the bit set and 0x500 with it clear.
    /// The smaller size exists for BeebEm, which appears to overrun its buffer on larger chunks.
    /// The reply that starts a SAVE reads the bit (see SaveWindow).
    /// Default for a new configuration: off.
    /// </remarks>
    public const byte FlagBigChunks = 0x04;
    /// <summary>
    /// Bit mask 0x08 in ConfigBits. When set, the server stores file attributes in :inf files and maps the Acorn slash to a Unix dot.
    /// </summary>
    /// <remarks>
    /// *NETCONF item COLONMAP. It means: use :inf instead of .inf for the attribute side file, and map an Acorn slash to a Unix dot instead of a Unix colon.
    /// This code does not read the bit. With the bit clear the side file is .inf, and this server keeps the attributes in the property bag of the object (an extended attribute, or the .xat file) whatever the bit says, so there is no matching clear state to switch from.
    /// Default for a new configuration: off.
    /// </remarks>
    public const byte FlagInfColon = 0x08;
    /// <summary>
    /// Bit mask 0x10 in ConfigBits. When set, ManyHandles is true and the server allows more than 8 open handles per station.
    /// </summary>
    /// <remarks>
    /// *NETCONF item BIGHANDLES. It means: allow user handles above 8 and show them as plain 8-bit numbers. With it clear, handle n is shown as the value 2 to the power n-1, as Acorn FS 3 does with its limit of 8 handles, and a ninth handle is refused.
    /// This code reads the bit through ManyHandles: it chooses plain or bit mask handle bytes on the wire (AcornFileHandles.DivHandle and MulHandle) and the largest file handle (8 or 32).
    /// Default for a new configuration: off.
    /// </remarks>
    public const byte FlagManyHandle = 0x10;
    /// <summary>
    /// Bit mask 0x20 in ConfigBits. When set, the *INFO output is longer, like MDFS.
    /// </summary>
    /// <remarks>
    /// *NETCONF item MDFSINFO. It means: longer output from *INFO, like MDFS. The long form adds the creation date and time and the internal number, and it also shows the owner and other access letters in lower case for objects the user does not own.
    /// This code reads the bit in the text catalogue (EXAMINE argument 3): the letters are lower case for the owner letters of an object the viewer does not own, and for the other-user letters of an object the viewer owns. It does not make the long *INFO text: that text carries the internal number of the object, which this server does not have, and *INFO here is a redirect to get object info.
    /// Default for a new configuration: off.
    /// </remarks>
    public const byte FlagMdfsInfo = 0x20;
    /// <summary>
    /// Bit mask 0x40 in ConfigBits. When set, directory permissions are more flexible and are enforced.
    /// </summary>
    /// <remarks>
    /// It means: more flexible permissions on directories, and enforce them.
    /// With it set, the create check would also let another user create in a directory that has the other-write and owner-write bits.
    /// This code does not read the bit, on purpose. The bit is only stored, sent and set; no known rule says what checks it changes. With no written rule to follow, nothing is enforced here.
    /// Default for a new configuration: off.
    /// </remarks>
    public const byte FlagFlexiblePerms = 0x40;
    /// <summary>
    /// Bit mask 0x80 in ConfigBits. When set, directories are shown the Acorn way, with the implied W/R access letters hidden.
    /// </summary>
    /// <remarks>
    /// *NETCONF item ACORNDIR. It means: mask off the owner write, owner read and other read bits of a directory in readable and raw output, because those are implied. It does not mask when the other write bit is set, or when the three bits are not all set, so access set by hand is shown as it is. Menu text: Acorn Directory display.
    /// This code reads the bit in the text catalogue (EXAMINE argument 3). It does not change the binary catalogue entries or the access byte of get object info, because this server sends its stored access bits there without converting them to the Acorn layout.
    /// Default for a new configuration: on.
    /// </remarks>
    public const byte FlagAcornDir = 0x80;

    /// <summary>
    /// Gets or sets the configuration flag byte. The default is FlagMdfsFunctions and FlagAcornDir, the value 0x82.
    /// </summary>
    /// <remarks>
    /// A new configuration sets the MDFS and ACORNDIR flags and leaves every other flag clear, which is 0x82.
    /// The flags are kept in a 32-bit value and sent to a client as four bytes, low byte first, followed by the file name length.
    /// The flags use bits 0 to 7 of that value, bits 8 to 15 are always 0, the default file permission is bits 16 to 23 and the default directory permission is bits 24 to 31. This class keeps the flag byte and the two permissions as separate properties and builds the four bytes in ToConfigReplyBytes.
    /// </remarks>
    public byte ConfigBits { get; set; } = FlagMdfsFunctions | FlagAcornDir;
    /// <summary>
    /// Gets or sets the access byte given to files that have no stored access. The default is 0x03.
    /// </summary>
    /// <remarks>
    /// Owner write and owner read (0x02 | 0x01) for a new configuration.
    /// </remarks>
    public byte DefaultFilePerm { get; set; } = 0x03;
    /// <summary>
    /// Gets or sets the access byte for directories. The default is 0x13.
    /// </summary>
    /// <remarks>
    /// Owner write, owner read and other read (0x02 | 0x01 | 0x10) for a new configuration.
    /// </remarks>
    public byte DefaultDirPerm { get; set; } = 0x13;
    /// <summary>
    /// Gets or sets the file name length value reported in the configuration reply. The default is 10.
    /// </summary>
    public byte FilenameLength { get; set; } = 10;

    /// <summary>
    /// Delay before SAVE close reply (ms). About 20 ms suits Beeb clients.
    /// </summary>
    public int SaveCloseDelayMs { get; set; } = 20;

    /// <summary>
    /// AUN reply retransmit interval (ms) between resends while waiting for a matching ACK.
    /// 1000 ms: BeebEm needs a long timeout and dislikes
    /// another packet arriving before it has ACKed the previous one.
    /// </summary>
    public int AunRetransmitIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Maximum total transmissions of an AUN data reply (initial send + retransmits). The default is
    /// 5. 1 disables retransmission (single shot).
    /// </summary>
    public int AunMaxTransmits { get; set; } = 5;

    /// <summary>
    /// The smallest file name length accepted in a set-parameters call.
    /// </summary>
    public const byte MinFilenameLength = 10;
    /// <summary>
    /// The largest file name length accepted in a set-parameters call.
    /// </summary>
    public const byte MaxFilenameLength = 79;

    /// <summary>
    /// Gets the largest data size the server announces when it starts a SAVE: 0x1000 with FlagBigChunks set, otherwise 0x500.
    /// </summary>
    public int SaveWindow => (ConfigBits & FlagBigChunks) != 0 ? 0x1000 : 0x500;

    /// <summary>
    /// Gets a value indicating whether FlagManyHandle is set in ConfigBits.
    /// </summary>
    public bool ManyHandles => (ConfigBits & FlagManyHandle) != 0;

    /// <summary>
    /// Gets a value indicating whether FlagMdfsFunctions is set in ConfigBits.
    /// </summary>
    public bool MdfsFunctions => (ConfigBits & FlagMdfsFunctions) != 0;

    /// <summary>
    /// Gets a value indicating whether FlagAcornHome is set in ConfigBits.
    /// </summary>
    public bool AcornHome => (ConfigBits & FlagAcornHome) != 0;

    /// <summary>
    /// Gets a value indicating whether FlagMdfsInfo is set in ConfigBits.
    /// </summary>
    public bool MdfsInfo => (ConfigBits & FlagMdfsInfo) != 0;

    /// <summary>
    /// Gets a value indicating whether FlagAcornDir is set in ConfigBits.
    /// </summary>
    public bool AcornDirectoryDisplay => (ConfigBits & FlagAcornDir) != 0;

    /// <summary>
    /// Builds the five configuration bytes sent in a configuration reply.
    /// </summary>
    /// <returns>
    /// The four bytes of the 32-bit parameter value, low byte first (ConfigBits, 0, DefaultFilePerm, DefaultDirPerm), then FilenameLength.
    /// </returns>
    public byte[] ToConfigReplyBytes()
    {
        return [ConfigBits, 0, DefaultFilePerm, DefaultDirPerm, FilenameLength];
    }

    /// <summary>
    /// Updates the configuration from the bytes of a set-parameters request. Bytes that are not present are left unchanged.
    /// </summary>
    /// <param name="data">
    /// The configuration bytes in the same layout as ToConfigReplyBytes. Byte 0 sets ConfigBits, byte 1 (bits 8 to 15) is ignored, byte 2 sets DefaultFilePerm, byte 3 sets DefaultDirPerm and byte 4 sets FilenameLength.
    /// </param>
    /// <returns>
    /// False when byte 4 is present and outside MinFilenameLength to MaxFilenameLength; nothing is changed then. Otherwise true.
    /// </returns>
    /// <remarks>
    /// PiBridge call 0x12 for the layout and the file name length check.
    /// The permission bytes are read where the configuration reply puts them, so a client that reads the parameters and writes them back keeps the permissions.
    /// </remarks>
    public bool ApplyConfigBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length > 4 && (data[4] < MinFilenameLength || data[4] > MaxFilenameLength))
            return false;

        if (data.Length > 0)
            ConfigBits = data[0];
        if (data.Length > 2)
            DefaultFilePerm = data[2];
        if (data.Length > 3)
            DefaultDirPerm = data[3];
        if (data.Length > 4)
            FilenameLength = data[4];

        return true;
    }
}
