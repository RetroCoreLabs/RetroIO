//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Locks the unrecognised-OSCLI reply against PiFS ground truth
/// (E:\Dev\Emulators\6502\PiEconetBridge\utilities\fs.c:11302 and :11327 — "Unknown command").
/// </summary>
/// <remarks>
/// REGRESSION GUARD. An OSCLI command the file server does not recognise is NOT an error. The
/// observed protocol behaviour is: return code <c>0x08</c>, then status <c>0x00</c>, then the
/// original command text echoed back unchanged including its terminating CR.
/// <c>0x08</c> tells the NFS/ANFS ROM "I don't know this — you handle it", and the ROM then performs
/// a <c>*RUN</c> of a file with that name (CSD, then library). This single mechanism is what makes
/// BOTH plain <c>*SOMEPROG</c> and <c>*OPT 4,n</c> / SHIFT-BREAK autoboot work: after a login that
/// reports a non-zero boot option, the ROM immediately issues <c>*!BOOT</c>.
///
/// RetroFS previously replied <c>&amp;FE "Bad command"</c>, which the ROM treats as final — so autoboot
/// and every <c>*program</c> invocation failed. Caught live in BeebEm on 2026-07-17: a login carrying
/// boot option 02 was followed by OSCLI "!BOOT" and answered "Bad command". Captured wire log:
/// E:\Dev\Ronny\RetroFS\docs\econet-captures\beebem-session-20260717-bdmzuib2v.log
///
/// Do NOT "fix" this back to an error reply.
/// </remarks>
[TestFixture]
[Category("Unit")]
public sealed class OsCliUnknownCommandTests
{
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new EconetFsTestHarness();
    }

    /// <summary>
    /// The exact case seen live: boot option set -> ROM sends "*!BOOT" -> must get 0x08, not &amp;FE.
    /// </summary>
    [Test]
    public void Bang_Boot_ReturnsUnknownCommand_NotBadCommand()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] payload = _harness.HandleOsCli("!BOOT\r").Payload;

        // GOLDEN: 08 00 '!' 'B' 'O' 'O' 'T' 0D  (fs.c:11302)
        byte[] expected = [0x08, 0x00, 0x21, 0x42, 0x4F, 0x4F, 0x54, FileServerReply.CarriageReturn];
        Assert.That(payload, Is.EqualTo(expected), "PiFS replies 0x08 + original command text");
        Assert.That(payload[1], Is.Not.EqualTo(FileServerErrors.BadCommand),
            "&FE would be final to the NFS ROM and would break autoboot");
    }

    [Test]
    public void UnknownCommand_ReturnCodeIsZeroEight()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] payload = _harness.HandleOsCli("WIBBLE\r").Payload;

        Assert.That(payload[0], Is.EqualTo(0x08), "PiFS 'Unknown command' return code (fs.c:11302)");
        Assert.That(payload[1], Is.EqualTo(0x00));
    }

    /// <summary>PiFS echoes the raw command bytes from data+5, INCLUDING the terminating CR.</summary>
    [Test]
    public void UnknownCommand_EchoesOriginalTextVerbatim_IncludingCr()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] payload = _harness.HandleOsCli("MYPROG ARG1 ARG2\r").Payload;

        string echoed = Encoding.ASCII.GetString(payload, 2, payload.Length - 3);
        Assert.That(echoed, Is.EqualTo("MYPROG ARG1 ARG2"), "arguments must survive for the ROM's *RUN");
        Assert.That(payload[^1], Is.EqualTo(FileServerReply.CarriageReturn), "CR is echoed too");
    }

    /// <summary>
    /// Case must NOT be folded: PiFS copies the raw bytes, and Acorn filenames are case-preserving.
    /// </summary>
    [Test]
    public void UnknownCommand_PreservesCaseOfOriginalText()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] payload = _harness.HandleOsCli("MixedCase\r").Payload;

        string echoed = Encoding.ASCII.GetString(payload, 2, payload.Length - 3);
        Assert.That(echoed, Is.EqualTo("MixedCase"));
    }

    /// <summary>
    /// Login is still enforced first: an unknown command from a station that is not logged in gets
    /// "Who are you ?" (&amp;BF), not 0x08. Verified live in BeebEm 2026-07-17 ("*P" after a server
    /// restart -> 00 BF "Who are you ?"; the message text was later aligned to the reference's space
    /// before '?').
    /// </summary>
    [Test]
    public void UnknownCommand_WhenNotLoggedIn_ReturnsWhoAreYou()
    {
        byte[] payload = _harness.HandleOsCli("WIBBLE\r").Payload;

        Assert.That(payload[0], Is.EqualTo(0), "error replies start with 0x00");
        Assert.That(payload[1], Is.EqualTo(FileServerErrors.WhoAreYou), "&BF Who are you?");
    }

    /// <summary>
    /// A recognised command must NOT fall through to 0x08 — guards against the fall-through being
    /// hoisted above the dispatch chain by a careless edit.
    /// </summary>
    [Test]
    public void RecognisedCommand_DoesNotReturnUnknownCommand()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] payload = _harness.HandleOsCli("DIR\r").Payload;

        Assert.That(payload[0], Is.EqualTo(0x07), "*DIR is recognised: PiFS return code 0x07");
    }
}
