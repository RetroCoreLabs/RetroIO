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
    /// Source: PiEconetBridge utilities/fs.c and include/econet-fs-hpbridge-common.h (struct fs_config, fs_acorn_home; the checks near lines 2784, 2928, 3061 and 4677). This code does not read the bit.
    /// PiFS default for a new configuration is off.
    /// </remarks>
    public const byte FlagAcornHome = 0x01;
    /// <summary>
    /// Bit mask 0x02 in ConfigBits. When set, the server turns on the SJ Research MDFS functions.
    /// </summary>
    /// <remarks>
    /// PiFS name FS_CONFIG_SJFUNC, NETCONF item MDFS. The PiFS field comment says it means turn on SJ MDFS functionality.
    /// With it set, PiFS accepts function codes 64 and above, and the %PASSWORDS file, the hidden or private permission bit, the extra owner bytes in object information, and a library search on load and open. With it clear, function codes 64 and above are refused.
    /// Source: PiEconetBridge utilities/fs.c and include/econet-fs-hpbridge-common.h (fs_sjfunc; the checks near lines 1104, 1131, 2203, 5089, 5392, 5744, 8178, 9197 and 9911). This code does not read the bit.
    /// PiFS default for a new configuration is on.
    /// </remarks>
    public const byte FlagMdfsFunctions = 0x02;
    /// <summary>
    /// Bit mask 0x04 in ConfigBits. When set, the server allows 4 KB data chunks in bulk transfers instead of 0x500 bytes.
    /// </summary>
    /// <remarks>
    /// PiFS name FS_CONFIG_BIGCHUNKS, NETCONF item BIGCHUNKS. The PiFS menu text calls it GBPB Big Chunks. In the reply that starts a bulk write (fs_putbytes) PiFS reports a maximum transfer size of 0x1000 with the bit set and 0x500 with it clear.
    /// The PiFS field comment says the smaller size exists for BeebEm compatibility, because BeebEm appears to overrun its buffer on larger chunks.
    /// Source: PiEconetBridge utilities/fs.c and include/econet-fs-hpbridge-common.h (fs_bigchunks; fs.c lines 200 and 9023 to 9024, FS_MAX_BULK_SIZE at line 184). This code does not read the bit.
    /// PiFS default for a new configuration is off.
    /// </remarks>
    public const byte FlagBigChunks = 0x04;
    /// <summary>
    /// Bit mask 0x08 in ConfigBits. When set, the server stores file attributes in :inf files and maps the Acorn slash to a Unix dot.
    /// </summary>
    /// <remarks>
    /// PiFS name FS_CONFIG_INFCOLON, NETCONF item COLONMAP. The PiFS field comment says it uses :inf instead of .inf for the attribute side file, and maps Acorn slash to Unix dot instead of Unix colon.
    /// Source: PiEconetBridge utilities/fs.c and include/econet-fs-hpbridge-common.h (fs_infcolon; fs.c lines 203, 1605, 1978, 2738, 2894, 2966, 3519 and 5559). This code does not read the bit.
    /// PiFS default for a new configuration is off.
    /// </remarks>
    public const byte FlagInfColon = 0x08;
    /// <summary>
    /// Bit mask 0x10 in ConfigBits. When set, ManyHandles is true and the server allows more than 8 open handles per station.
    /// </summary>
    /// <remarks>
    /// PiFS name FS_CONFIG_MANYHANDLE, NETCONF item BIGHANDLES. The PiFS field comment says it enables user handles above 8 and shows them as plain 8-bit numbers. With it clear, PiFS shows handle n as the value 2 to the power n-1, as Acorn FS 3 does with its limit of 8 handles, and refuses a ninth handle.
    /// Source: PiEconetBridge utilities/fs.c and include/econet-fs-hpbridge-common.h (fs_manyhandle; fs.c lines 71 to 72, 204, 1464, 1495 and 9941 to 9946). In this code only the ManyHandles property reads the bit, and nothing else reads that property.
    /// PiFS default for a new configuration is off.
    /// </remarks>
    public const byte FlagManyHandle = 0x10;
    /// <summary>
    /// Bit mask 0x20 in ConfigBits. When set, the *INFO output is longer, like MDFS.
    /// </summary>
    /// <remarks>
    /// PiFS name FS_CONFIG_MDFSINFO, NETCONF item MDFSINFO. The PiFS field comment says it enables longer output from *INFO akin to MDFS. The long form adds the creation date and time and the internal number, and it also shows the owner and other access letters in lower case for objects the user does not own.
    /// Source: PiEconetBridge utilities/fs.c and include/econet-fs-hpbridge-common.h (fs_mdfsinfo; fs.c lines 205, 5123 to 5128, 5180 to 5187 and 7195 to 7201). This code does not read the bit.
    /// PiFS default for a new configuration is off.
    /// </remarks>
    public const byte FlagMdfsInfo = 0x20;
    /// <summary>
    /// Bit mask 0x80 in ConfigBits. When set, directories are shown the Acorn way, with the implied W/R access letters hidden.
    /// </summary>
    /// <remarks>
    /// PiFS name FS_CONFIG_MASKDIRWRR, NETCONF item ACORNDIR. The PiFS field comment says it masks off the owner write, owner read and other read bits of a directory in readable and raw output, because those are implied. It does not mask when the other write bit is set, or when the three bits are not all set, so access set by hand is shown as it is. The PiFS menu text calls it Acorn Directory display.
    /// Source: PiEconetBridge utilities/fs.c and include/econet-fs-hpbridge-common.h (fs_mask_dir_wrr; fs.c lines 209, 1110, 5117, 5174 and 7191). This code does not read the bit.
    /// PiFS default for a new configuration is on.
    /// </remarks>
    public const byte FlagAcornDir = 0x80;

    /// <summary>
    /// Gets or sets the configuration flag byte. The default has FlagManyHandle and FlagMdfsInfo set.
    /// </summary>
    /// <remarks>
    /// Bit 0x40 is not defined in this class. PiFS defines it as FS_CONFIG_PIFSPERMS (NETCONF item PIFSPERMS), which enables more flexible directory permissions and enforces them. This code does not read or write that bit.
    /// PiFS keeps its flags in a 32-bit value and sends them to a client as four bytes, low byte first, followed by the file name length. This class keeps only the low byte.
    /// A new PiFS configuration starts with FS_CONFIG_SJFUNC and FS_CONFIG_MASKDIRWRR set (value 0x82) and the other flags clear, which differs from the default here.
    /// Source: PiEconetBridge utilities/fs.c lines 198 to 209, 564 to 571 and 3460 to 3464, and include/econet-fs-hpbridge-common.h lines 10 to 17.
    /// </remarks>
    public byte ConfigBits { get; set; } = FlagManyHandle | FlagMdfsInfo;
    /// <summary>
    /// Gets or sets the access byte given to files that have no stored access. The default is 0x33.
    /// </summary>
    public byte DefaultFilePerm { get; set; } = 0x33;
    /// <summary>
    /// Gets or sets the access byte for directories. The default is 0x33.
    /// </summary>
    public byte DefaultDirPerm { get; set; } = 0x33;
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
    /// Gets a value indicating whether FlagManyHandle is set in ConfigBits.
    /// </summary>
    public bool ManyHandles => (ConfigBits & FlagManyHandle) != 0;

    /// <summary>
    /// Builds the five configuration bytes sent in a configuration reply.
    /// </summary>
    /// <returns>
    /// The bytes ConfigBits, DefaultFilePerm, DefaultDirPerm, 0 and FilenameLength.
    /// </returns>
    public byte[] ToConfigReplyBytes()
    {
        return [ConfigBits, DefaultFilePerm, DefaultDirPerm, 0, FilenameLength];
    }

    /// <summary>
    /// Updates the configuration from the bytes of a configuration request. Bytes that are not present are left unchanged.
    /// </summary>
    /// <param name="data">
    /// The configuration bytes. Byte 0 sets ConfigBits, byte 1 DefaultFilePerm, byte 2 DefaultDirPerm and byte 4 FilenameLength. Byte 3 is ignored.
    /// </param>
    public void ApplyConfigBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length > 0)
            ConfigBits = data[0];
        if (data.Length > 1)
            DefaultFilePerm = data[1];
        if (data.Length > 2)
            DefaultDirPerm = data[2];
        if (data.Length > 4)
            FilenameLength = data[4];
    }
}
