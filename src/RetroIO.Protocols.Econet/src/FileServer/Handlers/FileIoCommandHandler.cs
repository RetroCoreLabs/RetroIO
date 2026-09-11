//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Bulk;
using RetroIO.Protocols.Econet.FileServer.Handles;
using RetroIO.Protocols.Econet.FileServer.Metadata;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Security.Implementation.Auditing;

namespace RetroIO.Protocols.Econet.FileServer.Handlers;

public sealed class FileIoCommandHandler
{
    private readonly FileServerContext _context;

    public FileIoCommandHandler(FileServerContext context)
    {
        _context = context;
    }

    public EconetMessage? Handle(EconetMessage request, FileServerRequest fsRequest)
    {
        switch (fsRequest.Opcode)
        {
            case FileServerOpcodes.Load:
            case FileServerOpcodes.LoadAs:
                return HandleLoad(request, fsRequest);
            case FileServerOpcodes.Save:
            case FileServerOpcodes.CreateFile:
                return HandleSave(request, fsRequest);
            case FileServerOpcodes.Open:
                return HandleOpen(request, fsRequest);
            case FileServerOpcodes.Close:
                return HandleClose(request, fsRequest);
            case FileServerOpcodes.Delete:
                return HandleDelete(request, fsRequest);
            case FileServerOpcodes.CreateDir:
                return HandleCreateDir(request, fsRequest);
            case FileServerOpcodes.Bye:
                return HandleBye(request, fsRequest);
            default:
                return null;
        }
    }

