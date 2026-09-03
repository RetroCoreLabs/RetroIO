//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Sessions;

namespace RetroIO.Protocols.Econet.Protocol.FileServer;

/// <summary>
/// Parses fsop <c>0x12</c> requests per PiFS <c>fs_get_object_info</c> (command at byte 5, path at byte 6).
/// DNFS/BeebEm often omit the command byte: catalogue step sends <c>.</c> or CR-only at byte 5 (implicit arg 6);
/// per-file step sends the filename at byte 5 (implicit arg 1).
/// </summary>
internal static class GetObjectInfoRequestParser
{
    public static bool IsKnownCommand(byte value) =>
        value is 1 or 2 or 3 or 4 or 5 or 6 or 64 or 65 or FileServerOpcodes.GetObjectInfoCanonical;

    public static bool TryParse(FileServerRequest request, out byte command, out ReadOnlySpan<byte> pathBytes)
    {
        ReadOnlySpan<byte> parameters = request.Parameters;
        if (parameters.IsEmpty)
        {
            command = 6;
            pathBytes = ReadOnlySpan<byte>.Empty;
            return true;
        }

        if (IsKnownCommand(parameters[0]))
        {
            command = parameters[0];
            int pathOffset = command == 3 ? 4 : 1;
            pathBytes = parameters.Length > pathOffset
                ? parameters.Slice(pathOffset)
                : ReadOnlySpan<byte>.Empty;
            return true;
        }

        pathBytes = parameters;
        command = LooksLikeCataloguePath(pathBytes) ? (byte)6 : (byte)1;
        return true;
    }

    /// <summary>Directory path for *CAT* cmd 6 (empty, <c>.</c>, <c>$</c>, or <c>:</c>).</summary>
    public static bool LooksLikeCataloguePath(ReadOnlySpan<byte> pathBytes)
    {
        if (pathBytes.IsEmpty)
            return true;

        string path = AcornPath.ReadCrTerminatedString(pathBytes).Trim();
        if (path.Length == 0 || path == ".")
            return true;

        return path[0] is '$' or ':';
    }

    public static byte ResolveRelativeTo(FileServerRequest request, FileServerSession session)
    {
        byte relativeTo = AcornFileHandles.DivHandle(request.Cwd);
        if (relativeTo == 0)
            relativeTo = session.CwdHandle;

        return relativeTo;
    }

    public static int GetExpectedMinReplyLength(byte command) =>
        command switch
        {
            1 => 5,
            2 => 11,
            3 => 6,
            4 => 5,
            5 => 18,
            6 => 17,
            FileServerOpcodes.GetObjectInfoCanonical => 11,
            _ => 3,
        };

    public static string FormatPathPreview(ReadOnlySpan<byte> pathBytes)
    {
        if (pathBytes.IsEmpty)
            return "(cwd)";

        return AcornPath.ReadCrTerminatedString(pathBytes).Trim();
    }
}
