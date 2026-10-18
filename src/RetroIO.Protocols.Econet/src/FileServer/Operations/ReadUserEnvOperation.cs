//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using RetroIO.Protocols.Econet.FileServer.Discs;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Metadata;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Operations;

/// <summary>
/// <c>ReadUserEnv</c> (fsop 0x15) — returns the caller's environment: current disc name, current
/// directory leaf, and library directory leaf (PiFS <c>fs_read_user_env</c>).
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandleReadUserEnv</c>, unchanged. Reply layout:
/// <c>00 00 + disc-name-length(16) + disc[16] + CSD leaf[10] + lib leaf[10]</c> = 39 bytes.
/// </remarks>
public sealed class ReadUserEnvOperation : IFileServerOperation
{
    /// <summary>
    /// Gets the fsop byte this operation serves: <see cref="FileServerOpcodes.ReadUserEnv"/> (0x15).
    /// The dispatcher uses it directly as the table index.
    /// </summary>
    public byte Opcode => FileServerOpcodes.ReadUserEnv;

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
    /// Returns the caller's environment: the current disc name, the current directory name and the
    /// library directory name.
    /// </summary>
    /// <param name="call">
    /// Everything the operation needs: the request, the decoded header, the session, and services.
    /// </param>
    /// <returns>
    /// A 39-byte reply: two zero bytes, the disc name length 16, the disc name padded with spaces to
    /// 16 bytes, then the 10-byte current directory name and the 10-byte library name.
    /// </returns>
    public byte[] Execute(in FsOpCall call)
    {
        FileServerSession session = call.Session!;
        FileServerContext context = call.Context;

        DiscEntry? disc = context.Discs.GetByIndex(session.CurrentDisc);
        string discName = disc?.AcornName ?? context.DiscName;

        // PiFS fs_read_user_env: 00 00 + disc-name-length(16) + disc[16] + CSD leaf[10] + lib leaf[10]
        byte[] reply = new byte[39];
        reply[2] = 16;
        reply.AsSpan(3, 16).Fill((byte)' ');
        byte[] discAscii = Encoding.ASCII.GetBytes(discName);
        discAscii.AsSpan(0, Math.Min(16, discAscii.Length)).CopyTo(reply.AsSpan(3));
        string discRootUnix = context.Discs.GetDiscDirectoryPath(session.CurrentDisc);
        CatalogueEnumerator.WriteCatalogueDirName10(reply.AsSpan(19, 10), context.FileSystem, session, session.CurrentDirectoryUnix, discRootUnix);
        CatalogueEnumerator.WriteCatalogueDirName10(reply.AsSpan(29, 10), context.FileSystem, session, session.LibraryDirectoryUnix, discRootUnix);
        return reply;
    }
}
