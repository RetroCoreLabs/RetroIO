//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet.Fsop;

/// <summary>
/// Breadth coverage of the authentication/authorisation gate for EVERY fsop, driven off the real
/// <see cref="FileServerCommandHandler"/> with a mock backend. Answers the two questions across the
/// whole opcode surface at once: does an opcode reject a caller with **no user**, and does a
/// **system-only** opcode reject a **normal user**?
///
/// These hold regardless of the FSOP table refactor: the login gate lives centrally in
/// <c>FileServerCommandHandler.Handle</c> and the admin gate in the individual handlers, and the
/// strangler migration preserves both — so this suite is a migration guard.
/// </summary>
[TestFixture]
[Category("Unit")]
internal sealed class FsopAuthGatingTests : FsopProtocolTestBase
{
    // Every fsop that REQUIRES login: opcodes 0x01–0x20 except the three login-exempt ones
    // (OsCli 0x00, ReadDiscNames 0x0E, ReadVersion 0x19). Name is for the test label only.
    private static IEnumerable<TestCaseData> LoginRequiredOpcodes()
    {
        yield return Case(FileServerOpcodes.Save, "Save");
        yield return Case(FileServerOpcodes.Load, "Load");
        yield return Case(FileServerOpcodes.Examine, "Examine");
        yield return Case(FileServerOpcodes.CatHeader, "CatHeader");
        yield return Case(FileServerOpcodes.LoadAs, "LoadAs");
        yield return Case(FileServerOpcodes.Open, "Open");
        yield return Case(FileServerOpcodes.Close, "Close");
        yield return Case(FileServerOpcodes.GetByte, "GetByte");
        yield return Case(FileServerOpcodes.PutByte, "PutByte");
        yield return Case(FileServerOpcodes.GetBytes, "GetBytes");
        yield return Case(FileServerOpcodes.PutBytes, "PutBytes");
        yield return Case(FileServerOpcodes.GetRandomAccessInfo, "GetRandomAccessInfo");
        yield return Case(FileServerOpcodes.SetRandomAccessInfo, "SetRandomAccessInfo");
        yield return Case(FileServerOpcodes.ReadLoggedOnUsers, "ReadLoggedOnUsers");
        yield return Case(FileServerOpcodes.ReadTime, "ReadTime");
        yield return Case(FileServerOpcodes.EndOfFile, "EndOfFile");
        yield return Case(FileServerOpcodes.GetObjectInfo, "GetObjectInfo");
        yield return Case(FileServerOpcodes.SetObjectInfo, "SetObjectInfo");
        yield return Case(FileServerOpcodes.Delete, "Delete");
        yield return Case(FileServerOpcodes.ReadUserEnv, "ReadUserEnv");
        yield return Case(FileServerOpcodes.SetBootOpt, "SetBootOpt");
        yield return Case(FileServerOpcodes.Bye, "Bye");
        yield return Case(FileServerOpcodes.ReadUserInfo, "ReadUserInfo");
        yield return Case(FileServerOpcodes.FreeSpace, "FreeSpace");
        yield return Case(FileServerOpcodes.CreateDir, "CreateDir");
        yield return Case(FileServerOpcodes.SetRtc, "SetRtc");
        yield return Case(FileServerOpcodes.CreateFile, "CreateFile");
        yield return Case(FileServerOpcodes.UserFreeSpace, "UserFreeSpace");
        yield return Case(FileServerOpcodes.SetUserFreeSpace, "SetUserFreeSpace");
        yield return Case(FileServerOpcodes.ReadClientId, "ReadClientId");
    }

    // fsops only a system-privileged (admin) user may run.
    private static IEnumerable<TestCaseData> SystemOnlyOpcodes()
    {
        yield return Case(FileServerOpcodes.SetRtc, "SetRtc");
        yield return Case(FileServerOpcodes.SetUserFreeSpace, "SetUserFreeSpace");
    }

    private static TestCaseData Case(byte opcode, string name) =>
        new TestCaseData(opcode).SetName($"{{m}}_0x{opcode:X2}_{name}");

    // -- No user: every login-required opcode must answer "Who are you ?" ---------------------------

    [TestCaseSource(nameof(LoginRequiredOpcodes))]
    public void WithoutLogin_LoginRequiredOpcode_ReturnsWhoAreYou(byte opcode)
    {
        // No *I AM was sent. The central gate rejects before any handler runs, so empty params are fine.
        EconetMessage reply = Harness.Send(opcode);

        AssertWhoAreYou(reply);
    }

    // -- No user: the three login-exempt opcodes must be served (NOT "Who are you ?"). They differ in
    //    shape, so each is explicit — OsCli's canonical pre-login use is *I AM (login) itself.

    [Test]
    public void WithoutLogin_OsCliLogin_IsServed()
    {
        // *I AM is processed with no pre-existing session (that is the whole point of OsCli exemption).
        EconetMessage reply = Harness.HandleOsCli("I AM SYST \r");

        AssertNotWhoAreYou(reply);
    }

    [Test]
    public void WithoutLogin_ReadDiscNames_IsServed()
    {
        AssertNotWhoAreYou(Harness.Send(FileServerOpcodes.ReadDiscNames));
    }

    [Test]
    public void WithoutLogin_ReadVersion_IsServed()
    {
        AssertNotWhoAreYou(Harness.Send(FileServerOpcodes.ReadVersion));
    }

    // -- Wrong privilege: a normal user hitting a system-only opcode gets "Insufficient access" -----

    [TestCaseSource(nameof(SystemOnlyOpcodes))]
    public void AsNormalUser_SystemOnlyOpcode_ReturnsInsufficientAccess(byte opcode)
    {
        LoginNormalUser(); // USER is not an admin

        EconetMessage reply = Harness.Send(opcode);

        AssertInsufficientAccess(reply);
    }

    // -- Sanity: the same system-only opcodes are ALLOWED for the admin (not 0xBF/0xBD) -------------

    [TestCaseSource(nameof(SystemOnlyOpcodes))]
    public void AsAdmin_SystemOnlyOpcode_IsNotRejectedByGates(byte opcode)
    {
        LoginAdmin(); // SYST is an admin

        EconetMessage reply = Harness.Send(opcode);

        Assert.That(StatusOf(reply), Is.Not.EqualTo(FileServerErrors.WhoAreYou));
        Assert.That(StatusOf(reply), Is.Not.EqualTo(FileServerErrors.InsufficientAccess));
    }

    // -- Wrong user at the login (OSCLI) layer ------------------------------------------------------

    [Test]
    public void Login_UnknownUser_IsRejected()
    {
        EconetMessage reply = Harness.HandleOsCli("I AM NOBODY \r");

        Assert.That(ReplyText(reply), Does.Contain("not known").IgnoreCase);
    }

    [Test]
    public void Login_WrongPassword_IsRejected()
    {
        // USER accepts an EMPTY password only; supplying one is a wrong-password attempt.
        EconetMessage reply = Harness.HandleOsCli("I AM USER SECRET \r");

        Assert.That(ReplyText(reply), Does.Contain("Wrong password").IgnoreCase);
    }

    // The human-readable text of an error reply is the ASCII between the 2-byte status and the CR.
    private static string ReplyText(EconetMessage reply) =>
        Encoding.ASCII.GetString(reply.Payload, 2, reply.Payload.Length - 3);
}
