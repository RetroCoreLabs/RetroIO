//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Golden wire fixtures transcribed from a REAL BeebEm (DNFS/ANFS 3.60) session against RetroFS on
/// 2026-07-17. Every REQUEST byte string below is what the NFS ROM actually put on the wire; every
/// expected REPLY is what RetroFS actually sent back and what the ROM accepted without retransmitting.
///
/// These complement <see cref="GoldenWireFormatTests"/>: that file locks the formats that once broke
/// *CAT; this file locks the exact request/reply PAIRS observed live, so that a change which is
/// "plausible" against fs.c but wrong against the real ROM still gets caught.
///
/// Each fixture cites the PiFS reference at E:\Dev\Emulators\6502\PiEconetBridge\utilities\fs.c.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class LiveCaptureGoldenTests
{
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new EconetFsTestHarness();

    /// <summary>
    /// LIVE CAPTURE — request <c>90 12 01 02 04 06 0D</c> (fsop 0x12 GetObjectInfo, command byte 6,
    /// empty path), reply <c>00 00 02 00 0A 55 53 45 52 20 20 20 20 20 20 00 04</c>.
    ///
    /// PiFS dispatches this at fs.c:11408 -> fs_get_object_info. Command 6 is the directory-info variant
    /// the ROM uses for *CAT. Layout (17 bytes):
    ///   [0..1]  = 00 00  (status: OK)
    ///   [2]     = object type (0x02 = directory)
    ///   [3]     = 0x00
    ///   [4]     = 0x0A  (name field length = 10)
    ///   [5..14] = directory name, space padded to 10
    ///   [15]    = owner/access flag
    ///   [16]    = number of entries in the directory
    ///
    /// The name is "USER" because the CSD immediately after login is the home dir $.USER, and PiFS
    /// reports "$" only at the disc root (npath == 0, fs.c:10388) — see handoff §4a item 4.
    /// </summary>
    [Test]
    public void GetObjectInfo_Cmd6_AfterLogin_MatchesLiveCaptureSeventeenBytes()
    {
        _harness.HandleOsCli("I AM SYST \r");

        // Request params as captured: 06 0D  (command byte 6, then the CR-terminated empty path).
        EconetMessage reply = _harness.HandleLoggedIn(
            FileServerOpcodes.GetObjectInfo,
            new byte[] { 0x06, 0x0D });

        // NOTE ON THE ENTRY COUNT: the live capture's home dir held 4 files (BASIC, HELLO, RCTEST,
        // README) and so reported 0x04 at [16]. EconetSampleCatalogFixture seeds a different number, so
        // [16] is asserted against the fixture's actual content rather than hard-coded to the live
        // value — the LAYOUT is the golden part, the file count is fixture data.
        Assert.That(reply.Payload, Has.Length.EqualTo(17), "LIVE CAPTURE: cmd 6 reply is 17 bytes");

        byte[] expectedPrefix =
        {
            0x00, 0x00,                                                 // OK
            0x02,                                                       // [2] type = directory
            0x00,                                                       // [3]
            0x0A,                                                       // [4] name length = 10
            0x55, 0x53, 0x45, 0x52, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, // [5..14] "USER      "
            0x00,                                                       // [15] owner flag
        };

        // Indexed loop only — the project forbids LINQ and foreach.
        for (int i = 0; i < expectedPrefix.Length; i++)
            Assert.That(reply.Payload[i], Is.EqualTo(expectedPrefix[i]), $"cmd 6 reply byte {i}");

        Assert.That(reply.Payload[16], Is.GreaterThan(0), "[16] = entry count in the CSD");
    }

    /// <summary>
    /// LIVE CAPTURE — fsop 0x15 (Read user environment), reply is 39 bytes:
    /// <c>00 00 10 "RetroFS         " "USER      " "USER      "</c>.
    ///
    /// PiFS dispatches at fs.c:11417 -> fs_read_user_env. Layout:
    ///   [0..1]   = 00 00
    ///   [2]      = 0x10 (16) — the disc-name field width
    ///   [3..18]  = current disc name, space padded to 16
    ///   [19..28] = CSD leaf name, space padded to 10
    ///   [29..38] = library leaf name, space padded to 10
    ///
    /// Returning a Unix path string here instead of this structure was one of the three original causes
    /// of the *CAT "Net Error" (handoff §1). Do not let it regress.
    /// </summary>
    [Test]
    public void ReadUserEnv_AfterLogin_MatchesLiveCaptureThirtyNineBytes()
    {
        _harness.HandleOsCli("I AM SYST \r");

        EconetMessage reply = _harness.HandleLoggedIn(FileServerOpcodes.ReadUserEnv, []);

        Assert.That(reply.Payload, Is.EqualTo(new byte[]
        {
            0x00, 0x00,
            0x10,                                                       // disc name field width = 16
            0x52, 0x65, 0x74, 0x72, 0x6F, 0x46, 0x53,                   // "RetroFS"
            0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20,       // padded to 16
            0x55, 0x53, 0x45, 0x52, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, // CSD leaf "USER      "
            0x55, 0x53, 0x45, 0x52, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, // lib leaf "USER      "
        }));
    }

    /// <summary>
    /// LIVE CAPTURE — request <c>90 03 01 02 04 03 00 0B 0D</c> (fsop 0x03 Examine, arg=3 human-readable,
    /// start=0, n=11, empty path). Reply is 81 bytes: <c>00 00 04 04</c> then four 19-byte text entries
    /// then the <c>0x80</c> terminator.
    ///
    /// PiFS dispatches at fs.c:11356 -> fs_examine. Arg 3 is the HUMAN-READABLE variant (fixed-width
    /// text the ROM prints verbatim); arg 0 is machine-readable and takes an entirely different code
    /// path. Serving arg-3 requests with arg-0 data was another of the original *CAT Net Error causes.
    ///
    /// Header: [2] = number of entries actually examined, [3] = total directory size.
    /// Entry:  10-byte name, space pad to 16, then "WR/WR", then 0x00. A directory carries a 'D' at
    ///         offset 16 (see the disc-root capture, where "USER" is followed by 0x44 = 'D').
    /// </summary>
    [Test]
    public void Examine_Arg3_FromStartOfHomeDir_MatchesLiveCaptureHeaderAndTerminator()
    {
        _harness.HandleOsCli("I AM SYST \r");

        // Request params as captured: 03 00 0B 0D  (arg=3, start=0, count=11, empty path).
        EconetMessage reply = _harness.HandleLoggedIn(
            FileServerOpcodes.Examine,
            new byte[] { 0x03, 0x00, 0x0B, 0x0D });

        // The live capture had 4 files in the home dir and so ran to 4 + (4 * 19) + 1 = 81 bytes.
        // EconetSampleCatalogFixture seeds a different number, so the ENTRY WIDTH (19 bytes) and the
        // header/terminator contract are the golden parts; the count itself is fixture data. Deriving
        // the expected length from [2] is what actually pins the 19-byte stride.
        Assert.That(reply.Payload[0], Is.EqualTo(0x00));
        Assert.That(reply.Payload[1], Is.EqualTo(0x00));

        byte examined = reply.Payload[2];
        byte dirSize = reply.Payload[3];
        Assert.That(examined, Is.GreaterThan(0), "[2] entries examined");
        Assert.That(examined, Is.EqualTo(dirSize), "[3] total dir size — the whole dir fits in one reply");
        Assert.That(reply.Payload, Has.Length.EqualTo(4 + (examined * 19) + 1),
            "LIVE CAPTURE stride: 4-byte header + 19 bytes per arg-3 text entry + 0x80 terminator");
        Assert.That(reply.Payload[^1], Is.EqualTo(0x80), "0x80 end-of-listing terminator");

        // Each entry is: 10-byte name field, space padded, then "WR/WR" then 0x00 — as captured live.
        // Indexed loop only — the project forbids LINQ and foreach.
        byte[] expectedAccess = Encoding.ASCII.GetBytes("WR/WR");
        for (int entry = 0; entry < examined; entry++)
        {
            int entryStart = 4 + (entry * 19);
            for (int i = 0; i < expectedAccess.Length; i++)
            {
                Assert.That(reply.Payload[entryStart + 13 + i], Is.EqualTo(expectedAccess[i]),
                    $"entry {entry} access byte {i} — live capture shows 'WR/WR' at offset 13");
            }

            Assert.That(reply.Payload[entryStart + 18], Is.EqualTo(0x00),
                $"entry {entry} terminator — live capture shows 0x00 closing each text entry");
        }
    }

    /// <summary>
    /// LIVE CAPTURE — request <c>90 03 01 02 04 03 04 0B 0D</c> (Examine arg=3 starting at index 4, i.e.
    /// one past the last entry of a 4-entry directory). Reply is exactly five bytes:
    /// <c>00 00 00 04 80</c> — zero examined, directory size still reported, immediate 0x80 terminator.
    ///
    /// This is the loop-termination contract: the ROM keeps re-issuing Examine with an advancing start
    /// index until it gets examined == 0. Getting this wrong is precisely what produced the endless
    /// re-request "Net error" loop described in handoff §6a step 5.
    /// </summary>
    [Test]
    public void Examine_Arg3_StartPastEnd_ReturnsGoldenFiveByteEmptyReply()
    {
        _harness.HandleOsCli("I AM SYST \r");

        EconetMessage reply = _harness.HandleLoggedIn(
            FileServerOpcodes.Examine,
            new byte[] { 0x03, 0x04, 0x0B, 0x0D });

        // Golden shape (LIVE CAPTURE): 00 00 00 <dirsize> 80. Byte [3] is fixture data (the live disc
        // held 4 files); bytes [0..2] and [4] are the contract that stops the ROM's Examine loop.
        Assert.That(reply.Payload, Has.Length.EqualTo(5), "LIVE CAPTURE: five-byte empty arg-3 reply");
        Assert.That(reply.Payload[0], Is.EqualTo(0x00));
        Assert.That(reply.Payload[1], Is.EqualTo(0x00));
        Assert.That(reply.Payload[2], Is.EqualTo(0x00), "examined = 0 -> the ROM stops here");
        Assert.That(reply.Payload[4], Is.EqualTo(0x80), "terminator");
    }

    /// <summary>
    /// LIVE CAPTURE — request <c>90 16 01 02 04 02 01 80 0D</c> (fsop 0x16 SetBootOpt). The first
    /// parameter byte is the boot option (2 = *RUN !BOOT); the trailing bytes are ROM padding that must
    /// be ignored. Reply: <c>00 00</c>.
    ///
    /// PiFS dispatches at fs.c:11420 -> fs_set_bootopt. The capture then shows the NEXT login returning
    /// <c>05 00 01 02 04 02</c> — proving the option persisted across the *BYE. That persistence is what
    /// <see cref="BootOptionTests"/> covers; this fixture only locks the reply bytes and the tolerance
    /// for trailing ROM padding.
    /// </summary>
    [Test]
    public void SetBootOpt_WithTrailingRomPadding_RepliesOkAndPersistsToNextLogin()
    {
        _harness.HandleOsCli("I AM SYST \r");

        EconetMessage reply = _harness.HandleLoggedIn(
            FileServerOpcodes.SetBootOpt,
            new byte[] { 0x02, 0x01, 0x80, 0x0D });

        Assert.That(reply.Payload, Is.EqualTo(new byte[] { 0x00, 0x00 }));

        // The live capture's very next login reported boot option 02 in the last byte.
        _harness.HandleOsCli("BYE\r");
        EconetMessage login = _harness.HandleOsCli("I AM SYST \r");

        Assert.That(login.Payload, Is.EqualTo(new byte[] { 0x05, 0x00, 0x01, 0x02, 0x04, 0x02 }));
    }

    /// <summary>
    /// LIVE CAPTURE — request <c>90 17 01 02</c> (fsop 0x17 BYE, note the SHORT 4-byte request with no
    /// LIB handle byte), reply <c>00 00</c>.
    ///
    /// PiFS dispatches at fs.c:11423 -> fs_bye. The short request matters: the handle-fixup loop at
    /// fs.c:9942-9950 is length-guarded (<c>if (datalen >= 5)</c>), so a 4-byte BYE must not be treated
    /// as malformed. RetroFS must tolerate the same truncation.
    /// </summary>
    [Test]
    public void Bye_ShortFourByteRequest_RepliesOkAndEndsTheSession()
    {
        _harness.HandleOsCli("I AM SYST \r");

        EconetMessage reply = _harness.HandleLoggedIn(FileServerOpcodes.Bye, []);

        Assert.That(reply.Payload, Is.EqualTo(new byte[] { 0x00, 0x00 }));

        // Session must be gone: any subsequent OSCLI gets &BF (fs.c:9988-9989).
        EconetMessage after = _harness.HandleOsCli("CAT\r");
        Assert.That(after.Payload[1], Is.EqualTo(FileServerErrors.WhoAreYou));
    }

    /// <summary>
    /// An fsop that arrives before login must be refused with &amp;BF regardless of which opcode it is.
    /// The live capture showed fsop 0x16 (SetBootOpt) arriving from a stale ROM state before *I AM.
    ///
    /// NOTE on the message bytes: the 2026-07-17 capture showed RetroFS emitting <c>"Who are you?"</c>
    /// (no space). The message text was later aligned to the reference's <c>"Who are you ?"</c> (space
    /// before '?', fs.c:9989) for byte-exact parity, so the golden bytes below now carry the extra
    /// 0x20 and no longer match that older capture for this one cosmetic field. The BBC only prints
    /// the text, so this is display-only. The gate itself (which opcodes are refused pre-login) is
    /// unchanged and still what the capture proves.
    ///
    /// PiFS applies this gate per-case in the big switch (e.g. fs.c:11420 for 0x16); RetroFS applies it
    /// centrally in FileServerCommandHandler.Handle using
    /// <c>FileServerOpcodes.IsAllowedWithoutLogin</c> (OsCli / ReadDiscNames / ReadVersion). Those three
    /// exemptions match PiFS: case 0x00 handles its own gate at fs.c:9988, and cases 0x0E (fs.c:11386)
    /// and 0x19 (fs.c:11432) have no login check at all.
    /// </summary>
    [Test]
    public void SetBootOpt_BeforeLogin_ReturnsWhoAreYouGoldenBytes()
    {
        EconetMessage reply = _harness.HandleLoggedIn(
            FileServerOpcodes.SetBootOpt,
            new byte[] { 0x01, 0x68, 0x6F, 0x20 });

        Assert.That(reply.Payload, Is.EqualTo(new byte[]
        {
            0x00, FileServerErrors.WhoAreYou,
            0x57, 0x68, 0x6F, 0x20, 0x61, 0x72, 0x65, 0x20, 0x79, 0x6F, 0x75, 0x20, 0x3F, // "Who are you ?"
            0x0D,
        }));
    }
}
