//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Covers <c>*PRIV</c> — a System user granting/revoking another user's System privilege. The effect is
/// proved through the login gate: a granted user reaches an admin-gated command's logic (past the
/// 0xBD gate); a revoked user is blocked again.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class PrivCommandTests
{
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new EconetFsTestHarness();
    }

    private static byte Status(EconetMessage reply) => reply.Payload[1];

    // An admin-gated probe: non-admins get 0xBD before Execute; admins reach the logic and (for a
    // non-existent directory) get 0xA8 "Bad directory".
    private byte SetHomeProbeAsUser()
    {
        _harness.HandleOsCli("I AM USER \r");
        return Status(_harness.HandleOsCli("SETHOME $.NOSUCHDIR\r"));
    }

    [Test]
    public void Priv_WhenNotLoggedIn_ReturnsWhoAreYou()
    {
        Assert.That(Status(_harness.HandleOsCli("PRIV USER S\r")), Is.EqualTo(FileServerErrors.WhoAreYou));
    }

    [Test]
    public void Priv_AsNonAdmin_ReturnsInsufficientAccess()
    {
        _harness.LoginAsUser();

        Assert.That(Status(_harness.HandleOsCli("PRIV USER S\r")), Is.EqualTo(FileServerErrors.InsufficientAccess));
    }

    [Test]
    public void Priv_GrantSystem_TargetReachesAdminGatedLogic()
    {
        _harness.LoginAsSyst();
        Assert.That(Status(_harness.HandleOsCli("PRIV USER S\r")), Is.EqualTo(0), "granting System should succeed");

        // USER is now admin: SETHOME passes the System gate and fails only on the missing directory.
        Assert.That(SetHomeProbeAsUser(), Is.EqualTo(FileServerErrors.BadDirectory));
    }

    [Test]
    public void Priv_GrantThenRevoke_TargetBlockedAgain()
    {
        _harness.LoginAsSyst();
        _harness.HandleOsCli("PRIV USER S\r");
        Assert.That(Status(_harness.HandleOsCli("PRIV USER -S\r")), Is.EqualTo(0), "revoking System should succeed");

        // Back to non-admin: SETHOME is refused at the gate.
        Assert.That(SetHomeProbeAsUser(), Is.EqualTo(FileServerErrors.InsufficientAccess));
    }

    [Test]
    public void Priv_PlusS_Grants()
    {
        _harness.LoginAsSyst();

        Assert.That(Status(_harness.HandleOsCli("PRIV USER +S\r")), Is.EqualTo(0));
        Assert.That(SetHomeProbeAsUser(), Is.EqualTo(FileServerErrors.BadDirectory));
    }

    [Test]
    public void Priv_UnknownUser_ReturnsUserNotFound()
    {
        _harness.LoginAsSyst();

        Assert.That(Status(_harness.HandleOsCli("PRIV GHOST S\r")), Is.EqualTo((byte)0xBC));
    }

    [Test]
    public void Priv_MissingPriv_ReturnsBadCommand()
    {
        _harness.LoginAsSyst();

        Assert.That(Status(_harness.HandleOsCli("PRIV USER\r")), Is.EqualTo(FileServerErrors.BadCommand));
    }

    [Test]
    public void Priv_LockUser_RefusesLoginWithCorrectPassword()
    {
        _harness.LoginAsSyst();
        Assert.That(Status(_harness.HandleOsCli("PRIV USER L\r")), Is.EqualTo(0), "locking should succeed");

        // The (correct, empty) password now yields "Account locked" (0xBC), not a successful login —
        // a non-locked user would return 0x00 here.
        Assert.That(Status(_harness.HandleOsCli("I AM USER \r")), Is.EqualTo((byte)0xBC));
    }

    [Test]
    public void Priv_LockThenUnlock_AllowsLoginAgain()
    {
        _harness.LoginAsSyst();
        _harness.HandleOsCli("PRIV USER L\r");
        Assert.That(Status(_harness.HandleOsCli("PRIV USER -L\r")), Is.EqualTo(0), "unlocking should succeed");

        Assert.That(Status(_harness.HandleOsCli("I AM USER \r")), Is.EqualTo(0), "USER can log in again");
    }

    [Test]
    public void Priv_GrantSystem_UnlocksALockedUser()
    {
        _harness.LoginAsSyst();
        _harness.HandleOsCli("PRIV USER L\r");           // lock
        _harness.HandleOsCli("PRIV USER S\r");           // System state also clears Locked

        Assert.That(Status(_harness.HandleOsCli("I AM USER \r")), Is.EqualTo(0), "granting System unlocks");
    }

    [Test]
    public void Priv_UnsupportedPriv_ReturnsError()
    {
        _harness.LoginAsSyst();

        // No-password-change (N) is not modelled yet -> honest "Unsupported privilege", not a no-op.
        Assert.That(Status(_harness.HandleOsCli("PRIV USER N\r")), Is.EqualTo(FileServerErrors.Unsupported));
    }
}
