//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.IO;
using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer;
using RetroIO.Protocols.Econet.FileServer.Handles;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Conformance tests derived from PiEconetBridge/docs/Econet_Message_Protocol_Analysis.md §6.3 / §7.1.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class ProtocolAnalysisConformanceTests
{
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new EconetFsTestHarness();

    [Test]
    public void UnknownOpcode_ReturnsFsError0xFF()
    {
        Login();
        EconetMessage response = _harness.HandleLoggedIn(0x25, []);
        Assert.That(response.Payload[1], Is.EqualTo(FileServerErrors.Unsupported));
        Assert.That(Encoding.ASCII.GetString(response.Payload, 2, response.Payload.Length - 3), Does.Contain("FS Error"));
    }

    [Test]
    public void UserFreeSpace_0x1E_Returns16MbDummy()
    {
        Login();
        EconetMessage response = _harness.HandleLoggedIn(FileServerOpcodes.UserFreeSpace, []);
        Assert.That(response.Payload, Is.EqualTo(FileServerReply.UserFreeSpaceDummy()));
    }

    [Test]
    public void SetUserFreeSpace_0x1F_NonAdmin_ReturnsInsufficientAccess()
    {
        LoginAs("USER");
        EconetMessage response = _harness.HandleLoggedIn(FileServerOpcodes.SetUserFreeSpace, []);
        Assert.That(response.Payload[1], Is.EqualTo(FileServerErrors.InsufficientAccess));
    }

    [Test]
    public void SetUserFreeSpace_0x1F_Syst_ReturnsSuccess()
    {
        Login();
        EconetMessage response = _harness.HandleLoggedIn(FileServerOpcodes.SetUserFreeSpace, []);
        Assert.That(response.Payload, Is.EqualTo(new byte[] { 0, 0 }));
    }

    [Test]
    public void SetRtc_0x1C_NonAdmin_ReturnsInsufficientAccess()
    {
        LoginAs("USER");
        byte[] time = [0, 0, 1, 1, 50 << 1];
        EconetMessage response = _harness.HandleLoggedIn(FileServerOpcodes.SetRtc, time);
        Assert.That(response.Payload[1], Is.EqualTo(FileServerErrors.InsufficientAccess));
    }

    [Test]
    public void SjAccount_0x40_RequiresLogin()
    {
        EconetMessage response = _harness.Handler.Handle(
            EconetFsTestHarness.CreateLoggedInRequest(FileServerOpcodes.SjOperation, []))!;
        Assert.That(response.Payload[1], Is.EqualTo(FileServerErrors.WhoAreYou));
    }

    [Test]
    public void SjAccount_0x40_LoggedIn_ReturnsStubEntry()
    {
        Login();
        EconetMessage response = _harness.HandleLoggedIn(FileServerOpcodes.SjOperation, new byte[13]);
        Assert.That(response.Payload[0], Is.EqualTo(0));
        Assert.That(response.Payload[1], Is.EqualTo(0));
        Assert.That(response.Payload[4], Is.EqualTo(1));
    }

    [Test]
    public void SjSysInfo_0x41_UnknownOp_ReturnsUnsupported()
    {
        Login();
        EconetMessage response = _harness.HandleLoggedIn(FileServerOpcodes.SjOperationReply, [99]);
        Assert.That(response.Payload[1], Is.EqualTo(FileServerErrors.Unsupported));
    }

    [Test]
    public void GetPutByte_UseWireBytesTwoAndThree()
    {
        Login();
        byte handle = OpenFileForByteIo("HELLO");
        byte getHandleWire = handle; // the open reply already carries the wire (bit mask) form

        EconetMessage getRequest = EconetFsTestHarness.CreateLoggedInRequest(
            FileServerOpcodes.GetByte,
            [],
            urd: getHandleWire);
        EconetMessage getResponse = _harness.Handler.Handle(getRequest)!;
        Assert.That(getResponse.Payload[1], Is.EqualTo(0));
        Assert.That(getResponse.Payload[2], Is.EqualTo((byte)'H'), "Handle must come from wire byte 2");

        byte putValue = (byte)'X';
        EconetMessage putRequest = EconetFsTestHarness.CreateLoggedInRequest(
            FileServerOpcodes.PutByte,
            [],
            urd: getHandleWire,
            cwd: putValue);
        EconetMessage putResponse = _harness.Handler.Handle(putRequest)!;
        Assert.That(putResponse.Payload, Is.EqualTo(new byte[] { 0, 0 }));

        _harness.HandleLoggedIn(FileServerOpcodes.Close, [handle]);
    }

    [Test]
    public void DiscFreeSpace_0x1A_ReturnsEightByteReply()
    {
        Login();
        byte[] name = Encoding.ASCII.GetBytes("RetroFS\r");
        EconetMessage response = _harness.HandleLoggedIn(FileServerOpcodes.FreeSpace, name);
        Assert.That(response.Payload.Length, Is.EqualTo(8));
        Assert.That(response.Payload[0], Is.EqualTo(0));
        Assert.That(response.Payload[1], Is.EqualTo(0));
    }

    private void Login() => LoginAs("SYST");

    private void LoginAs(string user)
    {
        _harness.HandleOsCli($"I AM {user} \r");
    }

    private byte OpenFileForByteIo(string acornName)
    {
        byte[] openParams = new byte[2 + acornName.Length + 1];
        openParams[0] = 1;
        openParams[1] = 0;
        Encoding.ASCII.GetBytes(acornName + "\r").CopyTo(openParams, 2);
        EconetMessage openResponse = _harness.HandleLoggedIn(FileServerOpcodes.Open, openParams);
        Assert.That(openResponse.Payload[1], Is.EqualTo(0), "Open failed");
        return openResponse.Payload[2];
    }
}
