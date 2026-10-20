//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.FileServer.Configuration;

/// <summary>
/// Runtime file server configuration (PiFS NETCONF-style flags).
/// </summary>
public sealed class FileServerConfiguration
{
    /// <summary>
    /// Bit mask 0x01 in ConfigBits. When set, the server uses Acorn home directory ownership rules.
    /// </summary>
    /// <remarks>
    /// PiFS name FS_CONFIG_ACORNHOME, NETCONF item ACORNHOME. The PiFS field comment says it means implement Acorn home directory ownership semantics.
    /// PiFS checks it when it resolves paths and at login (fs_login), where it looks up the home owner of a directory. The PiFS menu text calls it Acorn home dir permissions.
    /// Source: PiEconetBridge utilities/fs.c and include/econet-fs-hpbridge-common.h (struct fs_config, fs_acorn_home; the checks near lines 2784, 2928, 3061 and 4677). This code reads the bit through AcornHome. With it set, a user directory without a home owner gets the user number of the user who logs in (line 4677), and every object at or below a directory that has a home owner is owned by that user (lines 2784, 2928 and 3061). The owner and the home owner are kept in the sidecar file (see AcornInfStore and ObjectOwnership). *SETHOME writes the home owner whatever the bit says, as in PiFS (lines 10715 to 10751).
    /// This server gives every user the USER directory of the disc as the home, unless the user has a *SETHOME value, so users who share that directory share one home owner: the first one to log in with the bit set.
    /// PiFS default for a new configuration is off.
    /// </remarks>
    public const byte FlagAcornHome = 0x01;
    /// <summary>
    /// Bit mask 0x02 in ConfigBits. When set, the server turns on the SJ Research MDFS functions.
    /// </summary>
    /// <remarks>
    /// PiFS name FS_CONFIG_SJFUNC, NETCONF item MDFS. The PiFS field comment says it means turn on SJ MDFS functionality.
    /// With it set, PiFS accepts function codes 64 and above, and the %PASSWORDS file, the hidden or private permission bit, the extra owner bytes in object information, and a library search on load and open. With it clear, function codes 64 and above are refused.
    /// Source: PiEconetBridge utilities/fs.c and include/econet-fs-hpbridge-common.h (fs_sjfunc; the checks near lines 1104, 1131, 2203, 5089, 5392, 5744, 8178, 9197 and 9911).
    /// This code reads the bit for: operations from 64 up refused as Unsupported (line 9911), get object info command 0x40 refused (line 5392), and the library search on load (line 8178) and on open of an existing file (line 9197).
    /// It reads the bit for the owner bytes of the binary catalogue entry (EXAMINE argument 0, lines 5089 to 5094): the owner number, bits 8 to 10 of it shifted down by 3, and 0 for the auxiliary account. With the bit clear PiFS puts the internal number there, and this server has none, so those bytes stay 0.
    /// It does not implement the %PASSWORDS file (line 2203), the hidden bit conversion (lines 1104 and 1131) or the creation and modification time of get object info command 64 (lines 5741 to 5764), because this server has no MDFS password file, and it sends the stored access byte without converting it to the Acorn layout (see FlagAcornDir), so there is no conversion to add the hidden bit to. (Line 1104 masks fs_sjfunc (0x01 or 0) with FS_PERM_H (0x80), which is always 0, so the reference never sets 0x40 there either.)
    /// PiFS default for a new configuration is on.
    /// </remarks>
    public const byte FlagMdfsFunctions = 0x02;
    /// <summary>
    /// Bit mask 0x04 in ConfigBits. When set, the server allows 4 KB data chunks in bulk transfers instead of 0x500 bytes.
    /// </summary>
    /// <remarks>
    /// PiFS name FS_CONFIG_BIGCHUNKS, NETCONF item BIGCHUNKS. The PiFS menu text calls it GBPB Big Chunks. In the reply that starts a bulk write (fs_putbytes) PiFS reports a maximum transfer size of 0x1000 with the bit set and 0x500 with it clear.
    /// The PiFS field comment says the smaller size exists for BeebEm compatibility, because BeebEm appears to overrun its buffer on larger chunks.
    /// Source: PiEconetBridge utilities/fs.c and include/econet-fs-hpbridge-common.h (fs_bigchunks; fs.c lines 200 and 9023 to 9024, FS_MAX_BULK_SIZE at line 184). The reply that starts a SAVE reads the bit (see SaveWindow).
    /// PiFS default for a new configuration is off.
    /// </remarks>
    public const byte FlagBigChunks = 0x04;
    /// <summary>
    /// Bit mask 0x08 in ConfigBits. When set, the server stores file attributes in :inf files and maps the Acorn slash to a Unix dot.
    /// </summary>
    /// <remarks>
    /// PiFS name FS_CONFIG_INFCOLON, NETCONF item COLONMAP. The PiFS field comment says it uses :inf instead of .inf for the attribute side file, and maps Acorn slash to Unix dot instead of Unix colon.
    /// Source: PiEconetBridge utilities/fs.c and include/econet-fs-hpbridge-common.h (fs_infcolon; fs.c lines 203, 1605, 1978, 2738, 2894, 2966, 3519 and 5559). This code does not read the bit. The reference uses .inf when the bit is clear, and this server uses a comma-inf side file whatever the bit says, so there is no matching clear state to switch from.
    /// PiFS default for a new configuration is off.
    /// </remarks>
    public const byte FlagInfColon = 0x08;
    /// <summary>
    /// Bit mask 0x10 in ConfigBits. When set, ManyHandles is true and the server allows more than 8 open handles per station.
    /// </summary>
    /// <remarks>
    /// PiFS name FS_CONFIG_MANYHANDLE, NETCONF item BIGHANDLES. The PiFS field comment says it enables user handles above 8 and shows them as plain 8-bit numbers. With it clear, PiFS shows handle n as the value 2 to the power n-1, as Acorn FS 3 does with its limit of 8 handles, and refuses a ninth handle.
    /// Source: PiEconetBridge utilities/fs.c and include/econet-fs-hpbridge-common.h (fs_manyhandle; fs.c lines 71 to 72, 204, 1464, 1495 and 9941 to 9946). This code reads the bit through ManyHandles: it chooses plain or bit mask handle bytes on the wire (AcornFileHandles.DivHandle and MulHandle) and the largest file handle (8 or 32).
    /// PiFS default for a new configuration is off.
    /// </remarks>
    public const byte FlagManyHandle = 0x10;
    /// <summary>
    /// Bit mask 0x20 in ConfigBits. When set, the *INFO output is longer, like MDFS.
    /// </summary>
    /// <remarks>
    /// PiFS name FS_CONFIG_MDFSINFO, NETCONF item MDFSINFO. The PiFS field comment says it enables longer output from *INFO akin to MDFS. The long form adds the creation date and time and the internal number, and it also shows the owner and other access letters in lower case for objects the user does not own.
    /// Source: PiEconetBridge utilities/fs.c and include/econet-fs-hpbridge-common.h (fs_mdfsinfo; fs.c lines 205, 5123 to 5128, 5180 to 5187 and 7195 to 7201). This code reads the bit in the text catalogue (EXAMINE argument 3): the letters are lower case for the owner letters of an object the viewer does not own, and for the other-user letters of an object the viewer owns (lines 5180 to 5187). It does not make the long *INFO text (lines 7201 to 7216): that text carries the internal number of the object, which this server does not have, and *INFO here is a redirect to get object info, not the text of fs_info.
    /// PiFS default for a new configuration is off.
    /// </remarks>
    public const byte FlagMdfsInfo = 0x20;
    /// <summary>
    /// Bit mask 0x40 in ConfigBits. When set, directory permissions are more flexible and are enforced.
    /// </summary>
    /// <remarks>
    /// PiFS name FS_CONFIG_PIFSPERMS, NETCONF item PIFSPERMS. The PiFS field comment says it enables more flexible permissions on directories and enforces them.
    /// With it set, the create check also lets another user create in a directory that has the other-write and owner-write bits.
    /// Source: PiEconetBridge utilities/fs.c (fs_pifsperms, line 207; FS_PERM_CREATE, line 372) and include/econet-fs-hpbridge-common.h line 16. This code does not read the bit, on purpose. In fs.c the field is only stored, sent and set (lines 570, 591 and 10486 to 10487). The macros that would use it (FS_PERM_CREATE, FS_PERM_SAVE, FS_PERM_LOAD, FS_PERM_RENAME, FS_PERM_OPENOUT, FS_PERM_WRITE, lines 372 to 421) are never called, and they cannot compile as written: they name FS_CONFIG_PIFSPERMS as if it were a function, and use FS_PERM_SET_ and FS_PERM OTH(W) that are not defined. The checks that PiFS really makes (for example the save check at lines 5834 to 5846) do not look at the bit. With no written rule to follow, nothing is enforced here.
    /// PiFS default for a new configuration is off.
    /// </remarks>
    public const byte FlagPifsPerms = 0x40;
    /// <summary>
    /// Bit mask 0x80 in ConfigBits. When set, directories are shown the Acorn way, with the implied W/R access letters hidden.
    /// </summary>
    /// <remarks>
    /// PiFS name FS_CONFIG_MASKDIRWRR, NETCONF item ACORNDIR. The PiFS field comment says it masks off the owner write, owner read and other read bits of a directory in readable and raw output, because those are implied. It does not mask when the other write bit is set, or when the three bits are not all set, so access set by hand is shown as it is. The PiFS menu text calls it Acorn Directory display.
    /// Source: PiEconetBridge utilities/fs.c and include/econet-fs-hpbridge-common.h (fs_mask_dir_wrr; fs.c lines 209, 1110, 5117, 5174 and 7191). This code reads the bit in the text catalogue (EXAMINE argument 3). It does not change the binary catalogue entries or the access byte of get object info, because this server sends the PiFS access bits there without converting them to the Acorn layout.
    /// PiFS default for a new configuration is on.
    /// </remarks>
    public const byte FlagAcornDir = 0x80;

