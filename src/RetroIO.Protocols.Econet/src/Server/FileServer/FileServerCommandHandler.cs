//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.Extensions.Logging;
using RetroIO.Protocols.Econet.FileServer.Discs;
using RetroIO.Protocols.Econet.FileServer.Handlers;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.FileServer;

/// <summary>
/// Handles Level-3 file server operations on Econet port 0x99.
/// </summary>
public sealed class FileServerCommandHandler
{
    /// <summary>
    /// The version text the server reports when none is configured.
    /// </summary>
    public const string DefaultVersionString = "3 RetroFS Econet FS 1.0";

    private readonly FileServerContext _context;
    private readonly OsCliCommandHandler _osCli;
    private readonly ExamineCommandHandler _examine;
    private readonly FsOpCommandHandler _fsOp;
    private readonly FileIoCommandHandler _fileIo;
    private readonly PiBridgeCommandHandler _piBridge;
    private readonly SjOpcodeHandler _sj;
    private readonly FreeSpaceCommandHandler _freeSpace;
    private readonly ILogger<FileServerCommandHandler> _logger;

    /// <summary>
    /// Creates the handler and the handlers for the individual opcodes.
    /// </summary>
    /// <param name="context">
    /// The shared file server context.
    /// </param>
    /// <param name="logger">
    /// The logger.
    /// </param>
    public FileServerCommandHandler(FileServerContext context, ILogger<FileServerCommandHandler> logger)
    {
        _context = context;
        _logger = logger;
        _osCli = new OsCliCommandHandler(context);
        _examine = new ExamineCommandHandler(context);
        _fsOp = new FsOpCommandHandler(context);
        _fileIo = new FileIoCommandHandler(context);
        _piBridge = new PiBridgeCommandHandler(context);
        _sj = new SjOpcodeHandler(context);
        _freeSpace = new FreeSpaceCommandHandler(context);
    }

