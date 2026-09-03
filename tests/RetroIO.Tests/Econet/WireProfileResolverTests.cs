//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.FileServer.Configuration;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Task 0.4 — <see cref="WireProfileResolver"/> two-tier resolution: a per-machine override wins over
/// the server default; everything else takes the default.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class WireProfileResolverTests
{
    [Test]
    public void AllLaterFs_ReturnsLaterFsForEveryMachine()
    {
        WireProfileResolver resolver = WireProfileResolver.AllLaterFs;

        Assert.That(resolver.DefaultProfile, Is.EqualTo(WireProfile.LaterFS));
        Assert.That(resolver.OverrideCount, Is.EqualTo(0));
        Assert.That(resolver.Resolve(1, 1), Is.EqualTo(WireProfile.LaterFS));
        Assert.That(resolver.Resolve(0, 254), Is.EqualTo(WireProfile.LaterFS));
    }

    [Test]
    public void Default_AppliesWhenNoOverrideMatches()
    {
        WireProfileResolver resolver = new WireProfileResolver(WireProfile.Classic1983, null);

        Assert.That(resolver.Resolve(5, 20), Is.EqualTo(WireProfile.Classic1983));
    }

    [Test]
    public void PerMachineOverride_WinsOverDefault()
    {
        WireProfileResolver.MachineOverride[] overrides =
        {
            new WireProfileResolver.MachineOverride(1, 42, WireProfile.Classic1983),
        };
        WireProfileResolver resolver = new WireProfileResolver(WireProfile.LaterFS, overrides);

        // The exact machine gets the override...
        Assert.That(resolver.Resolve(1, 42), Is.EqualTo(WireProfile.Classic1983));
        // ...every other machine keeps the server default.
        Assert.That(resolver.Resolve(1, 43), Is.EqualTo(WireProfile.LaterFS));
        Assert.That(resolver.Resolve(2, 42), Is.EqualTo(WireProfile.LaterFS));
    }
}
