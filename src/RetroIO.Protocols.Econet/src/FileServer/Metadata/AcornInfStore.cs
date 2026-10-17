//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Globalization;
using System.Text;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Protocols.Econet.FileServer.Metadata;

/// <summary>
/// Reads Acorn .inf sidecar files (load/exec/length attributes).
/// </summary>
public static class AcornInfStore
{
    public static bool TryReadAttributes(IFileSystemBackend fileSystem, string unixPath, out uint load, out uint exec, out int length, out byte access)
    {
        load = 0;
        exec = 0;
        length = 0;
        access = 0x33;

        if (fileSystem.FileExists(unixPath))
        {
            System.IO.FileInfo? info = fileSystem.GetFileInfo(unixPath);
            length = (int)(info?.Length ?? 0);
        }

        string infPath = unixPath + ",inf";
        if (!fileSystem.FileExists(infPath))
            return fileSystem.FileExists(unixPath);

        byte[] buffer;
        using (FileStream stream = fileSystem.OpenFile(infPath, FileMode.Open, FileAccess.Read))
        {
            buffer = new byte[stream.Length];
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read < buffer.Length)
                Array.Resize(ref buffer, read);
        }

        string text = Encoding.ASCII.GetString(buffer);
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
                continue;

            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            string key = line.Substring(0, eq).Trim().ToLowerInvariant();
            string value = line.Substring(eq + 1).Trim();

            if (key == "load" && TryParseAcornHex(value, out uint parsedLoad))
                load = parsedLoad;
            else if (key == "exec" && TryParseAcornHex(value, out uint parsedExec))
                exec = parsedExec;
            else if (key == "length" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLength))
                length = parsedLength;
            else if (key == "access" && TryParseAcornHex(value, out uint parsedAccess))
                access = (byte)(parsedAccess & 0xFF);
        }

        return true;
    }

    public static void WriteAttributes(IFileSystemBackend fileSystem, string unixPath, uint load, uint exec, int length, byte access)
    {
        string infPath = unixPath + ",inf";
        StringBuilder builder = new StringBuilder();
        builder.Append("LOAD=");
        builder.Append(load.ToString("X8", CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.Append("EXEC=");
        builder.Append(exec.ToString("X8", CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.Append("LENGTH=");
        builder.Append(length.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.Append("ACCESS=");
        builder.Append(access.ToString("X2", CultureInfo.InvariantCulture));
        builder.AppendLine();

        byte[] bytes = Encoding.ASCII.GetBytes(builder.ToString());
        using FileStream stream = fileSystem.OpenFile(infPath, FileMode.Create, FileAccess.Write);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static bool TryParseAcornHex(string value, out uint result)
    {
        result = 0;
        if (value.StartsWith('&'))
            value = value.Substring(1);

        return uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
    }
}
