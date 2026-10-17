//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;

namespace RetroIO.Protocols.Econet.Protocol.FileServer;

/// <summary>
/// Formats Level-3 file server request bytes for activity log / diagnostics.
/// </summary>
public static class FileServerPacketFormatter
{
    /// <summary>
    /// Formats the reply port, the opcode and the handle bytes of a request as text, in hex.
    /// </summary>
    /// <param name="request">
    /// The request.
    /// </param>
    /// <returns>
    /// The text. A request shorter than 2 bytes gives its length and a hex dump of the bytes.
    /// </returns>
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

    /// <summary>
    /// Formats a request as text with its header, parameters in hex and as characters, and the raw bytes.
    /// </summary>
    /// <param name="request">
    /// The request.
    /// </param>
    /// <param name="maxHexBytes">
    /// The largest number of bytes shown for each hex dump.
    /// </param>
    /// <returns>
    /// The text.
    /// </returns>
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

    /// <summary>
    /// Formats a raw request payload as text, in the same way as a request.
    /// </summary>
    /// <param name="payload">
    /// The payload bytes.
    /// </param>
    /// <returns>
    /// The text, or empty payload for an empty payload.
    /// </returns>
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

    /// <summary>
    /// Formats bytes as hex pairs separated by spaces.
    /// </summary>
    /// <param name="data">
    /// The bytes.
    /// </param>
    /// <param name="maxBytes">
    /// The largest number of bytes shown. A count of the bytes left out is added after them.
    /// </param>
    /// <returns>
    /// The text, or (none) for empty data.
    /// </returns>
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
