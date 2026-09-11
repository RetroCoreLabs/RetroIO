//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Gate 0 for the SETLIB/SETHOME login-path wiring: pins the login reply and the ReadUserEnv (0x15)
/// reply for a DEFAULT user (SYST / USER carry no econet.home / econet.lib metadata) byte-for-byte.
/// The profile Home/Lib override added to the login path must be guarded so that a default user's
/// bytes stay IDENTICAL to before the change — this test is the regression tripwire that proves it.
/// </summary>
[TestFixture]
[Category("Unit")]
internal sealed class GoldenLoginEnvTests
{
    // Golden bytes for the default SYST user (no econet.home / econet.lib metadata), captured on the
    // pre-wiring login path. LOGIN = [replyCtrl, result=0, urd, cwd, lib, bootOpt]. ENV (0x15) =
    // [0, 0, 16, disc"RetroFS"+pad16, CSD-leaf "USER"+pad10, lib-leaf "USER"+pad10] — i.e. the default
    // home is discPath/USER, NOT disc root. The override must keep both identical for this default user.
    private const string GoldenLogin = "05-00-01-02-04-00";
    private const string GoldenEnv =
        "00-00-10-52-65-74-72-6F-46-53-20-20-20-20-20-20-20-20-20-55-53-45-52-20-20-20-20-20-20-55-53-45-52-20-20-20-20-20-20";

    [Test]
    public void DefaultUser_LoginReply_IsGolden()
    {
        EconetFsTestHarness harness = new EconetFsTestHarness();

        EconetMessage login = harness.LoginAsSyst();

        Assert.That(Hex(login.Payload), Is.EqualTo(GoldenLogin));
    }

    [Test]
    public void DefaultUser_ReadUserEnvReply_IsGolden()
    {
        EconetFsTestHarness harness = new EconetFsTestHarness();
        harness.LoginAsSyst();

        EconetMessage env = harness.Send(FileServerOpcodes.ReadUserEnv);

        Assert.That(Hex(env.Payload), Is.EqualTo(GoldenEnv));
    }

    private static string Hex(byte[] bytes) => System.BitConverter.ToString(bytes);
}
