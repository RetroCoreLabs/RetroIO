//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Protocols.Econet.Protocol.FileServer;

/// <summary>
/// Level-3 file server operation codes (fsop byte).
/// </summary>
public static class FileServerOpcodes
{
    public const byte OsCli = 0x00;
    public const byte Save = 0x01;
    public const byte Load = 0x02;
    public const byte Examine = 0x03;
    public const byte CatHeader = 0x04;
    public const byte LoadAs = 0x05;
    public const byte Open = 0x06;
    public const byte Close = 0x07;
    public const byte GetByte = 0x08;
    public const byte PutByte = 0x09;
    public const byte GetBytes = 0x0A;
    public const byte PutBytes = 0x0B;
    public const byte GetRandomAccessInfo = 0x0C;
    public const byte SetRandomAccessInfo = 0x0D;
    public const byte ReadDiscNames = 0x0E;
    public const byte ReadLoggedOnUsers = 0x0F;
    public const byte ReadTime = 0x10;
    public const byte EndOfFile = 0x11;
    public const byte GetObjectInfo = 0x12;
    public const byte SetObjectInfo = 0x13;
    public const byte Delete = 0x14;
    public const byte ReadUserEnv = 0x15;
    public const byte SetBootOpt = 0x16;
    public const byte Bye = 0x17;
    public const byte ReadUserInfo = 0x18;
    public const byte ReadVersion = 0x19;
    public const byte FreeSpace = 0x1A;
    public const byte CreateDir = 0x1B;
    public const byte SetRtc = 0x1C;
    public const byte CreateFile = 0x1D;
    public const byte UserFreeSpace = 0x1E;
    public const byte SetUserFreeSpace = 0x1F;
    public const byte ReadClientId = 0x20;
    public const byte PiBridge = 0x60;
    public const byte SjOperation = 0x40;
    public const byte SjOperationReply = 0x41;

    public const byte GetObjectInfoCanonical = 96;

    public const byte LoginSuccessReturnCode = 0x05;

    public static bool IsAllowedWithoutLogin(byte opcode) =>
        opcode is OsCli or ReadDiscNames or ReadVersion;

    public static bool IsSjOpcode(byte opcode) =>
        opcode is SjOperation or SjOperationReply;
}
