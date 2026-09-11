//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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

    /// <inheritdoc/>
    public string Name => _name;

    /// <inheritdoc/>
    public int MinimumAbbreviation => _minimumAbbreviation;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in OsCliCall call)
    {
        return FileServerReply.WithReturnCode(_redirectOpcode, Encoding.ASCII.GetBytes(call.Argument + "\r"));
    }
}
