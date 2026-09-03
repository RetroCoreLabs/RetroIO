//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Protocols.Econet.FileServer.Configuration;

/// <summary>
/// Runtime file server configuration (PiFS NETCONF-style flags).
/// </summary>
public sealed class FileServerConfiguration
{
    public const byte FlagAcornHome = 0x01;
    public const byte FlagMdfsFunctions = 0x02;
    public const byte FlagBigChunks = 0x04;
    public const byte FlagInfColon = 0x08;
    public const byte FlagManyHandle = 0x10;
    public const byte FlagMdfsInfo = 0x20;
    public const byte FlagAcornDir = 0x80;

    public byte ConfigBits { get; set; } = FlagManyHandle | FlagMdfsInfo;
    public byte DefaultFilePerm { get; set; } = 0x33;
    public byte DefaultDirPerm { get; set; } = 0x33;
    public byte FilenameLength { get; set; } = 10;

    /// <summary>Delay before SAVE close reply (ms). PiFS uses ~20ms for Beeb clients.</summary>
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

    public bool ManyHandles => (ConfigBits & FlagManyHandle) != 0;

    public byte[] ToConfigReplyBytes()
    {
        return [ConfigBits, DefaultFilePerm, DefaultDirPerm, 0, FilenameLength];
    }

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
