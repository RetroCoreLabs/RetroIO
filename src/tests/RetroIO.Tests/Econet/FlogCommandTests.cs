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
/// Covers <c>*FLOG</c> / <c>*LOGOFF</c> — a System user force-logging-off a station or user.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class FlogCommandTests
{
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new EconetFsTestHarness();
    }

    private static byte Status(EconetMessage reply) => reply.Payload[1];

    [Test]
    public void Flog_WhenNotLoggedIn_ReturnsWhoAreYou()
    {
        Assert.That(Status(_harness.HandleOsCli("LOGOFF 1\r")), Is.EqualTo(FileServerErrors.WhoAreYou));
    }

    [Test]
    public void Flog_AsNonAdmin_ReturnsInsufficientAccess()
    {
        _harness.LoginAsUser();

        Assert.That(Status(_harness.HandleOsCli("LOGOFF 1\r")), Is.EqualTo(FileServerErrors.InsufficientAccess));
    }

    [Test]
    public void Flog_ByStation_LogsOffThatSession()
    {
        _harness.LoginAsSyst(); // session at net 1, station 1

        Assert.That(Status(_harness.HandleOsCli("LOGOFF 1.1\r")), Is.EqualTo(0), "force-logoff should succeed");

        // The session is gone: a login-required fsop from that station now gets "Who are you ?".
        Assert.That(Status(_harness.Send(FileServerOpcodes.ReadUserEnv)), Is.EqualTo(FileServerErrors.WhoAreYou));
    }

    [Test]
    public void Flog_ByUsername_LogsOffThatUsersSession()
    {
        _harness.LoginAsSyst();

        Assert.That(Status(_harness.HandleOsCli("LOGOFF SYST\r")), Is.EqualTo(0));
        Assert.That(Status(_harness.Send(FileServerOpcodes.ReadUserEnv)), Is.EqualTo(FileServerErrors.WhoAreYou));
    }

    [Test]
    public void Flog_BadStationSpecification_ReturnsError()
    {
        _harness.LoginAsSyst();

        // Station octet 999 is out of range -> "Bad station specification" (0xFF).
        Assert.That(Status(_harness.HandleOsCli("LOGOFF 1.999\r")), Is.EqualTo(FileServerErrors.Unsupported));
    }

    [Test]
    public void Flog_UnknownUser_ReturnsError()
    {
        _harness.LoginAsSyst();

        Assert.That(Status(_harness.HandleOsCli("LOGOFF NOBODY\r")), Is.EqualTo(FileServerErrors.Unsupported));
    }

    [Test]
    public void Flog_Abbreviated_FLO_Works()
    {
        _harness.LoginAsSyst();

        // FLOG minimum abbreviation is 3, so "*FLO" must reach it.
        Assert.That(Status(_harness.HandleOsCli("FLO 1.1\r")), Is.EqualTo(0));
    }
}
