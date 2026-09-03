//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet;

/// <summary>
/// End-to-end DNFS command flows matching BeebEm: <c>*I AM</c>, <c>*BYE</c>, <c>*CAT</c>.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class DnfsOsCliFlowTests
{
    private EconetFsTestHarness _harness = null!;
    private DnfsClientSimulator _client = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new EconetFsTestHarness();
        _client = new DnfsClientSimulator(_harness);
    }

    [Test]
    public void IAmSyst_EmptyPassword_ReturnsWireHandles01_02_04()
    {
        DnfsReply login = _client.OsCli("I AM SYST \r");

        Assert.That(login.IsLoginSuccess, Is.True);
        Assert.That(_client.WireUrd, Is.EqualTo(AcornFileHandles.WireUrd));
        Assert.That(_client.WireCwd, Is.EqualTo(AcornFileHandles.WireCwd));
        Assert.That(_client.WireLib, Is.EqualTo(AcornFileHandles.WireLib));
        Assert.That(_client.IsLoggedIn, Is.True);
    }

    [Test]
    public void IAmSyst_MyPassword_WrongPassword_RejectsLogin()
    {
        _client.SetSystPassword("MYPASSWORD");

        DnfsReply login = _client.OsCli("I AM SYST WRONGPASSWORD\r");

        Assert.That(login.IsLoginSuccess, Is.False);
        Assert.That(login.ErrorCode, Is.EqualTo(FileServerErrors.WrongPassword));
        Assert.That(_client.IsLoggedIn, Is.False);

        IReadOnlyList<EconetCommandEntry> commands = _harness.Monitor.RecentCommands;
        Assert.That(commands.Count, Is.GreaterThanOrEqualTo(1));
        Assert.That(commands[0].Success, Is.False);
        Assert.That(commands[0].Result, Does.Not.Contain("Login OK"));
    }

    [Test]
    public void IAmSyst_MyPassword_CorrectPassword_ThenCat_Succeeds()
    {
        _client.SetSystPassword("MYPASSWORD");

        DnfsReply login = _client.OsCli("I AM SYST MYPASSWORD\r");
        Assert.That(login.IsLoginSuccess, Is.True);

        DnfsCatalogueResult cat = _client.Cat();
        Assert.That(cat.Succeeded, Is.True, () => DescribeCatFailure(cat));
        Assert.That(cat.FileNames, Is.EquivalentTo(EconetSampleCatalog.DefaultFileNames));
        Assert.That(cat.FileInfos, Has.All.Matches<DnfsReply>(r => r.Payload[2] == 0x01));
    }

    [Test]
    public void IAmSyst_WrongPassword_ThenCat_ReturnsWhoAreYou()
    {
        _client.SetSystPassword("MYPASSWORD");
        _client.OsCli("I AM SYST WRONGPASSWORD\r");

        DnfsReply cat = _client.OsCli("CAT\r");

        Assert.That(cat.IsLoginSuccess, Is.False);
        Assert.That(cat.ErrorCode, Is.EqualTo(FileServerErrors.WhoAreYou));
    }

    [Test]
    public void Bye_WithoutLogin_ReturnsWhoAreYou_PiFsAligned()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            DnfsReply bye = _client.OsCli("BYE\r");
            Assert.That(bye.Payload[1], Is.EqualTo(FileServerErrors.WhoAreYou), $"BYE attempt {attempt + 1}");
        }
    }

    [Test]
    public void Bye_OpcodeWithoutLogin_ReturnsWhoAreYou_PiFsAligned()
    {
        DnfsReply bye = _client.Opcode(FileServerOpcodes.Bye, []);
        Assert.That(bye.Payload[1], Is.EqualTo(FileServerErrors.WhoAreYou));
    }

    [Test]
    public void Bye_AfterLogin_ThenCat_ReturnsWhoAreYou()
    {
        _client.OsCli("I AM SYST \r");
        _client.OsCli("BYE\r");

        Assert.That(_client.IsLoggedIn, Is.False);

        DnfsReply cat = _client.OsCli("CAT\r");
        Assert.That(cat.ErrorCode, Is.EqualTo(FileServerErrors.WhoAreYou));
    }

    [Test]
    public void Cat_FullSequence_NoStepReturnsNetErrorByte()
    {
        _client.OsCli("I AM SYST \r");
        DnfsCatalogueResult cat = _client.Cat();

        Assert.That(cat.Succeeded, Is.True, () => DescribeCatFailure(cat));
        Assert.That(cat.Redirect.Payload[1], Is.EqualTo(0));
        Assert.That(cat.Header.Payload[1], Is.EqualTo(0));
        Assert.That(cat.DirectoryInfo.Payload[1], Is.EqualTo(0));
        Assert.That(cat.Examine.Payload[1], Is.EqualTo(0));
        Assert.That(cat.Header.Payload[^1], Is.EqualTo(0x80));
        // PiFS fs.c:10388 - "$" is the disc root; the logged-in home ($.USER) reports "USER".
        // Re-baselined 2026-07-17 (PiFS naming parity).
        Assert.That(Encoding.ASCII.GetString(cat.Header.Payload, 2, 11).TrimEnd(), Is.EqualTo("USER"));
    }

    [Test]
    public void Cat_ActivityLog_RecordsSuccessForEachOpcode()
    {
        _client.OsCli("I AM SYST \r");
        _client.Cat();

        IReadOnlyList<EconetCommandEntry> commands = _harness.Monitor.RecentCommands;
        Assert.That(commands.Count, Is.GreaterThanOrEqualTo(5));

        for (int i = 0; i < 5; i++)
        {
            Assert.That(commands[i].Success, Is.True, () => $"Command {commands[i].Command}\n{_harness.Monitor.FormatTrace()}");
            Assert.That(commands[i].Result, Does.StartWith("OK").Or.StartWith("Login OK"));
        }
    }

    [Test]
    public void GetObjectInfo_MissingFile_ActivityLogRecordsFailure()
    {
        _client.OsCli("I AM SYST \r");
        DnfsReply missing = _client.Opcode(
            FileServerOpcodes.GetObjectInfo,
            BuildGetObjectInfoRequest(1, "NOFILE"));

        Assert.That(missing.Payload, Is.EqualTo(new byte[] { 0, 0, 0 }));
        Assert.That(missing.IsProtocolSuccess, Is.False);

        IReadOnlyList<EconetCommandEntry> commands = _harness.Monitor.RecentCommands;
        Assert.That(commands.Count, Is.GreaterThanOrEqualTo(1));
        Assert.That(commands[0].Success, Is.False);
        Assert.That(commands[0].Result, Is.Not.EqualTo("OK"));
    }

    [Test]
    public void Cat_NonAdminUser_Cmd6ReportsOwnerAndSucceeds()
    {
        DnfsReply login = _client.OsCli("I AM USER \r");
        Assert.That(login.IsLoginSuccess, Is.True);

        DnfsCatalogueResult cat = _client.Cat();
        Assert.That(cat.Succeeded, Is.True, () => DescribeCatFailure(cat));
        Assert.That(cat.DirectoryInfo.Payload[15], Is.EqualTo(0x00), "owner byte must not depend on IsAdmin");
    }

    [Test]
    public void IAmSyst_EmptyPasswordUser_WrongPassword_RejectsEvenWhenAllowEmpty()
    {
        DnfsReply login = _client.OsCli("I AM SYST WRONGPASSWORD\r");

        Assert.That(login.IsLoginSuccess, Is.False);
        Assert.That(login.ErrorCode, Is.EqualTo(FileServerErrors.WrongPassword));
    }

    private static byte[] BuildGetObjectInfoRequest(byte command, string path)
    {
        byte[] pathBytes = Encoding.ASCII.GetBytes(path + "\r");
        byte[] request = new byte[1 + pathBytes.Length];
        request[0] = command;
        pathBytes.CopyTo(request, 1);
        return request;
    }

    private static string DescribeCatFailure(DnfsCatalogueResult cat)
    {
        if (cat.FailedReply == null)
            return "unknown failure";

        byte[] p = cat.FailedReply.Value.Payload;
        return $"{cat.FailedStage}: [{string.Join(", ", p)}]";
    }
}
