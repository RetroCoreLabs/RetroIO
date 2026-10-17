//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.FileServer.Handlers;

/// <summary>
/// Dispatches OSCLI ('*') commands through the command registry.
/// </summary>
/// <remarks>
/// The handler owns only wiring: it resolves the command, enforces the login and system-privilege
/// gates once, and wraps the reply into an AUN message. Every command's behaviour lives in its own
/// <see cref="RetroIO.Protocols.Econet.FileServer.Dispatch.IOsCliCommand"/> under
/// <c>FileServer/Commands/</c>.
/// </remarks>
public sealed class OsCliCommandHandler
{
    private readonly FileServerContext _context;
    private readonly OsCliCommandRegistry _registry;

    /// <summary>
    /// Creates the handler and builds the command registry.
    /// </summary>
    /// <param name="context">
    /// The shared file server context.
    /// </param>
    public OsCliCommandHandler(FileServerContext context)
    {
        _context = context;
        _registry = OsCliCommandTable.Build(context);
    }

    /// <summary>
    /// Runs the star command in the request after checking the login and system privilege the command needs.
    /// </summary>
    /// <param name="request">
    /// The inbound message.
    /// </param>
    /// <param name="fsRequest">
    /// The decoded file server request.
    /// </param>
    /// <returns>
    /// The reply message. A command that is not in the registry gets the unknown command reply.
    /// </returns>
    public EconetMessage? Handle(EconetMessage request, FileServerRequest fsRequest)
    {
        string command = AcornPath.ReadCrTerminatedString(fsRequest.Parameters);
        FileServerSession? session = _context.Sessions.GetSession(request.ClientNet, request.ClientStation);

        // Every recognised '*' command — login included — resolves through the registry. The
        // dispatcher owns the access checks so no command re-implements them: RequiresLogin gates
        // on a session (login itself does not require one), and RequiresSystem gates on admin.
        if (_registry.TryResolve(command, out IOsCliCommand? resolved, out string argument))
        {
            if (resolved!.RequiresLogin && session == null)
                return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.WhoAreYou, "Who are you ?"));

            if (resolved.RequiresSystem && (session == null || !session.IsAdmin))
                return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.InsufficientAccess, "Insufficient access"));

            OsCliCall call = new OsCliCall(request, fsRequest, session, _context, command, argument);
            return Reply(request, fsRequest.ReplyPort, resolved.Execute(in call));
        }

        // Unrecognised command. A station must be logged in before we even consider running a file
        // for it, matching the original session gate.
        if (session == null)
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.WhoAreYou, "Who are you ?"));

        // PiFS (fs.c:11302 / :11327) does NOT error here — it replies with return code 0x08
        // ("Unknown command") plus the original command text, which tells the NFS/ANFS ROM to handle
        // it, i.e. to *RUN a file of that name. Returning &FE "Bad command" instead is final to the
        // ROM and breaks *OPT 4,n / SHIFT-BREAK autoboot (the ROM issues "*!BOOT" after login) as
        // well as every plain "*program" invocation. Found live in BeebEm 2026-07-17.
        return Reply(request, fsRequest.ReplyPort, FileServerReply.UnknownCommand(fsRequest.Parameters));
    }

    private static EconetMessage Reply(EconetMessage request, byte replyPort, byte[] payload)
    {
        return new EconetMessage
        {
            PacketType = Protocol.Aun.AunPacketType.Unicast,
            EconetPort = replyPort,
            Control = 0x80,
            Sequence = request.Sequence,
            Payload = payload,
            ClientAddress = request.ClientAddress,
            ClientPort = request.ClientPort,
            ClientNet = request.ClientNet,
            ClientStation = request.ClientStation,
            Transport = request.Transport,
        };
    }
}
