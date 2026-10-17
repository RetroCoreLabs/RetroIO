//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;

namespace RetroIO.Protocols.Econet.FileServer.Dispatch;

/// <summary>
/// The ordered table of OSCLI commands. Resolution is first-match-wins, in registration order.
/// </summary>
/// <remarks>
/// <para><b>Order is part of the contract</b></para>
/// Acorn command names overlap, so the order candidates are tried in decides the winner. Register
/// commands in the same relative order the server has always tried them; reordering the composition
/// root can silently change which command a given abbreviation selects.
/// <para><b>Why a linear scan</b></para>
/// Overlapping prefixes and space-bearing names such as "I AM" mean this cannot be a dictionary
/// lookup: resolution is "the first registered command that accepts this text". The list holds a few
/// dozen entries and is walked once per OSCLI packet, on a protocol carrying a handful of packets
/// per second, so the scan costs nothing measurable and keeps the order explicit. File-server
/// operations differ: they are keyed by a unique opcode byte and so get a genuine indexed table.
/// </remarks>
public sealed class OsCliCommandRegistry
{
    private readonly List<IOsCliCommand> _commands = new List<IOsCliCommand>();

    /// <summary>
    /// Number of registered commands.
    /// </summary>
    public int Count => _commands.Count;

    /// <summary>
    /// Appends a command. Earlier registrations win ties, so order matters.
    /// </summary>
    /// <param name="command">
    /// The command to append.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="command"/> is null.
    /// </exception>
    public void Register(IOsCliCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _commands.Add(command);
    }

    /// <summary>
    /// Finds the first registered command that the typed text selects.
    /// </summary>
    /// <param name="typed">
    /// Command text as typed, without the leading '*' and without the CR.
    /// </param>
    /// <param name="command">
    /// The matched command, or null when nothing matched.
    /// </param>
    /// <param name="argument">
    /// The argument text with leading spaces removed; empty when there is none.
    /// </param>
    /// <returns>
    /// False when no registered command accepts <paramref name="typed"/>; the caller decides what
    /// that means.
    /// </returns>
    public bool TryResolve(string typed, out IOsCliCommand? command, out string argument)
    {
        command = null;
        argument = string.Empty;

        for (int i = 0; i < _commands.Count; i++)
        {
            IOsCliCommand candidate = _commands[i];
            if (OsCliCommandMatcher.TryMatch(typed, candidate.Name, candidate.MinimumAbbreviation, out string parsedArgument))
            {
                command = candidate;
                argument = parsedArgument;
                return true;
            }
        }

        return false;
    }
}
