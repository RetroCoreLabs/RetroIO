//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Econet.Protocol.FileServer;

/// <summary>
/// The Acorn file-server per-object access byte, as owner/public read-write-lock bits.
/// </summary>
/// <remarks>
/// This is the single definition of the access-byte bit layout. Before it, the same six
/// <c>const byte</c>s were re-declared everywhere the byte was read or formatted (e.g. inside
/// <c>ExamineCommandHandler.FormatTextEntry</c>), which is exactly the kind of duplicate-magic-number
/// drift that makes <c>*ACCESS</c> and <c>*EX</c> disagree. The bit values are the PiEconetBridge
/// <c>fs.c</c> layout (fs.c lines 334-340), and match the RetroFS default <c>0x33</c> =
/// <see cref="OwnerRead"/> | <see cref="OwnerWrite"/> | <see cref="PublicRead"/> | <see cref="PublicWrite"/>.
/// </remarks>
[Flags]
public enum EconetAccess : byte
{
    /// <summary>No permissions.</summary>
    None = 0x00,

    /// <summary>Owner may read (<c>R</c> before the slash). fs.c <c>FS_PERM_OWN_R</c> = 0x01.</summary>
    OwnerRead = 0x01,

    /// <summary>Owner may write (<c>W</c> before the slash). fs.c <c>FS_PERM_OWN_W</c> = 0x02.</summary>
    OwnerWrite = 0x02,

    /// <summary>Object is locked against deletion (<c>L</c>). fs.c <c>FS_PERM_L</c> = 0x04.</summary>
    Locked = 0x04,

    /// <summary>Others may read (<c>R</c> after the slash). fs.c <c>FS_PERM_OTH_R</c> = 0x10.</summary>
    PublicRead = 0x10,

    /// <summary>Others may write (<c>W</c> after the slash). fs.c <c>FS_PERM_OTH_W</c> = 0x20.</summary>
    PublicWrite = 0x20,

    /// <summary>Object is hidden from catalogues (<c>H</c>, also spelled <c>P</c>). fs.c 0x80.</summary>
    Hidden = 0x80,
}
