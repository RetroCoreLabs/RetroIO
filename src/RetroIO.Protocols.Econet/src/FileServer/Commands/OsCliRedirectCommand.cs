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
/// An OSCLI command that resolves to a file-server operation, returning that operation's return
/// code plus the CR-terminated argument for the NFS/ANFS ROM to re-issue.
/// </summary>
/// <remarks>
/// Several '*' commands are pure redirects of this shape: <c>*DELETE</c> becomes a Delete request,
/// <c>*INFO</c> a GetObjectInfo, <c>*EX</c> an Examine, and <c>*CREATE</c>/<c>*BUILD</c> a
/// CreateFile. They differ only in name, minimum abbreviation, and the operation byte they redirect
/// to, so one parameterised class covers them all instead of a class each.
/// </remarks>
public sealed class OsCliRedirectCommand : IOsCliCommand
{
    private readonly string _name;
    private readonly int _minimumAbbreviation;
    private readonly byte _redirectOpcode;

    /// <summary>
    /// Creates a redirecting command.
    /// </summary>
    /// <param name="name">
    /// The command name, upper case.
    /// </param>
    /// <param name="minimumAbbreviation">
    /// Shortest accepted abbreviation, in characters.
    /// </param>
    /// <param name="redirectOpcode">
    /// The file-server operation return code the reply carries.
    /// </param>
    public OsCliRedirectCommand(string name, int minimumAbbreviation, byte redirectOpcode)
    {
        _name = name;
        _minimumAbbreviation = minimumAbbreviation;
        _redirectOpcode = redirectOpcode;
    }

    /// <summary>
    /// Gets the command name given to the constructor, in upper case.
    /// </summary>
    public string Name => _name;

    /// <summary>
    /// Gets the minimum abbreviation given to the constructor, in characters.
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
    /// Answers with the operation byte given to the constructor as the return code, followed by the
    /// argument, for the ROM to re-issue as that operation.
    /// </summary>
    /// <param name="call">
    /// Everything the command needs, including the already-split argument.
    /// </param>
    /// <returns>
    /// The redirect return code followed by the argument and a CR.
    /// </returns>
    public byte[] Execute(in OsCliCall call)
    {
        return FileServerReply.WithReturnCode(_redirectOpcode, Encoding.ASCII.GetBytes(call.Argument + "\r"));
    }
}
