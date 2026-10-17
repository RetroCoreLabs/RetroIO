//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.Protocol.FileServer;

/// <summary>
/// Level-3 file server operation codes (fsop byte).
/// </summary>
public static class FileServerOpcodes
{
    /// <summary>
    /// Opcode 0x00, an OS command line.
    /// </summary>
    public const byte OsCli = 0x00;
    /// <summary>
    /// Opcode 0x01, save a file.
    /// </summary>
    public const byte Save = 0x01;
    /// <summary>
    /// Opcode 0x02, load a file.
    /// </summary>
    public const byte Load = 0x02;
    /// <summary>
    /// Opcode 0x03, examine a directory.
    /// </summary>
    public const byte Examine = 0x03;
    /// <summary>
    /// Opcode 0x04, read the catalogue header.
    /// </summary>
    public const byte CatHeader = 0x04;
    /// <summary>
    /// Opcode 0x05, load a file as a command.
    /// </summary>
    public const byte LoadAs = 0x05;
    /// <summary>
    /// Opcode 0x06, open a file.
    /// </summary>
    public const byte Open = 0x06;
    /// <summary>
    /// Opcode 0x07, close a file.
    /// </summary>
    public const byte Close = 0x07;
    /// <summary>
    /// Opcode 0x08, read one byte.
    /// </summary>
    public const byte GetByte = 0x08;
    /// <summary>
    /// Opcode 0x09, write one byte.
    /// </summary>
    public const byte PutByte = 0x09;
    /// <summary>
    /// Opcode 0x0A, read several bytes.
    /// </summary>
    public const byte GetBytes = 0x0A;
    /// <summary>
    /// Opcode 0x0B, write several bytes.
    /// </summary>
    public const byte PutBytes = 0x0B;
    /// <summary>
    /// Opcode 0x0C, read the file pointer or the file length.
    /// </summary>
    public const byte GetRandomAccessInfo = 0x0C;
    /// <summary>
    /// Opcode 0x0D, set the file pointer.
    /// </summary>
    public const byte SetRandomAccessInfo = 0x0D;
    /// <summary>
    /// Opcode 0x0E, read the disc names.
    /// </summary>
    public const byte ReadDiscNames = 0x0E;
    /// <summary>
    /// Opcode 0x0F, read the logged on users.
    /// </summary>
    public const byte ReadLoggedOnUsers = 0x0F;
    /// <summary>
    /// Opcode 0x10, read the server time.
    /// </summary>
    public const byte ReadTime = 0x10;
    /// <summary>
    /// Opcode 0x11, test for end of file.
    /// </summary>
    public const byte EndOfFile = 0x11;
    /// <summary>
    /// Opcode 0x12, read information about a file or directory.
    /// </summary>
    public const byte GetObjectInfo = 0x12;
    /// <summary>
    /// Opcode 0x13, set information about a file or directory.
    /// </summary>
    public const byte SetObjectInfo = 0x13;
    /// <summary>
    /// Opcode 0x14, delete a file or directory.
    /// </summary>
    public const byte Delete = 0x14;
    /// <summary>
    /// Opcode 0x15, read the user environment.
    /// </summary>
    public const byte ReadUserEnv = 0x15;
    /// <summary>
    /// Opcode 0x16, set the boot option.
    /// </summary>
    public const byte SetBootOpt = 0x16;
    /// <summary>
    /// Opcode 0x17, log out.
    /// </summary>
    public const byte Bye = 0x17;
    /// <summary>
    /// Opcode 0x18, read information about a user.
    /// </summary>
    public const byte ReadUserInfo = 0x18;
    /// <summary>
    /// Opcode 0x19, read the server version.
    /// </summary>
    public const byte ReadVersion = 0x19;
    /// <summary>
    /// Opcode 0x1A, read the free space of a disc.
    /// </summary>
    public const byte FreeSpace = 0x1A;
    /// <summary>
    /// Opcode 0x1B, create a directory.
    /// </summary>
    public const byte CreateDir = 0x1B;
    /// <summary>
    /// Opcode 0x1C, set the server clock.
    /// </summary>
    public const byte SetRtc = 0x1C;
    /// <summary>
    /// Opcode 0x1D, create a file.
    /// </summary>
    public const byte CreateFile = 0x1D;
    /// <summary>
    /// Opcode 0x1E, read the free space of a user.
    /// </summary>
    public const byte UserFreeSpace = 0x1E;
    /// <summary>
    /// Opcode 0x1F, set the free space of a user.
    /// </summary>
    public const byte SetUserFreeSpace = 0x1F;
    /// <summary>
    /// Opcode 0x20, read the client user name.
    /// </summary>
    public const byte ReadClientId = 0x20;
    /// <summary>
    /// Opcode 0x60, a PiBridge extension.
    /// </summary>
    public const byte PiBridge = 0x60;
    /// <summary>
    /// Opcode 0x40, an SJ operation.
    /// </summary>
    public const byte SjOperation = 0x40;
    /// <summary>
    /// Opcode 0x41, an SJ operation reply.
    /// </summary>
    public const byte SjOperationReply = 0x41;

    /// <summary>
    /// Value 96 of the command byte of a GetObjectInfo request. ObjectInfoForCommand builds the same full reply for it as for command 5.
    /// </summary>
    public const byte GetObjectInfoCanonical = 96;

    /// <summary>
    /// Return code 0x05, the first byte of a successful login reply.
    /// </summary>
    public const byte LoginSuccessReturnCode = 0x05;

    /// <summary>
    /// Reports whether an opcode may be used before login.
    /// </summary>
    /// <param name="opcode">
    /// The opcode byte.
    /// </param>
    /// <returns>
    /// True for OsCli, ReadDiscNames and ReadVersion.
    /// </returns>
    public static bool IsAllowedWithoutLogin(byte opcode) =>
        opcode is OsCli or ReadDiscNames or ReadVersion;

    /// <summary>
    /// Reports whether an opcode is one of the two SJ opcodes.
    /// </summary>
    /// <param name="opcode">
    /// The opcode byte.
    /// </param>
    /// <returns>
    /// True for SjOperation and SjOperationReply.
    /// </returns>
    public static bool IsSjOpcode(byte opcode) =>
        opcode is SjOperation or SjOperationReply;
}
