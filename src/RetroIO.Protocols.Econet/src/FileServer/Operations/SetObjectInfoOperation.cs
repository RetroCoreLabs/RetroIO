//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Metadata;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Operations;

/// <summary>
/// <c>SetObjectInfo</c> (fsop 0x13) — writes load/exec/length/access metadata for an existing file to
/// its property bag (the Acorn attribute system of record). A missing object is
/// <c>0xD6 "Not found"</c>; a malformed path is <c>0xCC "Bad filename"</c>.
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandleSetObjectInfo</c>, unchanged. This is the write half that
/// pairs with <c>GetObjectInfo</c> (0x12) and with the <c>*ACCESS</c> command — all three round-trip
/// the access byte through <see cref="AcornAttributeStore"/>.
/// </remarks>
public sealed class SetObjectInfoOperation : IFileServerOperation
{
    /// <summary>
    /// Gets the fsop byte this operation serves: <see cref="FileServerOpcodes.SetObjectInfo"/> (0x13).
    /// The dispatcher uses it directly as the table index.
    /// </summary>
    public byte Opcode => FileServerOpcodes.SetObjectInfo;

    /// <summary>
    /// Gets <c>true</c>: the operation needs a logged-in caller. The dispatcher answers an
    /// unauthenticated caller with "Who are you ?" and never calls <see cref="Execute"/>, so
    /// <see cref="FsOpCall.Session"/> is non-null inside it.
    /// </summary>
    public bool RequiresLogin => true;

    /// <summary>
    /// Gets <c>false</c>: the operation is not limited to system-privileged users.
    /// </summary>
    public bool RequiresSystem => false;

    /// <summary>
    /// Writes load, exec, length and access to the property bag of the existing file named in
    /// the request.
    /// </summary>
    /// <remarks>
    /// Only info commands 2, 5 and the canonical form write anything, and only when at least 12
    /// parameter bytes are present (the length field ends at byte 11); with fewer the reply is still
    /// success and nothing is written, and the same holds for every other command. The attribute bytes
    /// are read from parameter offset 1 onwards, the same bytes the path is read from. The access byte
    /// takes the configured default file permission when the parameters end before it.
    /// </remarks>
    /// <param name="call">
    /// Everything the operation needs: the request, the decoded header, the session, and services.
    /// </param>
    /// <returns>
    /// Success; 0xCC "Bad filename" when there are fewer than 2 parameter bytes; or 0xD6 "Not found"
    /// when the path does not resolve to an existing file.
    /// </returns>
    public byte[] Execute(in FsOpCall call)
    {
        FileServerSession session = call.Session!;
        FileServerContext context = call.Context;

        ReadOnlySpan<byte> parameters = call.FsRequest.Parameters;
        if (parameters.Length < 2)
            return FileServerReply.Error(FileServerErrors.BadPath, "Bad filename");

        byte command = parameters[0];
        string path = AcornPath.ReadCrTerminatedString(parameters.Slice(1));
        if (!context.Paths.TryResolve(session, path, call.FsRequest.ResolvedCwd(session, context.Configuration.ManyHandles), out string unixPath, out _, out _) ||
            !context.FileSystem.FileExists(unixPath))
        {
            return FileServerReply.Error(FileServerErrors.NotFound, "Not found");
        }

        if (command == 2 || command == 5 || command == FileServerOpcodes.GetObjectInfoCanonical)
        {
            if (parameters.Length >= 12)
            {
                uint load = (uint)(parameters[1] | (parameters[2] << 8) | (parameters[3] << 16) | (parameters[4] << 24));
                uint exec = (uint)(parameters[5] | (parameters[6] << 8) | (parameters[7] << 16) | (parameters[8] << 24));
                int length = parameters[9] | (parameters[10] << 8) | (parameters[11] << 16);
                byte access = parameters.Length > 12 ? parameters[12] : context.Configuration.DefaultFilePerm;
                AcornAttributeStore.WriteAttributes(context.FileSystem, unixPath, load, exec, length, access);
            }
        }

        return FileServerReply.Success();
    }
}
