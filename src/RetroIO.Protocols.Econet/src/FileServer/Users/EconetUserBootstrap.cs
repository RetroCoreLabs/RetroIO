//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Identity;

namespace RetroIO.Protocols.Econet.FileServer.Users;

/// <summary>
/// Ensures RetroIDP users have default Econet profile metadata, and purges legacy orphan keys.
/// </summary>
/// <remarks>
/// <para>
/// Intended Econet identity provisioning (task 0.1 — one owner of the SYST identity):
/// </para>
/// <list type="bullet">
///   <item><description>
///     The Acorn file-server MANAGER is the RetroIDP user whose <see cref="RetroUser.Username"/> is
///     literally <c>SYST</c> — created by <c>RetroIdpBootstrap</c> with <see cref="RetroUser.IsAdmin"/>
///     = true and a real password. A BBC logs in with <c>*I AM SYST &lt;pw&gt;</c>.
///   </description></item>
///   <item><description>
///     <c>*I AM</c> resolves purely by username (<see cref="EconetUserDirectory.FindUserByUsername"/>) —
///     there is NO alias mechanism. So <c>admin</c> is an ordinary, separate account; it does NOT and
///     must not claim the SYST identity.
///   </description></item>
/// </list>
/// </remarks>
public static class EconetUserBootstrap
{
    public static async Task EnsureProfilesAsync(IRetroIDP idp, CancellationToken ct = default)
    {
        bool anyChanged = false;

        RetroUser[] users = await idp.ListUsersAsync(ct).AsTask();
        for (int i = 0; i < users.Length; i++)
        {
            RetroUser user = users[i];
            bool changed = EnsureUserProfile(user);
            if (changed)
            {
                await idp.UpdateUserAsync(user, ct);
                anyChanged = true;
            }
        }

        RetroUser? admin = await idp.GetUserByUsernameAsync(WellKnownPrincipals.AdminUsername, ct);
        if (admin != null)
        {
            int before = admin.Metadata.Count;
            EconetUserProfile.ApplyDefaults(admin);
            if (admin.Metadata.Count != before)
            {
                await idp.UpdateUserAsync(admin, ct);
                anyChanged = true;
            }
        }

        // UpdateUserAsync only mutates the in-memory store; without this SaveAsync the purge/defaults
        // would be lost on restart (and re-appear from disk). Persist once, only if something changed.
        if (anyChanged)
            await idp.SaveAsync(ct);
    }

    private static bool EnsureUserProfile(RetroUser user)
    {
        int before = user.Metadata.Count;
        EconetUserProfile.ApplyDefaults(user);
        return user.Metadata.Count != before;
    }
}
