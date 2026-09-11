//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Identity;
using RetroIO.Server.Hosting;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Task 0.2 — <see cref="RetroIdpBootstrap"/> must guarantee the Acorn file-server manager account
/// (<c>SYST</c>) always carries System privilege, even on an install that predates that rule. A
/// non-admin SYST is the exact condition that blocked <c>*SETHOME</c> in live BeebEm testing.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class RetroIdpBootstrapSystAdminTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "retrofs-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private JsonRetroIDP NewIdp() => new JsonRetroIDP(
        Path.Combine(_dir, "users.json"),
        Path.Combine(_dir, "groups.json"));

    private static async Task RunBootstrapAsync(IRetroIDP idp)
    {
        ServiceCollection services = new ServiceCollection();
        services.AddSingleton<IRetroIDP>(idp);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        await using ServiceProvider sp = services.BuildServiceProvider();
        await RetroIdpBootstrap.InitializeAsync(sp);
    }

    [Test]
    public async Task Initialize_PreExistingNonAdminSyst_IsPromotedToAdmin()
    {
        // Simulate an OLD install: a SYST account that was created without System privilege.
        JsonRetroIDP seed = NewIdp();
        await seed.LoadAsync();
        RetroUser legacySyst = new RetroUser
        {
            UserId = WellKnownPrincipals.SystUserId,
            Username = WellKnownPrincipals.SystUsername,
            DisplayName = "System Manager",
            IsActive = true,
            IsAdmin = false,           // <-- the bug condition
            AllowEconet = true,
            AllowTnfs = false,
        };
        await seed.CreateUserAsync(legacySyst);
        await seed.SaveAsync();

        await RunBootstrapAsync(NewIdp());

        JsonRetroIDP verify = NewIdp();
        await verify.LoadAsync();
        RetroUser? syst = await verify.GetUserByUsernameAsync(WellKnownPrincipals.SystUsername);
        Assert.That(syst, Is.Not.Null);
        Assert.That(syst!.IsAdmin, Is.True, "SYST must be promoted to admin so *SETHOME/*PRIV work.");
    }

    [Test]
    public async Task Initialize_FreshInstall_CreatesSystAsAdmin()
    {
        // No users at all: bootstrap must seed SYST as an admin.
        await RunBootstrapAsync(NewIdp());

        JsonRetroIDP verify = NewIdp();
        await verify.LoadAsync();
        RetroUser? syst = await verify.GetUserByUsernameAsync(WellKnownPrincipals.SystUsername);
        Assert.That(syst, Is.Not.Null);
        Assert.That(syst!.IsAdmin, Is.True);
        Assert.That(syst.AllowEconet, Is.True);
        Assert.That(syst.AllowTnfs, Is.False);
    }
}
