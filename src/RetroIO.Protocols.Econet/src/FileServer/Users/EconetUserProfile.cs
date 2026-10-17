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
    public Guid UserId { get; init; }
    public string Username { get; init; } = string.Empty;
    public byte BootOpt { get; init; }
    public byte HomeDisc { get; init; }
    public ushort DiscMask { get; init; }
    public string Home { get; init; } = "$";
    public string Lib { get; init; } = "LIB:$.Library";
    public bool AllowEmptyPassword { get; init; }
    public bool IsAdmin { get; init; }
    public bool IsLocked { get; init; }
    public ushort UserIndex { get; init; }
    public byte Privilege => IsAdmin ? (byte)0xFF : (byte)0x00;
    public byte Privilege2 => 0;

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
