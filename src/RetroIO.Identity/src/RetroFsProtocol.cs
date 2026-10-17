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
    /// <summary>
    /// The TNFS protocol.
    /// </summary>
    Tnfs = 1,
    /// <summary>
    /// The Econet protocol.
    /// </summary>
    Econet = 2,
}

/// <summary>
/// Result of a protocol-scoped username/password check.
/// </summary>
public enum ProtocolAuthResult : byte
{
    /// <summary>
    /// The user name and password are correct and the protocol is allowed.
    /// </summary>
    Success = 0,
    /// <summary>
    /// No user has the given name.
    /// </summary>
    UnknownUser = 1,
    /// <summary>
    /// The password does not match.
    /// </summary>
    WrongPassword = 2,
    /// <summary>
    /// The user is not active.
    /// </summary>
    Inactive = 3,
    /// <summary>
    /// The user is not allowed to use the requested protocol.
    /// </summary>
    ProtocolNotAllowed = 4,
}
