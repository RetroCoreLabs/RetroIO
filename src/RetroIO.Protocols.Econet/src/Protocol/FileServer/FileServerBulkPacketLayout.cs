//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.Common;

namespace RetroIO.Protocols.Econet.Protocol.FileServer;

/// <summary>
/// Level-3 bulk opcodes (SAVE, LOAD, …) repurpose bytes 2–4 instead of URD/CWD/LIB.
/// Layout matches PiFS / Acorn DFS (see PiEconetBridge utilities/fs.c).
/// </summary>
public static class FileServerBulkPacketLayout
{
    public const int LoadFilenameOffset = 5;
    public const int SaveFilenameOffset = 16;
    public const int SaveMinimumLength = 17;

    public static bool TryParseLoad(ReadOnlySpan<byte> raw, out byte dataPort, out byte relativeTo, out string filename)
    {
        dataPort = 0;
        relativeTo = 0;
        filename = string.Empty;

        if (raw.Length < LoadFilenameOffset + 1)
            return false;

        dataPort = raw[2];
        relativeTo = raw[3];
        filename = AcornPath.StripQuotes(AcornPath.ReadCrTerminatedString(raw.Slice(LoadFilenameOffset)));
        return true;
    }

    public static bool TryParseSave(ReadOnlySpan<byte> raw, out byte ackPort, out uint load, out uint exec, out int length, out string filename)
    {
        ackPort = 0;
        load = 0;
        exec = 0;
        length = 0;
        filename = string.Empty;

        if (raw.Length < SaveMinimumLength)
            return false;

        ackPort = raw[2];
        load = ReadUInt32Le(raw, 5);
        exec = ReadUInt32Le(raw, 9);
        length = raw[13] | (raw[14] << 8) | (raw[15] << 16);
        filename = AcornPath.StripQuotes(AcornPath.ReadCrTerminatedString(raw.Slice(SaveFilenameOffset)));
        return true;
    }

    private static uint ReadUInt32Le(ReadOnlySpan<byte> data, int offset)
    {
        return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
    }
}
