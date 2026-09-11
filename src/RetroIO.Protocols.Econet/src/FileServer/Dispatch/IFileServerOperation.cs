//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.FileServer.Dispatch;

/// <summary>
/// Everything one file-server operation (fsop) implementation needs, gathered once by the dispatcher.
/// </summary>
/// <remarks>
/// The fsop analogue of <see cref="OsCliCall"/>. Unlike OSCLI there is no <c>CommandText</c>/<c>Argument</c>
/// split — an fsop's arguments come raw from <see cref="FileServerRequest.Parameters"/> (and the URD/CWD/LIB
/// handles). The whole <see cref="Request"/> is carried, not just the decoded header, because several
/// operations read <c>request.Control</c> (the odd/even sequence bit, e.g. GetByte/PutByte) and
/// <c>request.Sequence</c> (bulk packet numbering).
/// </remarks>
public readonly struct FsOpCall
{
    /// <summary>
    /// Creates a call context.
    /// </summary>
    /// <param name="request">
    /// The inbound AUN message, carrying the client's net and station, the return path, and the
    /// AUN control/sequence bytes some operations need.
    /// </param>
    /// <param name="fsRequest">
    /// The decoded file-server request header: opcode, reply port, handles, and raw parameters.
    /// </param>
    /// <param name="session">
    /// The caller's session. Guaranteed non-null when the operation declares
    /// <see cref="IFileServerOperation.RequiresLogin"/>; the dispatcher rejects the request otherwise.
    /// </param>
    /// <param name="context">
    /// Server-wide services: filesystem, discs, users, sessions, handles, bulk transfers, clock.
    /// </param>
    public FsOpCall(
        EconetMessage request,
        FileServerRequest fsRequest,
        FileServerSession? session,
        FileServerContext context)
    {
        Request = request;
        FsRequest = fsRequest;
        Session = session;
        Context = context;
    }

    /// <summary>
    /// The inbound AUN message. Carries the client net/station, the return path, and the AUN
    /// control byte (odd/even sequence bit) and sequence number some operations depend on.
    /// </summary>
    public readonly EconetMessage Request;

    /// <summary>
    /// The decoded file-server request header: opcode, reply port, URD/CWD/LIB handles, and the raw
    /// parameter bytes after the 5-byte header.
    /// </summary>
    public readonly FileServerRequest FsRequest;

    /// <summary>
    /// The caller's session. Guaranteed non-null when the operation declares
    /// <see cref="IFileServerOperation.RequiresLogin"/>.
    /// </summary>
    public readonly FileServerSession? Session;

    /// <summary>
    /// Server-wide services: filesystem, discs, users, sessions, handles, bulk transfers, and clock.
    /// </summary>
    public readonly FileServerContext Context;
}

/// <summary>
/// One file-server operation, keyed by its fsop byte. Implementations live one-per-file under
/// <c>FileServer/Operations/</c> and are placed into a direct-indexed <c>[256]</c> table by the
/// composition root.
/// </summary>
/// <remarks>
/// This is the fsop counterpart of <see cref="IOsCliCommand"/>, and the destination the
/// <see cref="OsCliCommandRegistry"/> doc-comment promised: because an fsop is identified by a unique
/// opcode byte (not a text prefix), dispatch is a single array index — there is no matcher and no
/// linear scan. That is why this interface has no <c>Name</c> or <c>MinimumAbbreviation</c>. The
/// dispatcher owns the login/system gates and the AUN reply framing; the implementation owns only the
/// work and returns the reply payload.
/// </remarks>
public interface IFileServerOperation
{
    /// <summary>
    /// The fsop byte this operation serves. Used directly as the table index, so it must be unique.
    /// </summary>
    byte Opcode { get; }

    /// <summary>
    /// False only for operations usable before authentication. When true the dispatcher answers an
    /// unauthenticated caller with "Who are you ?" and never calls <see cref="Execute"/>, so
    /// <see cref="FsOpCall.Session"/> is non-null inside it.
    /// </summary>
    bool RequiresLogin { get; }

    /// <summary>
    /// True for operations only a system-privileged user may run (e.g. set-RTC); the dispatcher
    /// answers everyone else with "Insufficient access".
    /// </summary>
    bool RequiresSystem { get; }

    /// <summary>
    /// Performs the operation and returns the reply payload bytes.
    /// </summary>
    /// <param name="call">
    /// Everything the operation needs: the request, the decoded header, the session, and services.
    /// </param>
    /// <returns>
    /// The reply payload; the dispatcher wraps it into an AUN message with the correct control byte.
    /// </returns>
    byte[] Execute(in FsOpCall call);
}
