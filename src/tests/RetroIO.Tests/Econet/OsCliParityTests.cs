//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.FileServer.Metadata;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Byte-for-byte parity tests for the OSCLI (fsop 0x00) command surface against the reference
/// behaviour.
///
/// Every <c>*</c> command is matched against a command name with a minimum abbreviation length
/// <c>min</c>, by this rule:
///
///   1. Scan from position <c>min</c> for the FIRST ' ' or '.'. Everything before it is the typed
///      word; everything after (with leading spaces skipped) is the parameter.
///   2. Match iff the typed word is a case-insensitive PREFIX OF THE COMMAND NAME, AND
///      EITHER it is a full-length match OR the separator was a '.' and
///      the typed word is at least <c>min</c> long — i.e. dot-abbreviation.
///
/// The direction of that prefix test is the crux: it asks "is what the user typed a prefix of my
/// command name?", not "does what the user typed start with my command name?". RetroFS now reproduces
/// this in <see cref="OsCliCommandMatcher"/>. The pre-refactor code used the inverted literal
/// StartsWith, which produced both false negatives (<c>*CA.</c>, <c>*I.</c>) and false positives
/// (<c>*EXEC</c> matching the <c>EX</c> arm); the tests below that were previously [Ignore]d for that
/// bug now run and pass.
///
/// Golden byte fixtures below marked "LIVE CAPTURE" are transcribed from a real BeebEm (DNFS/ANFS 3.60)
/// session against RetroFS on 2026-07-17.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class OsCliParityTests
{
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new EconetFsTestHarness();

    // ---------------------------------------------------------------------------------------------
    // The OSCLI fall-through: unknown command => [0x08, 0x00, <original text>]
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// An unrecognised OSCLI command is
    /// NOT an error. The server builds a reply with <c>data[0]=0x08</c> / <c>data[1]=0x00</c> and then copies
    /// <c>datalen-5</c> bytes verbatim from <c>data+5</c> — the raw parameter bytes after the 5-byte
    /// request header (reply_port, fsop, URD, CWD, LIB), INCLUDING the terminating CR.
    ///
    /// Return code 0x08 tells the NFS/ANFS ROM "unknown command — you deal with it", which is what makes
    /// the ROM *RUN a file of that name. This is the mechanism behind SHIFT-BREAK autoboot: after login
    /// with boot option 2 the ROM issues OSCLI "!BOOT" and expects 0x08 back so it can *RUN !BOOT.
    ///
    /// LIVE CAPTURE 2026-07-17: RetroFS answered <c>00 FE "Bad command" 0D</c> here, which is final to the
    /// ROM and killed autoboot. Fixed in the OSCLI dispatcher's unknown-command fall-through
    /// (<c>FileServerReply.UnknownCommand</c>). This test locks the fix.
    /// </summary>
    [Test]
    public void UnknownCommand_Boot_ReturnsZeroEightAndEchoesRawBytesIncludingCr()
    {
        _harness.HandleOsCli("I AM SYST \r");

        EconetMessage reply = _harness.HandleOsCli("!BOOT\r");

        // Golden: 08 00 21 42 4F 4F 54 0D  ("!BOOT" + CR echoed after the 08 00 header).
        Assert.That(reply.Payload, Is.EqualTo(new byte[]
        {
            0x08, 0x00,                         // "unknown command", no error
            0x21, 0x42, 0x4F, 0x4F, 0x54, 0x0D, // "!BOOT" + CR, verbatim from data+5
        }));
    }

    /// <summary>
    /// Same fall-through for an ordinary program name — this is how <c>*MYPROG</c> reaches the ROM's
    /// *RUN path.
    /// </summary>
    [Test]
    public void UnknownCommand_ArbitraryProgram_ReturnsZeroEightPrefix()
    {
        _harness.HandleOsCli("I AM SYST \r");

        EconetMessage reply = _harness.HandleOsCli("MYPROG\r");

        Assert.That(reply.Payload[0], Is.EqualTo(0x08), "unknown command return code");
        Assert.That(reply.Payload[1], Is.EqualTo(0x00), "not an error");
        Assert.That(Encoding.ASCII.GetString(reply.Payload, 2, 6), Is.EqualTo("MYPROG"));
        Assert.That(reply.Payload[8], Is.EqualTo(0x0D), "trailing CR echoed verbatim");
    }

    // ---------------------------------------------------------------------------------------------
    // Not-logged-in gate
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Any OSCLI command other than the four login spellings, issued by a station that is
    /// not logged in, gets error <c>0xBF "Who are you ?"</c>.
    ///
    /// The message text is now byte-exact with the reference: <c>"Who are you ?"</c> (space before the
    /// '?'). It was previously emitted without the space; that divergence was closed on 2026-07-17, so
    /// the golden bytes below carry the extra 0x20. This one field therefore no longer matches the
    /// older live capture (which recorded the no-space form) — the BBC only prints the text, so the
    /// change is display-only.
    /// </summary>
    [Test]
    public void NotLoggedIn_OsCli_ReturnsWhoAreYouGoldenBytes()
    {
        EconetMessage reply = _harness.HandleOsCli("CAT\r");

        // Golden (spec-exact): 00 BF 57 68 6F 20 61 72 65 20 79 6F 75 20 3F 0D
        Assert.That(reply.Payload, Is.EqualTo(new byte[]
        {
            0x00, FileServerErrors.WhoAreYou, // 0x00 0xBF
            0x57, 0x68, 0x6F, 0x20, 0x61, 0x72, 0x65, 0x20, 0x79, 0x6F, 0x75, 0x20, 0x3F, // "Who are you ?"
            0x0D,
        }));
    }

    /// <summary>
    /// The login spellings are tested BEFORE the logged-in gate, so a wrong password
    /// must produce 0xBC, not 0xBF. LIVE CAPTURE 2026-07-17: <c>00 BC "Wrong password" 0D</c>.
    /// </summary>
    [Test]
    public void Login_WrongPassword_ReturnsBcWrongPassword()
    {
        EconetMessage reply = _harness.HandleOsCli("I AM SYST NOTTHEPASSWORD\r");

        Assert.That(reply.Payload[0], Is.EqualTo(0x00));
        Assert.That(reply.Payload[1], Is.EqualTo(FileServerErrors.WrongPassword), "&BC");
        Assert.That(Encoding.ASCII.GetString(reply.Payload, 2, reply.Payload.Length - 3),
            Is.EqualTo("Wrong password"));
        Assert.That(reply.Payload[^1], Is.EqualTo(0x0D), "error strings are CR terminated");
    }

    // ---------------------------------------------------------------------------------------------
    // Login / *DIR / *LIB golden return codes
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// LIVE CAPTURE 2026-07-17: <c>*I AM SYST pw</c> -> <c>05 00 01 02 04 00</c>, and after
    /// <c>*OPT 4,2</c> the same login returned <c>05 00 01 02 04 02</c> — i.e. the last byte is the
    /// persisted boot option (return code 5 = "logged on").
    /// </summary>
    [Test]
    public void Login_Syst_GoldenSixBytesWithBootOptionInLastByte()
    {
        EconetMessage reply = _harness.HandleOsCli("I AM SYST \r");

        Assert.That(reply.Payload, Is.EqualTo(new byte[] { 0x05, 0x00, 0x01, 0x02, 0x04, 0x00 }));
    }

    /// <summary>
    /// A successful <c>*DIR</c> replies <c>[0x07, 0x00, new handle]</c> (the handle as a wire byte).
    /// LIVE CAPTURE 2026-07-17 (<c>*DIR :RETROFS</c>): <c>07 00 02</c>.
    /// </summary>
    [Test]
    public void Dir_ToDiscRoot_ReturnsGoldenSevenZeroHandle()
    {
        _harness.HandleOsCli("I AM SYST \r");

        EconetMessage reply = _harness.HandleOsCli("DIR :RetroFS\r");

        Assert.That(reply.Payload, Is.EqualTo(new byte[] { 0x07, 0x00, 0x02 }));
    }

    /// <summary>
    /// A successful <c>*LIB</c> replies <c>[0x09, 0x00, new handle]</c> (the handle as a wire byte).
    /// Note the server uses a DIFFERENT return code for LIB (0x09) than for DIR (0x07), and RetroFS matches.
    /// </summary>
    [Test]
    public void Lib_NoArgument_ReturnsGoldenNineZeroHandle()
    {
        _harness.HandleOsCli("I AM SYST \r");

        EconetMessage reply = _harness.HandleOsCli("LIB\r");

        Assert.That(reply.Payload[0], Is.EqualTo(0x09), "'changed library' return code");
        Assert.That(reply.Payload[1], Is.EqualTo(0x00));
        Assert.That(reply.Payload, Has.Length.EqualTo(3));
    }

    // =============================================================================================
    // KNOWN DIVERGENCES — each test below encodes the VERIFIED reference behaviour and is [Ignore]d
    // because RetroFS does not currently implement it. Do NOT weaken these to make them pass;
    // remove the [Ignore] when the production code is fixed.
    // =============================================================================================

    /// <summary>
    /// Dot-abbreviation: <c>*CA.</c> is treated as <c>*CAT</c>.
    ///
    /// The reference registers CAT with a minimum of 2, so the typed word "CA" (split at the dot) is a
    /// prefix of "CAT", length 2, dot-terminated => match. RetroFS now implements this in
    /// <see cref="OsCliCommandMatcher"/>; the pre-refactor literal StartsWith could not.
    /// </summary>
    [Test]
    public void DotAbbreviation_CaDot_IsTreatedAsCat()
    {
        _harness.HandleOsCli("I AM SYST \r");

        EconetMessage reply = _harness.HandleOsCli("CA.\r");

        // *CAT replies with return code 3 (Examine) plus the CR-terminated path.
        Assert.That(reply.Payload[0], Is.EqualTo(FileServerOpcodes.Examine));
        Assert.That(reply.Payload[1], Is.EqualTo(0x00));
    }

    /// <summary>
    /// Dot-abbreviation: <c>*I.</c> is treated as <c>*INFO</c>, not login.
    ///
    /// The reference registers INFO with a minimum of 1 and notes there that <c>*I.</c> is deliberately
    /// an abbreviation for <c>*INFO</c>. It does not collide with login because the login verb "I AM"
    /// has a minimum of 2, so comparing "I." against "I AM" pits '.' against ' ' and fails; INFO then
    /// splits "I." at the dot, leaving "I" — a prefix of "INFO", length 1, meeting its minimum. RetroFS
    /// now resolves this correctly via <see cref="OsCliCommandMatcher"/>.
    /// </summary>
    [Test]
    public void DotAbbreviation_IDot_IsTreatedAsInfo()
    {
        _harness.HandleOsCli("I AM SYST \r");

        EconetMessage reply = _harness.HandleOsCli("I.\r");

        // *INFO. RetroFS's own INFO arm redirects with return code 0x12.
        Assert.That(reply.Payload[0], Is.EqualTo(FileServerOpcodes.GetObjectInfo));
    }

    /// <summary>
    /// The prefix test runs the correct way: <c>*EXEC</c> is NOT swallowed by the EX redirect.
    ///
    /// A correct matcher requires the typed word to be a prefix OF the command name, so "EXEC" (not a
    /// prefix of "EX") does not match the EX redirect and correctly reaches the 0x08 unknown-command
    /// fall-through for the ROM to handle. The pre-refactor literal StartsWith("EX") got this backwards
    /// and wrongly answered <c>*EXEC</c> with the Examine return code; <see cref="OsCliCommandMatcher"/>
    /// fixes it (and the same inversion that would have hit *CATALOGUE and *RENUMBER).
    /// </summary>
    [Test]
    public void InvertedPrefix_Exec_MustFallThroughToUnknownCommand()
    {
        _harness.HandleOsCli("I AM SYST \r");

        EconetMessage reply = _harness.HandleOsCli("EXEC !BOOT\r");

        Assert.That(reply.Payload[0], Is.EqualTo(0x08), "must reach the unknown-command arm");
    }

    /// <summary>
    /// DIVERGENCE — <c>*CAT</c> with no argument sends one byte too many.
    ///
    /// The reference reply carries the return code, a status byte, the
    /// path argument, and a terminating CR — its length being the argument length plus three. For a
    /// bare <c>*CAT</c> the argument is the empty string, so the reference reply is
    /// exactly three bytes: <c>03 00 0D</c>.
    ///
    /// RetroFS substitutes a literal "." for the empty path (in <c>CatCommand</c>) and therefore
    /// sends <c>03 00 2E 0D</c>. Benign in practice — the ROM's follow-up Examine resolves "." to the CSD
    /// anyway, which is why *CAT is live-verified working — but it is not the reference's bytes.
    /// </summary>
    [Test]
    [Ignore("DIVERGENCE: bare *CAT must reply 03 00 0D (param is the empty string). " +
            "RetroFS injects a literal '.' in CatCommand and replies 03 00 2E 0D. " +
            "Harmless in practice (the ROM's Examine resolves '.' to the CSD) but not byte-exact.")]
    public void Cat_NoArgument_ReplyIsThreeBytesWithEmptyPath()
    {
        _harness.HandleOsCli("I AM SYST \r");

        EconetMessage reply = _harness.HandleOsCli("CAT\r");

        Assert.That(reply.Payload, Is.EqualTo(new byte[] { 0x03, 0x00, 0x0D }));
    }

    /// <summary>
    /// <c>*CDIR</c> creates a directory and replies OK.
    ///
    /// The reference dispatches <c>*CDIR</c> (min 2, so <c>*CD.</c> also works) straight to a
    /// make-directory relative to the CSD. RetroFS now has the OSCLI command (<c>CdirCommand</c>)
    /// creating the directory through the filesystem backend; previously only the CreateDir FSOP
    /// (0x1B) existed, with no OSCLI arm.
    /// </summary>
    [Test]
    public void Cdir_CreatesDirectoryAndRepliesOk()
    {
        _harness.HandleOsCli("I AM SYST \r");

        EconetMessage reply = _harness.HandleOsCli("CDIR NEWDIR\r");

        // *CDIR replies OK => [0x00, 0x00].
        Assert.That(reply.Payload, Is.EqualTo(new byte[] { 0x00, 0x00 }));
    }

    // ---------------------------------------------------------------------------------------------
    // *ACCESS — parses "<object> [<perms>]" and writes the access byte through the property bag,
    // as the reference does. Owner letters W R H/P L before the slash; only W R
    // after it (public). Empty perms => 0. A bad letter => 0xCF "Bad attribute". See AccessCommand.
    // Ownership is relaxed to "any logged-in session may set access on a resolvable object" because
    // RetroFS has no per-object owner model (documented divergence).
    // ---------------------------------------------------------------------------------------------

    // Reads the access byte back out of the property bag the command should have written.
    private byte ReadAccessByte(string acornName)
    {
        string path = _harness.FileSystem.CombinePath(EconetSampleCatalogFixture.UserDirectory, acornName);
        AcornAttributeStore.TryReadAttributes(_harness.FileSystem, path, out _, out _, out _, out byte access);
        return access;
    }

    [Test]
    public void Access_LockOnly_SetsExactlyTheLockedBit()
    {
        _harness.HandleOsCli("I AM SYST \r");
        EconetMessage reply = _harness.HandleOsCli("ACCESS HELLO L\r");

        // Success is the plain [0x00, 0x00] completion (return code 0).
        Assert.That(reply.Payload, Is.EqualTo(new byte[] { 0x00, 0x00 }));
        // "L" alone => only the Locked bit, nothing else.
        Assert.That(ReadAccessByte("HELLO"), Is.EqualTo((byte)EconetAccess.Locked));
    }

    [Test]
    public void Access_OwnerAndPublic_SetsBothSidesOfTheSlash()
    {
        _harness.HandleOsCli("I AM SYST \r");
        _harness.HandleOsCli("ACCESS HELLO WR/R\r");

        // WR (owner) + R (public) = OwnerWrite|OwnerRead|PublicRead = 0x02|0x01|0x10 = 0x13.
        byte expected = (byte)(EconetAccess.OwnerWrite | EconetAccess.OwnerRead | EconetAccess.PublicRead);
        Assert.That(ReadAccessByte("HELLO"), Is.EqualTo(expected));
    }

    [Test]
    public void Access_HiddenLetter_SetsTheHiddenBit()
    {
        _harness.HandleOsCli("I AM SYST \r");
        _harness.HandleOsCli("ACCESS HELLO H\r");

        Assert.That(ReadAccessByte("HELLO"), Is.EqualTo((byte)EconetAccess.Hidden));
    }

    [Test]
    public void Access_EmptyPerms_ClearsEveryBit()
    {
        _harness.HandleOsCli("I AM SYST \r");
        _harness.HandleOsCli("ACCESS HELLO WR/R\r"); // set something first
        _harness.HandleOsCli("ACCESS HELLO\r");      // no perm token => clear all

        Assert.That(ReadAccessByte("HELLO"), Is.EqualTo((byte)EconetAccess.None));
    }

    [Test]
    public void Access_BadLetter_ReturnsBadAttributeAndLeavesTheByteUntouched()
    {
        _harness.HandleOsCli("I AM SYST \r");
        EconetMessage reply = _harness.HandleOsCli("ACCESS HELLO Z\r");

        // 'Z' is not a legal permission letter => 0xCF "Bad attribute" (error code at payload[1]).
        Assert.That(reply.Payload[1], Is.EqualTo(FileServerErrors.BadAttribute));
        // The parse fails before any write, so no property bag exists and the default 0x33 is reported back.
        Assert.That(ReadAccessByte("HELLO"), Is.EqualTo(0x33));
    }

    [Test]
    public void Access_LockAfterSlash_IsRejected()
    {
        _harness.HandleOsCli("I AM SYST \r");
        // 'L' is only legal on the owner side; after the slash only W and R are allowed.
        EconetMessage reply = _harness.HandleOsCli("ACCESS HELLO R/L\r");

        Assert.That(reply.Payload[1], Is.EqualTo(FileServerErrors.BadAttribute));
    }

    [Test]
    public void Access_UnknownObject_ReturnsNotFound()
    {
        _harness.HandleOsCli("I AM SYST \r");
        EconetMessage reply = _harness.HandleOsCli("ACCESS NOSUCH L\r");

        Assert.That(reply.Payload[1], Is.EqualTo(FileServerErrors.NotFound));
    }

    /// <summary>
    /// DIVERGENCE (already recorded in the handoff §4a; VERIFIED here, not rediscovered).
    ///
    /// The reference <c>*DIR</c> answers a missing directory with
    /// error <c>0xFE "Not found"</c>. RetroFS returns
    /// <c>FileServerErrors.NotFound</c> = <c>0xD6</c> with the same text.
    ///
    /// For the record, the reference *DIR uses distinct codes that RetroFS should eventually mirror:
    /// 0xAF "Types don't match", 0xC7 "Dir unreadable", 0xBD "Insufficient
    /// access", 0xFE "Not found". Note *LIB uses 0xD6 for
    /// "Dir unreadable" where *DIR uses 0xC7 — the two commands are NOT symmetric.
    /// </summary>
    [Test]
    [Ignore("KNOWN DIVERGENCE (handoff §4a): the LaterFS DEFAULT *DIR not-found returns &D6 (RetroFS's " +
            "live-verified golden); the reference returns &FE 'Not found'. Task 0.4 makes &FE " +
            "reachable via WireProfile.Classic1983 (see DirCommandWireProfileTests) WITHOUT changing the " +
            "default byte. OPEN QUESTION: whether &FE is really the later-FS code or " +
            "the 1983 code (per wire-format-verification.md) is unresolved — needs a BeebEm re-test before " +
            "the default could flip. This test still asserts the default = &FE, so it stays ignored.")]
    public void Dir_NotFound_ReturnsFeNotFound()
    {
        _harness.HandleOsCli("I AM SYST \r");

        EconetMessage reply = _harness.HandleOsCli("DIR NOSUCHDIR\r");

        Assert.That(reply.Payload[1], Is.EqualTo(0xFE), "reference *DIR not-found code");
    }
}
