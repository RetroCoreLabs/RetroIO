//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Protocols.Econet.FileServer.Sessions;

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.Protocol.FileServer;

public sealed class FileServerSession
{
    public byte ClientNet { get; init; }
    public byte ClientStation { get; init; }
    public Guid RetroUserId { get; init; }
    public string Username { get; init; } = string.Empty;
    public bool IsAdmin { get; init; }
    public byte UrdHandle { get; set; } = 1;
    public byte CwdHandle { get; set; } = 2;
    public byte LibHandle { get; set; } = 3;
    public byte BootOpt { get; set; }
    public int CurrentDisc { get; set; }
    public string CurrentDirectoryUnix { get; set; } = string.Empty;
    public string HomeDirectoryUnix { get; set; } = string.Empty;
    public string LibraryDirectoryUnix { get; set; } = string.Empty;
    public byte PutByteSequence { get; set; } = 2;

    public string AcornHomePath { get; set; } = "$";
    public string AcornCwdPath { get; set; } = "$";
    public string AcornLibPath { get; set; } = "$";

    public void ApplyWireHandles(FileServerRequest request)
    {
        if (AcornFileHandles.SkipsWireHandleUpdate(request.Opcode))
            return;

        if (!AcornFileHandles.SkipsUrdDivHandle(request.Opcode))
        {
            byte urd = AcornFileHandles.DivHandle(request.Urd);
            if (urd != 0)
                UrdHandle = urd;
        }

        byte cwd = AcornFileHandles.DivHandle(request.Cwd);
        if (cwd != 0)
            CwdHandle = cwd;

        byte lib = AcornFileHandles.DivHandle(request.Lib);
        if (lib != 0)
            LibHandle = lib;
    }
}
