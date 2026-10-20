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
/// <c>GetByte</c> (fsop 0x08) — reads one byte from an open channel, honouring the AUN retransmit
/// sequence bit. Reply is <c>[0, 0, value, flags]</c> (flags bit7 = at-EOF); a directory or exhausted
/// channel returns the PiFS <c>0xFE/0xC0</c> get-byte sentinel; an unknown channel is <c>0xDE</c>.
/// </summary>
/// <remarks>
/// Migrated from <c>MiscOpcodeHandler.HandleGetByte</c>, unchanged. The channel handle comes from the
/// URD wire field via <see cref="AcornFileHandles.DivHandle"/>, and the sequence check reads
/// <c>call.Request.Control</c> — which is why <see cref="FsOpCall"/> carries the whole request.
/// </remarks>
public sealed class GetByteOperation : IFileServerOperation
{
    /// <summary>
    /// Gets the fsop byte this operation serves: <see cref="FileServerOpcodes.GetByte"/> (0x08).
    /// The dispatcher uses it directly as the table index.
    /// </summary>
    public byte Opcode => FileServerOpcodes.GetByte;

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
    /// Reads one byte from the channel named by the URD field.
    /// </summary>
    /// <remarks>
    /// Where the read starts depends on whether the sequence bit of the AUN control byte matches the
    /// channel's (<c>ChannelSequence.Matches</c>). After the read the channel takes the new sequence bit.
    /// </remarks>
    /// <param name="call">
    /// Everything the operation needs: the request, the decoded header, the session, and services.
    /// </param>
    /// <returns>
    /// The get-byte reply with the value and a flags byte (0x80 when the read reached the end); the
    /// 0xFE, 0xC0 get-byte reply for a directory or a read past the end; 0xDF "EOF" when the channel is
    /// already past the end; or 0xDE "Channel ?" for an unknown channel.
    /// </returns>
    public byte[] Execute(in FsOpCall call)
    {
        byte handle = AcornFileHandles.DivHandle(call.FsRequest.Urd, call.Context.Configuration.ManyHandles);
        byte ctrl = call.Request.Control;
        OpenFileHandle? open = call.Context.Handles.Get(call.Request.ClientNet, call.Request.ClientStation, handle);
        if (open == null)
            return FileServerReply.Error(FileServerErrors.BadChannel, "Channel ?");

        if (open.IsDirectory)
            return FileServerReply.GetByte(0xFE, 0xC0);

        if (open.PastEof)
            return FileServerReply.Error(FileServerErrors.Eof, "EOF");

        bool seqOk = ChannelSequence.Matches(open.Sequence, ctrl);
        open.SeekToCursor(!seqOk);
        int value = open.Stream.ReadByte();
        byte flags = 0;
        if (value < 0)
        {
            open.PastEof = true;
            return FileServerReply.GetByte(0xFE, 0xC0);
        }

        if (open.Stream.Position >= open.Stream.Length)
            flags = 0x80;

        open.SyncCursorFromStream();
        open.Sequence = (byte)(ctrl & 0x01);
        return FileServerReply.GetByte((byte)value, flags);
    }
}
