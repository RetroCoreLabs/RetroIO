//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Proves Acorn dot abbreviations work end-to-end through the OSCLI dispatcher, not just in the
/// matcher unit.
/// </summary>
/// <remarks>
/// Before the command registry, OSCLI dispatch used <c>normalized.StartsWith(name)</c>, which asked
/// the question backwards: it rejected the abbreviations users actually type (<c>*CA.</c>, <c>*I.</c>)
/// and captured commands it should not (<c>*EXEC</c> answered by the <c>EX</c> arm). These tests
/// lock the fixed behaviour at the dispatcher level.
/// </remarks>
[TestFixture]
[Category("Unit")]
public sealed class OsCliAbbreviationIntegrationTests
{
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new EconetFsTestHarness();
        _harness.HandleOsCli("I AM SYST \r");
    }

    [Test]
    public void CatAbbreviation_Redirects_LikeFullCat()
    {
        // "*CA." must reach CAT and produce the Examine redirect (return code 0x03), same as "*CAT".
        byte[] full = _harness.HandleOsCli("CAT\r").Payload;
        byte[] abbreviated = _harness.HandleOsCli("CA.\r").Payload;

        Assert.That(abbreviated[0], Is.EqualTo(FileServerOpcodes.Examine), "*CA. redirects to Examine");
        Assert.That(abbreviated[0], Is.EqualTo(full[0]), "*CA. behaves like *CAT");
    }

    [Test]
    public void InfoAbbreviation_DotI_RedirectsToGetObjectInfo()
    {
        // "*I." is the classic case: it must select INFO (GetObjectInfo redirect), NOT login.
        byte[] payload = _harness.HandleOsCli("I.README\r").Payload;
        Assert.That(payload[0], Is.EqualTo(FileServerOpcodes.GetObjectInfo), "*I. selects INFO");
    }

    [Test]
    public void LoadAbbreviation_Lo_IsRecognised()
    {
        // "*LO." must reach LOAD. With no such file it returns Not found — but crucially NOT the
        // 0x08 unknown-command reply, which is what the old StartsWith matching produced.
        byte[] payload = _harness.HandleOsCli("LO.NOSUCHFILE\r").Payload;
        Assert.That(payload[0], Is.EqualTo(0x00), "recognised command, error reply starts 0x00");
        Assert.That(payload[1], Is.EqualTo(FileServerErrors.NotFound), "LOAD of a missing file is Not found");
    }

    [Test]
    public void Exec_IsNotCapturedByExamine()
    {
        // "*EXEC" is not an abbreviation of "EX" (EXEC is not a prefix of EX), so it must fall
        // through to the unknown-command reply (0x08) for the ROM to *RUN — not be answered with an
        // Examine return code as the old arm did.
        byte[] payload = _harness.HandleOsCli("EXEC MYPROG\r").Payload;
        Assert.That(payload[0], Is.EqualTo(0x08), "*EXEC is unknown to the FS; the ROM runs it");
    }

    [Test]
    public void FullExamine_StillRedirects()
    {
        byte[] payload = _harness.HandleOsCli("EX\r").Payload;
        Assert.That(payload[0], Is.EqualTo(FileServerOpcodes.Examine), "*EX still redirects to Examine");
    }

    [Test]
    public void UnknownProgram_ReturnsUnknownCommand_NotBadCommand()
    {
        byte[] payload = _harness.HandleOsCli("SOMEPROG\r").Payload;
        Assert.That(payload[0], Is.EqualTo(0x08), "unknown *program -> 0x08 so the ROM *RUNs it");
    }
}
