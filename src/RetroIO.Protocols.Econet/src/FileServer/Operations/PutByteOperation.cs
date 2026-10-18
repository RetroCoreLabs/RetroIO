//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.FileServer.Handles;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Operations;

/// <summary>
/// <c>PutByte</c> (fsop 0x09) — writes one byte (carried in the CWD wire field) to an open channel,
/// honouring the AUN retransmit sequence bit. An unknown or read-only channel is <c>0xDE</c>.
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandlePutByte</c>, unchanged. Like GetByte, the handle comes from
/// the URD wire field and the sequence check reads <c>call.Request.Control</c>.
/// </remarks>
public sealed class PutByteOperation : IFileServerOperation
{
    /// <summary>
    /// Gets the fsop byte this operation serves: <see cref="FileServerOpcodes.PutByte"/> (0x09).
    /// The dispatcher uses it directly as the table index.
    /// </summary>
    public byte Opcode => FileServerOpcodes.PutByte;

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
    /// Writes the byte carried in the CWD field to the channel named by the URD field.
    /// </summary>
    /// <remarks>
    /// Where the write lands depends on whether the sequence bit of the AUN control byte matches the
    /// channel's (<c>ChannelSequence.Matches</c>). After the write the channel takes the new sequence bit.
    /// </remarks>
    /// <param name="call">
    /// Everything the operation needs: the request, the decoded header, the session, and services.
    /// </param>
    /// <returns>
    /// Success, or 0xDE "Channel ?" for an unknown or read-only channel.
    /// </returns>
    public byte[] Execute(in FsOpCall call)
    {
        byte handle = AcornFileHandles.DivHandle(call.FsRequest.Urd);
        byte value = call.FsRequest.Cwd;
        OpenFileHandle? open = call.Context.Handles.Get(call.Request.ClientNet, call.Request.ClientStation, handle);
        if (open == null || open.ReadOnly)
            return FileServerReply.Error(FileServerErrors.BadChannel, "Channel ?");

        bool seqOk = ChannelSequence.Matches(open.Sequence, call.Request.Control);
        open.SeekToCursor(!seqOk);
        open.Stream.WriteByte(value);
        open.SyncCursorFromStream();
        open.Sequence = (byte)(call.Request.Control & 0x01);
        return FileServerReply.Success();
    }
}
