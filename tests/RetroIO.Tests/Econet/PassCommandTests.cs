//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Covers <c>*PASS</c> — a previously missing password-change command — including its log redaction.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class PassCommandTests
{
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new EconetFsTestHarness();
        _harness.HandleOsCli("I AM SYST \r");
    }

    [Test]
    public void Pass_WithCorrectOldPassword_Succeeds()
    {
        // The seeded SYST test user has an empty password, so old is "".
        byte[] payload = _harness.HandleOsCli("PASS \"\" NEWPW\r").Payload;

        Assert.That(payload[0], Is.EqualTo(0), "success reply starts 0x00");
        Assert.That(payload[1], Is.EqualTo(0), "no error code");
    }

    [Test]
    public void Pass_WithWrongOldPassword_ReturnsWrongPassword()
    {
        byte[] payload = _harness.HandleOsCli("PASS WRONGOLD NEWPW\r").Payload;

        Assert.That(payload[0], Is.EqualTo(0));
        Assert.That(payload[1], Is.EqualTo(FileServerErrors.WrongPassword));
    }

    [Test]
    public void Pass_WithOneArgument_ReturnsBadCommand()
    {
        byte[] payload = _harness.HandleOsCli("PASS ONLYONE\r").Payload;

        Assert.That(payload[0], Is.EqualTo(0));
        Assert.That(payload[1], Is.EqualTo(FileServerErrors.BadCommand));
    }

    // ---- Redaction: neither password may appear in any log rendering ----

    [Test]
    public void Pass_PasswordsAreRedactedInCommandText()
    {
        string redacted = EconetLogRedaction.RedactCommandText("PASS OLDSECRET NEWSECRET");

        Assert.That(redacted, Does.Not.Contain("OLDSECRET"));
        Assert.That(redacted, Does.Not.Contain("NEWSECRET"));
        Assert.That(redacted, Does.StartWith("PASS "), "the verb stays visible");
    }

    [Test]
    public void Pass_PasswordsAreRedactedInRawBytes()
    {
        // Build the OSCLI raw request and confirm the assembled trace is clean.
        byte[] text = System.Text.Encoding.ASCII.GetBytes("PASS OLDSECRET NEWSECRET\r");
        byte[] raw = new byte[5 + text.Length];
        raw[0] = 0x90;
        for (int i = 0; i < text.Length; i++)
            raw[5 + i] = text[i];

        byte[]? redacted = EconetLogRedaction.RedactRawRequest(0x00, raw);
        Assert.That(redacted, Is.Not.Null, "PASS must be redacted");

        System.Text.StringBuilder ascii = new System.Text.StringBuilder();
        for (int i = 0; i < redacted!.Length; i++)
        {
            byte v = redacted[i];
            ascii.Append(v is >= 0x20 and <= 0x7E ? (char)v : '.');
        }
        Assert.That(ascii.ToString(), Does.Not.Contain("OLDSECRET"));
        Assert.That(ascii.ToString(), Does.Not.Contain("NEWSECRET"));
    }

    [Test]
    public void Pass_WhenNotLoggedIn_ReturnsWhoAreYou()
    {
        EconetFsTestHarness fresh = new EconetFsTestHarness();
        byte[] payload = fresh.HandleOsCli("PASS \"\" NEWPW\r").Payload;

        Assert.That(payload[1], Is.EqualTo(FileServerErrors.WhoAreYou));
    }
}
