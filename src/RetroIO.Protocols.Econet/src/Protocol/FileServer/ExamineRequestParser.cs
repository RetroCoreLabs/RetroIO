//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.Common;

namespace RetroIO.Protocols.Econet.Protocol.FileServer;

/// <summary>
/// Parses fsop <c>0x03</c> per PiFS <c>fs_examine</c> (arg/start/count then path at byte 8).
/// DNFS often omits arg/start/count and places the path at byte 5 (same layout as CatHeader).
/// </summary>
internal static class ExamineRequestParser
{
    public static bool TryParse(ReadOnlySpan<byte> parameters, out byte arg, out byte start, out byte count, out ReadOnlySpan<byte> pathBytes)
    {
        if (parameters.IsEmpty)
        {
            arg = 0;
            start = 0;
            count = 0xFF;
            pathBytes = ReadOnlySpan<byte>.Empty;
            return true;
        }

        if (parameters[0] <= 3 && parameters.Length >= 4)
        {
            arg = parameters[0];
            start = parameters[1];
            count = parameters[2];
            pathBytes = parameters.Slice(3);
            return true;
        }

        arg = 0;
        start = 0;
        count = 0xFF;
        pathBytes = parameters;
        return true;
    }

    public static string FormatPathPreview(ReadOnlySpan<byte> pathBytes)
    {
        if (pathBytes.IsEmpty)
            return "(cwd)";

        string path = AcornPath.ReadCrTerminatedString(pathBytes).Trim();
        return path.Length == 0 ? "(cwd)" : path;
    }
}
