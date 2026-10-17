//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading;
using System.Threading.Tasks;

namespace RetroIO.Identity;

/// <summary>
/// Shared protocol login policy and audit timestamps for RetroIDP users.
/// </summary>
public static class RetroUserProtocolAuth
{
    public static bool CanUseProtocol(RetroUser user, RetroFsProtocol protocol)
    {
        if (!user.IsActive)
            return false;

        return protocol switch
        {
            RetroFsProtocol.Tnfs => user.EffectiveAllowTnfs,
            RetroFsProtocol.Econet => user.EffectiveAllowEconet,
            _ => false,
        };
    }

    public static async ValueTask<ProtocolAuthResult> AuthenticateAsync(
        IRetroIDP idp,
        string username,
        string password,
        RetroFsProtocol protocol,
        CancellationToken ct = default)
    {
        RetroUser? user = await idp.GetUserByUsernameAsync(username, ct);
        if (user == null)
            return ProtocolAuthResult.UnknownUser;

        ProtocolAuthResult result = await AuthenticateKnownUserAsync(idp, user, password, protocol, ct);
        await idp.UpdateUserAsync(user, ct);
        await idp.SaveAsync(ct);
        return result;
    }

    public static async ValueTask<ProtocolAuthResult> AuthenticateKnownUserAsync(
        IRetroIDP idp,
        RetroUser user,
        string password,
        RetroFsProtocol protocol,
        CancellationToken ct = default)
    {
        if (!user.IsActive)
        {
            RecordFailedLogin(user);
            return ProtocolAuthResult.Inactive;
        }

        if (!await VerifyPasswordAsync(idp, user, password, ct))
        {
            RecordFailedLogin(user);
            return ProtocolAuthResult.WrongPassword;
        }

        if (!CanUseProtocol(user, protocol))
        {
            RecordFailedLogin(user);
            return ProtocolAuthResult.ProtocolNotAllowed;
        }

        RecordSuccessfulLogin(user, protocol);
        return ProtocolAuthResult.Success;
    }

    public static void RecordSuccessfulLogin(RetroUser user, RetroFsProtocol protocol)
    {
        DateTime now = DateTime.UtcNow;
        user.LastLoginAt = now;

        switch (protocol)
        {
            case RetroFsProtocol.Tnfs:
                user.LastLoginTnfsAt = now;
                break;
            case RetroFsProtocol.Econet:
                user.LastLoginEconetAt = now;
                break;
        }
    }

    public static void RecordFailedLogin(RetroUser user) =>
        user.LastFailedLoginAt = DateTime.UtcNow;

    private static async ValueTask<bool> VerifyPasswordAsync(
        IRetroIDP idp,
        RetroUser user,
        string password,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password))
            return string.IsNullOrEmpty(user.PasswordHash);

        return await idp.VerifyPasswordAsync(user.UserId, password, ct);
    }
}
