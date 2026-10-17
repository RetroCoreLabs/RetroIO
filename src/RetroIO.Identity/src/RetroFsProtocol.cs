//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Identity;

/// <summary>
/// RetroFS server protocols that authenticate against RetroIDP.
/// </summary>
public enum RetroFsProtocol : byte
{
    Tnfs = 1,
    Econet = 2,
}

/// <summary>
/// Result of a protocol-scoped username/password check.
/// </summary>
public enum ProtocolAuthResult : byte
{
    Success = 0,
    UnknownUser = 1,
    WrongPassword = 2,
    Inactive = 3,
    ProtocolNotAllowed = 4,
}
