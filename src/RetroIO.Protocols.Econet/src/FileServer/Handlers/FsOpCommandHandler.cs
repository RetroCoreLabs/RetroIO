//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.FileServer.Handlers;

/// <summary>
/// Dispatches file-server operations (fsops) through the direct-indexed
/// <see cref="IFileServerOperation"/> table, centralising the login/system gates and the AUN reply
/// framing that used to be duplicated across the individual opcode handlers.
/// </summary>
/// <remarks>
/// The fsop counterpart of <c>OsCliCommandHandler</c>. An opcode with no operation registered here
/// returns null from <see cref="Handle"/>, which lets the top dispatcher fall through to
/// <c>FileIoCommandHandler</c> (the open/close/save/load channel opcodes). The strangler migration
/// that grew this table out of the old <c>MiscOpcodeHandler</c> is complete — that handler is gone and
/// every one of its opcodes is now served from this table.
/// </remarks>
public sealed class FsOpCommandHandler
{
    private readonly FileServerContext _context;
    private readonly IFileServerOperation?[] _table;

    /// <summary>
    /// Creates the handler and builds the operation table.
    /// </summary>
    /// <param name="context">
    /// The shared file server context.
    /// </param>
    public FsOpCommandHandler(FileServerContext context)
    {
        _context = context;
        _table = FsOperationTable.Build(context);
    }

    /// <summary>
    /// Serves one fsop if it is in the table; returns null (not an error) if it is not, so the caller
    /// can fall through to the legacy handlers.
    /// </summary>
    /// <param name="request">
    /// The inbound AUN message.
    /// </param>
    /// <param name="fsRequest">
    /// The decoded file-server request header.
    /// </param>
    /// <returns>
    /// The AUN reply, or null when no operation is registered for <see cref="FileServerRequest.Opcode"/>.
    /// </returns>
    public EconetMessage? Handle(EconetMessage request, FileServerRequest fsRequest)
    {
        // Direct O(1) index by opcode byte — no scan, no matcher (see IFileServerOperation remarks).
        IFileServerOperation? operation = _table[fsRequest.Opcode];
        if (operation == null)
            return null; // not handled here — the top dispatcher falls through to the legacy handlers.

        FileServerSession? session = _context.Sessions.GetSession(request.ClientNet, request.ClientStation);

        // Gates enforced ONCE, centrally — the operation body never re-checks these.
        if (operation.RequiresLogin && session == null)
            return Wrap(request, fsRequest, FileServerReply.Error(FileServerErrors.WhoAreYou, "Who are you ?"));

        if (operation.RequiresSystem && (session == null || !session.IsAdmin))
            return Wrap(request, fsRequest, FileServerReply.Error(FileServerErrors.InsufficientAccess, "Insufficient access"));

        FsOpCall call = new FsOpCall(request, fsRequest, session, _context);
        return Wrap(request, fsRequest, operation.Execute(in call));
    }

    // Opcode-aware AUN framing: a successful GetObjectInfo (0x12) reply must go out with ctrl=0, all
    // others with ctrl=0x80. This is the SAME framing the legacy Misc handler used (AunFsReply.Build),
    // NOT the flat OSCLI ctrl=0x80 — getting this wrong silently breaks *CAT/GetObjectInfo on the wire.
    private static EconetMessage Wrap(EconetMessage request, FileServerRequest fsRequest, byte[] payload) =>
        AunFsReply.Build(request, fsRequest.ReplyPort, fsRequest.Opcode, payload);
}