    /// <summary>
    /// Gets or sets the configuration flag byte. The default is FlagMdfsFunctions and FlagAcornDir, the value 0x82.
    /// </summary>
    /// <remarks>
    /// A new PiFS configuration sets fs_sjfunc and fs_mask_dir_wrr and leaves every other flag clear (fs.c lines 3460 to 3464), which is 0x82.
    /// PiFS keeps the flags in a 32-bit value (struct fs_config holds one byte per flag, and fs_get_parameters builds the value, fs.c lines 555 to 571) and sends it to a client as four bytes, low byte first, followed by the file name length (fs.c lines 4254 to 4266).
    /// The flags use bits 0 to 7 of that value, bits 8 to 15 are always 0, the default file permission is bits 16 to 23 and the default directory permission is bits 24 to 31. This class keeps the flag byte and the two permissions as separate properties and builds the four bytes in ToConfigReplyBytes.
    /// Source: PiEconetBridge utilities/fs.c lines 198 to 209, 555 to 571, 3460 to 3467 and 4240 to 4300, and include/econet-fs-hpbridge-common.h lines 10 to 17.
    /// </remarks>
    public byte ConfigBits { get; set; } = FlagMdfsFunctions | FlagAcornDir;
    /// <summary>
    /// Gets or sets the access byte given to files that have no stored access. The default is 0x03.
    /// </summary>
    /// <remarks>
    /// PiFS sets FS_PERM_OWN_W | FS_PERM_OWN_R (0x02 | 0x01) for a new configuration (fs.c line 3467).
    /// </remarks>
    public byte DefaultFilePerm { get; set; } = 0x03;
    /// <summary>
    /// Gets or sets the access byte for directories. The default is 0x13.
    /// </summary>
    /// <remarks>
    /// PiFS sets FS_PERM_OWN_W | FS_PERM_OWN_R | FS_PERM_OTH_R (0x02 | 0x01 | 0x10) for a new configuration (fs.c line 3466).
    /// </remarks>
    public byte DefaultDirPerm { get; set; } = 0x13;
    /// <summary>
    /// Gets or sets the file name length value reported in the configuration reply. The default is 10.
    /// </summary>
    public byte FilenameLength { get; set; } = 10;

