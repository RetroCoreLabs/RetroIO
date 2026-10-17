//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Identity;

namespace RetroIO.Tests.Econet;

/// <summary>
/// <see cref="EconetUserBootstrap.EnsureProfilesAsync"/> stamps default Econet profile metadata AND
/// persists it: it previously only called <c>UpdateUserAsync</c> (in-memory), so its stamping was lost
/// on restart until some later write happened to save. This proves the change reaches disk.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class EconetUserBootstrapTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "retrofs-econetboot-" + Guid.NewGuid().ToString("N"));
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

    [Test]
    public async Task EnsureProfiles_StampsDefaults_AndPersistsToDisk()
    {
        // Seed a user with NO econet metadata at all.
        JsonRetroIDP seed = NewIdp();
        await seed.LoadAsync();
        RetroUser user = new RetroUser
        {
            UserId = Guid.NewGuid(),
            Username = "fred",
            IsActive = true,
            AllowEconet = true,
        };
        await seed.CreateUserAsync(user);
        await seed.SaveAsync();

        // Run the Econet profile bootstrap over a freshly-loaded store.
        JsonRetroIDP idp = NewIdp();
        await idp.LoadAsync();
        await EconetUserBootstrap.EnsureProfilesAsync(idp);

        // Reload from DISK: the stamped defaults must be there (proves EnsureProfilesAsync saved).
        JsonRetroIDP verify = NewIdp();
        await verify.LoadAsync();
        RetroUser? reloaded = await verify.GetUserByUsernameAsync("fred");
        Assert.That(reloaded, Is.Not.Null);
        Assert.That(reloaded!.Metadata.ContainsKey(EconetMetadataKeys.Home), Is.True,
            "Default econet.home must be stamped and persisted to disk.");
        Assert.That(reloaded.Metadata.ContainsKey(EconetMetadataKeys.Lib), Is.True);
        Assert.That(reloaded.Metadata.ContainsKey(EconetMetadataKeys.DiscMask), Is.True);
    }
}
