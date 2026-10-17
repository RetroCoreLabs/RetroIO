//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;

namespace RetroIO.Protocols.Econet.Common;

/// <summary>
/// Acorn path parsing and Unix path mapping for PiFS-style layouts.
/// </summary>
public static class AcornPath
{
    /// <summary>
    /// Reads an ASCII string up to the first carriage return or the end of the data.
    /// </summary>
    /// <param name="data">
    /// The bytes to read from.
    /// </param>
    /// <returns>
    /// The text before the first carriage return, not including it.
    /// </returns>
    public static string ReadCrTerminatedString(ReadOnlySpan<byte> data)
    {
        int length = 0;
        while (length < data.Length && data[length] != 0x0D)
            length++;

        return Encoding.ASCII.GetString(data.Slice(0, length));
    }

    /// <summary>
    /// Trims a command line and converts it to upper case.
    /// </summary>
    /// <param name="command">
    /// The command line.
    /// </param>
    /// <returns>
    /// The trimmed upper case command.
    /// </returns>
    public static string NormalizeCommand(string command)
    {
        return command.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Trims a string and removes one pair of double quotes around it.
    /// </summary>
    /// <param name="value">
    /// The text to clean.
    /// </param>
    /// <returns>
    /// The trimmed text without the surrounding quotes.
    /// </returns>
    public static string StripQuotes(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            return trimmed.Substring(1, trimmed.Length - 2);

        return trimmed;
    }

    /// <summary>
    /// Parses a login command of the form I AM, IAM, LOGIN or LOGON followed by a user name and an optional password.
    /// </summary>
    /// <param name="command">
    /// The command line.
    /// </param>
    /// <param name="username">
    /// Receives the user name padded to 10 characters, or an empty string when parsing fails.
    /// </param>
    /// <param name="password">
    /// Receives the password padded to 10 characters, or an empty string when parsing fails.
    /// </param>
    /// <returns>
    /// True when the command starts with one of the four login words, otherwise false.
    /// </returns>
    public static bool TryParseIam(string command, out string username, out string password)
    {
        username = string.Empty;
        password = string.Empty;

        string normalized = NormalizeCommand(command);
        string prefix = string.Empty;
        if (normalized.StartsWith("I AM ", StringComparison.Ordinal))
            prefix = "I AM ";
        else if (normalized.StartsWith("IAM ", StringComparison.Ordinal))
            prefix = "IAM ";
        else if (normalized.StartsWith("LOGIN ", StringComparison.Ordinal))
            prefix = "LOGIN ";
        else if (normalized.StartsWith("LOGON ", StringComparison.Ordinal))
            prefix = "LOGON ";
        else
            return false;

        string remainder = command.Substring(prefix.Length).Trim();
        int spaceIndex = remainder.IndexOf(' ');
        if (spaceIndex < 0)
        {
            username = PadUsername(remainder);
            password = PadPassword(string.Empty);
            return true;
        }

        username = PadUsername(remainder.Substring(0, spaceIndex));
        password = PadPassword(remainder.Substring(spaceIndex + 1).Trim());
        return true;
    }

    /// <summary>
    /// Cuts a user name to 10 characters and pads it with spaces.
    /// </summary>
    /// <param name="value">
    /// The user name.
    /// </param>
    /// <returns>
    /// The name cut to 10 characters and padded on the right with spaces to 10 characters.
    /// </returns>
    public static string PadUsername(string value)
    {
        if (value.Length > 10)
            value = value.Substring(0, 10);
        return value.PadRight(10, ' ');
    }

    /// <summary>
    /// Compares two user names after trimming and padding them to 10 characters, ignoring case.
    /// </summary>
    /// <param name="left">
    /// The first user name.
    /// </param>
    /// <param name="right">
    /// The second user name.
    /// </param>
    /// <returns>
    /// True when the two padded names are equal ignoring case.
    /// </returns>
    public static bool UsernamesEqual(string left, string right)
    {
        return string.Equals(PadUsername(left.Trim()), PadUsername(right.Trim()), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Cuts a password to 10 characters and pads it with spaces.
    /// </summary>
    /// <param name="value">
    /// The password text.
    /// </param>
    /// <returns>
    /// The password cut to 10 characters and padded on the right with spaces to 10 characters.
    /// </returns>
    public static string PadPassword(string value)
    {
        if (value.Length > 10)
            value = value.Substring(0, 10);
        return value.PadRight(10, ' ');
    }

    /// <summary>
    /// Reports whether the Acorn 10-character password field is empty, meaning spaces only.
    /// </summary>
    /// <param name="paddedPassword">
    /// The space-padded 10-character password field.
    /// </param>
    /// <returns>
    /// True when <paramref name="paddedPassword"/> holds no characters other than spaces.
    /// </returns>
    public static bool IsBlankPassword(string paddedPassword) =>
        string.IsNullOrEmpty(paddedPassword.TrimEnd(' '));

    /// <summary>
    /// Combines a relative Acorn path onto a directory Acorn path, using dot notation.
    /// </summary>
    /// <param name="baseAcornPath">
    /// The directory path to combine onto.
    /// </param>
    /// <param name="relativePath">
    /// The relative path. An absolute path, starting ':' or '$', replaces
    /// <paramref name="baseAcornPath"/> entirely.
    /// </param>
    /// <returns>
    /// The combined Acorn path.
    /// </returns>
    public static string CombineAcorn(string baseAcornPath, string relativePath)
    {
        string relative = relativePath.Trim();
        if (string.IsNullOrEmpty(relative) || relative == ".")
            return baseAcornPath;

        if (relative.StartsWith(':') || relative.StartsWith('$'))
            return relative;

        if (baseAcornPath.EndsWith('$') && !relative.StartsWith('.'))
            return baseAcornPath + "." + relative;

        if (baseAcornPath.EndsWith('.'))
            return baseAcornPath + relative;

        return baseAcornPath + "." + relative;
    }

    /// <summary>
    /// Maximum disc-name length. PiFS compiles its disc-name regex as <c>^(FSREGEX{1,16})</c>
    /// (utilities/fs.c:3409), so a disc name is 1-16 characters — never zero.
    /// </summary>
    private const int MaxDiscNameLength = 16;

    /// <summary>
    /// Split and normalise a <c>:</c>-prefixed (disc-qualified) Acorn path, byte-faithfully to PiFS
    /// <c>fs_normalize_path_wildcard</c> (utilities/fs.c:2377-2406).
    /// </summary>
    /// <remarks>
    /// <para><b>Rules</b></para>
    /// The reference behaviour this reproduces:
    ///  - A disc name is 1 to 16 characters, so it can never be empty. A bare <c>":"</c> names nothing
    ///    and is rejected with <c>FS_PATH_ERR_NODISC</c> (fs.c:2399); it never resolves to a directory.
    ///  - The disc name must be followed by a dot or end-of-string, else <c>FS_PATH_ERR_FORMAT</c>
    ///    (fs.c:2402-2406).
    ///  - <c>":DISC"</c> with nothing after it becomes <c>"$"</c>, the disc root (fs.c:2387).
    ///  - <c>":DISC.X"</c> becomes <c>"$.X"</c>, while an explicit <c>":DISC.$..."</c> is kept as-is
    ///    (fs.c:2384-2394).
    /// <para><b>Why this exists</b></para>
    /// Accepting a bare <c>":"</c> let <c>*DIR :</c> set the current directory to the raw string
    /// <c>":"</c>, which resolved to the disc container directory above the user's root, leaked the
    /// host folder name to the client, and trapped the session: the <c>$</c> clamp in
    /// <see cref="RetroIO.Protocols.Econet.FileServer.Handlers.OsCliCommandHandler"/> only fires on
    /// paths containing <c>'$'</c>, and <c>":"</c> never did, so <c>*DIR ^</c> could not escape.
    /// Found via BeebEm on 2026-07-17.
    /// </remarks>
    /// <param name="acornPath">
    /// A path beginning with ':'.
    /// </param>
    /// <param name="discName">
    /// The parsed disc name; never empty on success.
    /// </param>
    /// <param name="rootedRemainder">
    /// The '$'-rooted path within that disc, or "$" for the disc root itself.
    /// </param>
    /// <returns>
    /// False when the disc name in <paramref name="acornPath"/> is missing, longer than 16
    /// characters, or not terminated by a dot.
    /// </returns>
    public static bool TryParseDiscPath(string acornPath, out string discName, out string rootedRemainder)
    {
        discName = string.Empty;
        rootedRemainder = string.Empty;

        string path = acornPath.Trim();
        if (path.Length < 2 || path[0] != ':')
            return false; // A bare ":" (length 1) has no disc name -> PiFS FS_PATH_ERR_NODISC.

        // Disc name runs to the first '.' or to end-of-string.
        int dot = path.IndexOf('.', 1);
        int nameEnd = dot < 0 ? path.Length : dot;
        int nameLength = nameEnd - 1;
        if (nameLength < 1 || nameLength > MaxDiscNameLength)
            return false; // Zero-length (":" or ":.") or longer than PiFS's {1,16} -> reject.

        discName = path.Substring(1, nameLength);

        if (dot < 0)
        {
            // ":DISC" with nothing after it -> the disc root, i.e. the residual path becomes "$".
            rootedRemainder = "$";
            return true;
        }

        string remainder = path.Substring(dot + 1);
        if (remainder.Length == 0)
            return false; // ":DISC." with nothing after the dot is malformed.

        // An explicit '$' is kept; anything else is rooted by inserting "$.".
        rootedRemainder = remainder[0] == '$' ? remainder : "$." + remainder;
        return true;
    }

    /// <summary>
    /// Converts a fully-qualified Acorn path into a host path relative to a disc root.
    /// </summary>
    /// <param name="acornFullPath">
    /// The Acorn path, optionally carrying a ':' disc specifier and a '$' root marker.
    /// </param>
    /// <returns>
    /// The path relative to the disc root, with Acorn dot separators converted to host separators.
    /// </returns>
    public static string AcornFullToUnixRelative(string acornFullPath)
    {
        string path = acornFullPath.Trim();
        if (path.StartsWith(':'))
        {
            int dot = path.IndexOf('.');
            if (dot >= 0)
                path = path.Substring(dot + 1);
        }

        if (path.StartsWith('$'))
            path = path.Substring(1);

        if (path.StartsWith('.'))
            path = path.Substring(1);

        return AcornToUnixRelative(path);
    }

    /// <summary>
    /// Encode Acorn/ADFS day and month-year bytes from a timestamp, byte-for-byte matching PiFS
    /// <c>fs_date_to_two_bytes</c> (utilities/fs.c:1143) with its DEFAULT <c>fs_sevenbitbodge = 1</c>
    /// (utilities/econet-hpbridge.c:9321 — only disabled by an explicit CLI option). This is the standard
    /// Acorn date packing:
    ///  - 7-bit year relative to 1981: <c>yearBits = year - 1981</c>.
    ///  - <c>day byte = (day AND 0x1f) OR ((yearBits AND 0x70) shifted left by 1)</c>, so the top 3 year bits
    ///    ride in day bits 5-7.
    ///  - <c>monthyear byte = (month AND 0x0f) OR ((yearBits AND 0x0f) shifted left by 4)</c>, so the month
    ///    sits in the low nibble and the low 4 year bits in the high nibble.
    /// NOTE: the previous RetroFS packing put month in the HIGH nibble and a 2-digit year in the LOW
    /// nibble (no year bits in the day byte) — the opposite nibble order and wrong epoch, so it did not
    /// match PiFS. Fixed 2026-07-01.
    /// </summary>
    public static void EncodeFileDate(DateTime utc, out byte day, out byte monthYear)
    {
        // Clamp the day into the 5-bit field; years before 1981 collapse to yearBits 0 (Acorn epoch).
        int dayOfMonth = Math.Clamp(utc.Day, 1, 31) & 0x1F;
        int yearBits = utc.Year <= 1981 ? 0 : (utc.Year - 1981) & 0x7F;

        day = (byte)(dayOfMonth | ((yearBits & 0x70) << 1));
        monthYear = (byte)((utc.Month & 0x0F) | ((yearBits & 0x0F) << 4));
    }

    /// <summary>
    /// Converts an Acorn dot-separated path to a host relative path.
    /// </summary>
    /// <param name="acornPath">
    /// The Acorn path. A leading colon is removed.
    /// </param>
    /// <returns>
    /// The path with dots replaced by slashes, the pound sign replaced by a hash sign and leading and trailing slashes removed. Returns an empty string for an empty path or a single dot.
    /// </returns>
    public static string AcornToUnixRelative(string acornPath)
    {
        if (string.IsNullOrWhiteSpace(acornPath) || acornPath == ".")
            return string.Empty;

        string path = acornPath.Trim();
        if (path.StartsWith(':'))
            path = path.Substring(1);

        path = path.Replace('.', '/');
        path = path.Replace('£', '#');
        return path.Trim('/');
    }

    /// <summary>
    /// Pads or cuts a name to the 10 characters of an Acorn catalogue name.
    /// </summary>
    /// <param name="unixName">
    /// The host file or directory name.
    /// </param>
    /// <param name="isDirectory">
    /// Not used.
    /// </param>
    /// <returns>
    /// The name cut to 10 characters and padded on the right with spaces to 10 characters.
    /// </returns>
    public static string ToAcornCatalogName(string unixName, bool isDirectory)
    {
        string name = unixName;
        if (name.Length > 10)
            name = name.Substring(0, 10);
        return name.PadRight(10, ' ');
    }
}
