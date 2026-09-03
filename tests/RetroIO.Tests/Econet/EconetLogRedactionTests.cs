//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Guards that login passwords never survive into any log rendering.
/// </summary>
/// <remarks>
/// A live BeebEm trace on 2026-07-17 showed <c>ascii="I AM SYST MYPASSWORD"</c> logged in the clear.
/// Redaction happens at the byte level, so these tests assert the secret is gone from BOTH the
/// command-text form and the raw request bytes, since both feed the log formatter.
/// </remarks>
[TestFixture]
[Category("Unit")]
public sealed class EconetLogRedactionTests
{
    private const byte OsCli = 0x00;

    // Builds the raw request bytes for an OSCLI command: 5-byte header then the command text + CR.
    private static byte[] BuildOsCliRaw(string command)
    {
        byte[] text = Encoding.ASCII.GetBytes(command + "\r");
        byte[] raw = new byte[5 + text.Length];
        raw[0] = 0x90; // reply port
        raw[1] = OsCli;
        // URD/CWD/LIB left zero
        for (int i = 0; i < text.Length; i++)
            raw[5 + i] = text[i];
        return raw;
    }

    private static string AsciiOf(byte[] bytes)
    {
        StringBuilder builder = new StringBuilder(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            byte v = bytes[i];
            builder.Append(v is >= 0x20 and <= 0x7E ? (char)v : '.');
        }
        return builder.ToString();
    }

    // ---- Command-text redaction ----

    [Test]
    public void IAm_WithPassword_MasksPassword()
    {
        string redacted = EconetLogRedaction.RedactCommandText("I AM SYST MYPASSWORD");
        Assert.That(redacted, Is.EqualTo("I AM SYST **********"));
        Assert.That(redacted, Does.Not.Contain("MYPASSWORD"));
    }

    [Test]
    public void IAm_WithoutPassword_IsUnchanged()
    {
        // Username only — nothing secret to hide, and the wrong-password path needs to still work.
        Assert.That(EconetLogRedaction.RedactCommandText("I AM SYST"), Is.EqualTo("I AM SYST"));
    }

    [TestCase("IAM SYST SECRET", "IAM SYST ******")]
    [TestCase("LOGIN BOB HUNTER2", "LOGIN BOB *******")]
    [TestCase("LOGON ADMIN LETMEIN", "LOGON ADMIN *******")]
    public void AllLoginVerbs_MaskPassword(string input, string expected)
    {
        Assert.That(EconetLogRedaction.RedactCommandText(input), Is.EqualTo(expected));
    }

    [Test]
    public void LoginVerb_IsCaseInsensitive()
    {
        string redacted = EconetLogRedaction.RedactCommandText("i am syst secret");
        Assert.That(redacted, Does.Not.Contain("secret"));
        Assert.That(redacted, Does.EndWith("******"));
    }

    [Test]
    public void NonLoginCommand_IsUnchanged()
    {
        Assert.That(EconetLogRedaction.RedactCommandText("CAT"), Is.EqualTo("CAT"));
        Assert.That(EconetLogRedaction.RedactCommandText("LOAD README 3000"), Is.EqualTo("LOAD README 3000"));
    }

    // ---- Raw-byte redaction: the password must be gone from the actual bytes ----

    [Test]
    public void RawRequest_Login_HasPasswordBytesMasked()
    {
        byte[] raw = BuildOsCliRaw("I AM SYST MYPASSWORD");
        byte[]? redacted = EconetLogRedaction.RedactRawRequest(OsCli, raw);

        Assert.That(redacted, Is.Not.Null, "a login with a password must be redacted");
        string ascii = AsciiOf(redacted!);
        Assert.That(ascii, Does.Not.Contain("MYPASSWORD"));
        Assert.That(ascii, Does.Contain("I AM SYST "), "the verb and username stay visible");
        Assert.That(ascii, Does.Contain("**********"), "the password is masked in the bytes");
    }

    [Test]
    public void RawRequest_Login_KeepsHeaderAndUsername()
    {
        byte[] raw = BuildOsCliRaw("I AM SYST SECRET");
        byte[]? redacted = EconetLogRedaction.RedactRawRequest(OsCli, raw);

        Assert.That(redacted, Is.Not.Null);
        Assert.That(redacted!.Length, Is.EqualTo(raw.Length), "redaction must not change length");
        Assert.That(redacted[0], Is.EqualTo((byte)0x90), "reply port preserved");
        Assert.That(redacted[1], Is.EqualTo(OsCli), "opcode preserved");
    }

    [Test]
    public void RawRequest_NonLogin_ReturnsNull()
    {
        byte[] raw = BuildOsCliRaw("CAT");
        Assert.That(EconetLogRedaction.RedactRawRequest(OsCli, raw), Is.Null);
    }

    [Test]
    public void RawRequest_NonOsCliOpcode_ReturnsNull()
    {
        // A SAVE (0x01) whose data happens to spell a login verb must not be treated as one.
        byte[] raw = BuildOsCliRaw("I AM SYST SECRET");
        raw[1] = 0x01;
        Assert.That(EconetLogRedaction.RedactRawRequest(0x01, raw), Is.Null);
    }

    [Test]
    public void RawRequest_LoginWithoutPassword_ReturnsNull()
    {
        byte[] raw = BuildOsCliRaw("I AM SYST");
        Assert.That(EconetLogRedaction.RedactRawRequest(OsCli, raw), Is.Null);
    }

    [Test]
    public void FullTrace_NeverContainsThePassword()
    {
        // End-to-end: the assembled request trace (what actually reaches the log) must be clean.
        byte[] raw = BuildOsCliRaw("I AM SYST TOPSECRET");
        Assert.That(FileServerRequest.TryParse(raw, out FileServerRequest request), Is.True);

        string trace = EconetProtocolDiagnostics.FormatRequestTrace(request, null);

        Assert.That(trace, Does.Not.Contain("TOPSECRET"), "password must not appear in the hex/ascii/parse trace");
        // And the hex bytes for "TOPSECRET" (54 4F 50 53 45 43 52 45 54) must be gone too.
        Assert.That(trace, Does.Not.Contain("54 4F 50 53 45"), "password bytes must not appear in the hex dump");
    }

    [Test]
    public void ShortSummary_Login_IsRedacted()
    {
        byte[] raw = BuildOsCliRaw("I AM SYST HUNTER2");
        Assert.That(FileServerRequest.TryParse(raw, out FileServerRequest request), Is.True);

        string summary = EconetProtocolDiagnostics.FormatShortSummary(request);

        Assert.That(summary, Does.Not.Contain("HUNTER2"));
        Assert.That(summary, Does.Contain("I AM SYST"));
    }
}
