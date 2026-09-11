//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Protocols.Econet.FileServer.Users;

/// <summary>
/// RetroIDP user metadata keys for Acorn file server profile fields.
/// </summary>
public static class EconetMetadataKeys
{
    public const string BootOpt = "econet.bootOpt";
    public const string HomeDisc = "econet.homeDisc";
    public const string DiscMask = "econet.discMask";
    public const string Home = "econet.home";
    public const string Lib = "econet.lib";
    public const string AllowEmptyPassword = "econet.allowEmptyPassword";
    // Locked account (PiFS FS_PRIV_LOCKED): the password verifies but login is refused.
    public const string Locked = "econet.locked";
}
