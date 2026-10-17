//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.FileServer.Users;

/// <summary>
/// RetroIDP user metadata keys for Acorn file server profile fields.
/// </summary>
public static class EconetMetadataKeys
{
    /// <summary>
    /// Metadata key of the boot option of a user.
    /// </summary>
    public const string BootOpt = "econet.bootOpt";
    /// <summary>
    /// Metadata key of the index of the home disc of a user.
    /// </summary>
    public const string HomeDisc = "econet.homeDisc";
    /// <summary>
    /// Metadata key of the disc mask of a user, as a decimal number. Bit n allows the disc with index n.
    /// </summary>
    public const string DiscMask = "econet.discMask";
    /// <summary>
    /// Metadata key of the Acorn path of the home directory of a user.
    /// </summary>
    public const string Home = "econet.home";
    /// <summary>
    /// Metadata key of the Acorn path of the library directory of a user.
    /// </summary>
    public const string Lib = "econet.lib";
    /// <summary>
    /// Metadata key of the flag that lets a user log in with a blank password.
    /// </summary>
    public const string AllowEmptyPassword = "econet.allowEmptyPassword";
    // Locked account (PiFS FS_PRIV_LOCKED): the password verifies but login is refused.
    /// <summary>
    /// Metadata key of the flag that marks an account as locked.
    /// </summary>
    public const string Locked = "econet.locked";
}
