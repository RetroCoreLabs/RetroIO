//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet.Fsop;

/// <summary>
/// Base class for the self-contained fsop protocol fixtures. Each test gets a fresh
/// <see cref="EconetFsTestHarness"/> wired to a fresh <see cref="RecordingFileSystemBackend"/>, so
/// tests never touch disk or a running server. Provides the login-state helpers and the reply-status
/// assertions the per-opcode fixtures share.
/// </summary>
internal abstract class FsopProtocolTestBase
{
    /// <summary>The recording backend wired into the server (assert its <c>Calls</c>).</summary>
    protected RecordingFileSystemBackend Backend = null!;

    /// <summary>The harness driving <c>FileServerCommandHandler</c>.</summary>
    protected EconetFsTestHarness Harness = null!;

    [SetUp]
    public void FsopBaseSetUp()
    {
        Backend = new RecordingFileSystemBackend();
        Harness = new EconetFsTestHarness(Backend);
    }

    // ---- login-state helpers --------------------------------------------------------------------

    /// <summary>Logs in the admin user (SYST) and forgets the login's backend traffic.</summary>
    protected void LoginAdmin()
    {
        Harness.LoginAsSyst();
        Backend.ResetCalls();
    }

    /// <summary>Logs in the normal, non-admin user (USER) and forgets the login's backend traffic.</summary>
    protected void LoginNormalUser()
    {
        Harness.LoginAsUser();
        Backend.ResetCalls();
    }

    // ---- reply-status assertions ----------------------------------------------------------------

    /// <summary>The error/return code lives at payload[1] for both Success ([0,0]) and Error replies.</summary>
    protected static byte StatusOf(EconetMessage reply) => reply.Payload[1];

    /// <summary>Asserts the reply carries the given file-server error code.</summary>
    protected static void AssertErrorCode(EconetMessage reply, byte expected, string? because = null) =>
        Assert.That(StatusOf(reply), Is.EqualTo(expected), because);

    /// <summary>Asserts "Who are you ?" (0xBF) — the not-logged-in rejection.</summary>
    protected static void AssertWhoAreYou(EconetMessage reply) =>
        AssertErrorCode(reply, FileServerErrors.WhoAreYou, "expected 'Who are you ?' (not logged in)");

    /// <summary>Asserts "Insufficient access" (0xBD) — the wrong-privilege rejection.</summary>
    protected static void AssertInsufficientAccess(EconetMessage reply) =>
        AssertErrorCode(reply, FileServerErrors.InsufficientAccess, "expected 'Insufficient access' (non-admin)");

    /// <summary>Asserts the reply is NOT the not-logged-in rejection (used for login-exempt opcodes).</summary>
    protected static void AssertNotWhoAreYou(EconetMessage reply) =>
        Assert.That(StatusOf(reply), Is.Not.EqualTo(FileServerErrors.WhoAreYou));
}
