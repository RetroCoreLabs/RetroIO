//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Econet.FileServer.Dispatch;

/// <summary>
/// Matches a typed OSCLI command against a registered command name, honouring Acorn's dot
/// abbreviations (<c>*CA.</c> for <c>*CAT</c>, <c>*I.</c> for <c>*INFO</c>), and splits off the
/// argument text.
/// </summary>
/// <remarks>
/// This is the single place the abbreviation rule is expressed. Every registered command gets
/// correct abbreviation handling for free by declaring a minimum length, instead of each dispatch
/// arm re-inventing (and mis-inventing) its own prefix test.
///
/// <para><b>The rule, as observable behaviour</b></para>
/// Applied in order:
///  - Scan for a word boundary: the first space or dot at or after index
///    <c>minimumAbbreviation</c>. Starting the scan at the minimum rather than at zero
///    is load-bearing. If neither is found, the boundary is end-of-text.
///  - The typed word is everything before that boundary. The argument is everything after it, with
///    leading spaces skipped.
///  - The word must be a case-insensitive prefix of the command name, and then either be exactly as
///    long as that name (a full, unabbreviated match), or be dot-terminated and at least
///    <c>minimumAbbreviation</c> characters long (an abbreviation).
/// <para><b>Consequences</b></para>
/// Each is covered by a test:
///  - A bare prefix is not a match. <c>*CA</c> does not select CAT; only <c>*CA.</c> does, because
///    Acorn abbreviations require the dot.
///  - Command names may contain spaces, such as "I AM". That works only because the boundary scan
///    starts at the minimum length and therefore skips the space inside the name.
///  - <c>*I.</c> selects INFO (minimum 1), not the login command "I AM" (minimum 2). For login the
///    scan starts at index 2 of "I.", already the end, so the word stays "I." and comparing it
///    against "I AM" pits '.' (0x2E) against ' ' (0x20), which cannot match. INFO then splits "I."
///    at the dot, leaving "I": a prefix of "INFO", dot-terminated, length 1, meeting its minimum of
///    1. The collision resolves itself with no special-casing.
/// <para><b>Not this method's job</b></para>
/// Registration order also decides matching, and belongs to
/// <see cref="OsCliCommandRegistry"/>. This method answers only whether one given command matches.
/// </remarks>
public static class OsCliCommandMatcher
{
    /// <summary>
    /// Tests a typed OSCLI command against one command name.
    /// </summary>
    /// <param name="typed">
    /// The command text as typed, without the leading '*' and without the terminating CR.
    /// </param>
    /// <param name="commandName">
    /// The full command name, such as "INFO" or "I AM". May contain spaces.
    /// </param>
    /// <param name="minimumAbbreviation">
    /// Shortest accepted abbreviation, in characters. 1 means <c>*I.</c> selects this command.
    /// Values below 1 are treated as 1; a value equal to the length of
    /// <paramref name="commandName"/> means it can only be typed in full.
    /// </param>
    /// <param name="argument">
    /// The text after the command word, with leading spaces removed; empty when there is none.
    /// </param>
    /// <returns>
    /// True when <paramref name="typed"/> selects <paramref name="commandName"/>.
    /// </returns>
    public static bool TryMatch(string typed, string commandName, int minimumAbbreviation, out string argument)
    {
        argument = string.Empty;

        if (string.IsNullOrEmpty(typed) || string.IsNullOrEmpty(commandName))
            return false;

        if (minimumAbbreviation < 1)
            minimumAbbreviation = 1;

        // Step 1 — find the word boundary: the earliest space or dot at or after the minimum.
        // Scanning from the minimum (not from 0) is what lets a command name contain a space.
        int boundary = typed.Length;
        int scanFrom = minimumAbbreviation;
        if (scanFrom < typed.Length)
        {
            for (int i = scanFrom; i < typed.Length; i++)
            {
                char c = typed[i];
                if (c == ' ' || c == '.')
                {
                    boundary = i;
                    break;
                }
            }
        }

        // Step 2 — split into the typed word and its argument.
        string word = typed.Substring(0, boundary);
        if (word.Length == 0)
            return false;

        if (boundary < typed.Length)
        {
            int argStart = boundary + 1; // skip the separator itself
            while (argStart < typed.Length && typed[argStart] == ' ')
                argStart++;

            argument = argStart < typed.Length ? typed.Substring(argStart) : string.Empty;
        }

        // Step 3 — the word must be a case-insensitive prefix of the command name. A word longer
        // than the name cannot be a prefix of it.
        if (word.Length > commandName.Length)
            return false;

        if (string.Compare(word, 0, commandName, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;

        // Full-length match — no dot required.
        if (word.Length == commandName.Length)
            return true;

        // Shorter than the name: only a dot-terminated abbreviation of sufficient length counts.
        bool dotTerminated = boundary < typed.Length && typed[boundary] == '.';
        return dotTerminated && word.Length >= minimumAbbreviation;
    }
}
