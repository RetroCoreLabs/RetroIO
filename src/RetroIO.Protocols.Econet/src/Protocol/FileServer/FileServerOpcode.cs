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
    OsCli = 0x00,
    Save = 0x01,
    Load = 0x02,
    Examine = 0x03,
    CatHeader = 0x04,
    LoadAs = 0x05,
    Open = 0x06,
    Close = 0x07,
    GetByte = 0x08,
    PutByte = 0x09,
    GetBytes = 0x0A,
    PutBytes = 0x0B,
    GetRandomAccessInfo = 0x0C,
    SetRandomAccessInfo = 0x0D,
    ReadDiscNames = 0x0E,
    ReadLoggedOnUsers = 0x0F,
    ReadTime = 0x10,
    EndOfFile = 0x11,
    GetObjectInfo = 0x12,
    SetObjectInfo = 0x13,
    Delete = 0x14,
    ReadUserEnv = 0x15,
    SetBootOpt = 0x16,
    Bye = 0x17,
    ReadUserInfo = 0x18,
    ReadVersion = 0x19,
    FreeSpace = 0x1A,
    CreateDir = 0x1B,
    SetRtc = 0x1C,
    CreateFile = 0x1D,
    UserFreeSpace = 0x1E,
    SetUserFreeSpace = 0x1F,
    ReadClientId = 0x20,
    SjOperation = 0x40,
    SjOperationReply = 0x41,
    PiBridge = 0x60,
}
