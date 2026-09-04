//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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
    /// <inheritdoc/>
    public byte Opcode => FileServerOpcodes.ReadUserEnv;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
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
