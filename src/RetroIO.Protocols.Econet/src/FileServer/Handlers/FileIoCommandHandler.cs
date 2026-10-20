//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
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

/// <summary>
/// Handles the file opcodes Load, LoadAs, Save, CreateFile, Open, Close, Delete, CreateDir and Bye.
/// </summary>
public sealed class FileIoCommandHandler
{
    private readonly FileServerContext _context;

    /// <summary>
    /// Creates the handler.
    /// </summary>
    /// <param name="context">
    /// The shared file server context.
    /// </param>
    public FileIoCommandHandler(FileServerContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Runs the handler for the opcode of the request.
    /// </summary>
    /// <param name="request">
    /// The inbound message.
    /// </param>
    /// <param name="fsRequest">
    /// The decoded file server request.
    /// </param>
    /// <returns>
    /// The reply message, or null when the opcode is not one this handler serves.
    /// </returns>
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

        // The directory handle in the request is a bit mask unless ManyHandle is set (fs.c FS_DIVHANDLE, line 9944).
        relativeTo = AcornFileHandles.DivHandle(relativeTo, _context.Configuration.ManyHandles);
        bool found = _context.Paths.TryResolve(session, filename, relativeTo, out string unixPath, out _, out _) &&
            _context.FileSystem.FileExists(unixPath);
        // fs.c lines 8176 to 8178: the library is searched when the file is not in the current directory, for a
        // *RUN (load as command) and also for every load when the MdfsFunctions flag is set.
        if (!found && (loadAsCommand || _context.Configuration.MdfsFunctions))
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

        RetroIO.Server.Core.FileSystem.BackendFileInfo? info = _context.FileSystem.GetFileInfo(unixPath);
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
        AcornAttributeStore.TryReadAttributes(_context.FileSystem, unixPath, out uint load, out uint exec, out _, out byte access);
        byte day = 0;
        byte monthYear = 0;
        if (info?.LastWriteTimeUtc != null)
            AcornPath.EncodeFileDate(info.LastWriteTimeUtc.Value, out day, out monthYear);

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

            // fs.c line 5873: the user who saves a file becomes its owner.
            AcornAttributeStore.WriteAttributes(_context.FileSystem, unixPath, load, exec, defaultAccess, session.UserIndex);
            AcornPath.EncodeFileDate(_context.ServerClock.UtcNow, out byte day, out byte monthYear);
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
            OwnerId = session.UserIndex,
        });

        return Reply(request, fsRequest.ReplyPort, FileServerReply.SaveSetup(bulkPort, _context.Configuration.SaveWindow));
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

        bool resolved = _context.Paths.TryResolve(session, filename, out string unixPath, out _, out _);

        // fs.c lines 9195 to 9197: with the MdfsFunctions flag set, a file that has to exist and is not in the
        // current directory is looked for in the library.
        if (mustExist && _context.Configuration.MdfsFunctions &&
            (!resolved || !_context.FileSystem.FileExists(unixPath)) &&
            _context.Paths.TryResolve(session, filename, session.LibHandle, out string libraryPath, out _, out _) &&
            _context.FileSystem.FileExists(libraryPath))
        {
            unixPath = libraryPath;
            resolved = true;
        }

        if (!resolved)
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.NotFound, "Not found"));

        if (mustExist && !_context.FileSystem.FileExists(unixPath))
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.NotFound, "Not found"));

        FileMode mode = mustExist ? FileMode.Open : FileMode.Create;
        FileAccess access = readOnly ? FileAccess.Read : FileAccess.ReadWrite;

        // PiFS answers 0xC0 "Too many open files" when no handle is free.
        byte handle = _context.Handles.AllocateHandle(request.ClientNet, request.ClientStation, _context.Configuration.ManyHandles);
        if (handle == 0)
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.Locked, "Too many open files"));

        FileStream stream = _context.FileSystem.OpenFile(unixPath, mode, access);
        if (!_context.Handles.TryOpen(handle, unixPath, stream, readOnly, request.ClientNet, request.ClientStation, out _))
        {
            stream.Dispose();
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.AlreadyOpen, "Already open"));
        }

        // The reply carries the handle as a bit mask unless ManyHandle is set (fs.c FS_MULHANDLE, line 9329).
        byte[] payload = [0, 0, AcornFileHandles.MulHandle(handle, _context.Configuration.ManyHandles)];
        return Reply(request, fsRequest.ReplyPort, payload);
    }

    private EconetMessage HandleClose(EconetMessage request, FileServerRequest fsRequest)
    {
        // The handle byte on the wire is a bit mask unless ManyHandle is set (fs.c FS_DIVHANDLE, line 11367).
        byte handle = AcornFileHandles.DivHandle(fsRequest.Parameters.Length > 0 ? fsRequest.Parameters[0] : (byte)0, _context.Configuration.ManyHandles);
        if (handle == 0)
        {
            _context.Handles.CloseAllForStation(request.ClientNet, request.ClientStation);
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Success());
        }

        if (!_context.Handles.Close(request.ClientNet, request.ClientStation, handle))
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

        // Through the backend: the path belongs to it and need not be a path on the host disk.
        if (_context.FileSystem.FileExists(unixPath))
            _context.FileSystem.DeleteFile(unixPath);
        else if (_context.FileSystem.DirectoryExists(unixPath))
            _context.FileSystem.DeleteDirectory(unixPath);
        else
            return Reply(request, fsRequest.ReplyPort, FileServerReply.Error(FileServerErrors.NotFound, "Not found"));

        // fs.c lines 7087 and 7088: the attribute file goes with the object, so a new object of the same name does not get the old owner.
        AcornAttributeStore.DeleteAttributes(_context.FileSystem, unixPath);

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
        bool existed = _context.FileSystem.DirectoryExists(unixPath);
        _context.FileSystem.CreateDirectory(unixPath);

        // fs.c line 7131: a new directory is owned by the user who made it. A directory that was there keeps its owner.
        if (!existed)
            ObjectOwnership.SetOwner(_context, session, unixPath);

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
