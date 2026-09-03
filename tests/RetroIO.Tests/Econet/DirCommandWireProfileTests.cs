//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.FileServer.Configuration;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Task 0.4 — the <c>*DIR</c> not-found error code is the wire-format divergence wired end-to-end:
/// the later FS reports <c>&amp;D6</c> (the verified default), while a machine pinned to
/// <see cref="WireProfile.Classic1983"/> gets the 1983 <c>&amp;FE</c>. The success path and message
/// layout are otherwise unchanged.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class DirCommandWireProfileTests
{
    [Test]
    public void Dir_NotFound_LaterFs_Returns_D6()
    {
        // Default harness = all-LaterFS resolver: the verified golden byte is preserved.
        EconetFsTestHarness harness = new EconetFsTestHarness();
        harness.LoginAsSyst();

        byte[] payload = harness.HandleOsCli("DIR NOSUCH\r").Payload;

        Assert.That(payload[0], Is.EqualTo(0), "error reply starts 0x00");
        Assert.That(payload[1], Is.EqualTo(FileServerErrors.NotFound), "later FS uses &D6 for *DIR not-found");
    }

    [Test]
    public void Dir_NotFound_Classic1983_Returns_FE()
    {
        // Server default flipped to Classic1983 (applies to the default test client net 1 / station 1).
        EconetFsTestHarness harness =
            new EconetFsTestHarness(new WireProfileResolver(WireProfile.Classic1983, null));
        harness.LoginAsSyst();

        byte[] payload = harness.HandleOsCli("DIR NOSUCH\r").Payload;

        Assert.That(payload[0], Is.EqualTo(0), "error reply starts 0x00");
        Assert.That(payload[1], Is.EqualTo(FileServerErrors.BadCommand), "1983 NFS ROM uses &FE for *DIR not-found");
    }

    [Test]
    public void Dir_NotFound_PerMachineOverride_OnlyAffectsThatStation()
    {
        // Only net 1 / station 1 (the default test client) is pinned to Classic1983; the default stays LaterFS.
        WireProfileResolver.MachineOverride[] overrides =
        {
            new WireProfileResolver.MachineOverride(1, 1, WireProfile.Classic1983),
        };
        EconetFsTestHarness harness =
            new EconetFsTestHarness(new WireProfileResolver(WireProfile.LaterFS, overrides));
        harness.LoginAsSyst();

        byte[] payload = harness.HandleOsCli("DIR NOSUCH\r").Payload;

        Assert.That(payload[1], Is.EqualTo(FileServerErrors.BadCommand),
            "the pinned station (1.1) gets the 1983 &FE even though the server default is LaterFS");
    }

    [Test]
    public void Dir_Success_IsUnaffectedByProfile()
    {
        // Changing the profile must not disturb the success path — *DIR to the home dir still works.
        EconetFsTestHarness harness =
            new EconetFsTestHarness(new WireProfileResolver(WireProfile.Classic1983, null));
        harness.LoginAsSyst();

        byte[] payload = harness.HandleOsCli("DIR\r").Payload;

        // *DIR success reply is [0x07 directory-changed, 0x00 ok, handle] — result byte 0x07, no error.
        Assert.That(payload[0], Is.EqualTo(0x07), "*DIR home returns the directory-changed result byte");
        Assert.That(payload[1], Is.EqualTo(0x00), "*DIR home succeeds (no error) regardless of wire profile");
    }
}
