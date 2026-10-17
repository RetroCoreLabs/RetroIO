//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.Common;

namespace RetroIO.Protocols.Econet.Protocol.FileServer;

/// <summary>
/// Level-3 bulk opcodes (SAVE, LOAD, …) repurpose bytes 2–4 instead of URD/CWD/LIB.
/// Layout matches PiFS / Acorn DFS (see PiEconetBridge utilities/fs.c).
/// </summary>
public static class FileServerBulkPacketLayout
{
    /// <summary>
    /// Offset of the file name in a load request.
    /// </summary>
    public const int LoadFilenameOffset = 5;
    /// <summary>
    /// Offset of the file name in a save request.
    /// </summary>
    public const int SaveFilenameOffset = 16;
    /// <summary>
    /// The smallest length of a save request in bytes.
    /// </summary>
    public const int SaveMinimumLength = 17;

    /// <summary>
    /// Reads the fields of a load request.
    /// </summary>
    /// <param name="raw">
    /// The whole request, starting with the reply port byte.
    /// </param>
    /// <param name="dataPort">
    /// Receives the port the file data is sent to, from byte 2.
    /// </param>
    /// <param name="relativeTo">
    /// Receives the directory handle the name is relative to, from byte 3.
    /// </param>
    /// <param name="filename">
    /// Receives the file name, up to the first carriage return and without surrounding quotes.
    /// </param>
    /// <returns>
    /// True when the request is long enough, otherwise false.
    /// </returns>
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

    /// <summary>
    /// Reads the fields of a save request.
    /// </summary>
    /// <param name="raw">
    /// The whole request, starting with the reply port byte.
    /// </param>
    /// <param name="ackPort">
    /// Receives the port the acknowledgements are sent to, from byte 2.
    /// </param>
    /// <param name="load">
    /// Receives the load address, from bytes 5 to 8.
    /// </param>
    /// <param name="exec">
    /// Receives the execution address, from bytes 9 to 12.
    /// </param>
    /// <param name="length">
    /// Receives the file length, from bytes 13 to 15.
    /// </param>
    /// <param name="filename">
    /// Receives the file name, up to the first carriage return and without surrounding quotes.
    /// </param>
    /// <returns>
    /// True when the request is at least as long as the minimum save length, otherwise false.
    /// </returns>
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
