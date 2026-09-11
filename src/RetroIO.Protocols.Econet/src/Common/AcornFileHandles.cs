//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.Common;

/// <summary>
/// Acorn Level-3 wire handle encoding (PiFS FS_MULHANDLE / FS_DIVHANDLE, NFS3/BBC B mode).
/// Login returns bit-mask handles; requests carry the same masks on the wire.
/// </summary>
public static class AcornFileHandles
{
    public const byte WireUrd = 0x01;
    public const byte WireCwd = 0x02;
    public const byte WireLib = 0x04;

    public const byte InternalUrd = 0x01;
    public const byte InternalCwd = 0x02;
    public const byte InternalLib = 0x03;

    /// <summary>Wire bitmask → internal handle index (1..8).</summary>
    public static byte DivHandle(byte wireHandle) =>
        wireHandle switch
        {
            128 => 8,
            64 => 7,
            32 => 6,
            16 => 5,
            8 => 4,
            4 => 3,
            2 => 2,
            1 => 1,
            _ => wireHandle,
        };

    /// <summary>Internal handle index → wire bitmask for login replies.</summary>
    public static byte MulHandle(byte internalHandle) =>
        internalHandle is >= 1 and <= 8 ? (byte)(1 << (internalHandle - 1)) : internalHandle;

    public static byte ResolveUrd(byte wireHandle, FileServerSession session, byte opcode)
    {
        if (SkipsUrdDivHandle(opcode))
            return wireHandle;

        byte internalHandle = DivHandle(wireHandle);
        return internalHandle != 0 ? internalHandle : session.UrdHandle;
    }

    public static byte ResolveCwd(byte wireHandle, FileServerSession session) =>
        DivHandle(wireHandle) is byte h and not 0 ? h : session.CwdHandle;

    public static byte ResolveLib(byte wireHandle, FileServerSession session) =>
        DivHandle(wireHandle) is byte h and not 0 ? h : session.LibHandle;

    public static bool SkipsUrdDivHandle(byte opcode) =>
        opcode is FileServerOpcodes.Save
            or FileServerOpcodes.Load
            or FileServerOpcodes.LoadAs
            or FileServerOpcodes.GetBytes
            or FileServerOpcodes.PutBytes;

    /// <summary>
    /// Get/Put byte (0x08/0x09) omit the usual three handle bytes — PiFS §7.1.
    /// </summary>
    public static bool SkipsWireHandleUpdate(byte opcode) =>
        opcode is FileServerOpcodes.GetByte or FileServerOpcodes.PutByte;
}
