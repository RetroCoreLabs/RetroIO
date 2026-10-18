//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*CAT</c> (and its alias <c>*.</c>) — catalogues a directory.
/// </summary>
/// <remarks>
/// The server does not produce the listing itself. It answers with the Examine return code and the
/// path, which redirects the NFS/ANFS ROM to re-issue the request as an Examine (fsop 0x03); the
/// ROM then formats the catalogue. So this command is a redirect, not a directory read.
///
/// Registered TWICE — once as "CAT" (minimum 2, so "*CA." works) and once as "." (minimum 1), which
/// is how "*." is a synonym for "*CAT". One class, two registrations, because the two names select
/// identical behaviour.
/// </remarks>
public sealed class CatCommand : IOsCliCommand
{
    private readonly string _name;
    private readonly int _minimumAbbreviation;

    /// <summary>
    /// Creates the command under one of its names.
    /// </summary>
    /// <param name="name">
    /// Either "CAT" or ".".
    /// </param>
    /// <param name="minimumAbbreviation">
    /// 2 for "CAT", 1 for ".".
    /// </param>
    public CatCommand(string name, int minimumAbbreviation)
    {
        _name = name;
        _minimumAbbreviation = minimumAbbreviation;
    }

    /// <summary>
    /// Gets the name given to the constructor: <c>CAT</c> or <c>.</c>.
    /// </summary>
    public string Name => _name;

    /// <summary>
    /// Gets the minimum abbreviation given to the constructor: 2 for <c>CAT</c>, 1 for <c>.</c>.
    /// </summary>
    public int MinimumAbbreviation => _minimumAbbreviation;

    /// <summary>
    /// Gets <c>true</c>: the command needs a logged-in caller. The dispatcher answers an
    /// unauthenticated caller with "Who are you ?" and never calls <see cref="Execute"/>, so
    /// <see cref="OsCliCall.Session"/> is non-null inside it.
    /// </summary>
    public bool RequiresLogin => true;

    /// <summary>
    /// Gets <c>false</c>: the command is not limited to system-privileged users.
    /// </summary>
    public bool RequiresSystem => false;

    /// <summary>
    /// Answers with the Examine return code and the path, so the ROM re-issues the request as an
    /// Examine. No directory is read here.
    /// </summary>
    /// <param name="call">
    /// Everything the command needs, including the already-split argument.
    /// </param>
    /// <returns>
    /// The Examine return code followed by the argument, or <c>.</c> when there is no argument, and a CR.
    /// </returns>
    /// <remarks>
    /// A bare "*CAT" sends a literal "." as the path, meaning "the current directory". This is a
    /// known, DELIBERATE divergence carried over unchanged from the dispatch arm this replaces: the
    /// reference sends an empty path instead, so its bare-*CAT reply is 3 bytes (03 00 0D) where
    /// ours is 4 (03 00 2E 0D). It is benign — the ROM's follow-up Examine resolves "." to the CSD
    /// either way, which is why *CAT is live-verified working — and it is preserved here on purpose
    /// so this refactor changes no bytes on the wire. See the ignored divergence spec in
    /// OsCliParityTests. Fix it separately, with a BeebEm re-test, not as a side effect of a move.
    /// </remarks>
    public byte[] Execute(in OsCliCall call)
    {
        string path = call.Argument.Length > 0 ? call.Argument : ".";
        return FileServerReply.WithReturnCode(FileServerOpcodes.Examine, Encoding.ASCII.GetBytes(path + "\r"));
    }
}
