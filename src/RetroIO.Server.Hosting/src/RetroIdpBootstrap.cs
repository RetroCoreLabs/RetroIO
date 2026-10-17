//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroIO.Identity;

namespace RetroIO.Server.Hosting;

/// <summary>
/// Prepares the identity provider at server start.
/// </summary>
public static class RetroIdpBootstrap
{
    /// <summary>
    /// Loads the identity provider, makes sure the admin and SYST users exist with protocol access, and saves.
    /// </summary>
    /// <remarks>
    /// A missing admin user or SYST user is created with the password retroadmin. An existing admin is set to allow TNFS and Econet. An existing SYST user is set to allow Econet, to refuse TNFS and to be an administrator. A changed user is updated, and the provider is saved at the end.
    /// </remarks>
    /// <param name="services">
    /// The service provider that supplies the logger factory and the identity provider.
    /// </param>
    /// <param name="ct">
    /// A token that cancels the calls to the identity provider.
    /// </param>
    /// <returns>
    /// A task that completes when the setup is saved.
    /// </returns>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        ILoggerFactory loggerFactory = services.GetRequiredService<ILoggerFactory>();
        ILogger logger = loggerFactory.CreateLogger("RetroIO.Server.Hosting.RetroIdpBootstrap");
        IRetroIDP idp = services.GetRequiredService<IRetroIDP>();

        await idp.LoadAsync(ct);

        RetroUser? adminUser = await idp.GetUserByUsernameAsync(WellKnownPrincipals.AdminUsername, ct);
        if (adminUser == null)
        {
            logger.LogInformation("Creating default admin user...");

            RetroUser newAdmin = WellKnownPrincipals.CreateAdminPrincipal();
            await idp.CreateUserAsync(newAdmin, ct);
            await idp.SetPasswordAsync(WellKnownPrincipals.AdminUserId, "retroadmin", ct);

            logger.LogWarning(
                "Default admin user created with username '{Username}' and password 'retroadmin'",
                WellKnownPrincipals.AdminUsername);
            logger.LogWarning("SECURITY: Please change the default admin password immediately!");
        }
        else
        {
            logger.LogInformation("Admin user exists: {Username} ({UserId})", adminUser.Username, adminUser.UserId);
            if (EnsureAdminProtocolAccess(adminUser))
                await idp.UpdateUserAsync(adminUser, ct);
        }

        RetroUser? systUser = await idp.GetUserByUsernameAsync(WellKnownPrincipals.SystUsername, ct);
        if (systUser == null)
        {
            logger.LogInformation("Creating default SYST user for Econet file-server logins...");

            RetroUser newSyst = WellKnownPrincipals.CreateSystPrincipal();
            await idp.CreateUserAsync(newSyst, ct);
            await idp.SetPasswordAsync(WellKnownPrincipals.SystUserId, "retroadmin", ct);

            logger.LogWarning(
                "Default SYST user created with password 'retroadmin' (BBC: *I AM SYST retroadmin)");
        }
        else if (EnsureSystProtocolAccess(systUser))
        {
            await idp.UpdateUserAsync(systUser, ct);
        }

        await idp.SaveAsync(ct);
    }

    private static bool EnsureAdminProtocolAccess(RetroUser admin)
    {
        bool changed = false;
        if (admin.AllowTnfs != true)
        {
            admin.AllowTnfs = true;
            changed = true;
        }

        if (admin.AllowEconet != true)
        {
            admin.AllowEconet = true;
            changed = true;
        }

        return changed;
    }

    private static bool EnsureSystProtocolAccess(RetroUser syst)
    {
        bool changed = false;
        if (syst.AllowEconet != true)
        {
            syst.AllowEconet = true;
            changed = true;
        }

        if (syst.AllowTnfs != false)
        {
            syst.AllowTnfs = false;
            changed = true;
        }

        // SYST is the Acorn file-server MANAGER account: it must always carry System privilege so a BBC
        // logged in as SYST can run the RequiresSystem OSCLI commands (*SETHOME/*SETLIB/*PRIV/…). Fresh
        // installs already get this via CreateSystPrincipal (IsAdmin=true); this promotes a PRE-EXISTING
        // SYST that predates that rule (or was demoted) so it isn't stuck non-admin — the exact bug that
        // blocked *SETHOME in live BeebEm testing (task 0.2).
        if (!syst.IsAdmin)
        {
            syst.IsAdmin = true;
            changed = true;
        }

        return changed;
    }
}
