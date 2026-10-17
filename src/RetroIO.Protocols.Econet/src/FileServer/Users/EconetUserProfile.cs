//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Globalization;
using RetroIO.Identity;

namespace RetroIO.Protocols.Econet.FileServer.Users;

/// <summary>
/// Acorn file server profile derived from a RetroIDP user.
/// </summary>
public sealed class EconetUserProfile
{
    /// <summary>
    /// Gets the user identifier.
    /// </summary>
    public Guid UserId { get; init; }
    /// <summary>
    /// Gets the user name.
    /// </summary>
    public string Username { get; init; } = string.Empty;
    /// <summary>
    /// Gets the boot option.
    /// </summary>
    public byte BootOpt { get; init; }
    /// <summary>
    /// Gets the index of the home disc.
    /// </summary>
    public byte HomeDisc { get; init; }
    /// <summary>
    /// Gets the disc mask. Bit n allows the disc with index n.
    /// </summary>
    public ushort DiscMask { get; init; }
    /// <summary>
    /// Gets the Acorn path of the home directory. The default is the dollar sign.
    /// </summary>
    public string Home { get; init; } = "$";
    /// <summary>
    /// Gets the Acorn path of the library directory. The default is LIB:$.Library.
    /// </summary>
    public string Lib { get; init; } = "LIB:$.Library";
    /// <summary>
    /// Gets a value indicating whether the user may log in with a blank password.
    /// </summary>
    public bool AllowEmptyPassword { get; init; }
    /// <summary>
    /// Gets a value indicating whether the user is an administrator.
    /// </summary>
    public bool IsAdmin { get; init; }
    /// <summary>
    /// Gets a value indicating whether the account is locked.
    /// </summary>
    public bool IsLocked { get; init; }
    /// <summary>
    /// Gets the 16-bit user index, built from the first two bytes of the user identifier.
    /// </summary>
    public ushort UserIndex { get; init; }
    /// <summary>
    /// Gets the privilege byte, 0xFF for an administrator and 0 for anyone else.
    /// </summary>
    public byte Privilege => IsAdmin ? (byte)0xFF : (byte)0x00;
    /// <summary>
    /// Gets the second privilege byte, always 0.
    /// </summary>
    public byte Privilege2 => 0;

    /// <summary>
    /// Builds the profile of a user from the user record and its metadata. Missing values get defaults.
    /// </summary>
    /// <param name="user">
    /// The user record.
    /// </param>
    /// <returns>
    /// The profile.
    /// </returns>
    public static EconetUserProfile FromRetroUser(RetroUser user)
    {
        ushort discMask = ReadUInt16(user, EconetMetadataKeys.DiscMask, user.IsAdmin ? (ushort)0xFFFF : (ushort)0x0001);

        ushort userIndex = (ushort)(user.UserId.ToByteArray()[0] | (user.UserId.ToByteArray()[1] << 8));
        return new EconetUserProfile
        {
            UserId = user.UserId,
            Username = user.Username,
            BootOpt = ReadByte(user, EconetMetadataKeys.BootOpt, 0),
            HomeDisc = ReadByte(user, EconetMetadataKeys.HomeDisc, 0),
            DiscMask = discMask,
            Home = ReadString(user, EconetMetadataKeys.Home, "$"),
            Lib = ReadString(user, EconetMetadataKeys.Lib, "LIB:$.Library"),
            AllowEmptyPassword = ReadBool(user, EconetMetadataKeys.AllowEmptyPassword, false),
            IsAdmin = user.IsAdmin,
            IsLocked = ReadBool(user, EconetMetadataKeys.Locked, false),
            UserIndex = userIndex,
        };
    }

    /// <summary>
    /// Adds the Home, Lib and DiscMask metadata to a user record when they are missing.
    /// </summary>
    /// <param name="user">
    /// The user record to update. The default disc mask is 65535 for an administrator and 1 for anyone else.
    /// </param>
    public static void ApplyDefaults(RetroUser user)
    {
        if (!user.Metadata.ContainsKey(EconetMetadataKeys.Home))
            user.Metadata[EconetMetadataKeys.Home] = "$";

        if (!user.Metadata.ContainsKey(EconetMetadataKeys.Lib))
            user.Metadata[EconetMetadataKeys.Lib] = "LIB:$.Library";

        if (!user.Metadata.ContainsKey(EconetMetadataKeys.DiscMask))
            user.Metadata[EconetMetadataKeys.DiscMask] = user.IsAdmin ? "65535" : "1";
    }

    private static string ReadString(RetroUser user, string key, string defaultValue)
    {
        if (user.Metadata.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            return value;

        return defaultValue;
    }

    private static byte ReadByte(RetroUser user, string key, byte defaultValue)
    {
        if (user.Metadata.TryGetValue(key, out string? value) &&
            byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static ushort ReadUInt16(RetroUser user, string key, ushort defaultValue)
    {
        if (user.Metadata.TryGetValue(key, out string? value) &&
            ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static bool ReadBool(RetroUser user, string key, bool defaultValue)
    {
        if (user.Metadata.TryGetValue(key, out string? value) &&
            bool.TryParse(value, out bool parsed))
        {
            return parsed;
        }

        return defaultValue;
    }
}
