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
    /// Bit mask 0x01 in ConfigBits, the Acorn home flag.
    /// </summary>
    public const byte FlagAcornHome = 0x01;
    /// <summary>
    /// Bit mask 0x02 in ConfigBits, the MDFS functions flag.
    /// </summary>
    public const byte FlagMdfsFunctions = 0x02;
    /// <summary>
    /// Bit mask 0x04 in ConfigBits, the big chunks flag.
    /// </summary>
    public const byte FlagBigChunks = 0x04;
    /// <summary>
    /// Bit mask 0x08 in ConfigBits, the INF colon flag.
    /// </summary>
    public const byte FlagInfColon = 0x08;
    /// <summary>
    /// Bit mask 0x10 in ConfigBits. When set, ManyHandles is true.
    /// </summary>
    public const byte FlagManyHandle = 0x10;
    /// <summary>
    /// Bit mask 0x20 in ConfigBits, the MDFS info flag.
    /// </summary>
    public const byte FlagMdfsInfo = 0x20;
    /// <summary>
    /// Bit mask 0x80 in ConfigBits, the Acorn directory flag.
    /// </summary>
    public const byte FlagAcornDir = 0x80;

    /// <summary>
    /// Gets or sets the configuration flag byte. The default has FlagManyHandle and FlagMdfsInfo set.
    /// </summary>
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
