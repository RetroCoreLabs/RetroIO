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
public enum FileServerOpcode : byte
{
    /// <summary>
    /// Operation 0x00, an OS command line.
    /// </summary>
    OsCli = 0x00,
    /// <summary>
    /// Operation 0x01, save a file.
    /// </summary>
    Save = 0x01,
    /// <summary>
    /// Operation 0x02, load a file.
    /// </summary>
    Load = 0x02,
    /// <summary>
    /// Operation 0x03, examine a directory.
    /// </summary>
    Examine = 0x03,
    /// <summary>
    /// Operation 0x04, read the catalogue header.
    /// </summary>
    CatHeader = 0x04,
    /// <summary>
    /// Operation 0x05, load a file as a command.
    /// </summary>
    LoadAs = 0x05,
    /// <summary>
    /// Operation 0x06, open a file.
    /// </summary>
    Open = 0x06,
    /// <summary>
    /// Operation 0x07, close a file.
    /// </summary>
    Close = 0x07,
    /// <summary>
    /// Operation 0x08, read one byte.
    /// </summary>
    GetByte = 0x08,
    /// <summary>
    /// Operation 0x09, write one byte.
    /// </summary>
    PutByte = 0x09,
    /// <summary>
    /// Operation 0x0A, read several bytes.
    /// </summary>
    GetBytes = 0x0A,
    /// <summary>
    /// Operation 0x0B, write several bytes.
    /// </summary>
    PutBytes = 0x0B,
    /// <summary>
    /// Operation 0x0C, read the file pointer or the file length.
    /// </summary>
    GetRandomAccessInfo = 0x0C,
    /// <summary>
    /// Operation 0x0D, set the file pointer.
    /// </summary>
    SetRandomAccessInfo = 0x0D,
    /// <summary>
    /// Operation 0x0E, read the disc names.
    /// </summary>
    ReadDiscNames = 0x0E,
    /// <summary>
    /// Operation 0x0F, read the logged on users.
    /// </summary>
    ReadLoggedOnUsers = 0x0F,
    /// <summary>
    /// Operation 0x10, read the server time.
    /// </summary>
    ReadTime = 0x10,
    /// <summary>
    /// Operation 0x11, test for end of file.
    /// </summary>
    EndOfFile = 0x11,
    /// <summary>
    /// Operation 0x12, read information about a file or directory.
    /// </summary>
    GetObjectInfo = 0x12,
    /// <summary>
    /// Operation 0x13, set information about a file or directory.
    /// </summary>
    SetObjectInfo = 0x13,
    /// <summary>
    /// Operation 0x14, delete a file or directory.
    /// </summary>
    Delete = 0x14,
    /// <summary>
    /// Operation 0x15, read the user environment.
    /// </summary>
    ReadUserEnv = 0x15,
    /// <summary>
    /// Operation 0x16, set the boot option.
    /// </summary>
    SetBootOpt = 0x16,
    /// <summary>
    /// Operation 0x17, log out.
    /// </summary>
    Bye = 0x17,
    /// <summary>
    /// Operation 0x18, read information about a user.
    /// </summary>
    ReadUserInfo = 0x18,
    /// <summary>
    /// Operation 0x19, read the server version.
    /// </summary>
    ReadVersion = 0x19,
    /// <summary>
    /// Operation 0x1A, read the free space of a disc.
    /// </summary>
    FreeSpace = 0x1A,
    /// <summary>
    /// Operation 0x1B, create a directory.
    /// </summary>
    CreateDir = 0x1B,
    /// <summary>
    /// Operation 0x1C, set the server clock.
    /// </summary>
    SetRtc = 0x1C,
    /// <summary>
    /// Operation 0x1D, create a file.
    /// </summary>
    CreateFile = 0x1D,
    /// <summary>
    /// Operation 0x1E, read the free space of a user.
    /// </summary>
    UserFreeSpace = 0x1E,
    /// <summary>
    /// Operation 0x1F, set the free space of a user.
    /// </summary>
    SetUserFreeSpace = 0x1F,
    /// <summary>
    /// Operation 0x20, read the client user name.
    /// </summary>
    ReadClientId = 0x20,
    /// <summary>
    /// Operation 0x40, an SJ operation.
    /// </summary>
    SjOperation = 0x40,
    /// <summary>
    /// Operation 0x41, an SJ operation reply.
    /// </summary>
    SjOperationReply = 0x41,
    /// <summary>
    /// Operation 0x60, a PiBridge extension.
    /// </summary>
    PiBridge = 0x60,
}