    private EconetMessage HandleLoad(EconetMessage request, FileServerRequest fsRequest)
    {
        FileServerSession? session = _context.Sessions.GetSession(request.ClientNet, request.ClientStation);
        if (session == null)
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.WhoAreYou, "Who are you ?"));

        if (!FileServerBulkPacketLayout.TryParseLoad(fsRequest.Raw, out byte dataPort, out byte relativeTo, out string filename))
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.BadCommand, "Bad command"));

        // fsop 0x05 (*RUN / load-and-run): BBCs send the whole command line — truncate at the first space,
        // then search the library if the file is not found in the current directory (PiFS fs_load loadas).
        bool loadAsCommand = fsRequest.Opcode == FileServerOpcodes.LoadAs;
        if (loadAsCommand)
        {
            int space = filename.IndexOf(' ');
            if (space >= 0)
                filename = filename.Substring(0, space);
        }

        bool found = _context.Paths.TryResolve(session, filename, relativeTo, out string unixPath, out _, out _) &&
            _context.FileSystem.FileExists(unixPath);
        if (!found && loadAsCommand)
        {
            found = _context.Paths.TryResolve(session, filename, session.LibHandle, out unixPath, out _, out _) &&
                _context.FileSystem.FileExists(unixPath);
        }

        if (!found)
        {
            EconetSecurityAudit.EmitFileAccessAsync(
                _context.SecurityEvents, session, SecurityEventType.FileRead, filename,
                request.ClientAddress?.ToString() ?? "UNKNOWN", false, "Not found").AsTask().GetAwaiter().GetResult();
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.NotFound, "Not found"));
        }

        EconetSecurityAudit.EmitFileAccessAsync(
            _context.SecurityEvents, session, SecurityEventType.FileRead, unixPath,
            request.ClientAddress?.ToString() ?? "UNKNOWN", true).AsTask().GetAwaiter().GetResult();

        System.IO.FileInfo? info = _context.FileSystem.GetFileInfo(unixPath);
        int length = (int)(info?.Length ?? 0);
        byte[] fileBytes = new byte[length];
        using (FileStream stream = _context.FileSystem.OpenFile(unixPath, FileMode.Open, FileAccess.Read))
        {
            int read = stream.Read(fileBytes, 0, length);
            if (read != length)
                length = read;
        }

        Queue<BulkPacket> queue = new Queue<BulkPacket>();
        int offset = 0;
        while (offset < length)
        {
            int chunkSize = Math.Min(BulkTransferManager.MaxChunkSize, length - offset);
            byte[] chunk = new byte[chunkSize];
            fileBytes.AsSpan(offset, chunkSize).CopyTo(chunk);
            queue.Enqueue(new BulkPacket
            {
                EconetPort = dataPort,
                Control = 0x80,
                Sequence = request.Sequence + (uint)(offset / BulkTransferManager.MaxChunkSize) + 1,
                Payload = chunk,
            });
            offset += chunkSize;
        }

        queue.Enqueue(new BulkPacket
        {
            EconetPort = fsRequest.ReplyPort,
            Control = 0x80,
            Sequence = request.Sequence + 1000,
            Payload = [0, 0],
        });

        _context.BulkTransfers.RegisterLoadTransfer(new OutboundLoadTransfer
        {
            ClientNet = request.ClientNet,
            ClientStation = request.ClientStation,
            DataPort = dataPort,
            ReplyPort = fsRequest.ReplyPort,
            TriggerSequence = request.Sequence,
            Queue = queue,
            FilePath = filename.Length > 0 ? filename : unixPath,
            TotalBytes = length,
            Username = session.Username,
        });

        // PiFS fs_load: metadata reply carries the file's real load/exec/length/access/date so the
        // BBC can place the image and (for CHAIN/*RUN) obtain the execution address.
        AcornInfStore.TryReadAttributes(_context.FileSystem, unixPath, out uint load, out uint exec, out _, out byte access);
        byte day = 0;
        byte monthYear = 0;
        if (info != null)
            AcornPath.EncodeFileDate(info.LastWriteTimeUtc, out day, out monthYear);

        byte[] metadata = FileServerReply.LoadMetadata(load, exec, length, access, day, monthYear);
        return Reply(request, fsRequest.ReplyPort, metadata);
    }

    private EconetMessage HandleSave(EconetMessage request, FileServerRequest fsRequest)
    {
        FileServerSession? session = _context.Sessions.GetSession(request.ClientNet, request.ClientStation);
        if (session == null)
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.WhoAreYou, "Who are you ?"));

        if (!FileServerBulkPacketLayout.TryParseSave(
                fsRequest.Raw,
                out byte ackPort,
                out uint load,
                out uint exec,
                out int length,
                out string filename))
        {
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.BadCommand, "Bad command"));
        }

        if (!_context.Paths.TryResolve(session, filename, out string unixPath, out _, out _))
            unixPath = _context.FileSystem.CombinePath(session.CurrentDirectoryUnix, AcornPath.AcornToUnixRelative(filename));

        const byte defaultAccess = 0x33;
        if (length <= 0)
        {
            using (FileStream stream = _context.FileSystem.OpenFile(unixPath, FileMode.Create, FileAccess.Write))
            {
            }

            AcornInfStore.WriteAttributes(_context.FileSystem, unixPath, load, exec, 0, defaultAccess);
            EncodeAcornDate(_context.ServerClock.UtcNow, out byte day, out byte monthYear);
            // PiFS create_only / length==0 path sends the short 5-byte completion (return code 0),
            // not the 21-byte FS3 data-transfer close packet.
            return Reply(
                request,
                fsRequest.ReplyPort,
                FileServerReply.SaveCreateComplete(defaultAccess, day, monthYear));
        }

        if (!_context.BulkTransfers.TryAllocatePort(out byte bulkPort))
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.Locked, "Locked"));

        FileStream saveStream = _context.FileSystem.OpenFile(unixPath, FileMode.Create, FileAccess.Write);
        _context.BulkTransfers.RegisterSaveTransfer(new InboundSaveTransfer
        {
            ClientNet = request.ClientNet,
            ClientStation = request.ClientStation,
            BulkPort = bulkPort,
            AckPort = ackPort,
            ReplyPort = fsRequest.ReplyPort,
            Stream = saveStream,
            Remaining = length,
            ExpectedBytes = length,
            UnixPath = unixPath,
            AcornFilename = filename,
            LoadAddress = load,
            ExecAddress = exec,
            Username = session.Username,
        });

        return Reply(request, fsRequest.ReplyPort, FileServerReply.SaveSetup(bulkPort));
    }

    private static void EncodeAcornDate(DateTime utc, out byte day, out byte monthYear)
    {
        day = (byte)utc.Day;
        int yearOffset = utc.Year - 1981;
        monthYear = (byte)(((yearOffset & 0x0F) << 4) | (utc.Month & 0x0F));
    }

    private EconetMessage HandleOpen(EconetMessage request, FileServerRequest fsRequest)
    {
        FileServerSession? session = _context.Sessions.GetSession(request.ClientNet, request.ClientStation);
        if (session == null)
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.WhoAreYou, "Who are you ?"));

        ReadOnlySpan<byte> parameters = fsRequest.Parameters;
        bool mustExist = parameters.Length > 0 && parameters[0] != 0;
        bool readOnly = parameters.Length > 1 && parameters[1] != 0;
        string filename = parameters.Length > 2 ? AcornPath.ReadCrTerminatedString(parameters.Slice(2)) : string.Empty;

        if (!_context.Paths.TryResolve(session, filename, out string unixPath, out _, out _))
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.NotFound, "Not found"));

        if (mustExist && !_context.FileSystem.FileExists(unixPath))
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.NotFound, "Not found"));

        FileMode mode = mustExist ? FileMode.Open : FileMode.Create;
        FileAccess access = readOnly ? FileAccess.Read : FileAccess.ReadWrite;
        FileStream stream = _context.FileSystem.OpenFile(unixPath, mode, access);
        byte handle = _context.Sessions.AllocateHandle();
        if (!_context.Handles.TryOpen(handle, unixPath, stream, readOnly, request.ClientNet, request.ClientStation, out _))
        {
            stream.Dispose();
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.AlreadyOpen, "Already open"));
        }

        byte[] payload = [0, 0, handle];
        return Reply(request, fsRequest.ReplyPort, payload);
    }

    private EconetMessage HandleClose(EconetMessage request, FileServerRequest fsRequest)
    {
        byte handle = fsRequest.Parameters.Length > 0 ? fsRequest.Parameters[0] : (byte)0;
        if (handle == 0)
        {
            _context.Handles.CloseAllForStation(request.ClientNet, request.ClientStation);
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Success());
        }

        if (!_context.Handles.Close(handle))
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.BadChannel, "Channel ?"));

        return Reply(request, fsRequest.ReplyPort, FileServerReply.Success());
    }

    private EconetMessage HandleDelete(EconetMessage request, FileServerRequest fsRequest)
    {
        FileServerSession? session = _context.Sessions.GetSession(request.ClientNet, request.ClientStation);
        if (session == null)
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.WhoAreYou, "Who are you ?"));

        string filename = fsRequest.Parameters.Length > 0
            ? AcornPath.ReadCrTerminatedString(fsRequest.Parameters)
            : string.Empty;

        if (!_context.Paths.TryResolve(session, filename, out string unixPath, out _, out _))
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.NotFound, "Not found"));

        if (_context.FileSystem.FileExists(unixPath))
            File.Delete(unixPath);
        else if (_context.FileSystem.DirectoryExists(unixPath))
            Directory.Delete(unixPath, false);
        else
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.NotFound, "Not found"));

        return Reply(request, fsRequest.ReplyPort, FileServerReply.Success());
    }

    private EconetMessage HandleCreateDir(EconetMessage request, FileServerRequest fsRequest)
    {
        FileServerSession? session = _context.Sessions.GetSession(request.ClientNet, request.ClientStation);
        if (session == null)
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.WhoAreYou, "Who are you ?"));

        string dirname = fsRequest.Parameters.Length > 1
            ? AcornPath.ReadCrTerminatedString(fsRequest.Parameters.Slice(1))
            : string.Empty;

        string unixPath = _context.FileSystem.CombinePath(session.CurrentDirectoryUnix, AcornPath.AcornToUnixRelative(dirname));
        // Go through the backend, not raw System.IO — the raw call never worked on non-local
        // backends (e.g. Azure blob storage).
        _context.FileSystem.CreateDirectory(unixPath);
        return Reply(request, fsRequest.ReplyPort, FileServerReply.Success());
    }

    private EconetMessage HandleBye(EconetMessage request, FileServerRequest fsRequest)
    {
        return Reply(request, fsRequest.ReplyPort, FileServerLogout.Execute(_context, request.ClientNet, request.ClientStation));
    }

    private static EconetMessage Reply(EconetMessage request, byte replyPort, byte[] payload)
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