    /// <summary>
    /// Handles a file server request: a request that is too short is recorded as malformed and gets no reply, a station that is not logged in is refused for opcodes that need a login, and any other request goes to the handler for its opcode. The command is recorded in the activity log and the result is logged.
    /// </summary>
    /// <param name="request">
    /// The inbound message.
    /// </param>
    /// <returns>
    /// The reply message, or null when the request is too short to parse or no reply is built.
    /// </returns>
    public EconetMessage? Handle(EconetMessage request)
    {
        if (!FileServerRequest.TryParse(request.Payload, out FileServerRequest fsRequest))
        {
            _logger.LogWarning(
                "Rejected malformed file server request from {Net}.{Station} ({Length} bytes)",
                request.ClientNet,
                request.ClientStation,
                request.Payload.Length);
            EconetActivityRecorder.RecordMalformedCommand(
                _context.ActivityLog,
                request.ClientNet,
                request.ClientStation,
                request.Payload);
            return null;
        }

        EconetMessage? response;

        if (fsRequest.Opcode == FileServerOpcodes.PiBridge)
            response = _piBridge.Handle(request, fsRequest);
        else if (fsRequest.Opcode >= 64 &&
                 fsRequest.Opcode != FileServerOpcodes.PiBridge &&
                 !FileServerOpcodes.IsSjOpcode(fsRequest.Opcode))
            response = BuildReply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.Unsupported, "Unsupported"));
        else if (!FileServerOpcodes.IsAllowedWithoutLogin(fsRequest.Opcode) &&
                 !_context.Sessions.IsLoggedIn(request.ClientNet, request.ClientStation))
            response = BuildReply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.WhoAreYou, "Who are you ?"));
        else
        {
            FileServerSession? session = _context.Sessions.GetSession(request.ClientNet, request.ClientStation);
            session?.ApplyWireHandles(fsRequest);
            response = DispatchLoggedIn(request, fsRequest);
        }

        RecordCommand(request, fsRequest, response);

        if (response?.Payload != null)
        {
            FileServerSession? logSession = _context.Sessions.GetSession(request.ClientNet, request.ClientStation);
            bool success = EconetProtocolDiagnostics.EvaluateReplySuccess(fsRequest, response.Payload);

            // Normal operation: one short, redacted line — enough to see who did what and how it
            // ended, with no hex and no private detail. Failures log at Warning so they stand out.
            _logger.Log(
                success ? LogLevel.Information : LogLevel.Warning,
                "Econet FS {ClientNet}.{ClientStation} {OpcodeName} {Summary} -> {Result}",
                request.ClientNet,
                request.ClientStation,
                FileServerOpcodeNames.GetName(fsRequest.Opcode),
                EconetProtocolDiagnostics.FormatShortSummary(fsRequest),
                EconetProtocolDiagnostics.FormatShortResult(response.Payload, success));

            // Deep debugging only: the full byte-level request/reply trace, still redacted. Guarded
            // so the (expensive) hex formatting is skipped entirely unless Debug is enabled.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Econet FS {ClientNet}.{ClientStation} {OpcodeName} detail\n  REQUEST: {RequestTrace}\n  REPLY: {ReplyTrace}",
                    request.ClientNet,
                    request.ClientStation,
                    FileServerOpcodeNames.GetName(fsRequest.Opcode),
                    EconetProtocolDiagnostics.FormatRequestTrace(fsRequest, logSession),
                    EconetProtocolDiagnostics.FormatReplyTrace(fsRequest, response.Payload, success));
            }
        }

        return response;
    }

    private EconetMessage? DispatchLoggedIn(EconetMessage request, FileServerRequest fsRequest)
    {
        switch (fsRequest.Opcode)
        {
            case FileServerOpcodes.OsCli:
                return _osCli.Handle(request, fsRequest);

            case FileServerOpcodes.ReadVersion:
                return BuildReply(request, fsRequest.ReplyPort, FileServerReply.Version(_context.VersionString));

            case FileServerOpcodes.ReadDiscNames:
                return HandleReadDiscNames(request, fsRequest);

            case FileServerOpcodes.Examine:
                return _examine.Handle(request, fsRequest);

            case FileServerOpcodes.SjOperation:
            case FileServerOpcodes.SjOperationReply:
                return _sj.Handle(request, fsRequest)!;

            case FileServerOpcodes.FreeSpace:
            case FileServerOpcodes.UserFreeSpace:
                return _freeSpace.Handle(request, fsRequest)!;

            default:
                // The indexed fsop table serves every opcode that was migrated out of the old
                // MiscOpcodeHandler (now gone). Empty slots return null and fall through to the
                // FileIo handler, which owns the open/close/save/load channel opcodes. The two sets
                // are disjoint, so table lookup can never steal a FileIo opcode; anything neither
                // handles is a genuine "unsupported" opcode.
                EconetMessage? fsOpResponse = _fsOp.Handle(request, fsRequest);
                if (fsOpResponse != null)
                    return fsOpResponse;

                EconetMessage? fileIoResponse = _fileIo.Handle(request, fsRequest);
                if (fileIoResponse != null)
                    return fileIoResponse;

                return BuildReply(
                    request,
                    fsRequest.ReplyPort,
                    FileServerReply.Error(FileServerErrors.Unsupported, "FS Error"));
        }
    }

    private void RecordCommand(EconetMessage request, FileServerRequest fsRequest, EconetMessage? response)
    {
        if (_context.ActivityLog == null || response == null)
            return;

        FileServerSession? session = _context.Sessions.GetSession(request.ClientNet, request.ClientStation);
        string username = session?.Username ?? string.Empty;
        EconetActivityRecorder.RecordCommand(
            _context.ActivityLog,
            request.ClientNet,
            request.ClientStation,
            username,
            fsRequest,
            response.Payload,
            session,
            request.Sequence);
    }

    private EconetMessage HandleReadDiscNames(EconetMessage request, FileServerRequest fsRequest)
    {
        ReadOnlySpan<byte> parameters = fsRequest.Parameters;
        byte start = parameters.Length > 0 ? parameters[0] : (byte)0;
        byte count = parameters.Length > 1 ? parameters[1] : (byte)1;

        EconetUserProfile? profile = null;
        FileServerSession? session = _context.Sessions.GetSession(request.ClientNet, request.ClientStation);
        if (session != null)
            profile = _context.Users.GetProfile(session.RetroUserId);

        ushort discMask = profile?.DiscMask ?? 0xFFFF;
        IReadOnlyList<DiscEntry> visible = _context.Discs.GetVisibleDiscs(discMask, start, count);
        DiscNameEntry[] discs = new DiscNameEntry[visible.Count];
        for (int i = 0; i < visible.Count; i++)
            discs[i] = new DiscNameEntry((byte)visible[i].Index, visible[i].AcornName);

        return BuildReply(request, fsRequest.ReplyPort, FileServerReply.DiscNames(discs));
    }

    private static EconetMessage BuildReply(EconetMessage request, byte replyPort, byte[] payload)
    {
        return new EconetMessage
        {
            PacketType = AunPacketType.Unicast,
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
