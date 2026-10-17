//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Identity;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Validates DNFS-style <c>*I AM</c> login and <c>*CAT</c> catalogue flows over OSCLI (fsop 0x00).
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class OsCliIamCatTests
{
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new EconetFsTestHarness();
    }

    [Test]
    public void Iam_Syst_ReturnsPiFsLoginSuccessPacket()
    {
        byte[] payload = _harness.HandleOsCli("I AM SYST \r").Payload;

        Assert.That(payload, Is.EqualTo(new byte[]
        {
            FileServerOpcodes.LoginSuccessReturnCode,
            0,
            AcornFileHandles.WireUrd,
            AcornFileHandles.WireCwd,
            AcornFileHandles.WireLib,
            0,
        }));
    }

    [Test]
    public void Iam_SystUsername_IsCaseInsensitive()
    {
        byte[] payload = _harness.HandleOsCli("I am syst \r").Payload;

        Assert.That(payload[0], Is.EqualTo(FileServerOpcodes.LoginSuccessReturnCode));
        Assert.That(payload[1], Is.EqualTo(0));
    }

    [Test]
    public void Iam_UnknownUser_ReturnsUserNotKnown()
    {
        byte[] payload = _harness.HandleOsCli("I AM NOBODY \r").Payload;

        Assert.That(payload[1], Is.EqualTo(FileServerErrors.WrongPassword));
        Assert.That(Encoding.ASCII.GetString(payload, 2, payload.Length - 3), Does.Contain("not known").IgnoreCase);
    }

    [Test]
    public void Iam_SystWithWrongPassword_ReturnsWrongPassword()
    {
        byte[] payload = _harness.HandleOsCli("I AM SYST WRONGPASSWORD\r").Payload;

        Assert.That(payload[0], Is.Not.EqualTo(FileServerOpcodes.LoginSuccessReturnCode));
        Assert.That(payload[1], Is.EqualTo(FileServerErrors.WrongPassword));
    }

    [Test]
    public void Iam_SystWithPasswordHash_RejectsWrongPassword()
    {
        RetroUser? syst = _harness.IdentityProvider.GetUserByUsernameAsync("SYST").AsTask().GetAwaiter().GetResult();
        Assert.That(syst, Is.Not.Null);
        _harness.IdentityProvider.SetPasswordAsync(syst!.UserId, "MYPASSWORD").AsTask().GetAwaiter().GetResult();

        byte[] payload = _harness.HandleOsCli("I AM SYST WRONGPASSWORD\r").Payload;

        Assert.That(payload[0], Is.Not.EqualTo(FileServerOpcodes.LoginSuccessReturnCode));
        Assert.That(payload[1], Is.EqualTo(FileServerErrors.WrongPassword));
    }

    [Test]
    public void Iam_SystWithPasswordHash_AcceptsCorrectPassword()
    {
        RetroUser? syst = _harness.IdentityProvider.GetUserByUsernameAsync("SYST").AsTask().GetAwaiter().GetResult();
        Assert.That(syst, Is.Not.Null);
        _harness.IdentityProvider.SetPasswordAsync(syst!.UserId, "MYPASSWORD").AsTask().GetAwaiter().GetResult();

        byte[] payload = _harness.HandleOsCli("I AM SYST MYPASSWORD\r").Payload;

        Assert.That(payload[0], Is.EqualTo(FileServerOpcodes.LoginSuccessReturnCode));
        Assert.That(payload[1], Is.EqualTo(0));
    }

    [Test]
    public void Bye_WithoutLogin_ReturnsWhoAreYou()
    {
        byte[] payload = _harness.HandleOsCli("BYE\r").Payload;

        Assert.That(payload[1], Is.EqualTo(FileServerErrors.WhoAreYou));
    }

    [Test]
    public void Bye_OpcodeWithoutLogin_ReturnsWhoAreYou()
    {
        byte[] payload = _harness.HandleLoggedIn(FileServerOpcodes.Bye, []).Payload;

        Assert.That(payload[1], Is.EqualTo(FileServerErrors.WhoAreYou));
    }

    [Test]
    public void Bye_AfterLogin_ReturnsSuccess()
    {
        _harness.HandleOsCli("I AM SYST \r");
        byte[] payload = _harness.HandleOsCli("BYE\r").Payload;

        Assert.That(payload[0], Is.EqualTo(0));
        Assert.That(payload[1], Is.EqualTo(0));
    }

    [Test]
    public void Iam_WithPasswordUser_SucceedsWhenPasswordMatches()
    {
        RetroUser alice = new RetroUser
        {
            UserId = Guid.NewGuid(),
            Username = "alice",
            DisplayName = "Alice",
            IsActive = true,
            AllowEconet = true,
        };
        _harness.IdentityProvider.CreateUserAsync(alice).AsTask().GetAwaiter().GetResult();
        _harness.IdentityProvider.SetPasswordAsync(alice.UserId, "secret").AsTask().GetAwaiter().GetResult();

        byte[] payload = _harness.HandleOsCli("I AM alice secret\r").Payload;

        Assert.That(payload[0], Is.EqualTo(FileServerOpcodes.LoginSuccessReturnCode));
        Assert.That(payload[1], Is.EqualTo(0));
    }

    [Test]
    public void Iam_SecondIam_ReplacesStaleSession()
    {
        _harness.HandleOsCli("I AM SYST \r");
        byte[] payload = _harness.HandleOsCli("I AM SYST \r").Payload;

        Assert.That(payload[0], Is.EqualTo(FileServerOpcodes.LoginSuccessReturnCode));
        Assert.That(payload[1], Is.EqualTo(0));
        Assert.That(payload[1], Is.Not.EqualTo(FileServerErrors.BadCommand));
    }

    [Test]
    public void Cat_WithoutLogin_ReturnsWhoAreYou()
    {
        byte[] payload = _harness.HandleOsCli("CAT\r").Payload;

        Assert.That(payload[1], Is.EqualTo(FileServerErrors.WhoAreYou));
    }

    [Test]
    public void Cat_AfterIam_RedirectsToExamineWithCurrentDirectory()
    {
        _harness.HandleOsCli("I AM SYST \r");
        byte[] payload = _harness.HandleOsCli("CAT\r").Payload;

        Assert.That(payload[0], Is.EqualTo(FileServerOpcodes.Examine));
        Assert.That(payload[1], Is.EqualTo(0));
        Assert.That(AcornPath.ReadCrTerminatedString(payload.AsSpan(2)), Is.EqualTo("."));
        Assert.That(payload[^1], Is.EqualTo(FileServerReply.CarriageReturn));
    }

    [Test]
    public void Cat_DotOsCli_RedirectsSameAsCat()
    {
        _harness.HandleOsCli("I AM SYST \r");
        byte[] payload = _harness.HandleOsCli(".\r").Payload;

        Assert.That(payload[0], Is.EqualTo(FileServerOpcodes.Examine));
        Assert.That(AcornPath.ReadCrTerminatedString(payload.AsSpan(2)), Is.EqualTo("."));
    }

    [Test]
    public void Cat_AfterIam_FullDnfsSequence_ListsDirectoryEntries()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] redirect = _harness.HandleOsCli("CAT\r").Payload;
        Assert.That(redirect[0], Is.EqualTo(FileServerOpcodes.Examine));

        byte[] header = _harness.HandleLoggedIn(
            FileServerOpcodes.CatHeader,
            Encoding.ASCII.GetBytes(".\r")).Payload;
        Assert.That(header, Has.Length.EqualTo(35));
        Assert.That(header[^1], Is.EqualTo(0x80));
        // PiFS fs.c:10388 - "$" names the disc root only; the home dir ($.USER) reports "USER".
        // The CatHeader (0x04) and the cmd6 directory block below MUST agree on this name.
        // Re-baselined 2026-07-17 (PiFS naming parity).
        Assert.That(Encoding.ASCII.GetString(header, 2, 11).TrimEnd(), Is.EqualTo("USER"));

        byte[] directoryInfo = _harness.HandleLoggedIn(
            FileServerOpcodes.GetObjectInfo,
            [(byte)6, (byte)'.', FileServerReply.CarriageReturn]).Payload;
        Assert.That(directoryInfo, Has.Length.EqualTo(17));
        Assert.That(directoryInfo[2], Is.EqualTo(0x02));
        Assert.That(Encoding.ASCII.GetString(directoryInfo, 5, 10), Is.EqualTo("USER      "));
        Assert.That(directoryInfo[16], Is.EqualTo(3));

        byte[] helloInfo = _harness.HandleLoggedIn(
            FileServerOpcodes.GetObjectInfo,
            BuildGetObjectInfoRequest(1, "HELLO")).Payload;
        Assert.That(helloInfo, Has.Length.EqualTo(5));
        Assert.That(helloInfo[2], Is.EqualTo(0x01));

        byte[] readmeInfo = _harness.HandleLoggedIn(
            FileServerOpcodes.GetObjectInfo,
            BuildGetObjectInfoRequest(1, "README")).Payload;
        Assert.That(readmeInfo, Has.Length.EqualTo(5));
        Assert.That(readmeInfo[2], Is.EqualTo(0x01));

        byte[] basicInfo = _harness.HandleLoggedIn(
            FileServerOpcodes.GetObjectInfo,
            BuildGetObjectInfoRequest(1, "BASIC")).Payload;
        Assert.That(basicInfo, Has.Length.EqualTo(5));
        Assert.That(basicInfo[2], Is.EqualTo(0x01));
    }

    [Test]
    public void Cat_AfterIam_ExamineListsHelloAndReadme()
    {
        _harness.HandleOsCli("I AM SYST \r");
        _harness.HandleOsCli("CAT\r");

        byte[] examine = _harness.HandleLoggedIn(
            FileServerOpcodes.Examine,
            [0, 0, 0xFF, (byte)'.']).Payload;

        Assert.That(examine[2], Is.EqualTo(3));
        Assert.That(examine[^1], Is.EqualTo(0x80));

        string first = Encoding.ASCII.GetString(examine, 4, 10).TrimEnd();
        string second = Encoding.ASCII.GetString(examine, 4 + 27, 10).TrimEnd();
        string third = Encoding.ASCII.GetString(examine, 4 + 54, 10).TrimEnd();
        Assert.That(new[] { first, second, third }, Is.EquivalentTo(EconetSampleCatalog.DefaultFileNames));
    }

    [Test]
    public void Cat_WithPath_RedirectsToExamineWithRequestedPath()
    {
        _harness.HandleOsCli("I AM SYST \r");
        byte[] payload = _harness.HandleOsCli("CAT FOO\r").Payload;

        Assert.That(payload[0], Is.EqualTo(FileServerOpcodes.Examine));
        Assert.That(AcornPath.ReadCrTerminatedString(payload.AsSpan(2)), Is.EqualTo("FOO"));
    }

    [Test]
    public void GetObjectInfo_Cmd1_WireCwdZero_ReturnsNotFound()
    {
        // PiFS ground truth (utilities/fs.c): a wire CWD handle of 0 overwrites the session's
        // current handle (fs.c:9951) and then fs_normalize_path_wildcard rejects it because
        // fhandles[0] is never an open handle (fs.c:2341, FS_PATH_ERR_NODIR). For commands 1-5
        // that NODIR is returned as the 3-byte soft not-found [0,0,0] (fs.c:5641-5642), NOT a
        // resolved file block. Do not "resolve to the session CSD" here - that reintroduces the
        // BeebEm *CAT "Net error".
        _harness.HandleOsCli("I AM SYST \r");

        byte[] parameters = new byte[1 + 6];
        parameters[0] = 1;
        Encoding.ASCII.GetBytes("HELLO\r").CopyTo(parameters, 1);
        EconetMessage request = EconetFsTestHarness.CreateLoggedInRequest(
            FileServerOpcodes.GetObjectInfo,
            parameters,
            urd: 0,
            cwd: 0,
            lib: 0);
        byte[] payload = _harness.Handler.Handle(request)!.Payload;

        Assert.That(payload, Has.Length.EqualTo(3));
        Assert.That(payload[0], Is.EqualTo(0));
        Assert.That(payload[1], Is.EqualTo(0));
        Assert.That(payload[2], Is.EqualTo(0), "PiFS flags cmd 1-5 not-found with a single 0 byte");
    }

    [Test]
    public void GetObjectInfo_FilenameAtByteFiveWithoutCommandByte_WireCwdZero_ReturnsNotFound()
    {
        // See GetObjectInfo_Cmd1_WireCwdZero_ReturnsNotFound: wire CWD 0 -> PiFS NODIR ->
        // 3-byte soft not-found (fs.c:2341, 5641-5642), regardless of the parsed command.
        _harness.HandleOsCli("I AM SYST \r");

        byte[] parameters = Encoding.ASCII.GetBytes("HELLO\r");
        EconetMessage request = EconetFsTestHarness.CreateLoggedInRequest(
            FileServerOpcodes.GetObjectInfo,
            parameters,
            urd: 0,
            cwd: 0,
            lib: 0);
        byte[] payload = _harness.Handler.Handle(request)!.Payload;

        Assert.That(payload, Has.Length.EqualTo(3));
        Assert.That(payload[2], Is.EqualTo(0));
    }

    [Test]
    public void GetObjectInfo_Cmd6OnlyCr_ReturnsSeventeenByteDirBlock()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] parameters = [(byte)6, FileServerReply.CarriageReturn];
        byte[] payload = _harness.HandleLoggedIn(FileServerOpcodes.GetObjectInfo, parameters).Payload;

        Assert.That(payload, Has.Length.EqualTo(17));
        Assert.That(payload[2], Is.EqualTo(0x02));
        Assert.That(payload[4], Is.EqualTo(10));
        Assert.That(payload[16], Is.EqualTo(3));
    }

    [Test]
    public void GetObjectInfo_DotPathWithoutCommandByte_ReturnsCmd6DirBlock()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] parameters = Encoding.ASCII.GetBytes(".\r");
        byte[] payload = _harness.HandleLoggedIn(FileServerOpcodes.GetObjectInfo, parameters).Payload;

        Assert.That(payload, Has.Length.EqualTo(17));
        Assert.That(payload[2], Is.EqualTo(0x02));
    }

    [Test]
    public void Examine_DotPathWithoutArgBytes_ReturnsCatalogueEntries()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] parameters = Encoding.ASCII.GetBytes(".\r");
        byte[] examine = _harness.HandleLoggedIn(FileServerOpcodes.Examine, parameters).Payload;

        Assert.That(examine[2], Is.EqualTo(3));
        Assert.That(examine[^1], Is.EqualTo(0x80));
        int firstFileOffset = 4;
        Assert.That(examine[firstFileOffset + 24], Is.Not.EqualTo(0x02));
        Assert.That(examine[firstFileOffset + 25], Is.EqualTo(0x00).Or.EqualTo((byte)(examine[firstFileOffset + 24] >> 8)));
    }

    [Test]
    public void Cat_FullFlow_WithCmd6CrOnlyAndPathOnlyExamine_Succeeds()
    {
        DnfsClientSimulator client = new DnfsClientSimulator(_harness);
        Assert.That(client.OsCli("I AM SYST \r").IsLoginSuccess, Is.True);

        DnfsReply redirect = client.OsCli("CAT\r");
        Assert.That(redirect.IsExamineRedirect, Is.True);

        DnfsReply header = client.Opcode(FileServerOpcodes.CatHeader, Encoding.ASCII.GetBytes(".\r"));
        Assert.That(header.IsProtocolSuccess, Is.True);

        DnfsReply directoryInfo = client.Opcode(FileServerOpcodes.GetObjectInfo, [(byte)6, FileServerReply.CarriageReturn]);
        Assert.That(directoryInfo.Payload.Length, Is.EqualTo(17));

        DnfsReply examine = client.Opcode(FileServerOpcodes.Examine, Encoding.ASCII.GetBytes(".\r"));
        Assert.That(examine.IsProtocolSuccess, Is.True);
        Assert.That(examine.Payload[2], Is.GreaterThan(0));
    }

    private static byte[] BuildGetObjectInfoRequest(byte command, string path)
    {
        byte[] pathBytes = Encoding.ASCII.GetBytes(path + "\r");
        byte[] request = new byte[1 + pathBytes.Length];
        request[0] = command;
        pathBytes.CopyTo(request, 1);
        return request;
    }
}
