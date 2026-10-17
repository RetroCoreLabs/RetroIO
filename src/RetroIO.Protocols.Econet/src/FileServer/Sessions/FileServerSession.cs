//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.FileServer.Sessions;

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.Protocol.FileServer;

/// <summary>
/// The state of one logged in station: its user, its directory handles and its directories.
/// </summary>
public sealed class FileServerSession
{
    /// <summary>
    /// Gets the network number of the station.
    /// </summary>
    public byte ClientNet { get; init; }
    /// <summary>
    /// Gets the station number of the station.
    /// </summary>
    public byte ClientStation { get; init; }
    /// <summary>
    /// Gets the identifier of the user account.
    /// </summary>
    public Guid RetroUserId { get; init; }
    /// <summary>
    /// Gets the user name.
    /// </summary>
    public string Username { get; init; } = string.Empty;
    /// <summary>
    /// Gets a value indicating whether the user is an administrator.
    /// </summary>
    public bool IsAdmin { get; init; }
    /// <summary>
    /// Gets or sets the handle of the user root directory. The default is 1.
    /// </summary>
    public byte UrdHandle { get; set; } = 1;
    /// <summary>
    /// Gets or sets the handle of the current directory. The default is 2.
    /// </summary>
    public byte CwdHandle { get; set; } = 2;
    /// <summary>
    /// Gets or sets the handle of the library directory. The default is 3.
    /// </summary>
    public byte LibHandle { get; set; } = 3;
    /// <summary>
    /// Gets or sets the boot option of the user.
    /// </summary>
    public byte BootOpt { get; set; }
    /// <summary>
    /// Gets or sets the index of the current disc.
    /// </summary>
    public int CurrentDisc { get; set; }
    /// <summary>
    /// Gets or sets the host path of the current directory.
    /// </summary>
    public string CurrentDirectoryUnix { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the host path of the home directory.
    /// </summary>
    public string HomeDirectoryUnix { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the host path of the library directory.
    /// </summary>
    public string LibraryDirectoryUnix { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the PutByte sequence value. The default is 2.
    /// </summary>
    public byte PutByteSequence { get; set; } = 2;

    /// <summary>
    /// Gets or sets the Acorn path of the home directory. The default is the dollar sign.
    /// </summary>
    public string AcornHomePath { get; set; } = "$";
    /// <summary>
    /// Gets or sets the Acorn path of the current directory. The default is the dollar sign.
    /// </summary>
    public string AcornCwdPath { get; set; } = "$";
    /// <summary>
    /// Gets or sets the Acorn path of the library directory. The default is the dollar sign.
    /// </summary>
    public string AcornLibPath { get; set; } = "$";

    /// <summary>
    /// Updates the three directory handles from the wire handles in a request. A wire handle that converts to 0 is ignored.
    /// </summary>
    /// <param name="request">
    /// The request. GetByte and PutByte requests do not change any handle, and the opcodes that skip the user root conversion do not change UrdHandle.
    /// </param>
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
