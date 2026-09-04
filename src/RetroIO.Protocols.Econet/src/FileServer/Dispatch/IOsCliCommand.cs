//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.FileServer.Dispatch;

/// <summary>
/// Everything one OSCLI command implementation needs, gathered once by the dispatcher.
/// </summary>
/// <remarks>
/// Passing this instead of the old (request, fsRequest, command, session) parameter quartet keeps
/// command signatures uniform, which is what lets them live in a table. <see cref="Argument"/> is
/// already split out by <see cref="OsCliCommandMatcher"/>, so no command re-parses the command text.
/// </remarks>
public readonly struct OsCliCall
{
    /// <summary>
    /// Creates a call context.
    /// </summary>
    /// <param name="request">
    /// The inbound AUN message.
    /// </param>
    /// <param name="fsRequest">
    /// The decoded file-server request header.
    /// </param>
    /// <param name="session">
    /// The caller's session, or null when not logged in.
    /// </param>
    /// <param name="context">
    /// Server-wide services.
    /// </param>
    /// <param name="commandText">
    /// The command text as typed, without the leading '*' and without the CR.
    /// </param>
    /// <param name="argument">
    /// The argument text already split out by <see cref="OsCliCommandMatcher"/>.
    /// </param>
    public OsCliCall(
        EconetMessage request,
        FileServerRequest fsRequest,
        FileServerSession? session,
        FileServerContext context,
        string commandText,
        string argument)
    {
        Request = request;
        FsRequest = fsRequest;
        Session = session;
        Context = context;
        CommandText = commandText;
        Argument = argument;
    }

    /// <summary>
    /// The inbound AUN message, carrying the client's net and station and the return path.
    /// </summary>
    public readonly EconetMessage Request;

    /// <summary>
    /// The decoded file-server request header: reply port, handles, and raw parameters.
    /// </summary>
    public readonly FileServerRequest FsRequest;

    /// <summary>
    /// The caller's session. Guaranteed non-null when the command declares
    /// <see cref="IOsCliCommand.RequiresLogin"/>; the dispatcher rejects the request otherwise.
    /// </summary>
    public readonly FileServerSession? Session;

    /// <summary>
    /// Server-wide services: filesystem, discs, users, sessions, and the path resolver.
    /// </summary>
    public readonly FileServerContext Context;

    /// <summary>
    /// The full command text as typed, without the leading '*' and without the CR.
    /// </summary>
    public readonly string CommandText;

    /// <summary>
    /// The argument text following the command word, leading spaces removed; empty when absent.
    /// Already split by <see cref="OsCliCommandMatcher"/>.
    /// </summary>
    public readonly string Argument;
}

/// <summary>
/// One OSCLI ('*') command. Implementations live one-per-file under <c>FileServer/Commands/</c> and
/// are registered, in order, by the composition root.
/// </summary>
/// <remarks>
/// This replaces arms of a growing if/else chain. The chain conflated three concerns in every arm —
/// name matching, the login check, and the actual work — so each new command re-implemented all
/// three and could get any of them subtly wrong. Here the dispatcher owns matching and the access
/// checks, and the implementation owns only the work.
/// </remarks>
public interface IOsCliCommand
{
    /// <summary>
    /// The full command name in upper case, e.g. "INFO". May contain a space, e.g. "I AM".
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Shortest accepted dot abbreviation, in characters. 1 means "*I." selects this command; a
    /// value equal to <see cref="Name"/>'s length means it can only be typed in full.
    /// </summary>
    int MinimumAbbreviation { get; }

    /// <summary>
    /// False only for commands usable before authentication (login). When true the dispatcher
    /// answers an unauthenticated caller with "Who are you ?" and never calls
    /// <see cref="Execute"/>, so <see cref="OsCliCall.Session"/> is non-null inside it.
    /// </summary>
    bool RequiresLogin { get; }

    /// <summary>
    /// True for commands only a system-privileged user may run; the dispatcher answers everyone
    /// else with "Insufficient access".
    /// </summary>
    bool RequiresSystem { get; }

    /// <summary>
    /// Performs the command and returns the reply payload bytes.
    /// </summary>
    /// <param name="call">
    /// Everything the command needs, including the already-split argument.
    /// </param>
    /// <returns>
    /// The reply payload; the dispatcher wraps it into an AUN message.
    /// </returns>
    byte[] Execute(in OsCliCall call);
}