    /// <summary>
    /// Delay before SAVE close reply (ms). PiFS uses ~20ms for Beeb clients.
    /// </summary>
    public int SaveCloseDelayMs { get; set; } = 20;

    /// <summary>
    /// AUN reply retransmit interval (ms) between resends while waiting for a matching ACK.
    /// Mirrors PiEconetBridge EB_CONFIG_AUN_RETX (1000): BeebEm needs a long timeout and dislikes
    /// another packet arriving before it has ACKed the previous one.
    /// </summary>
    public int AunRetransmitIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Maximum total transmissions of an AUN data reply (initial send + retransmits), matching
    /// PiEconetBridge EB_CONFIG_AUN_RETRIES (5). 1 disables retransmission (single shot).
    /// </summary>
    public int AunMaxTransmits { get; set; } = 5;

    /// <summary>
    /// The smallest file name length PiFS accepts in a set-parameters call.
    /// </summary>
    public const byte MinFilenameLength = 10;
    /// <summary>
    /// The largest file name length PiFS accepts in a set-parameters call.
    /// </summary>
    public const byte MaxFilenameLength = 79;

    /// <summary>
    /// Gets the largest data size the server announces when it starts a SAVE: 0x1000 with FlagBigChunks set, otherwise 0x500.
    /// </summary>
    /// <remarks>
    /// Source: PiEconetBridge utilities/fs.c lines 184, 9023 and 9024 (FS_MAX_BULK_SIZE is 0x1000).
    /// </remarks>
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
    /// <remarks>
    /// Source: PiEconetBridge utilities/fs.c lines 555 to 571 (fs_get_parameters) and 4254 to 4266 (PiBridge call 0x11).
    /// </remarks>
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
    /// Source: PiEconetBridge utilities/fs.c lines 4272 to 4291 (PiBridge call 0x12) for the layout and the file name length check.
    /// The reference decodes the permission bytes with masks that cannot match the layout it sends (fs_set_parameters at lines 579 and 580 masks 0x00ff0000 and then shifts by 24, so both come out 0). This code reads them where fs_get_parameters puts them, so a client that reads the parameters and writes them back keeps the permissions.
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
