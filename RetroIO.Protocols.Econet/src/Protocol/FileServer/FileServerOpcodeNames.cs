//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.Protocol;

namespace RetroIO.Protocols.Econet.Protocol.FileServer;

/// <summary>
/// Human-readable names for Level-3 file server opcodes.
/// </summary>
public static class FileServerOpcodeNames
{
    public static bool TryGetOpcode(byte value, out FileServerOpcode opcode)
    {
        if (Enum.IsDefined(typeof(FileServerOpcode), value))
        {
            opcode = (FileServerOpcode)value;
            return true;
        }

        opcode = default;
        return false;
    }

    public static string GetName(byte opcode)
    {
        if (opcode == EconetPorts.FileServer)
            return "Bad fsop (Econet port 0x99)";

        return TryGetOpcode(opcode, out FileServerOpcode parsed)
            ? GetName(parsed)
            : "Unknown opcode";
    }

    public static string GetDisplayName(byte opcode)
    {
        if (opcode == EconetPorts.FileServer)
            return "Bad fsop: used Econet port 0x99 as opcode";

        string name = GetName(opcode);
        return $"{name} (0x{opcode:X2})";
    }

    /// <summary>
    /// Short label for monitor Command column.
    /// </summary>
    public static string GetMonitorCommandLabel(byte opcode, FileServerRequest? fsRequest = null)
    {
        if (fsRequest != null && fsRequest.Value.Opcode == FileServerOpcodes.OsCli)
        {
            string text = AcornPath.ReadCrTerminatedString(fsRequest.Value.Parameters).Trim();
            if (text.Length > 0)
                return FileServerOpcodeNames.GetOsCliDisplayName(text);
        }

        if (opcode == EconetPorts.FileServer)
            return "Bad fsop 0x99";

        if (TryGetOpcode(opcode, out FileServerOpcode parsed))
            return GetName(parsed);

        return $"Unknown 0x{opcode:X2}";
    }

    public static string GetName(FileServerOpcode opcode) => opcode switch
    {
        FileServerOpcode.OsCli => "OS CLI",
        FileServerOpcode.Save => "Save file",
        FileServerOpcode.Load => "Load file",
        FileServerOpcode.Examine => "Examine directory",
        FileServerOpcode.CatHeader => "Catalogue header",
        FileServerOpcode.LoadAs => "Load file as",
        FileServerOpcode.Open => "Open file",
        FileServerOpcode.Close => "Close file",
        FileServerOpcode.GetByte => "Read byte",
        FileServerOpcode.PutByte => "Write byte",
        FileServerOpcode.GetBytes => "Read bytes",
        FileServerOpcode.PutBytes => "Write bytes",
        FileServerOpcode.GetRandomAccessInfo => "Get file pointer",
        FileServerOpcode.SetRandomAccessInfo => "Set file pointer",
        FileServerOpcode.ReadDiscNames => "Read disc names",
        FileServerOpcode.ReadLoggedOnUsers => "Read logged-on users",
        FileServerOpcode.ReadTime => "Read time",
        FileServerOpcode.EndOfFile => "End of file",
        FileServerOpcode.GetObjectInfo => "Get object info",
        FileServerOpcode.SetObjectInfo => "Set object info",
        FileServerOpcode.Delete => "Delete",
        FileServerOpcode.ReadUserEnv => "Read user environment",
        FileServerOpcode.SetBootOpt => "Set boot option",
        FileServerOpcode.Bye => "Logout",
        FileServerOpcode.ReadUserInfo => "Read user info",
        FileServerOpcode.ReadVersion => "Read version",
        FileServerOpcode.FreeSpace => "Read free space",
        FileServerOpcode.CreateDir => "Create directory",
        FileServerOpcode.SetRtc => "Set clock",
        FileServerOpcode.CreateFile => "Create file",
        FileServerOpcode.UserFreeSpace => "Read user free space",
        FileServerOpcode.SetUserFreeSpace => "Set user free space",
        FileServerOpcode.ReadClientId => "Read client ID",
        FileServerOpcode.SjOperation => "SJ operation",
        FileServerOpcode.SjOperationReply => "SJ operation reply",
        FileServerOpcode.PiBridge => "PiBridge extension",
        _ => opcode.ToString(),
    };

    public static string GetOsCliDisplayName(string commandText)
    {
        string normalized = AcornPath.NormalizeCommand(commandText);
        if (normalized.Length == 0)
            return "OS CLI";

        if (normalized.StartsWith("I AM ", StringComparison.Ordinal) ||
            normalized.StartsWith("IAM ", StringComparison.Ordinal) ||
            normalized.StartsWith("LOGIN ", StringComparison.Ordinal) ||
            normalized.StartsWith("LOGON ", StringComparison.Ordinal))
        {
            return "*I AM";
        }

        if (normalized == "BYE" || normalized.StartsWith("BYE ", StringComparison.Ordinal))
            return "*BYE";

        if (normalized == "." || normalized.StartsWith("CAT", StringComparison.Ordinal))
            return normalized == "." ? "*CAT" : "*CAT";

        if (normalized.StartsWith("DIR", StringComparison.Ordinal))
            return "*DIR";

        if (normalized.StartsWith("LOAD", StringComparison.Ordinal))
            return "*LOAD";

        if (normalized.StartsWith("SAVE", StringComparison.Ordinal))
            return "*SAVE";

        if (normalized.StartsWith("DELETE", StringComparison.Ordinal) || normalized.StartsWith("DEL ", StringComparison.Ordinal))
            return "*DELETE";

        if (normalized.StartsWith("INFO", StringComparison.Ordinal))
            return "*INFO";

        if (normalized.StartsWith("EX", StringComparison.Ordinal))
            return "*EX";

        if (normalized.StartsWith("REN", StringComparison.Ordinal) || normalized.StartsWith("RENAME", StringComparison.Ordinal))
            return "*REN";

        if (normalized.StartsWith("ACCESS", StringComparison.Ordinal))
            return "*ACCESS";

        if (normalized.StartsWith("CREATE", StringComparison.Ordinal) || normalized.StartsWith("BUILD", StringComparison.Ordinal))
            return "*CREATE";

        if (normalized == "FSLIST" || normalized.StartsWith("FSLIST ", StringComparison.Ordinal))
            return "*FSLIST";

        int space = normalized.IndexOf(' ');
        string verb = space >= 0 ? normalized.Substring(0, space) : normalized;
        return "*" + verb;
    }
}
