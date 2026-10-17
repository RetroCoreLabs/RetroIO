//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.FileServer.Operations;

/// <summary>
/// The Acorn per-channel sequence check shared by the byte/bulk channel operations.
/// </summary>
/// <remarks>
/// The low bit of the channel's stored sequence must equal the low bit of the inbound AUN control
/// byte. A mismatch means the client retransmitted the same request (the reply was lost), so the
/// caller re-seeks the stream to the saved cursor and repeats the byte rather than advancing. Used by
/// GetByte, PutByte and GetBytes — lifted verbatim from the old <c>MiscOpcodeHandler.CheckSequence</c>
/// so all three share one definition while the migration is in flight.
/// </remarks>
internal static class ChannelSequence
{
    /// <summary>True when the stored sequence's low bit matches the control byte's low bit.</summary>
    public static bool Matches(byte storedSequence, byte control) =>
        (storedSequence & 0x01) == (control & 0x01);
}
