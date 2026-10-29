//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.DriveWire.Protocol;

/// <summary>
/// The error codes the server answers in the one-byte status of a sector transaction.
/// </summary>
/// <remarks>
/// <para>
/// The specification ("DriveWire Protocol Version 4.0.0", Appendix A "Error Codes") says the codes "mirror the same codes
/// used in OS-9" and lists $F3, $F4, $F5 and $F6. The values below are those OS-9 codes as the NitrOS-9 definitions give
/// them (defs/os9.d, the E$Unit to E$NotRdy block that starts at $F0).
/// </para>
/// <para>
/// The NitrOS-9 driver rbdw.asm retries only on <see cref="Crc"/> (up to 8 times, NUMRETRIES) and hands every other
/// non-zero code to the caller as the error of the I/O call.
/// </para>
/// </remarks>
public static class DriveWireError
{
    /// <summary>
    /// No error.
    /// </summary>
    public const byte None = 0x00;

    /// <summary>
    /// E$WP ($F2), write protect. Not in Appendix A; answered when a write goes to a drive that is read-only. OS-9 reports
    /// it to the user as "write protect", which says what happened, where the listed $F5 would only say "write error".
    /// </summary>
    public const byte WriteProtect = 0xF2;

    /// <summary>
    /// E$CRC ($F3), checksum mismatch (Appendix A). The client may then retry with the reread or rewrite operation.
    /// </summary>
    public const byte Crc = 0xF3;

    /// <summary>
    /// E$Read ($F4), read error (Appendix A). Answered when the sector cannot be read, which includes an LSN at or past the
    /// end of the volume.
    /// </summary>
    public const byte Read = 0xF4;

    /// <summary>
    /// E$Write ($F5), write error (Appendix A). Answered when the sector cannot be written, which includes an LSN at or past
    /// the end of the volume.
    /// </summary>
    public const byte Write = 0xF5;

    /// <summary>
    /// E$NotRdy ($F6), not ready (Appendix A: "if the a command requests accesses a non-existent virtual drive"). Answered
    /// when no image is mounted in the drive.
    /// </summary>
    public const byte NotReady = 0xF6;
}
