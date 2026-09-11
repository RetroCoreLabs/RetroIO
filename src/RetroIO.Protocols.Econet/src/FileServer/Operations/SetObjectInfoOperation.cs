//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Metadata;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Operations;

/// <summary>
/// <c>SetObjectInfo</c> (fsop 0x13) — writes load/exec/length/access metadata for an existing file to
/// its <c>,inf</c> sidecar (the Acorn attribute system of record). A missing object is
/// <c>0xD6 "Not found"</c>; a malformed path is <c>0xCC "Bad filename"</c>.
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandleSetObjectInfo</c>, unchanged. This is the write half that
/// pairs with <c>GetObjectInfo</c> (0x12) and with the <c>*ACCESS</c> command — all three round-trip
/// the access byte through <see cref="AcornInfStore"/>.
/// </remarks>
public sealed class SetObjectInfoOperation : IFileServerOperation
{
    /// <inheritdoc/>
    public byte Opcode => FileServerOpcodes.SetObjectInfo;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in FsOpCall call)
    {
        FileServerSession session = call.Session!;
        FileServerContext context = call.Context;

        ReadOnlySpan<byte> parameters = call.FsRequest.Parameters;
        if (parameters.Length < 2)
            return FileServerReply.Error(FileServerErrors.BadPath, "Bad filename");

        byte command = parameters[0];
        string path = AcornPath.ReadCrTerminatedString(parameters.Slice(1));
        if (!context.Paths.TryResolve(session, path, call.FsRequest.ResolvedCwd(session), out string unixPath, out _, out _) ||
            !context.FileSystem.FileExists(unixPath))
        {
            return FileServerReply.Error(FileServerErrors.NotFound, "Not found");
        }

        if (command == 2 || command == 5 || command == FileServerOpcodes.GetObjectInfoCanonical)
        {
            if (parameters.Length >= 10)
            {
                uint load = (uint)(parameters[1] | (parameters[2] << 8) | (parameters[3] << 16) | (parameters[4] << 24));
                uint exec = (uint)(parameters[5] | (parameters[6] << 8) | (parameters[7] << 16) | (parameters[8] << 24));
                int length = parameters[9] | (parameters[10] << 8) | (parameters[11] << 16);
                byte access = parameters.Length > 12 ? parameters[12] : context.Configuration.DefaultFilePerm;
                AcornInfStore.WriteAttributes(context.FileSystem, unixPath, load, exec, length, access);
            }
        }

        return FileServerReply.Success();
    }
}
