//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Identity;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Embedded RetroFS over UDP — validates wire replies and activity monitor (commands + transfers).
/// </summary>
[TestFixture]
[Category("Unit")]
[NonParallelizable]
public sealed class LoginByeUdpServerTests
{
    private EconetUdpTestServer _server = null!;
    private EconetAunTestClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp() =>
        _server = await EconetUdpTestServer.StartAsync();

    [OneTimeTearDown]
    public async Task OneTimeTearDown() =>
        await _server.DisposeAsync();

    [SetUp]
    public void SetUp() =>
        _client = new EconetAunTestClient(serverPort: _server.UdpPort);

    [TearDown]
    public void TearDown() =>
        _client.Dispose();

    [Test]
    public void LoginThenBye_EmbeddedUdpServer_MonitorShowsOneLinePerRequest()
    {
        EconetTestMonitor monitor = _server.Monitor;
        monitor.Mark();

        byte[] loginPayload = _client.Login("SYST");
        Assert.That(loginPayload, Is.EqualTo(new byte[]
        {
            FileServerOpcodes.LoginSuccessReturnCode,
            0,
            AcornFileHandles.WireUrd,
            AcornFileHandles.WireCwd,
            AcornFileHandles.WireLib,
            0,
        }), () => monitor.FormatTrace());

        IReadOnlyList<EconetCommandEntry> loginLines = monitor.CommandsSinceMark();
        Assert.That(loginLines.Count, Is.EqualTo(1), () => monitor.FormatTraceSinceMark());
        EconetActivityAssertions.AssertCommand(loginLines[0], "*I AM", true, "Login OK", monitor);
        monitor.Mark();

        byte[] byePayload = _client.Bye(sendWireHandles: true);
        Assert.That(byePayload, Is.EqualTo(new byte[] { 0, 0 }), () => monitor.FormatTrace());
        Assert.That(_client.IsLoggedIn, Is.False);

        IReadOnlyList<EconetCommandEntry> byeLines = monitor.CommandsSinceMark();
        Assert.That(byeLines.Count, Is.EqualTo(1), () => monitor.FormatTraceSinceMark());
        EconetActivityAssertions.AssertCommand(byeLines[0], "*BYE", true, "OK", monitor);
        monitor.Mark();

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            byte[] extraBye = _client.Bye(sendWireHandles: false);
            Assert.That(extraBye[1], Is.EqualTo(FileServerErrors.WhoAreYou),
                () => $"PiFS: BYE without session attempt {attempt}\n{monitor.FormatTrace()}");
        }

        IReadOnlyList<EconetCommandEntry> extraByeLines = monitor.CommandsSinceMark();
        Assert.That(extraByeLines.Count, Is.EqualTo(3), () => monitor.FormatTraceSinceMark());
        for (int i = 0; i < 3; i++)
        {
            Assert.That(extraByeLines[i].Success, Is.False, () => monitor.FormatTraceSinceMark());
            Assert.That(extraByeLines[i].Result, Does.Contain("Not logged in").IgnoreCase,
                () => monitor.FormatTraceSinceMark());
        }
    }

    [Test]
    public void WrongPassword_MonitorShowsFailureNotLoginOk()
    {
        RetroUser? syst = _server.IdentityProvider.GetUserByUsernameAsync("SYST").AsTask().GetAwaiter().GetResult();
        Assert.That(syst, Is.Not.Null);
        _server.IdentityProvider.SetPasswordAsync(syst!.UserId, "MYPASSWORD").AsTask().GetAwaiter().GetResult();

        EconetTestMonitor monitor = _server.Monitor;
        monitor.Mark();

        byte[] payload = _client.Login("SYST", "WRONGPASSWORD");
        Assert.That(payload[0], Is.Not.EqualTo(FileServerOpcodes.LoginSuccessReturnCode));
        Assert.That(payload[1], Is.EqualTo(FileServerErrors.WrongPassword));

        IReadOnlyList<EconetCommandEntry> lines = monitor.CommandsSinceMark();
        Assert.That(lines.Count, Is.EqualTo(1), () => monitor.FormatTraceSinceMark());
        Assert.That(lines[0].Success, Is.False, () => monitor.FormatTraceSinceMark());
        Assert.That(lines[0].Result, Does.Not.Contain("Login OK"), () => monitor.FormatTraceSinceMark());
    }

    [Test]
    public void LoadHello_EmbeddedUdpServer_MonitorShowsSendTransfer()
    {
        EconetTestMonitor monitor = _server.Monitor;
        int transferMark = monitor.TransferCount;

        _client.Login("SYST");
        monitor.Mark();

        byte[] data = _client.LoadFile("HELLO");
        Assert.That(Encoding.ASCII.GetString(data), Does.Contain("Hello from RetroFS Econet"));

        EconetActivityAssertions.AssertOneNewTransfer(
            monitor,
            transferMark,
            EconetFileTransferDirection.Send,
            "HELLO",
            data.Length);
    }
}
