//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Identity;

namespace RetroIO.Tests.RetroIDP;

[TestFixture]
[Category("Unit")]
public sealed class Argon2idPasswordHasherTests
{
    [Test]
    public void Hash_ProducesArgon2idPhcString()
    {
        string hash = Argon2idPasswordHasher.Hash("retroadmin");
        Assert.That(hash.StartsWith("$argon2id$v=19$"), Is.True);
    }

    [Test]
    public void Verify_WithMatchingPassword_ReturnsTrue()
    {
        string hash = Argon2idPasswordHasher.Hash("retroadmin");
        bool valid = Argon2idPasswordHasher.Verify("retroadmin", hash, out bool upgraded);
        Assert.That(valid, Is.True);
        Assert.That(upgraded, Is.False);
    }

    [Test]
    public void Verify_WithWrongPassword_ReturnsFalse()
    {
        string hash = Argon2idPasswordHasher.Hash("retroadmin");
        bool valid = Argon2idPasswordHasher.Verify("wrong", hash, out _);
        Assert.That(valid, Is.False);
    }

    [Test]
    public void Verify_WithLegacyHash_UpgradesFlagIsSet()
    {
        string legacy = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("retroadmin_HASHED"));
        bool valid = Argon2idPasswordHasher.Verify("retroadmin", legacy, out bool upgraded);
        Assert.That(valid, Is.True);
        Assert.That(upgraded, Is.True);
    }

    [Test]
    public void JsonRetroIDP_SetPassword_StoresArgon2idHash()
    {
        string directory = Path.Combine(Path.GetTempPath(), "retrofs-idp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        JsonRetroIDP idp = new JsonRetroIDP(
            Path.Combine(directory, "users.json"),
            Path.Combine(directory, "groups.json"));

        RetroUser user = new RetroUser
        {
            UserId = Guid.NewGuid(),
            Username = "alice",
            IsActive = true,
        };

        idp.CreateUserAsync(user).AsTask().GetAwaiter().GetResult();
        idp.SetPasswordAsync(user.UserId, "secret").AsTask().GetAwaiter().GetResult();

        RetroUser? loaded = idp.GetUserAsync(user.UserId).AsTask().GetAwaiter().GetResult();
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.PasswordHash, Does.StartWith("$argon2id$"));
        Assert.That(idp.VerifyPasswordAsync(user.UserId, "secret").AsTask().GetAwaiter().GetResult(), Is.True);
    }
}
