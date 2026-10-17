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
    /// <summary>
    /// Checks whether a user may use a protocol.
    /// </summary>
    /// <remarks>
    /// Inactive users are refused. For a protocol the method does not know, the result is false.
    /// </remarks>
    /// <param name="user">
    /// The user to check.
    /// </param>
    /// <param name="protocol">
    /// The protocol to check.
    /// </param>
    /// <returns>
    /// True when the user is active and the matching effective permission is true.
    /// </returns>
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

    /// <summary>
    /// Looks up a user by name, checks the password and protocol, then saves the result.
    /// </summary>
    /// <remarks>
    /// After the check, including a failed one for a known user, the user is updated in the provider and SaveAsync is called, so the login time stamps are stored.
    /// </remarks>
    /// <param name="idp">
    /// The identity provider.
    /// </param>
    /// <param name="username">
    /// The username to look up.
    /// </param>
    /// <param name="password">
    /// The password to check.
    /// </param>
    /// <param name="protocol">
    /// The protocol the user wants to use.
    /// </param>
    /// <param name="ct">
    /// A token that cancels the calls to the provider.
    /// </param>
    /// <returns>
    /// UnknownUser when no user has the name. Otherwise the result of AuthenticateKnownUserAsync.
    /// </returns>
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

    /// <summary>
    /// Checks the password and protocol permission of a known user and records the time stamps.
    /// </summary>
    /// <remarks>
    /// The checks run in this order: the user is active, the password is correct, the protocol is allowed. Each failure records a failed login. An empty password is accepted only when the user has no stored hash. This method does not save the provider.
    /// </remarks>
    /// <param name="idp">
    /// The identity provider used to verify the password.
    /// </param>
    /// <param name="user">
    /// The user to check.
    /// </param>
    /// <param name="password">
    /// The password to check.
    /// </param>
    /// <param name="protocol">
    /// The protocol the user wants to use.
    /// </param>
    /// <param name="ct">
    /// A token that cancels the calls to the provider.
    /// </param>
    /// <returns>
    /// Inactive, WrongPassword, ProtocolNotAllowed or Success.
    /// </returns>
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

    /// <summary>
    /// Sets the last login time of the user and of the given protocol to the current UTC time.
    /// </summary>
    /// <param name="user">
    /// The user to update.
    /// </param>
    /// <param name="protocol">
    /// The protocol that was used.
    /// </param>
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

    /// <summary>
    /// Sets the last failed login time of the user to the current UTC time.
    /// </summary>
    /// <param name="user">
    /// The user to update.
    /// </param>
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
