//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;

namespace RetroIO.Protocols.Econet.Protocol.FileServer;

/// <summary>
/// Removes private details from file-server bytes before they reach any log.
/// </summary>
/// <remarks>
/// Login commands (<c>*I AM user password</c>) carry a plaintext password in their OSCLI parameter
/// bytes. Redaction happens at the byte level so the password is gone from every rendering — the
/// hex dump, the ASCII preview, and the parsed command text alike — and from every sink, whether
/// the console logger, the JSON audit log, or the activity recorder. A live BeebEm trace on
/// 2026-07-17 showed <c>ascii="I AM SYST MYPASSWORD"</c> in the clear; this closes that.
///
/// The mask character is <c>'*'</c> (0x2A), so a redacted login reads <c>I AM SYST **********</c> —
/// still enough to see that a login happened and for whom, with nothing secret left.
/// </remarks>
public static class EconetLogRedaction
{
    private const byte MaskByte = (byte)'*';
    private const byte Space = (byte)' ';
    private const byte CarriageReturn = 0x0D;

    // Verbs of the form "<verb> <username> <password>" — the FINAL token is a password to mask.
    // Upper-case; matched case-insensitively. SETPASS (SYST sets another user's password) has the same
    // shape as login, and PiFS logs it in the clear (fs.c:10921) — so we mask it here too. Every
    // SETPASS abbreviation down to its minimum (4: SETP) is listed, because this matcher is text-based
    // and would otherwise leak the password when the command is abbreviated.
    private static readonly string[] LoginPrefixes =
    {
        "I AM ", "IAM ", "LOGIN ", "LOGON ",
        "SETPASS ", "SETPAS ", "SETPA ", "SETP ",
    };

    // "*PASS old new" carries TWO passwords — everything after the verb is secret.
    private const string PassPrefix = "PASS ";

    /// <summary>
    /// Returns a redacted copy of a raw file-server request when it carries a secret, or null when
    /// there is nothing to redact.
    /// </summary>
    /// <param name="opcode">
    /// The file-server operation byte. Only OSCLI (fsop 0x00) can carry a login password.
    /// </param>
    /// <param name="raw">
    /// The full request bytes: 5-byte header (reply port, fsop, URD, CWD, LIB) then parameters.
    /// </param>
    /// <returns>
    /// A new array with the password bytes masked, or null when <paramref name="raw"/> is safe to
    /// log unchanged.
    /// </returns>
    public static byte[]? RedactRawRequest(byte opcode, ReadOnlySpan<byte> raw)
    {
        if (opcode != FileServerOpcodes.OsCli)
            return null;

        // Parameters (the OSCLI command text) begin at offset 5, after the fixed header.
        const int parameterStart = 5;
        if (raw.Length <= parameterStart)
            return null;

        ReadOnlySpan<byte> command = raw.Slice(parameterStart);
        if (!TryFindPasswordRange(command, out int passwordStart, out int passwordEnd))
            return null;

        byte[] copy = raw.ToArray();
        for (int i = parameterStart + passwordStart; i < parameterStart + passwordEnd; i++)
            copy[i] = MaskByte;

        return copy;
    }

    /// <summary>
    /// Returns command text with any login password masked, for the human-readable "parse" line.
    /// </summary>
    /// <param name="command">
    /// The OSCLI command text without the leading '*' and without the CR.
    /// </param>
    /// <returns>
    /// The same text with the password replaced by asterisks, or the original when there is nothing
    /// to redact.
    /// </returns>
    public static string RedactCommandText(string command)
    {
        if (string.IsNullOrEmpty(command))
            return command;

        for (int i = 0; i < LoginPrefixes.Length; i++)
        {
            string prefix = LoginPrefixes[i];
            if (command.Length >= prefix.Length &&
                string.Compare(command, 0, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                // After the verb comes the username, then a space, then the password.
                int afterVerb = prefix.Length;
                int usernameEnd = command.IndexOf(' ', afterVerb);
                if (usernameEnd < 0)
                    return command; // username only, no password present

                int passwordStart = usernameEnd + 1;
                while (passwordStart < command.Length && command[passwordStart] == ' ')
                    passwordStart++;

                if (passwordStart >= command.Length)
                    return command; // trailing spaces only

                return command.Substring(0, passwordStart) + new string('*', command.Length - passwordStart);
            }
        }

        // *PASS old new — both passwords are secret, so mask everything after the verb.
        if (command.Length >= PassPrefix.Length &&
            string.Compare(command, 0, PassPrefix, 0, PassPrefix.Length, StringComparison.OrdinalIgnoreCase) == 0)
        {
            int argStart = PassPrefix.Length;
            while (argStart < command.Length && command[argStart] == ' ')
                argStart++;

            if (argStart >= command.Length)
                return command;

            return command.Substring(0, argStart) + new string('*', command.Length - argStart);
        }

        return command;
    }

    // Locates the password token within OSCLI command bytes: the run after "<verb> <username> ".
    private static bool TryFindPasswordRange(ReadOnlySpan<byte> command, out int start, out int end)
    {
        start = 0;
        end = 0;

        for (int i = 0; i < LoginPrefixes.Length; i++)
        {
            string prefix = LoginPrefixes[i];
            if (!StartsWithIgnoreCase(command, prefix))
                continue;

            int index = prefix.Length;

            // Skip the username token up to the next space.
            while (index < command.Length && command[index] != Space && command[index] != CarriageReturn)
                index++;

            // A space must separate username and password; otherwise there is no password.
            if (index >= command.Length || command[index] != Space)
                return false;

            // Skip the separating spaces.
            while (index < command.Length && command[index] == Space)
                index++;

            // The password runs to the CR or end of buffer.
            int passwordStart = index;
            while (index < command.Length && command[index] != CarriageReturn)
                index++;

            if (index <= passwordStart)
                return false; // empty password

            start = passwordStart;
            end = index;
            return true;
        }

        // *PASS old new — mask everything after the verb (both passwords are secret).
        if (StartsWithIgnoreCase(command, PassPrefix))
        {
            int index = PassPrefix.Length;
            while (index < command.Length && command[index] == Space)
                index++;

            int passwordStart = index;
            while (index < command.Length && command[index] != CarriageReturn)
                index++;

            if (index <= passwordStart)
                return false;

            start = passwordStart;
            end = index;
            return true;
        }

        return false;
    }

    private static bool StartsWithIgnoreCase(ReadOnlySpan<byte> data, string prefix)
    {
        if (data.Length < prefix.Length)
            return false;

        for (int i = 0; i < prefix.Length; i++)
        {
            char actual = char.ToUpperInvariant((char)data[i]);
            if (actual != prefix[i])
                return false;
        }

        return true;
    }
}
