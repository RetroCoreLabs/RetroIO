//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Text;

namespace RetroIO.Protocols.Econet.Protocol.FileServer;

/// <summary>
/// Formats Level-3 file server request bytes for activity log / diagnostics.
/// </summary>
public static class FileServerPacketFormatter
{
    public static string FormatRequestHeader(FileServerRequest request)
    {
        if (request.Length < 2)
            return $"len={request.Length} | raw {FormatHex(request.Raw)}";

        StringBuilder builder = new StringBuilder();
        builder.Append($"reply=&{request.ReplyPort:X2}");
        builder.Append($" fsop=&{request.Opcode:X2}");
        if (request.Length > 2)
            builder.Append($" URD=&{request.Urd:X2}");
        if (request.Length > 3)
            builder.Append($" CWD=&{request.Cwd:X2}");
        if (request.Length > 4)
            builder.Append($" LIB=&{request.Lib:X2}");
        return builder.ToString();
    }

    public static string FormatRequestDump(FileServerRequest request, int maxHexBytes = 64)
    {
        string header = FormatRequestHeader(request);
        ReadOnlySpan<byte> parameters = request.Parameters;
        if (parameters.IsEmpty)
            return $"{header} | len={request.Length} | raw {FormatHex(request.Raw, maxHexBytes)}";

        string paramHex = FormatHex(parameters, maxHexBytes);
        string paramAscii = FormatAsciiPreview(parameters);
        return $"{header} | params {paramHex} ({parameters.Length} bytes) ascii=\"{paramAscii}\" | raw {FormatHex(request.Raw, maxHexBytes)}";
    }

    public static string FormatRawPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
            return "empty payload";

        if (payload.Length < 2)
            return $"len={payload.Length} | raw {FormatHex(payload)}";

        byte replyPort = payload[0];
        byte opcode = payload[1];
        StringBuilder builder = new StringBuilder();
        builder.Append($"reply=&{replyPort:X2} fsop=&{opcode:X2}");
        if (payload.Length > 2)
            builder.Append($" URD=&{payload[2]:X2}");
        if (payload.Length > 3)
            builder.Append($" CWD=&{payload[3]:X2}");
        if (payload.Length > 4)
            builder.Append($" LIB=&{payload[4]:X2}");
        if (payload.Length > 5)
        {
            ReadOnlySpan<byte> tail = payload.Slice(5);
            builder.Append($" | params {FormatHex(tail)} ascii=\"{FormatAsciiPreview(tail)}\"");
        }

        builder.Append($" | raw {FormatHex(payload)}");
        return builder.ToString();
    }

    public static string FormatHex(ReadOnlySpan<byte> data, int maxBytes = 64)
    {
        if (data.IsEmpty)
            return "(none)";

        int count = Math.Min(data.Length, maxBytes);
        StringBuilder builder = new StringBuilder(count * 3);
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
                builder.Append(' ');
            builder.Append(data[i].ToString("X2"));
        }

        if (data.Length > maxBytes)
            builder.Append($" …+{data.Length - maxBytes}");

        return builder.ToString();
    }

    private static string FormatAsciiPreview(ReadOnlySpan<byte> data, int maxChars = 32)
    {
        if (data.IsEmpty)
            return string.Empty;

        int count = Math.Min(data.Length, maxChars);
        StringBuilder builder = new StringBuilder(count);
        for (int i = 0; i < count; i++)
        {
            byte value = data[i];
            builder.Append(value is >= 0x20 and <= 0x7E ? (char)value : '.');
        }

        if (data.Length > maxChars)
            builder.Append('…');

        return builder.ToString();
    }
}
