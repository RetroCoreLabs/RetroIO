//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.Protocol.Aun;

/// <summary>
/// PiFS AUN outbound framing for file-server replies (see <c>fs_get_object_info</c> in PiFS <c>fs.c</c>).
/// </summary>
internal static class AunFsReply
{
    /// <summary>
    /// Successful fsop <c>0x12</c> replies use AUN <c>ctrl=0</c>; errors and other fsops use <c>0x80</c>.
    /// </summary>
    public static byte ResolveOutboundControl(byte fsOpcode, ReadOnlySpan<byte> payload)
    {
        if (fsOpcode != FileServerOpcodes.GetObjectInfo)
            return 0x80;

        if (payload.Length < 2 || payload[0] != 0 || payload[1] != 0)
            return 0x80;

        // Soft not-found for cmd 1–5 is exactly 3 bytes [00 00 00] — still ctrl 0x80 in PiFS.
        if (payload.Length == 3 && payload[2] == 0)
            return 0x80;

        return 0;
    }

    public static EconetMessage Build(EconetMessage request, byte replyPort, byte fsOpcode, byte[] payload)
    {
        return new EconetMessage
        {
            PacketType = AunPacketType.Unicast,
            EconetPort = replyPort,
            Control = ResolveOutboundControl(fsOpcode, payload),
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
