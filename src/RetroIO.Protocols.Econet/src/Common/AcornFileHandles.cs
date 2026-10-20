//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
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
    /// <summary>
    /// Wire bit mask for the user root directory handle.
    /// </summary>
    public const byte WireUrd = 0x01;
    /// <summary>
    /// Wire bit mask for the current directory handle.
    /// </summary>
    public const byte WireCwd = 0x02;
    /// <summary>
    /// Wire bit mask for the library directory handle.
    /// </summary>
    public const byte WireLib = 0x04;

    /// <summary>
    /// Internal handle index of the user root directory.
    /// </summary>
    public const byte InternalUrd = 0x01;
    /// <summary>
    /// Internal handle index of the current directory.
    /// </summary>
    public const byte InternalCwd = 0x02;
    /// <summary>
    /// Internal handle index of the library directory.
    /// </summary>
    public const byte InternalLib = 0x03;

    /// <summary>
    /// Converts a wire handle byte to an internal handle number.
    /// </summary>
    /// <param name="wireHandle">
    /// The handle byte as sent on the wire.
    /// </param>
    /// <param name="manyHandles">
    /// True when FileServerConfiguration.FlagManyHandle is set: handles are plain numbers and the byte is returned as it is. False: a power of two from 1 to 128 is a bit mask and gives its bit number from 1 to 8.
    /// </param>
    /// <returns>
    /// The internal handle number. In the bit mask mode any byte that is not a power of two is returned as it is.
    /// </returns>
    /// <remarks>
    /// Source: PiEconetBridge utilities/fs.c line 71 (FS_DIVHANDLE).
    /// </remarks>
    public static byte DivHandle(byte wireHandle, bool manyHandles)
    {
        if (manyHandles)
            return wireHandle;

        return wireHandle switch
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
    }

    /// <summary>
    /// Converts an internal handle number to the byte sent on the wire.
    /// </summary>
    /// <param name="internalHandle">
    /// The internal handle number.
    /// </param>
    /// <param name="manyHandles">
    /// True when FileServerConfiguration.FlagManyHandle is set: the number is sent as it is. False: the number 1 to 8 is sent as a bit mask, 1 shifted left by one less than the number.
    /// </param>
    /// <returns>
    /// The wire byte. In the bit mask mode a number outside 1 to 8 is returned as it is.
    /// </returns>
    /// <remarks>
    /// Source: PiEconetBridge utilities/fs.c line 72 (FS_MULHANDLE).
    /// </remarks>
    public static byte MulHandle(byte internalHandle, bool manyHandles)
    {
        if (manyHandles)
            return internalHandle;

        return internalHandle is >= 1 and <= 8 ? (byte)(1 << (internalHandle - 1)) : internalHandle;
    }

    /// <summary>
    /// Converts the wire user root handle of a request to an internal handle.
    /// </summary>
    /// <param name="wireHandle">
    /// The handle byte as sent on the wire.
    /// </param>
    /// <param name="session">
    /// The session whose user root handle is used as fallback.
    /// </param>
    /// <param name="opcode">
    /// The file server opcode of the request.
    /// </param>
    /// <param name="manyHandles">
    /// True when FileServerConfiguration.FlagManyHandle is set.
    /// </param>
    /// <returns>
    /// The wire handle unchanged when the opcode skips the conversion, otherwise the internal handle, or the session user root handle when the conversion gives 0.
    /// </returns>
    public static byte ResolveUrd(byte wireHandle, FileServerSession session, byte opcode, bool manyHandles)
    {
        if (SkipsUrdDivHandle(opcode))
            return wireHandle;

        byte internalHandle = DivHandle(wireHandle, manyHandles);
        return internalHandle != 0 ? internalHandle : session.UrdHandle;
    }

    /// <summary>
    /// Converts the wire current directory handle of a request to an internal handle.
    /// </summary>
    /// <param name="wireHandle">
    /// The handle byte as sent on the wire.
    /// </param>
    /// <param name="session">
    /// The session whose current directory handle is used as fallback.
    /// </param>
    /// <param name="manyHandles">
    /// True when FileServerConfiguration.FlagManyHandle is set.
    /// </param>
    /// <returns>
    /// The internal handle, or the session current directory handle when the conversion gives 0.
    /// </returns>
    public static byte ResolveCwd(byte wireHandle, FileServerSession session, bool manyHandles) =>
        DivHandle(wireHandle, manyHandles) is byte h and not 0 ? h : session.CwdHandle;

    /// <summary>
    /// Converts the wire library handle of a request to an internal handle.
    /// </summary>
    /// <param name="wireHandle">
    /// The handle byte as sent on the wire.
    /// </param>
    /// <param name="session">
    /// The session whose library handle is used as fallback.
    /// </param>
    /// <param name="manyHandles">
    /// True when FileServerConfiguration.FlagManyHandle is set.
    /// </param>
    /// <returns>
    /// The internal handle, or the session library handle when the conversion gives 0.
    /// </returns>
    public static byte ResolveLib(byte wireHandle, FileServerSession session, bool manyHandles) =>
        DivHandle(wireHandle, manyHandles) is byte h and not 0 ? h : session.LibHandle;

    /// <summary>
    /// Reports whether the user root handle of the opcode is left as sent instead of being converted.
    /// </summary>
    /// <param name="opcode">
    /// The file server opcode of the request.
    /// </param>
    /// <returns>
    /// True for Save, Load, LoadAs, GetBytes and PutBytes, otherwise false.
    /// </returns>
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
